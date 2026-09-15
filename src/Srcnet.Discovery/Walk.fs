/// ディレクトリ走査（段 1）、分類（段 2）、読取とハッシュ（段 3）。
///
/// 3 段を 1 回の走査で行う。ファイル一覧を作ってから読み直すと、ディレクトリ項目の
/// キャッシュが効かず I/O が二重になるためである。
/// 結果は論理パスの序数順に整列するので、並列度は所要時間だけを変え、出力は変えない。
/// docs/architecture.md 2, 3 を参照。
module Srcnet.Discovery.Walk

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Threading
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Srcnet.Core
open Srcnet.Core.Diagnostics
open Srcnet.Core.Graph
open Srcnet.Core.Paths
open Srcnet.Extraction
open Srcnet.Text

type WalkOptions =
  {
    /// シンボリック リンクを追跡するか。既定は追跡しない。
    FollowSymbolicLinks: bool
    /// `.gitignore` と `.srcnetignore` を尊重するか。
    RespectIgnoreFiles: bool
    MaxDepth: int
    /// 走査するファイル数とディレクトリ数それぞれの上限。超過分は診断して切り捨てる。
    MaxEntries: int
    MaxFileSizeBytes: int64
    Jobs: int
    /// 階層を問わず除外するディレクトリ名。
    ExcludedDirectoryNames: string[]
    /// 除外する論理パス。生成物の出力先が解析ルート配下にある場合に使う。
    ExcludedPaths: string[]
    /// 抽出の条件。走査の 1 回読みの中で段 4 まで行う（backlog 014）。
    Extraction: Extractor.ExtractionOptions
    /// 符号化が曖昧なファイルに適用する符号化。利用者が明示した場合だけ設定する。
    /// 候補に含まれない場合は適用しない。docs/decisions.md ADR-5, ADR-9 を参照。
    AssumeEncoding: Encodings.DetectedEncoding voption
  }

module WalkOptions =

  [<Literal>]
  let MaxJobs = 256

  /// 既定の上限。いずれも資源枯渇を防ぐためのもので、超過は失敗ではなく診断である。
  [<Literal>]
  let DefaultMaxEntries = 8_000_000

  [<Literal>]
  let DefaultMaxFileSizeBytes = 67_108_864L

  let defaults =
    { FollowSymbolicLinks = false
      RespectIgnoreFiles = true
      MaxDepth = MaxDepth
      MaxEntries = DefaultMaxEntries
      MaxFileSizeBytes = DefaultMaxFileSizeBytes
      Jobs = min MaxJobs Environment.ProcessorCount
      ExcludedDirectoryNames = [| ".git"; ".hg"; ".svn" |]
      ExcludedPaths = Array.empty
      Extraction = Extractor.ExtractionOptions.defaults
      AssumeEncoding = ValueNone }

type DiscoveredFile =
  {
    Path: LogicalPath
    SizeBytes: int64
    Language: Language
    Flags: NodeFlags
    /// 内容ハッシュ (SHA-256) の 32 バイト ダイジェスト。読み取れなかった場合はすべて 0。
    Hash: byte[]
    Encoding: Encodings.DetectedEncoding
    /// 構造規則を満たした符号化候補。単一に確定した場合は空。
    EncodingCandidates: Encodings.DetectedEncoding[]
    LineCount: int
    /// このファイルの抽出結果。段階 0 では空。
    Extraction: Model.ExtractedFile
  }

/// 段階ごとの処理件数。`index` の出力へそのまま載せる（backlog 021）。
type TierCounts =
  {
    /// T2 まで到達したファイル数。
    Syntax: int
    /// T1 で止まったファイル数。
    LineOriented: int
    /// 抽出しなかったファイル数（バイナリ、上限超過、読み取り失敗）。
    NotExtracted: int
  }

type WalkResult =
  {
    /// 論理パスの序数昇順。ルートは含まない。
    Directories: LogicalPath[]
    /// 論理パスの序数昇順。
    Files: DiscoveredFile[]
    /// 上限や権限による打ち切りがなく、ツリー全体を走査できたか。
    Complete: bool
    /// 実際に到達した最も高い段階。要求した段階と一致しない場合は縮退している。
    AppliedTier: Model.Tier
    /// 構文解析器を利用できたか。
    ParserAvailable: bool
    Tiers: TierCounts
  }

[<Literal>]
let private GitIgnoreFileName = ".gitignore"

[<Literal>]
let private SrcnetIgnoreFileName = ".srcnetignore"

let private emptyHash = Array.zeroCreate<byte> Hashing.HashLength

[<Struct>]
type private WorkItem =
  {
    Path: LogicalPath
    Physical: string
    /// 親ディレクトリのリンクを解決済みの実体パス。
    /// 子の実体パスをここから組み立てることで、経路の途中にあるリンクも必ず解決される。
    RealPath: string
    /// この経路の祖先だけを比較し、別経路から同じ実体を指す正当なリンクを循環と誤認しない。
    Ancestors: string list
    Depth: int
    RuleSets: Ignore.RuleSet[]
  }

/// パスを完全に正準化する。
///
/// `FileSystemInfo.LinkTarget` は最終要素のリンクしか教えないため、経路の途中にある
/// リンクは解決されない。ルート側から 1 要素ずつ解決しなければ、ルート内のリンクを
/// 中継した名前が文字列上はルート内に見え、ルート外の内容を読めてしまう。
/// docs/security.md C-3 を参照。
///
/// `budget` はリンクの連鎖と循環に対する打ち切り上限で、無限再帰を防ぐ。
let rec private canonicalize (path: string) (budget: int) =
  if budget <= 0 then
    path
  else
    let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)

    match Path.GetDirectoryName full, Path.GetFileName full with
    | null, _ -> full // ファイルシステムのルート
    | _, null -> full
    | parentPath, name ->
      let canonicalParent = canonicalize parentPath (budget - 1)
      let combined = Path.Combine(canonicalParent, name)

      match FileInfo(combined).LinkTarget with
      | null -> Path.TrimEndingDirectorySeparator combined
      | target ->
        let absolute =
          if Path.IsPathRooted target then
            target
          else
            Path.Combine(canonicalParent, target)

        canonicalize absolute (budget - 1)

/// リンクの連鎖と階層の深さに対する打ち切り上限。
[<Literal>]
let private CanonicalizeBudget = 512

let private canonicalizePath (path: string) = canonicalize path CanonicalizeBudget

/// 正準な親のもとで子の実体パスを求める。
///
/// 親が正準であれば未解決の要素は `name` だけなので、リンクでなければ連結するだけでよい。
/// これにより通常のファイルでは追加のシステム コールが発生しない。
let private resolveChild (parentRealPath: string) (name: string) (linkTarget: string | null) =
  match linkTarget with
  | null -> Path.Combine(parentRealPath, name)
  | target ->
    let absolute =
      if Path.IsPathRooted target then
        target
      else
        Path.Combine(parentRealPath, target)

    canonicalizePath absolute

/// 無視ファイルの読取結果。
///
/// 読めなかった場合と打ち切った場合は、適用できなかった規則が残る。
/// 走査対象を確定できないため、いずれも走査の完全性を損なう事実として扱う。
[<Struct>]
type private IgnoreOutcome =
  | IgnoreLoaded of ruleSet: Ignore.RuleSet
  | IgnoreRejected of reason: string

/// 無視ファイル 1 つを読む。
///
/// 通常のエントリと同じ順序で属性とリンクを検証してから開く。先に読んでしまうと、
/// `--follow-symlinks` を指定していなくても解析ルート外のファイルを読み込む。
/// docs/security.md C-3 を参照。
///
/// サイズ 0 のファイルは開かない。FIFO やキャラクタ デバイスは `stat` 上のサイズが 0 で
/// 通常ファイルと managed API では区別できず、開くと無期限に blocking するためである。
let private readIgnoreFile
  (entry: FileSystemInfo)
  (parentRealPath: string)
  (isInsideRoot: string -> bool)
  (baseDepth: int)
  (cancellation: CancellationToken)
  =
  if not(isNull entry.LinkTarget) then
    IgnoreRejected "リンク経由の無視ファイルは信頼境界の外にあるため読みません"
  else

    let real = Path.Combine(parentRealPath, entry.Name)

    if not(isInsideRoot real) then
      IgnoreRejected "実体が解析ルートの外です"
    else

      let length =
        match entry with
        | :? FileInfo as file -> file.Length
        | _ -> 0L

      if length = 0L then
        // 空ファイルの規則集合は開かずに決定できる。特殊ファイルによる停止を構造的に防ぐ。
        IgnoreLoaded(Ignore.parse baseDepth Seq.empty)
      else

        try
          use stream =
            new FileStream(
              entry.FullName,
              FileStreamOptions(
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = (FileShare.ReadWrite ||| FileShare.Delete),
                BufferSize = 0,
                Options = FileOptions.SequentialScan
              )
            )

          IgnoreLoaded(Ignore.read baseDepth stream cancellation)
        with
        | :? IOException as ex -> IgnoreRejected ex.Message
        | :? UnauthorizedAccessException -> IgnoreRejected "読み取り権限がありません"

/// 解析ルート配下を走査し、ファイル ノードの材料を集める。
///
/// 例外を投げるのは取り消し時のみで、個々のファイルやディレクトリの失敗は診断として
/// 記録し走査を続ける。docs/architecture.md 5 を参照。
let run
  (physicalRoot: string)
  (options: WalkOptions)
  (diagnostics: DiagnosticSink)
  (cancellation: CancellationToken)
  : Task<WalkResult> =
  task {
    if options.Jobs < 0 || options.Jobs > WalkOptions.MaxJobs then
      invalidArg (nameof options) $"Jobs must be between 0 and {WalkOptions.MaxJobs}"

    let rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath physicalRoot)

    // ルート自身がリンク経由で与えられることがある（macOS の /tmp など）。
    // 比較の両辺を同じ正準形に揃えなければ、正当なリンクを誤って拒否する。
    let rootReal = canonicalizePath rootFull

    let rootPrefix =
      if Path.EndsInDirectorySeparator rootReal then
        rootReal
      else
        rootReal + string Path.DirectorySeparatorChar

    let excludedDirectoryNames =
      HashSet<string>(options.ExcludedDirectoryNames, StringComparer.Ordinal)

    let excludedPaths = HashSet<string>(options.ExcludedPaths, StringComparer.Ordinal)

    let files = ConcurrentBag<DiscoveredFile>()
    let directories = ConcurrentBag<LogicalPath>()
    let queue = ConcurrentQueue<WorkItem>()
    let failures = ConcurrentQueue<exn>()

    // ワーカーの致命的な失敗でも全員を起こせるよう、利用者のトークンに連結した
    // 取り消し元を用意する。これがないと 1 つの失敗が全体の停止になる。
    use failureCancellation =
      CancellationTokenSource.CreateLinkedTokenSource cancellation

    use available = new SemaphoreSlim(0)
    let mutable pending = 0
    let mutable drained = false
    let mutable entryCount = 0
    let mutable directoryCount = 0
    let mutable truncated = 0
    let mutable syntaxFiles = 0
    let mutable lineOrientedFiles = 0
    let mutable notExtractedFiles = 0
    let missingGrammars = ConcurrentDictionary<Language, byte>()

    let enqueue item =
      Interlocked.Increment &pending |> ignore
      queue.Enqueue item
      available.Release() |> ignore

    let workerCount = max 1 options.Jobs

    let completeOne () =
      if Interlocked.Decrement &pending = 0 then
        Volatile.Write(&drained, true)
        // 待機中のワーカーをすべて起こして終了させる。空回りの待機を残さない。
        available.Release workerCount |> ignore

    let noteTruncation (reason: string) =
      if Interlocked.Exchange(&truncated, 1) = 0 then
        diagnostics.Add(EntryLimitExceeded, "", reason)

    let isInsideRoot (candidate: string) =
      // 大文字小文字を無視しないのは意図的である。区別するファイルシステム上で
      // 別ディレクトリを同一と誤認するより、拒否して診断するほうが安全側に倒れる。
      candidate = rootReal
      || candidate.StartsWith(rootPrefix, StringComparison.Ordinal)

    let processDirectory (item: WorkItem) (reader: Content.ContentReader) (extractor: Extractor.Extractor) =
      let entries =
        try
          DirectoryInfo(item.Physical).GetFileSystemInfos()
        with
        | :? UnauthorizedAccessException ->
          diagnostics.Add(PermissionDenied, value item.Path, "ディレクトリを列挙できません")
          Array.empty
        | :? DirectoryNotFoundException ->
          diagnostics.Add(FileUnreadable, value item.Path, "走査中にディレクトリが消えました")
          Array.empty
        | :? IOException as ex ->
          diagnostics.Add(FileUnreadable, value item.Path, ex.Message)
          Array.empty

      Array.sortInPlaceWith
        (fun (a: FileSystemInfo) (b: FileSystemInfo) -> String.CompareOrdinal(a.Name, b.Name))
        entries

      let ruleSets =
        if not options.RespectIgnoreFiles then
          item.RuleSets
        else
          let baseDepth = depth item.Path

          let found =
            entries
            |> Array.filter(fun entry ->
              (entry.Name = GitIgnoreFileName || entry.Name = SrcnetIgnoreFileName)
              && not(entry :? DirectoryInfo))
            |> Array.choose(fun entry ->
              match readIgnoreFile entry item.RealPath isInsideRoot baseDepth cancellation with
              | IgnoreRejected reason ->
                diagnostics.Add(IgnoreFileUnreadable, value item.Path, $"{entry.Name}: {reason}")
                None
              | IgnoreLoaded ruleSet ->
                match ruleSet.Truncation with
                | ValueSome truncation ->
                  diagnostics.Add(
                    IgnoreFileUnreadable,
                    value item.Path,
                    $"{entry.Name}: {Ignore.Truncation.describe truncation}"
                  )
                | ValueNone -> ()

                if Ignore.isEmpty ruleSet then None else Some ruleSet)

          if found.Length = 0 then
            item.RuleSets
          else
            Array.append item.RuleSets found

      let caseFolded = Dictionary<string, string>(StringComparer.Ordinal)

      // 走査中に他プロセスがファイルを消す、リンクの解決が失敗するといった競合は
      // 単一項目の失敗であり、走査全体を止めてはならない。項目ごとに封じ込める。
      let processEntry (entry: FileSystemInfo) =
        let folded = Unicode.caseFold entry.Name

        match caseFolded.TryGetValue folded with
        | true, first -> diagnostics.Add(CaseOnlyCollision, value item.Path, $"{first} と {entry.Name} が大文字小文字だけで異なります")
        | false, _ -> caseFolded[folded] <- entry.Name

        match append item.Path entry.Name with
        | Error error -> diagnostics.Add(PathRejected, value item.Path, $"{entry.Name}: {PathError.describe error}")
        | Ok childPath ->
          let childValue = value childPath
          let isDirectory = entry :? DirectoryInfo
          let isLink = not(isNull entry.LinkTarget)
          let segments = Ignore.segmentsOf childPath

          let excluded =
            excludedPaths.Contains childValue
            || (isDirectory
                && (entry.Name.StartsWith('.') || excludedDirectoryNames.Contains entry.Name))

          let ignored =
            excluded
            || (options.RespectIgnoreFiles
                && Ignore.decide ruleSets segments isDirectory = Ignore.Ignored)

          if ignored then
            ()
          elif isLink && not options.FollowSymbolicLinks then
            diagnostics.Add(SymbolicLinkSkipped, childValue, "既定ではシンボリック リンクを追跡しません")
          else

            // 親の実体パスは正準なので、リンクのときだけ完全な正準化を行えばよい。
            let childReal = resolveChild item.RealPath entry.Name entry.LinkTarget

            let containment =
              if isInsideRoot childReal then
                ValueSome childReal
              else
                diagnostics.Add(SymbolicLinkEscapesRoot, childValue, "解決先が解析ルートの外です")
                ValueNone

            match containment with
            | ValueNone -> ()
            | ValueSome resolved ->

              if isDirectory then
                if item.Depth + 1 > options.MaxDepth then
                  diagnostics.Add(DepthLimitExceeded, childValue, $"深さ上限 {options.MaxDepth} を超えました")
                elif isLink && List.contains resolved item.Ancestors then
                  diagnostics.Add(DirectoryCycle, childValue, "この経路の祖先を指しています")
                elif Interlocked.Increment(&directoryCount) > options.MaxEntries then
                  noteTruncation $"ディレクトリ項目数が上限 {options.MaxEntries} を超えました"
                else
                  directories.Add childPath

                  enqueue
                    { Path = childPath
                      Physical = entry.FullName
                      RealPath = resolved
                      Ancestors = resolved :: item.Ancestors
                      Depth = item.Depth + 1
                      RuleSets = ruleSets }
              else
                let index = Interlocked.Increment &entryCount

                // 上限は資源枯渇に対する安全弁である。到達した場合、どの項目が残るかは
                // ワーカーの進行順に依存するため成果物は決定的でなくなる。そのため
                // `EntryLimitExceeded` は走査を不完全として扱い、既定では既存の成果物を
                // 上書きしない（--allow-partial が必要）。有界メモリでの完全な走査は
                // spill と外部ソートの設計が要るため M7 で扱う。
                if index > options.MaxEntries then
                  noteTruncation $"ファイル項目数が上限 {options.MaxEntries} を超えました"
                else
                  let fileInfo = entry :?> FileInfo
                  let language = Classify.language childPath
                  let pathFlags = Classify.pathFlags childPath
                  let linkFlag = if isLink then NodeFlags.SymbolicLink else NodeFlags.None

                  match reader.Read(entry.FullName, fileInfo.Length, cancellation) with
                  | Ok summary ->
                    if summary.Encoding = Encodings.Undetermined then
                      diagnostics.Add(UndeterminedEncoding, childValue, "置換文字で復号します")

                    // 候補が複数残った場合、判定順の先頭を確定値として黙って採用しない。
                    // ADR-5 に従い曖昧さを利用者へ渡す。
                    if summary.EncodingCandidates.Length > 1 then
                      let names =
                        summary.EncodingCandidates |> Array.map Encodings.name |> String.concat ", "

                      diagnostics.Add(AmbiguousEncoding, childValue, $"複数の符号化に適合します ({names})")

                    let discoveredFlags = pathFlags ||| linkFlag ||| summary.Flags

                    // 抽出は読み取りと同じ 1 回の走査の中で行う。ここを後段へ移すと
                    // ファイルを二度開くことになり、走査時間がほぼ倍になる（backlog 014）。
                    let extraction =
                      if options.Extraction.Tier = Model.Structure then
                        Model.ExtractedFile.empty Model.Structure
                      elif summary.Decoded then
                        extractor.Extract(
                          language,
                          childValue,
                          reader.Decoded,
                          summary.DecodedLength,
                          discoveredFlags,
                          cancellation
                        )
                      elif discoveredFlags.HasFlag NodeFlags.Binary then
                        Model.ExtractedFile.skipped Model.Structure Model.NotText
                      elif fileInfo.Length > options.Extraction.MaxExtractionBytes then
                        Model.ExtractedFile.skipped Model.Structure (Model.TooLargeToExtract fileInfo.Length)
                      elif summary.EncodingCandidates.Length > 1 then
                        // 候補の先頭を確定値として扱わない。抽出せず、曖昧さを成果物へ残す。
                        let names =
                          summary.EncodingCandidates |> Array.map Encodings.name |> String.concat ", "

                        Model.ExtractedFile.skipped Model.Structure (Model.AmbiguousEncoding names)
                      else
                        Model.ExtractedFile.skipped
                          Model.Structure
                          (Model.UnsupportedEncoding(Encodings.name summary.Encoding))

                    match extraction.Tier with
                    | Model.Syntax -> Interlocked.Increment &syntaxFiles |> ignore
                    | Model.LineOriented -> Interlocked.Increment &lineOrientedFiles |> ignore
                    | Model.Structure
                    | Model.BuildAware -> Interlocked.Increment &notExtractedFiles |> ignore

                    if extraction.Truncated then
                      diagnostics.Add(ExtractionTruncated, childValue, "抽出の上限に達したため打ち切りました")

                    match extraction.Skipped with
                    | ValueSome((Model.ParseTimedOut | Model.ParseUnavailable _) as reason) ->
                      diagnostics.Add(ExtractionDegraded, childValue, Model.SkipReason.describe reason)
                    | ValueSome Model.NoGrammar -> missingGrammars.TryAdd(language, 0uy) |> ignore
                    | ValueSome(Model.TooLargeToExtract _)
                    | ValueSome Model.NotText
                    | ValueSome(Model.AmbiguousEncoding _)
                    | ValueSome(Model.UnsupportedEncoding _)
                    | ValueNone -> ()

                    files.Add
                      { Path = childPath
                        SizeBytes = fileInfo.Length
                        Language = language
                        Flags = discoveredFlags ||| extraction.FileFlags
                        Hash = summary.Hash
                        Encoding = summary.Encoding
                        EncodingCandidates = summary.EncodingCandidates
                        LineCount =
                          match extraction.LineCount with
                          | ValueSome count -> count
                          | ValueNone -> summary.LineCount
                        Extraction = extraction }
                  | Error error ->
                    let kind =
                      match error with
                      | Content.TooLarge _ -> FileTooLarge
                      | Content.AccessDenied -> PermissionDenied
                      | Content.NotFound -> FileUnreadable
                      | Content.IoFailure _ -> FileUnreadable

                    diagnostics.Add(kind, childValue, Content.ReadError.describe error)

                    Interlocked.Increment &notExtractedFiles |> ignore

                    files.Add
                      { Path = childPath
                        SizeBytes = fileInfo.Length
                        Language = language
                        Flags = pathFlags ||| linkFlag ||| NodeFlags.Skipped
                        Hash = emptyHash
                        Encoding = Encodings.Undetermined
                        EncodingCandidates = Array.empty
                        LineCount = 0
                        Extraction = Model.ExtractedFile.empty Model.Structure }

      for entry in entries do
        cancellation.ThrowIfCancellationRequested()

        try
          processEntry entry
        with
        | :? IOException as ex -> diagnostics.Add(FileUnreadable, value item.Path, $"{entry.Name}: {ex.Message}")
        | :? UnauthorizedAccessException ->
          diagnostics.Add(PermissionDenied, value item.Path, $"{entry.Name}: 読み取り権限がありません")
        // 不正な名前などに由来する引数エラーも単一項目の失敗として扱う。
        // 1 件の異常な名前で走査全体を止めてはならない。
        | :? ArgumentException as ex -> diagnostics.Add(PathRejected, value item.Path, ex.Message)

    let worker () =
      task {
        // 抽出しない段階では本文を保持しない。保持量が走査の常駐メモリを決める。
        let retained =
          if options.Extraction.Tier = Model.Structure then
            0L
          else
            options.Extraction.MaxExtractionBytes

        use reader =
          new Content.ContentReader(options.MaxFileSizeBytes, retained, options.AssumeEncoding)

        let extractor = Extractor.Extractor options.Extraction
        let mutable running = true

        while running do
          try
            do! available.WaitAsync failureCancellation.Token

            match queue.TryDequeue() with
            | true, item ->
              try
                processDirectory item reader extractor
              finally
                completeOne()
            | false, _ ->
              if Volatile.Read &drained then
                running <- false
          with
          | :? OperationCanceledException -> running <- false
          | ex ->
            // 1 つのワーカーが想定外に失敗したまま放置すると、残りのワーカーは
            // 到達しない完了を待ち続けて停止する。取り消して全員を起こす。
            failures.Enqueue ex
            failureCancellation.Cancel()
            running <- false
      }
      :> Task

    enqueue
      { Path = root
        Physical = rootFull
        RealPath = rootReal
        Ancestors = [ rootReal ]
        Depth = 0
        RuleSets = Array.empty }

    // SemaphoreSlim.WaitAsync は許可があると同期完了するため、worker() をその場で
    // 呼ぶと最初の 1 つがキューを走査し尽くしてしまい、並列度が 1 に潰れる。
    // 必ずスレッド プールへ載せてから待ち合わせる。
    do! Task.WhenAll(Array.init workerCount (fun _ -> Task.Run(fun () -> worker())))

    // 取り消しが先に立つ。利用者の中断を内部エラーとして報告しない。
    cancellation.ThrowIfCancellationRequested()

    match failures.TryDequeue() with
    | true, ex -> ExceptionDispatchInfo.Capture(ex).Throw()
    | false, _ -> ()

    let orderedDirectories = directories.ToArray()
    Array.sortInPlaceWith comparePath orderedDirectories

    let orderedFiles = files.ToArray()
    Array.sortInPlaceWith (fun (a: DiscoveredFile) (b: DiscoveredFile) -> comparePath a.Path b.Path) orderedFiles

    // 走査の網羅性を損なう診断。個々のファイルの読み取り失敗と違い、
    // これらは「どれだけ見落としたか分からない」状態を意味する。
    //
    // `IgnoreFileUnreadable` を含めるのは、適用できなかった無視規則があると
    // 索引対象そのものが確定しないためである。除外すべきファイルを取り込んだのか、
    // 取り込むべきファイルを落としたのかを、成果物から判別できない。
    let incompleteKinds =
      [| PermissionDenied
         DepthLimitExceeded
         EntryLimitExceeded
         DirectoryCycle
         FileUnreadable
         PathRejected
         IgnoreFileUnreadable |]

    let observed = diagnostics.Counts() |> Array.map(fun (struct (kind, _)) -> kind)

    let complete =
      Volatile.Read &truncated = 0
      && diagnostics.ErrorCount = 0
      && not(incompleteKinds |> Array.exists(fun kind -> Array.contains kind observed))

    // 解析器そのものの不在は実行あたり 1 回にまとめる。ファイルごとに出すと、数千万ファイルで
    // 同じ内容が繰り返される（backlog 021）。
    let parserAvailable = Parsing.isAvailable.Value

    let appliedTier =
      if options.Extraction.Tier = Model.Structure then
        Model.Structure
      elif Volatile.Read &syntaxFiles > 0 then
        Model.Syntax
      elif
        Model.Tier.rank options.Extraction.Tier >= Model.Tier.rank Model.Syntax
        && not parserAvailable
      then
        Model.LineOriented
      else
        Model.LineOriented

    if
      Model.Tier.rank options.Extraction.Tier >= Model.Tier.rank Model.Syntax
      && not parserAvailable
    then
      diagnostics.Add(ExtractionDegraded, "", "構文解析器を利用できないため、抽出は T1（行指向）までで実行しました")
    elif parserAvailable then
      for language in missingGrammars.Keys |> Seq.sortBy Language.toCode do
        diagnostics.Add(ExtractionDegraded, "", $"{Language.name language} の文法を利用できないため、抽出は T1（行指向）までで実行しました")

    return
      { Directories = orderedDirectories
        Files = orderedFiles
        Complete = complete
        AppliedTier = appliedTier
        ParserAvailable = parserAvailable
        Tiers =
          { Syntax = Volatile.Read &syntaxFiles
            LineOriented = Volatile.Read &lineOrientedFiles
            NotExtracted = Volatile.Read &notExtractedFiles } }
  }
