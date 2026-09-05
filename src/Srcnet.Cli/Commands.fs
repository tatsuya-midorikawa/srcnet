/// サブコマンドの実装。
///
/// `stdout` には結果のみ、進捗と診断は `stderr` へ出す。終了コードは
/// docs/query-and-cli.md 2.2 に従う。
module Srcnet.Cli.Commands

open System
open System.IO
open System.Runtime.ExceptionServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Srcnet.Core.Diagnostics
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Core.Paths
open Srcnet.Discovery
open Srcnet.Storage
open Srcnet.Text

module ExitCode =

  [<Literal>]
  let Success = 0

  [<Literal>]
  let NoResults = 1

  [<Literal>]
  let UserError = 2

  [<Literal>]
  let MissingArtifact = 3

  [<Literal>]
  let CompletedWithDiagnostics = 4

  [<Literal>]
  let Interrupted = 5

  [<Literal>]
  let InternalError = 70

/// JSON 出力のスキーマ版。破壊的変更時に増やす。docs/query-and-cli.md 5.1 を参照。
[<Literal>]
let SchemaVersion = 1

let private jsonWriterOptions =
  JsonWriterOptions(Indented = true, IndentCharacter = ' ', IndentSize = 2, NewLine = Terminal.Newline)

let private writeJson (build: Utf8JsonWriter -> unit) =
  use buffer = new MemoryStream()
  use writer = new Utf8JsonWriter(buffer, jsonWriterOptions)
  writer.WriteStartObject()
  writer.WriteNumber("schemaVersion", SchemaVersion)
  build writer
  writer.WriteEndObject()
  writer.Flush()
  Terminal.out (Text.Encoding.UTF8.GetString(buffer.ToArray()))
  Terminal.out Terminal.Newline

let private reportDiagnostics (diagnostics: DiagnosticSink) =
  let counts = diagnostics.Counts()

  if counts.Length > 0 then
    Terminal.errLine "診断:"

    for struct (kind, count) in counts do
      Terminal.errLine $"  {DiagnosticKind.name kind}: {count} 件"

    for sample in diagnostics.Samples() do
      let location = if sample.Path.Length = 0 then "" else sample.Path + ": "
      Terminal.diagnosticLine (Terminal.fitToWidth $"  [{DiagnosticKind.name sample.Kind}] {location}{sample.Detail}")

    let hidden = diagnostics.Total - diagnostics.Samples().Length

    if hidden > 0 then
      Terminal.errLine $"  ほか {hidden} 件は省略しました (種別ごとに最大 {diagnostics.MaxSamplesPerKind} 件を表示)"

let private diagnosticCounts (diagnostics: DiagnosticSink) =
  diagnostics.Counts()
  |> Array.map (fun (struct (kind, count)) ->
    { Manifest.Kind = DiagnosticKind.name kind
      Manifest.Count = count })

/// 例外を stack trace を保ったまま再送出する。`raise ex` では発生位置を失う。
let private reraise' (ex: exn) : 'T =
  ExceptionDispatchInfo.Capture(ex).Throw()
  Unchecked.defaultof<'T>

let private resolveOutputDirectory (explicit: string voption) (rootFullPath: string) =
  match explicit with
  | ValueSome directory -> Path.GetFullPath directory
  | ValueNone -> Path.Combine(rootFullPath, Args.DefaultOutputDirectoryName)

/// 出力先が解析ルート配下にある場合、その論理パスを走査から除外する。
/// 生成物を自分で索引してしまうのを防ぐ。
let private excludedOutputPath (rootFullPath: string) (outputDirectory: string) =
  let rootPrefix = Path.TrimEndingDirectorySeparator rootFullPath + string Path.DirectorySeparatorChar

  if not (outputDirectory.StartsWith(rootPrefix, StringComparison.Ordinal)) then Array.empty
  else
    let relative = outputDirectory.Substring rootPrefix.Length

    match tryCreate relative with
    | Ok path -> [| value path |]
    | Error _ -> Array.empty

let private walkOptions (arguments: Args.IndexArguments) (excluded: string[]) =
  { Walk.WalkOptions.defaults with
      FollowSymbolicLinks = arguments.FollowSymbolicLinks
      RespectIgnoreFiles = arguments.RespectIgnoreFiles
      MaxDepth =
        match arguments.MaxDepth with
        | ValueSome value -> min value MaxDepth
        | ValueNone -> Walk.WalkOptions.defaults.MaxDepth
      MaxFileSizeBytes =
        match arguments.MaxFileSizeBytes with
        | ValueSome value -> value
        | ValueNone -> Walk.WalkOptions.defaults.MaxFileSizeBytes
      Jobs =
        match arguments.Jobs with
        | ValueSome value when value > 0 -> value
        | ValueSome _
        | ValueNone -> Walk.WalkOptions.defaults.Jobs
      ExcludedPaths = excluded }

/// staging へ書き出す。失敗したら書きかけを捨ててから送出し、既存の成果物を残す。
/// `task { }` の中に `try/with` を置くとステート マシンが静的にコンパイルできなくなるため、
/// 同期処理としてここに分離している。
let private writeStaged
  (outputDirectory: string)
  (stagedSegments: string)
  (input: Writer.IndexInput)
  (diagnostics: DiagnosticSink)
  (cancellation: CancellationToken)
  =
  try
    Writer.write stagedSegments input diagnostics cancellation
  with ex ->
    Manifest.discardStaging outputDirectory
    reraise' ex

let private toFileInput (file: Walk.DiscoveredFile) : Writer.FileInput =
  { Path = file.Path
    SizeBytes = file.SizeBytes
    Language = file.Language
    EncodingCode = Encodings.toCode file.Encoding
    Flags = file.Flags
    LineCount = file.LineCount
    ContentHash = file.Hash }

/// インデックス生成が成果物を残さずに終わる理由。
/// 終了コードを分けるため、利用者の入力に起因するものと走査結果に起因するものを区別する。
type private IndexFailure =
  /// 走査が不完全で、既存の成果物を上書きしなかった。
  | PartialRefused of message: string
  /// 出力先が信頼できず、書き込みを行わなかった。
  | OutputRejected of message: string

module private IndexFailure =

  let message failure =
    match failure with
    | PartialRefused text -> text
    | OutputRejected text -> text

/// 走査結果からセグメントを書き、成果物を公開する。
///
/// 同期処理としてここへ分離しているのは、`task { }` の中で分岐と `return` が増えると
/// ステート マシンを静的にコンパイルできず、遅い動的実装へ落ちるためである。
let private publishIndex
  (outputDirectory: string)
  (repository: RepositoryId)
  (options: Walk.WalkOptions)
  (walk: Walk.WalkResult)
  (diagnostics: DiagnosticSink)
  (cancellation: CancellationToken)
  : Result<Manifest.Manifest, IndexFailure> =
  // 出力先がリンクだと、生成、移動、再帰削除のすべてが管理外のファイルへ届く。
  // 書き始める前に信頼できる出力ルートを確定させる。docs/security.md C-3 を参照。
  match Artifact.prepareRoot outputDirectory with
  | Error error -> Error(OutputRejected(Artifact.PathError.describe error))
  | Ok trustedOutput ->

  let input: Writer.IndexInput =
    { Repository = repository
      Directories = walk.Directories
      Files = walk.Files |> Array.map toFileInput }

  // セグメントはいったん staging へ書き、成功したときだけ差し替える。
  // 最終位置へ直接書くと、途中で失敗したときに既存の成果物を壊す。
  match Manifest.prepareStaging trustedOutput with
  | Error error -> Error(OutputRejected(Artifact.PathError.describe error))
  | Ok stagedSegments ->

  let result = writeStaged trustedOutput stagedSegments input diagnostics cancellation

  // 書き出しは長い。中断したのに新しい世代を公開して正常終了しないよう、
  // 切替の直前で必ず確認する。docs/query-and-cli.md 2.2 の終了コード 5 に対応する。
  cancellation.ThrowIfCancellationRequested()

  let generation = Manifest.generationOf result.Segments

  let manifest: Manifest.Manifest =
    { ManifestVersion = Manifest.ManifestVersion
      FormatVersion = Format.FormatVersion
      ToolVersion = Manifest.toolVersion
      RepositoryId = RepositoryId.value repository
      Complete = walk.Complete
      Options =
        { FollowSymbolicLinks = options.FollowSymbolicLinks
          RespectIgnoreFiles = options.RespectIgnoreFiles
          MaxDepth = options.MaxDepth
          MaxFileSizeBytes = options.MaxFileSizeBytes }
      Counts =
        { Nodes = result.NodeCount
          Edges = result.EdgeCount
          Strings = result.StringCount
          StringBytes = result.StringBytes
          Directories = walk.Directories.Length
          Files = walk.Files.Length }
      Segments = Manifest.qualify generation result.Segments
      Diagnostics = diagnosticCounts diagnostics }

  match Manifest.publish trustedOutput generation manifest with
  | Error error ->
    Manifest.discardStaging trustedOutput
    Error(OutputRejected(Artifact.PathError.describe error))
  | Ok() -> Ok manifest

/// インデックスを生成し、書き出したマニフェストを返す。
let private buildIndex
  (rootFullPath: string)
  (outputDirectory: string)
  (repository: RepositoryId)
  (options: Walk.WalkOptions)
  (allowPartial: bool)
  (diagnostics: DiagnosticSink)
  (cancellation: CancellationToken)
  : Task<Result<Manifest.Manifest, IndexFailure>> =
  task {
    let! walk = Walk.run rootFullPath options diagnostics cancellation

    if not walk.Complete && not allowPartial && (Manifest.read outputDirectory |> Result.isOk) then
      return
        Error(
          PartialRefused
            "走査が不完全なため、既存の成果物を上書きしませんでした。上書きするには --allow-partial を指定してください"
        )
    else
      return publishIndex outputDirectory repository options walk diagnostics cancellation
  }

let index (arguments: Args.IndexArguments) (cancellation: CancellationToken) : Task<int> =
  task {
    let rootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath arguments.RootPath)

    if not (Directory.Exists rootFullPath) then
      Terminal.errLine $"解析ルートが見つかりません: {Sanitize.forTerminal arguments.RootPath}"
      return ExitCode.UserError
    else

    match Args.repositoryIdFor arguments.RepositoryId rootFullPath with
    | Error error ->
      Terminal.errLine (RepositoryId.describe error)
      return ExitCode.UserError
    | Ok repository ->

    let outputDirectory = resolveOutputDirectory arguments.OutputDirectory rootFullPath
    let excluded = excludedOutputPath rootFullPath outputDirectory
    let options = walkOptions arguments excluded
    let diagnostics = DiagnosticSink()

    match! buildIndex rootFullPath outputDirectory repository options arguments.AllowPartial diagnostics cancellation with
    | Error failure ->
      reportDiagnostics diagnostics
      Terminal.errLine (IndexFailure.message failure)

      return
        match failure with
        | OutputRejected _ -> ExitCode.UserError
        | PartialRefused _ -> ExitCode.CompletedWithDiagnostics
    | Ok manifest ->
      reportDiagnostics diagnostics

      if arguments.Json then
        writeJson (fun writer ->
          writer.WriteString("command", "index")
          writer.WriteString("repositoryId", manifest.RepositoryId)
          writer.WriteBoolean("complete", manifest.Complete)
          writer.WriteNumber("nodes", manifest.Counts.Nodes)
          writer.WriteNumber("edges", manifest.Counts.Edges)
          writer.WriteNumber("directories", manifest.Counts.Directories)
          writer.WriteNumber("files", manifest.Counts.Files)
          writer.WriteNumber("strings", manifest.Counts.Strings)
          writer.WriteNumber("diagnostics", diagnostics.Total))
      else
        Terminal.resultLine $"リポジトリ: {manifest.RepositoryId}"
        Terminal.outLine $"ディレクトリ: {manifest.Counts.Directories}"
        Terminal.outLine $"ファイル: {manifest.Counts.Files}"
        Terminal.outLine $"ノード: {manifest.Counts.Nodes}"
        Terminal.outLine $"エッジ (CONTAINS): {manifest.Counts.Edges}"
        Terminal.outLine $"文字列: {manifest.Counts.Strings} ({manifest.Counts.StringBytes} バイト)"
        let completeText = if manifest.Complete then "はい" else "いいえ"
        Terminal.outLine $"完全な走査: {completeText}"

      return
        if diagnostics.HasAny then ExitCode.CompletedWithDiagnostics
        else ExitCode.Success
  }

let private locateArtifact (explicitOutput: string voption) (rootPath: string voption) =
  match explicitOutput with
  | ValueSome directory -> Path.GetFullPath directory
  | ValueNone ->
    let root =
      match rootPath with
      | ValueSome path -> Path.GetFullPath path
      | ValueNone -> Directory.GetCurrentDirectory()

    Path.Combine(Path.TrimEndingDirectorySeparator root, Args.DefaultOutputDirectoryName)

let stats (arguments: Args.StatsArguments) : int =
  let outputDirectory = locateArtifact arguments.OutputDirectory arguments.RootPath

  // マニフェストとセグメントを別々に開くと、その間に公開が完了したとき、
  // 旧世代の件数と新世代の集計を混ぜた結果を正常終了で返してしまう。
  let observed =
    Manifest.readStable outputDirectory (fun manifest ->
      struct (manifest, Stats.readFileStatistics outputDirectory manifest))

  match observed with
  | Error error ->
    Terminal.errLine (Manifest.ManifestError.describe error)
    ExitCode.MissingArtifact
  | Ok(struct (_, Error error)) ->
    Terminal.errLine (Reader.OpenError.describe error)
    ExitCode.MissingArtifact
  | Ok(struct (manifest, Ok statistics)) ->
      if arguments.Json then
        writeJson (fun writer ->
          writer.WriteString("command", "stats")
          writer.WriteString("repositoryId", manifest.RepositoryId)
          writer.WriteNumber("nodes", manifest.Counts.Nodes)
          writer.WriteNumber("edges", manifest.Counts.Edges)
          writer.WriteNumber("directories", manifest.Counts.Directories)
          writer.WriteNumber("files", manifest.Counts.Files)
          writer.WriteNumber("totalBytes", statistics.TotalBytes)
          writer.WriteNumber("totalLines", statistics.TotalLines)
          writer.WriteStartArray "languages"

          for entry in statistics.Languages do
            writer.WriteStartObject()
            writer.WriteString("language", Language.name entry.Language)
            writer.WriteNumber("files", entry.Files)
            writer.WriteNumber("bytes", entry.Bytes)
            writer.WriteNumber("lines", entry.Lines)
            writer.WriteEndObject()

          writer.WriteEndArray()
          writer.WriteStartArray "encodings"

          for entry in statistics.Encodings do
            writer.WriteStartObject()
            writer.WriteString("encoding", Encodings.name entry.Encoding)
            writer.WriteNumber("files", entry.Files)
            // 検出器が単一へ確定できなかった件数。利用側が曖昧さを検知できるようにする。
            writer.WriteNumber("ambiguous", entry.Ambiguous)
            writer.WriteEndObject()

          writer.WriteEndArray())
      else
        Terminal.resultLine $"リポジトリ: {manifest.RepositoryId}"
        Terminal.outLine $"ノード: {manifest.Counts.Nodes} / エッジ: {manifest.Counts.Edges}"
        Terminal.outLine $"ディレクトリ: {manifest.Counts.Directories} / ファイル: {manifest.Counts.Files}"
        Terminal.outLine $"総バイト数: {statistics.TotalBytes} / 総行数: {statistics.TotalLines}"
        Terminal.outLine ""
        Terminal.outLine "言語:"

        for entry in statistics.Languages do
          Terminal.outLine $"  {Language.name entry.Language}: {entry.Files} ファイル / {entry.Lines} 行"

        Terminal.outLine ""
        Terminal.outLine "符号化:"

        for entry in statistics.Encodings do
          if entry.Ambiguous = 0 then
            Terminal.outLine $"  {Encodings.name entry.Encoding}: {entry.Files} ファイル"
          else
            Terminal.outLine
              $"  {Encodings.name entry.Encoding}: {entry.Files} ファイル (うち {entry.Ambiguous} 件は候補が複数で未確定)"

      if manifest.Counts.Files = 0 then ExitCode.NoResults else ExitCode.Success

/// 成果物ディレクトリ内のファイルを、相対パス昇順で列挙する。
///
/// 生成中の一時領域と、退役待ちの旧世代は現行の内容ではないため除く。旧世代は
/// 読み手への猶予として意図的に残しているので、差として報告してはならない。
/// それ以外の残骸は数え、追加・欠落として検出できるようにする。
let private artifactFiles (directory: string) (generation: string voption) =
  let isRetiredGeneration (relative: string) =
    let parts = relative.Split '/'

    parts.Length > 2
    && parts[0] = Artifact.SegmentDirectory
    && Artifact.isGeneration parts[1]
    && ValueSome parts[1] <> generation

  if not (Directory.Exists directory) then Array.empty
  else
    Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
    |> Seq.map (fun path -> Path.GetRelativePath(directory, path).Replace('\\', '/'))
    |> Seq.filter (fun relative ->
      not (relative.StartsWith(Manifest.StagingDirectory, StringComparison.Ordinal))
      && not (isRetiredGeneration relative))
    |> Seq.sortWith (fun left right -> String.CompareOrdinal(left, right))
    |> Seq.toArray

/// 二つの成果物ディレクトリをファイル単位で突き合わせる。
///
/// セグメントの記述子だけを比べると、`manifest.json` にしか現れない非決定性
/// （診断、オプション、件数、版、プロパティ順、シリアライズ形式）を見逃す。
/// 相対パスの一覧と全ファイルのバイト列を比べることで、追加・欠落・順序差も検出する。
let private compareArtifacts
  (original: string)
  (originalGeneration: string voption)
  (rebuilt: string)
  (rebuiltGeneration: string voption)
  =
  let mismatches = ResizeArray<string>()
  let left = artifactFiles original originalGeneration
  let right = artifactFiles rebuilt rebuiltGeneration
  let leftSet = Set.ofArray left
  let rightSet = Set.ofArray right

  for name in Set.difference leftSet rightSet do
    mismatches.Add $"再生成した成果物に {name} がありません"

  for name in Set.difference rightSet leftSet do
    mismatches.Add $"再生成した成果物にだけ {name} があります"

  for name in Set.intersect leftSet rightSet do
    let leftBytes = File.ReadAllBytes(Path.Combine(original, name.Replace('/', Path.DirectorySeparatorChar)))
    let rightBytes = File.ReadAllBytes(Path.Combine(rebuilt, name.Replace('/', Path.DirectorySeparatorChar)))

    if not (leftBytes.AsSpan().SequenceEqual(ReadOnlySpan rightBytes)) then
      mismatches.Add $"{name} がバイト単位で一致しません"

  // 検出順が集合演算の走査順に依存しないよう、報告は序数順に固定する。
  let ordered = mismatches.ToArray()
  Array.sortInPlaceWith (fun (a: string) (b: string) -> String.CompareOrdinal(a, b)) ordered
  ordered

/// 同一入力から二度生成し、成果物がバイト単位で一致することを確認する。
/// docs/architecture.md 2 の `srcnet verify --deterministic` に対応する。
let private checkDeterminism
  (rootPath: string)
  (originalOutputDirectory: string)
  (manifest: Manifest.Manifest)
  (cancellation: CancellationToken)
  =
  task {
    let rootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath rootPath)

    match RepositoryId.tryCreate manifest.RepositoryId with
    | Error error -> return Error(RepositoryId.describe error)
    | Ok repository ->

    let temporary =
      Path.Combine(Path.GetTempPath(), "srcnet-verify-" + Guid.NewGuid().ToString "N")

    try
      let options =
        { Walk.WalkOptions.defaults with
            FollowSymbolicLinks = manifest.Options.FollowSymbolicLinks
            RespectIgnoreFiles = manifest.Options.RespectIgnoreFiles
            MaxDepth = manifest.Options.MaxDepth
            MaxFileSizeBytes = manifest.Options.MaxFileSizeBytes
            // 並列度は所要時間だけを変え、出力を変えないことをここで検証する。
            Jobs = 1
            // 既存の成果物が解析ルート配下にある場合、初回と同じ条件になるよう除外する。
            ExcludedPaths = excludedOutputPath rootFullPath originalOutputDirectory }

      let diagnostics = DiagnosticSink()

      match! buildIndex rootFullPath temporary repository options true diagnostics cancellation with
      | Error failure -> return Error(IndexFailure.message failure)
      | Ok rebuilt ->
        return
          Ok(
            compareArtifacts
              originalOutputDirectory
              (Manifest.generationIn manifest)
              temporary
              (Manifest.generationIn rebuilt)
          )
    finally
      if Directory.Exists temporary then
        try
          Directory.Delete(temporary, true)
        with :? IOException ->
          ()
  }

let verify (arguments: Args.VerifyArguments) (cancellation: CancellationToken) : Task<int> =
  task {
    let outputDirectory = locateArtifact arguments.OutputDirectory arguments.RootPath

    match Verify.run outputDirectory cancellation with
    | Error error ->
      Terminal.errLine (Manifest.ManifestError.describe error)
      return ExitCode.MissingArtifact
    | Ok report ->

    let! determinism =
      task {
        if not arguments.Deterministic then return ValueNone
        else
          match arguments.RootPath with
          | ValueNone ->
            Terminal.errLine "--deterministic には解析ルートの指定が必要です"
            return ValueSome(Error "解析ルートが指定されていません")
          | ValueSome rootPath ->
            match Manifest.read outputDirectory with
            | Error error -> return ValueSome(Error(Manifest.ManifestError.describe error))
            | Ok manifest ->
              let! result = checkDeterminism rootPath outputDirectory manifest cancellation

              return
                ValueSome(
                  match result with
                  | Error message -> Error message
                  | Ok mismatches -> Ok mismatches
                )
      }

    let determinismIssues =
      match determinism with
      | ValueNone -> Array.empty
      | ValueSome(Ok mismatches) -> mismatches
      | ValueSome(Error message) -> [| message |]

    if arguments.Json then
      writeJson (fun writer ->
        writer.WriteString("command", "verify")
        writer.WriteBoolean("valid", report.IsValid && determinismIssues.Length = 0)
        writer.WriteNumber("segmentsChecked", report.SegmentsChecked)
        writer.WriteNumber("bytesChecked", report.BytesChecked)
        writer.WriteStartArray "issues"

        for issue in report.Issues do
          writer.WriteStringValue(Verify.Issue.describe issue)

        for issue in determinismIssues do
          writer.WriteStringValue issue

        writer.WriteEndArray())
    else
      Terminal.outLine $"検証したセグメント: {report.SegmentsChecked} ({report.BytesChecked} バイト)"

      for issue in report.Issues do
        Terminal.resultLine $"問題: {Verify.Issue.describe issue}"

      for issue in determinismIssues do
        Terminal.resultLine $"問題: {issue}"

      if arguments.Deterministic && determinismIssues.Length = 0 then
        Terminal.outLine "決定性: 二度の生成で成果物がバイト単位で一致しました"

      if report.IsValid && determinismIssues.Length = 0 then Terminal.outLine "整合性: 問題ありません"

    return
      if report.IsValid && determinismIssues.Length = 0 then ExitCode.Success
      else ExitCode.CompletedWithDiagnostics
  }
