/// サブコマンドの実装。
///
/// `stdout` には結果のみ、進捗と診断は `stderr` へ出す。終了コードは
/// docs/query-and-cli.md 2.2 に従う。
module Srcnet.Cli.Commands

open System
open System.Buffers
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Srcnet.Core.Diagnostics
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Core.Paths
open Srcnet.Discovery
open Srcnet.Extraction
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

/// JSON 出力の符号化。
///
/// 既定のエンコーダーは非 ASCII をすべて `\uXXXX` へ逃がす。CJK では 1 文字が 6 バイトになり、
/// トークン削減という目的に反する。`UnicodeRanges.All` を許可すると CJK はそのまま出るが、
/// `<`、`>`、`&`、`'`、`"`、`+` は依然として逃がされるため、HTML へ埋め込んでも安全である。
let internal jsonEncoder =
  Encodings.Web.JavaScriptEncoder.Create Unicode.UnicodeRanges.All

let private jsonWriterOptions indented =
  JsonWriterOptions(
    Indented = indented,
    IndentCharacter = ' ',
    IndentSize = 2,
    NewLine = Terminal.Newline,
    Encoder = jsonEncoder
  )

let internal renderJson (indented: bool) (build: Utf8JsonWriter -> unit) =
  use buffer = new MemoryStream()
  use writer = new Utf8JsonWriter(buffer, jsonWriterOptions indented)
  build writer
  writer.Flush()
  Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, int buffer.Length)

let writeJson (build: Utf8JsonWriter -> unit) =
  let text =
    renderJson true (fun writer ->
      writer.WriteStartObject()
      writer.WriteNumber("schemaVersion", SchemaVersion)
      build writer
      writer.WriteEndObject())

  Terminal.outLine text

let private boundedDiagnostic (text: string) =
  if text.Length <= 2048 then
    text
  else
    let length = if Char.IsSurrogatePair(text, 2047) then 2047 else 2048
    text.Substring(0, length) + "..."

let private writeManagementStatus
  (writer: Utf8JsonWriter)
  (command: string)
  (code: int)
  (hasResult: bool)
  (total: int)
  (samples: struct (string * string * string)[])
  =
  writer.WriteString("command", command)
  writer.WriteNumber("exitCode", code)
  writer.WriteBoolean("hasResult", hasResult)
  writer.WriteNumber("diagnostics", total)
  writer.WriteStartArray "diagnosticDetails"
  let kept = min 32 samples.Length
  let mutable truncated = kept < total

  for index in 0 .. kept - 1 do
    let struct (kind, path, detail) = samples[index]
    truncated <- truncated || path.Length > 2048 || detail.Length > 2048
    writer.WriteStartObject()
    writer.WriteString("kind", kind)
    writer.WriteString("path", boundedDiagnostic path)
    writer.WriteString("detail", boundedDiagnostic detail)
    writer.WriteEndObject()

  writer.WriteEndArray()
  writer.WriteBoolean("diagnosticsTruncated", truncated)
  writer.WriteNumber("omittedDiagnosticCount", max 0 (total - kept))

let writeManagementError (command: string) (code: int) (message: string) (json: bool) =
  if json then
    writeJson(fun writer ->
      writeManagementStatus writer command code false 1 [| struct ("command-error", "", message) |])
  else
    Terminal.diagnosticLine message

  code

let private flagTable =
  [| NodeFlags.Definition, "definition"
     NodeFlags.DeclarationOnly, "declaration"
     NodeFlags.Test, "test"
     NodeFlags.Generated, "generated"
     NodeFlags.Vendored, "vendored"
     NodeFlags.Conditional, "conditional"
     NodeFlags.Binary, "binary"
     NodeFlags.UndeterminedEncoding, "undetermined-encoding"
     NodeFlags.Skipped, "skipped"
     NodeFlags.SymbolicLink, "symlink"
     NodeFlags.AmbiguousEncoding, "ambiguous-encoding"
     NodeFlags.InternalLinkage, "internal-linkage"
     NodeFlags.ExtractionTruncated, "extraction-truncated" |]

let internal flagNames (flags: NodeFlags) =
  flagTable |> Array.filter(fun (flag, _) -> flags.HasFlag flag) |> Array.map snd

let private reportDiagnostics (diagnostics: DiagnosticSink) =
  let counts = diagnostics.Counts()

  if counts.Length > 0 then
    Terminal.errLine "診断:"

    for struct (kind, count) in counts do
      Terminal.errLine $"  {DiagnosticKind.name kind}: {count} 件"

    for sample in diagnostics.Samples() do
      let location = if sample.Path.Length = 0 then "" else sample.Path + ": "
      Terminal.diagnosticLine(Terminal.fitToWidth $"  [{DiagnosticKind.name sample.Kind}] {location}{sample.Detail}")

    let hidden = diagnostics.Total - diagnostics.Samples().Length

    if hidden > 0 then
      Terminal.errLine $"  ほか {hidden} 件は省略しました (種別ごとに最大 {diagnostics.MaxSamplesPerKind} 件を表示)"

let private diagnosticCounts (diagnostics: DiagnosticSink) =
  diagnostics.Counts()
  |> Array.map(fun (struct (kind, count)) ->
    { Manifest.Kind = DiagnosticKind.name kind
      Manifest.Count = count })

let internal locateArtifact (explicitOutput: string voption) (rootPath: string voption) =
  match explicitOutput with
  | ValueSome directory -> Path.GetFullPath directory
  | ValueNone ->
    let root =
      match rootPath with
      | ValueSome path -> Path.GetFullPath path
      | ValueNone -> Directory.GetCurrentDirectory()

    Path.Combine(Path.TrimEndingDirectorySeparator root, Args.DefaultOutputDirectoryName)

/// 出力先が解析ルート配下にある場合、その論理パスを走査から除外する。
/// 生成物を自分で索引してしまうのを防ぐ。
let private excludedOutputPath (rootFullPath: string) (outputDirectory: string) =
  let relative = Path.GetRelativePath(rootFullPath, outputDirectory)

  match tryCreate relative with
  | Ok path -> [| value path |]
  | Error _ -> Array.empty

/// 成果物へ記録する文法の版。コンパイル時に埋め込んだ固定値だけを使い、
/// 実行時に `native/` を読む経路は作らない（docs/security.md C-1、backlog 020）。
let private grammarRecords: Manifest.GrammarRecord[] =
  GrammarVersions.all
  |> Array.map(fun entry ->
    { Manifest.Language = entry.Language
      Manifest.Version = entry.Version
      Manifest.Sha256 = entry.Sha256 })

/// 段階の番号から抽出段階を決める。`Args` が範囲を検証済みなので、既定へ落ちることはない。
let private tierOf (value: int) =
  match Model.Tier.ofCode(byte(max 0 (min 3 value))) with
  | ValueSome tier -> tier
  | ValueNone -> Model.Syntax

let private walkOptions (arguments: Args.IndexArguments) (excluded: string[]) =
  { Walk.WalkOptions.defaults with
      AssumeEncoding =
        match arguments.AssumeEncoding with
        | ValueSome text -> Encodings.tryParse text
        | ValueNone -> ValueNone
      Extraction =
        { Extractor.ExtractionOptions.defaults with
            Tier = tierOf arguments.Tier }
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

let private toSymbolInput (symbol: Model.ExtractedSymbol) : Writer.SymbolInput =
  { Kind = symbol.Kind
    Name = symbol.Name
    QualifiedName = symbol.QualifiedName
    Parent = symbol.Parent
    Ordinal = symbol.Ordinal
    StartLine = symbol.StartLine
    EndLine = symbol.EndLine
    StartByte = symbol.StartByte
    EndByte = symbol.EndByte
    Flags = symbol.Flags }

let private toReferenceInput (reference: Model.ExtractedReference) : Writer.ReferenceInput =
  { Source = reference.Source
    Kind = reference.Kind
    Target = reference.Target
    Qualifier = reference.Qualifier
    Language = reference.Language
    StartByte = reference.StartByte
    EndByte = reference.EndByte
    Line = reference.Line
    Stage = reference.Stage
    Confidence = reference.Confidence }

let private toFileInput (file: Walk.DiscoveredFile) : Writer.FileInput =
  { Path = file.Path
    SizeBytes = file.SizeBytes
    Language = file.Language
    EncodingCode = Encodings.toCode file.Encoding
    Flags = file.Flags
    LineCount = file.LineCount
    ContentHash = file.Hash
    Symbols = file.Extraction.Symbols |> Array.map toSymbolInput
    References = file.Extraction.References |> Array.map toReferenceInput }

[<Sealed>]
type private ExtractionCoverage(options: Walk.WalkOptions) =
  let counts =
    Collections.Generic.Dictionary<
      struct (Language * int * string),
      struct (int * Collections.Generic.SortedSet<string>)
     >()

  let gate = obj()

  member _.Add(file: Walk.DiscoveredFile) =
    let extraction = file.Extraction

    let reason =
      if file.Flags &&& NodeFlags.Skipped <> NodeFlags.None then
        if file.SizeBytes > options.MaxFileSizeBytes then
          "file-size-limit"
        else
          "read-error"
      else
        match extraction.Skipped with
        | ValueSome(Model.TooLargeToExtract _) -> "extraction-size-limit"
        | ValueSome Model.NotText -> "binary"
        | ValueSome Model.NoGrammar ->
          if Parsing.isAvailable.Value then
            "grammar-unavailable"
          else
            "parser-unavailable"
        | ValueSome Model.ParseTimedOut -> "parse-timeout"
        | ValueSome(Model.ParseUnavailable _) -> "parse-failed"
        | ValueSome(Model.AmbiguousEncoding _) -> "ambiguous-encoding"
        | ValueSome(Model.UnsupportedEncoding _) -> "unsupported-encoding"
        | ValueNone when extraction.Truncated -> "extraction-limit"
        | ValueNone when
          options.Extraction.Tier = Model.Syntax
          && not(Extractor.syntaxImplemented file.Language)
          ->
          "syntax-unsupported"
        | ValueNone -> "requested-tier"

    let key = struct (file.Language, Model.Tier.rank extraction.Tier, reason)

    lock gate (fun () ->
      let struct (count, examples) =
        match counts.TryGetValue key with
        | true, found -> found
        | false, _ -> struct (0, Collections.Generic.SortedSet<string>(StringComparer.Ordinal))

      let path = value file.Path

      let length =
        if path.Length > 512 && Char.IsSurrogatePair(path, 508) then
          508
        else
          509

      examples.Add(
        if path.Length <= 512 then
          path
        else
          path.Substring(0, length) + "..."
      )
      |> ignore

      if examples.Count > 2 then
        match examples.Max with
        | null -> invalidOp "Nonempty example set has no maximum"
        | last -> examples.Remove last |> ignore

      counts[key] <- struct (count + 1, examples))

  member _.Snapshot() =
    counts
    |> Seq.map(fun pair ->
      let struct (language, tier, reason) = pair.Key
      let struct (count, examples) = pair.Value

      let row: Manifest.ExtractionCount =
        { Language = Language.name language
          Tier = tier
          Reason = reason
          Count = count
          SyntaxSupported = Extractor.syntaxImplemented language
          GrammarAvailable = Parsing.supports language
          Examples = examples |> Seq.toArray }

      row)
    |> Seq.sortWith(fun left right ->
      let language = String.CompareOrdinal(left.Language, right.Language)

      if language <> 0 then
        language
      else
        let tier = compare left.Tier right.Tier

        if tier <> 0 then
          tier
        else
          String.CompareOrdinal(left.Reason, right.Reason))
    |> Seq.toArray

let private writeCoverage (writer: Utf8JsonWriter) (manifest: Manifest.Manifest) =
  writer.WriteBoolean("coverageAvailable", manifest.Extraction |> ValueOption.isSome)
  writer.WriteStartArray "extractionCoverage"

  match manifest.Extraction with
  | ValueNone -> ()
  | ValueSome entries ->
    for language, rows in entries |> Array.groupBy(fun entry -> entry.Language) do
      writer.WriteStartObject()
      writer.WriteString("language", language)
      writer.WriteNumber("files", rows |> Array.sumBy(fun entry -> entry.Count))
      writer.WriteNumber("tier2", rows |> Array.sumBy(fun entry -> if entry.Tier = 2 then entry.Count else 0))
      writer.WriteNumber("tier1", rows |> Array.sumBy(fun entry -> if entry.Tier = 1 then entry.Count else 0))
      writer.WriteNumber("notExtracted", rows |> Array.sumBy(fun entry -> if entry.Tier = 0 then entry.Count else 0))
      writer.WriteBoolean("syntaxSupported", rows |> Array.exists(fun entry -> entry.SyntaxSupported))
      writer.WriteBoolean("grammarAvailable", rows |> Array.exists(fun entry -> entry.GrammarAvailable))
      writer.WriteStartArray "reasons"

      for entry in rows do
        writer.WriteStartObject()
        writer.WriteString("reason", entry.Reason)
        writer.WriteNumber("count", entry.Count)
        writer.WriteNumber("tier", entry.Tier)

        writer.WriteBoolean(
          "examplesTruncated",
          entry.Count > entry.Examples.Length
          || entry.Examples |> Array.exists(fun path -> path.Length >= 511)
        )

        writer.WriteStartArray "examples"

        for path in entry.Examples do
          writer.WriteStringValue path

        writer.WriteEndArray()
        writer.WriteEndObject()

      writer.WriteEndArray()
      writer.WriteEndObject()

  writer.WriteEndArray()

let private reportCoverage (manifest: Manifest.Manifest) =
  match manifest.Extraction with
  | ValueNone -> Terminal.outLine "抽出状況: この索引には集計がありません。再索引すると記録されます。"
  | ValueSome entries ->
    for language, rows in entries |> Array.groupBy(fun entry -> entry.Language) do
      let tierCount tier =
        rows |> Array.sumBy(fun entry -> if entry.Tier = tier then entry.Count else 0)

      Terminal.resultLine $"{language}: T2 {tierCount 2} / T1 {tierCount 1} / 抽出なし {tierCount 0}"

      for entry in rows do
        Terminal.resultLine $"  {entry.Reason}: {entry.Count}"

/// インデックス生成が成果物を残さずに終わる理由。
/// 終了コードを分けるため、利用者の入力に起因するものと走査結果に起因するものを区別する。
type private IndexFailure =
  /// 走査が不完全で、既存の成果物を上書きしなかった。
  | PartialRefused of message: string
  /// 出力先が信頼できず、書き込みを行わなかった。
  | OutputRejected of message: string
  /// ノード ID が衝突した。推測で片方を捨てず、公開せずに失敗させる。
  | IdCollision of message: string

type private IndexProgress(mode: string) =
  let enabled = mode = "always" || (mode = "auto" && not Console.IsErrorRedirected)
  let interval = if Console.IsErrorRedirected then 5.0 else 1.0
  let clock = System.Diagnostics.Stopwatch.StartNew()
  let gate = obj()
  let mutable lastUpdate = -interval

  let mutable last: Walk.Progress =
    { Phase = ""
      Files = 0
      Directories = 0
      Bytes = 0L }

  member _.Report(snapshot: Walk.Progress) =
    if enabled then
      lock gate (fun () ->
        let elapsed = clock.Elapsed.TotalSeconds

        if snapshot.Phase <> last.Phase || elapsed - lastUpdate >= interval then
          let seconds = elapsed.ToString("F1", Globalization.CultureInfo.InvariantCulture)

          Terminal.progressLine
            $"progress {snapshot.Phase}: files={snapshot.Files} directories={snapshot.Directories} bytes={snapshot.Bytes} elapsed={seconds}s"

          lastUpdate <- elapsed

        last <- snapshot)

  member this.Phase phase =
    lock gate (fun () -> this.Report { last with Phase = phase })

  member _.Resources(memory: int64, disk: int64, allocated: int64) =
    if enabled then
      lock gate (fun () ->
        Terminal.progressLine $"resources peakRss={memory} temporaryBytes={disk} allocatedBytes={allocated}")

[<Sealed>]
type private IndexResources(limit: int64, cancellation: CancellationToken) =
  let currentProcess = System.Diagnostics.Process.GetCurrentProcess()
  let source = CancellationTokenSource.CreateLinkedTokenSource cancellation
  let gate = obj()
  let mutable exceeded = false
  let mutable disposed = false
  let mutable peak = 0L

  let inspect () =
    lock gate (fun () ->
      if not disposed then
        try
          currentProcess.Refresh()
          let current = currentProcess.WorkingSet64
          peak <- max peak current

          if current >= limit - limit / 8L then
            exceeded <- true
            source.Cancel()
        with :? InvalidOperationException ->
          exceeded <- true
          source.Cancel())

  let timer = new Timer(TimerCallback(fun _ -> inspect()), gate, 0, 100)
  member _.Token = source.Token
  member _.Exceeded = lock gate (fun () -> exceeded)
  member _.Peak = lock gate (fun () -> max peak currentProcess.PeakWorkingSet64)

  member _.Check() =
    inspect()
    source.Token.ThrowIfCancellationRequested()

  interface IDisposable with
    member _.Dispose() =
      lock gate (fun () ->
        disposed <- true
        timer.Dispose()
        source.Dispose()
        currentProcess.Dispose())

module private IndexFailure =

  let message failure =
    match failure with
    | PartialRefused text -> text
    | OutputRejected text -> text
    | IdCollision text -> text

/// 走査結果からセグメントを書き、成果物を公開する。
///
/// 同期処理としてここへ分離しているのは、`task { }` の中で分岐と `return` が増えると
/// ステート マシンを静的にコンパイルできず、遅い動的実装へ落ちるためである。
let private publishIndex
  (trustedOutput: string)
  (repository: RepositoryId)
  (options: Walk.WalkOptions)
  (requestedTier: int voption)
  (walk: Walk.WalkResult)
  (input: Writer.IndexSource)
  (coverage: Manifest.ExtractionCount[])
  (stagedSegments: string)
  (releaseInput: unit -> unit)
  (budget: ExternalSort.TemporaryBudget)
  (allowPartial: bool)
  (diagnostics: DiagnosticSink)
  (progress: IndexProgress)
  (cancellation: CancellationToken)
  : Result<Manifest.Manifest, IndexFailure> =
  if
    not walk.Complete
    && not allowPartial
    && (Manifest.read trustedOutput |> Result.isOk)
  then
    Error(PartialRefused "走査が不完全なため、既存の成果物を上書きしませんでした。上書きするには --allow-partial を指定してください")
  else

    progress.Phase "write"

    let result =
      Writer.writeSourceWithBudget stagedSegments input diagnostics budget cancellation

    let fileCount = input.Files.Count
    let directoryCount = input.Directories.Count
    releaseInput()

    // 書き出しは長い。中断したのに新しい世代を公開して正常終了しないよう、
    // 切替の直前で必ず確認する。docs/query-and-cli.md 2.2 の終了コード 5 に対応する。
    cancellation.ThrowIfCancellationRequested()

    // ID の衝突は推測で片方を捨てず、公開せずに失敗させる（docs/graph-model.md 4.2）。
    if result.IdCollisions > 0 then
      Manifest.discardStaging trustedOutput

      Error(IdCollision $"ノード ID が {result.IdCollisions} 件衝突したため、成果物を公開しませんでした")
    else

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
              MaxFileSizeBytes = options.MaxFileSizeBytes
              Tier = int(Model.Tier.toCode walk.AppliedTier)
              RequestedTier = requestedTier
              ParserAvailable = walk.ParserAvailable
              Grammars = grammarRecords
              AssumedEncoding =
                match options.AssumeEncoding with
                | ValueSome encoding -> Encodings.name encoding
                | ValueNone -> "" }
          Counts =
            { Nodes = result.NodeCount
              Edges = result.EdgeCount
              Strings = result.StringCount
              StringBytes = result.StringBytes
              Directories = directoryCount
              Files = fileCount
              Symbols = result.SymbolCount
              ReferenceCandidates = result.ReferenceCount
              NodeKinds = result.NodeKinds
              EdgeKinds = result.EdgeKinds }
          Segments = Manifest.qualify generation result.Segments
          Diagnostics = diagnosticCounts diagnostics
          Extraction = ValueSome coverage }

      progress.Phase "publish"

      match Manifest.publishWithCancellation trustedOutput generation manifest cancellation with
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
  (requestedTier: int voption)
  (allowPartial: bool)
  (diagnostics: DiagnosticSink)
  (progress: IndexProgress)
  (memoryLimit: int64)
  (temporaryLimit: int64)
  (cancellation: CancellationToken)
  : Task<Result<struct (Manifest.Manifest * Walk.TierCounts), IndexFailure>> =
  task {
    cancellation.ThrowIfCancellationRequested()
    use resources = new IndexResources(memoryLimit, cancellation)
    let budget = ExternalSort.TemporaryBudget temporaryLimit
    let allocatedBefore = GC.GetTotalAllocatedBytes false

    match Artifact.prepareRoot outputDirectory with
    | Error error -> return Error(OutputRejected(Artifact.PathError.describe error))
    | Ok trustedOutput ->
      match Manifest.acquireWriter trustedOutput with
      | Error error -> return Error(OutputRejected(Artifact.PathError.describe error))
      | Ok lease ->
        use _lease = lease

        try
          try
            match Manifest.prepareStaging trustedOutput with
            | Error error -> return Error(OutputRejected(Artifact.PathError.describe error))
            | Ok stagedSegments ->
              use spill =
                new Spill.Input(
                  Path.Combine(trustedOutput, Manifest.StagingDirectory),
                  min 67108864L (memoryLimit / 16L),
                  budget,
                  resources.Token
                )

              let coverage = ExtractionCoverage options

              let emit file =
                spill.AddFile(toFileInput file)
                coverage.Add file

              progress.Phase "discovery/extraction"

              let! walk =
                Walk.runTo rootFullPath options diagnostics progress.Report spill.AddDirectory emit resources.Token

              let input = spill.Finish repository
              resources.Check()

              return
                publishIndex
                  trustedOutput
                  repository
                  options
                  requestedTier
                  walk
                  input
                  (coverage.Snapshot())
                  stagedSegments
                  (fun () -> (spill :> IDisposable).Dispose())
                  budget
                  allowPartial
                  diagnostics
                  progress
                  resources.Token
                |> Result.map(fun manifest -> struct (manifest, walk.Tiers))
          with
          | :? OperationCanceledException when resources.Exceeded && not cancellation.IsCancellationRequested ->
            return
              Error(OutputRejected $"Memory budget approached ({memoryLimit} bytes); the previous index was retained")
          | Walk.SinkFailure error -> return Error(OutputRejected error.Message)
          | ExternalSort.ResourceLimitExceeded message -> return Error(OutputRejected message)
          | :? IOException as error -> return Error(OutputRejected error.Message)
          | :? UnauthorizedAccessException as error -> return Error(OutputRejected error.Message)
          | :? OutOfMemoryException -> return Error(OutputRejected $"Memory budget exhausted ({memoryLimit} bytes)")
        finally
          if resources.Token.IsCancellationRequested then
            progress.Phase "cleanup"

          Manifest.discardStaging trustedOutput
          progress.Resources(resources.Peak, budget.Peak, GC.GetTotalAllocatedBytes(false) - allocatedBefore)
  }

/// 生成結果を報告し、終了コードを決める。
///
/// `task { }` の中で分岐と `return` が増えるとステート マシンを静的にコンパイルできず、
/// 遅い動的実装へ落ちる。同期処理としてここへ分離する。
let private reportIndex
  (arguments: Args.IndexArguments)
  (diagnostics: DiagnosticSink)
  (outcome: Result<struct (Manifest.Manifest * Walk.TierCounts), IndexFailure>)
  : int =
  if not arguments.Json then
    reportDiagnostics diagnostics

  let samples =
    diagnostics.Samples()
    |> Array.map(fun sample -> struct (DiagnosticKind.name sample.Kind, sample.Path, sample.Detail))

  match outcome with
  | Error failure ->
    let code =
      match failure with
      | OutputRejected _ -> ExitCode.UserError
      | IdCollision _ -> ExitCode.InternalError
      | PartialRefused _ -> ExitCode.CompletedWithDiagnostics

    if arguments.Json then
      writeJson(fun writer ->
        let details =
          Array.append [| struct ("command-error", "", IndexFailure.message failure) |] samples

        writeManagementStatus writer "index" code false (diagnostics.Total + 1) details)
    else
      Terminal.diagnosticLine(IndexFailure.message failure)

    code
  | Ok(struct (manifest, tiers)) ->
    let appliedTier = manifest.Options.Tier

    let code =
      if diagnostics.HasAny then
        ExitCode.CompletedWithDiagnostics
      else
        ExitCode.Success

    if arguments.Json then
      writeJson(fun writer ->
        writeManagementStatus writer "index" code true diagnostics.Total samples
        writeCoverage writer manifest
        writer.WriteString("repositoryId", manifest.RepositoryId)
        writer.WriteBoolean("complete", manifest.Complete)
        writer.WriteNumber("tier", appliedTier)
        writer.WriteNumber("requestedTier", arguments.Tier)
        writer.WriteBoolean("parserAvailable", manifest.Options.ParserAvailable)
        writer.WriteNumber("nodes", manifest.Counts.Nodes)
        writer.WriteNumber("edges", manifest.Counts.Edges)
        writer.WriteNumber("directories", manifest.Counts.Directories)
        writer.WriteNumber("files", manifest.Counts.Files)
        writer.WriteNumber("symbols", manifest.Counts.Symbols)
        writer.WriteNumber("referenceCandidates", manifest.Counts.ReferenceCandidates)
        writer.WriteNumber("strings", manifest.Counts.Strings)
        writer.WriteStartObject "extractedFiles"
        writer.WriteNumber("tier2", tiers.Syntax)
        writer.WriteNumber("tier1", tiers.LineOriented)
        writer.WriteNumber("notExtracted", tiers.NotExtracted)
        writer.WriteEndObject()
        writer.WriteStartArray "nodeKinds"

        for entry in manifest.Counts.NodeKinds do
          writer.WriteStartObject()
          writer.WriteString("kind", entry.Kind)
          writer.WriteNumber("count", entry.Count)
          writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteStartArray "edgeKinds"

        for entry in manifest.Counts.EdgeKinds do
          writer.WriteStartObject()
          writer.WriteString("kind", entry.Kind)
          writer.WriteNumber("count", entry.Count)
          writer.WriteEndObject()

        writer.WriteEndArray())
    else
      Terminal.resultLine $"リポジトリ: {manifest.RepositoryId}"
      Terminal.outLine $"抽出段階: T{appliedTier}"
      Terminal.outLine $"ディレクトリ: {manifest.Counts.Directories}"
      Terminal.outLine $"ファイル: {manifest.Counts.Files}"

      Terminal.outLine $"  T2 まで: {tiers.Syntax} / T1 まで: {tiers.LineOriented} / 抽出なし: {tiers.NotExtracted}"

      Terminal.outLine $"ノード: {manifest.Counts.Nodes} (うちシンボル {manifest.Counts.Symbols})"

      for entry in manifest.Counts.NodeKinds do
        Terminal.outLine $"  {entry.Kind}: {entry.Count}"

      Terminal.outLine $"エッジ: {manifest.Counts.Edges}"

      for entry in manifest.Counts.EdgeKinds do
        Terminal.outLine $"  {entry.Kind}: {entry.Count}"

      Terminal.outLine $"参照候補: {manifest.Counts.ReferenceCandidates}"
      Terminal.outLine $"文字列: {manifest.Counts.Strings} ({manifest.Counts.StringBytes} バイト)"
      let completeText = if manifest.Complete then "はい" else "いいえ"
      Terminal.outLine $"完全な走査: {completeText}"
      reportCoverage manifest

    code

let index (arguments: Args.IndexArguments) (cancellation: CancellationToken) : Task<int> =
  task {
    let rootFullPath =
      Path.TrimEndingDirectorySeparator(Path.GetFullPath arguments.RootPath)

    if not(Directory.Exists rootFullPath) then
      return writeManagementError "index" ExitCode.UserError $"解析ルートが見つかりません: {arguments.RootPath}" arguments.Json
    else

      match Args.repositoryIdFor arguments.RepositoryId rootFullPath with
      | Error error ->
        return writeManagementError "index" ExitCode.UserError (RepositoryId.describe error) arguments.Json
      | Ok repository ->

        let outputDirectory =
          locateArtifact arguments.OutputDirectory (ValueSome rootFullPath)

        if Path.GetRelativePath(rootFullPath, outputDirectory) = "." then
          return
            writeManagementError
              "index"
              ExitCode.UserError
              "解析ルート自身を --out に指定できません。専用の出力ディレクトリを指定してください"
              arguments.Json
        else
          let excluded = excludedOutputPath rootFullPath outputDirectory
          let options = walkOptions arguments excluded

          let options =
            { options with
                Jobs = min options.Jobs (max 1 (int(arguments.MemoryLimit / 134217728L))) }

          let diagnostics = DiagnosticSink()
          let progress = IndexProgress arguments.Progress

          use _registration =
            cancellation.Register(fun () -> progress.Phase "cancellation-requested")

          let! outcome =
            buildIndex
              rootFullPath
              outputDirectory
              repository
              options
              (ValueSome arguments.Tier)
              arguments.AllowPartial
              diagnostics
              progress
              arguments.MemoryLimit
              arguments.TemporaryLimit
              cancellation

          progress.Phase "finished"
          return reportIndex arguments diagnostics outcome
  }

let statsWithCancellation (arguments: Args.StatsArguments) (cancellation: CancellationToken) : int =
  let outputDirectory = locateArtifact arguments.OutputDirectory arguments.RootPath

  // マニフェストとセグメントを別々に開くと、その間に公開が完了したとき、
  // 旧世代の件数と新世代の集計を混ぜた結果を正常終了で返してしまう。
  let observed =
    Manifest.readStable outputDirectory (fun manifest ->
      struct (manifest,
              Stats.readFileStatisticsWithCancellation outputDirectory manifest cancellation,
              Stats.readReferenceStatisticsWithCancellation outputDirectory manifest cancellation))

  match observed with
  | Error error ->
    writeManagementError "stats" ExitCode.MissingArtifact (Manifest.ManifestError.describe error) arguments.Json
  | Ok(struct (_, Error error, _)) ->
    writeManagementError "stats" ExitCode.MissingArtifact (Reader.OpenError.describe error) arguments.Json
  | Ok(struct (_, _, Error error)) ->
    writeManagementError "stats" ExitCode.MissingArtifact (Reader.OpenError.describe error) arguments.Json
  | Ok(struct (manifest, Ok statistics, Ok references)) ->
    let code =
      if manifest.Counts.Files = 0 then
        ExitCode.NoResults
      else
        ExitCode.Success

    if arguments.Json then
      writeJson(fun writer ->
        writeManagementStatus
          writer
          "stats"
          code
          true
          (manifest.Diagnostics |> Array.sumBy(fun entry -> entry.Count))
          Array.empty

        writeCoverage writer manifest
        writer.WriteBoolean("complete", manifest.Complete)

        writer.WriteNumber(
          "requestedTier",
          manifest.Options.RequestedTier |> ValueOption.defaultValue manifest.Options.Tier
        )

        writer.WriteString("repositoryId", manifest.RepositoryId)
        writer.WriteNumber("nodes", manifest.Counts.Nodes)
        writer.WriteNumber("edges", manifest.Counts.Edges)
        writer.WriteNumber("directories", manifest.Counts.Directories)
        writer.WriteNumber("files", manifest.Counts.Files)
        writer.WriteNumber("symbols", manifest.Counts.Symbols)
        writer.WriteNumber("referenceCandidates", manifest.Counts.ReferenceCandidates)
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

        writer.WriteEndArray()
        writer.WriteStartArray "referenceKinds"

        for entry in references do
          writer.WriteStartObject()
          writer.WriteString("kind", EdgeKind.name entry.Kind)
          writer.WriteNumber("count", entry.Count)
          writer.WriteNumber("extracted", entry.Extracted)
          writer.WriteNumber("ambiguous", entry.Ambiguous)
          writer.WriteEndObject()

        writer.WriteEndArray())
    else
      Terminal.resultLine $"リポジトリ: {manifest.RepositoryId}"
      Terminal.outLine $"ノード: {manifest.Counts.Nodes} (うちシンボル {manifest.Counts.Symbols}) / エッジ: {manifest.Counts.Edges}"
      Terminal.outLine $"ディレクトリ: {manifest.Counts.Directories} / ファイル: {manifest.Counts.Files}"
      Terminal.outLine $"総バイト数: {statistics.TotalBytes} / 総行数: {statistics.TotalLines}"
      reportCoverage manifest
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
          Terminal.outLine $"  {Encodings.name entry.Encoding}: {entry.Files} ファイル (うち {entry.Ambiguous} 件は候補が複数で未確定)"

      Terminal.outLine ""
      Terminal.outLine $"参照候補: {manifest.Counts.ReferenceCandidates}"

      for entry in references do
        if entry.Ambiguous = 0 then
          Terminal.outLine $"  {EdgeKind.name entry.Kind}: {entry.Count}"
        else
          Terminal.outLine $"  {EdgeKind.name entry.Kind}: {entry.Count} (うち {entry.Ambiguous} 件は AMBIGUOUS)"

    code

let stats (arguments: Args.StatsArguments) =
  statsWithCancellation arguments CancellationToken.None

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

  if not(Directory.Exists directory) then
    Array.empty
  else
    Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
    |> Seq.map(fun path -> Path.GetRelativePath(directory, path).Replace('\\', '/'))
    |> Seq.filter(fun relative ->
      not(relative.StartsWith(Manifest.StagingDirectory, StringComparison.Ordinal))
      && relative <> ".writer.lock"
      && relative <> "graph.html"
      && relative <> "agent-context.json"
      && not(isRetiredGeneration relative))
    |> Seq.sortWith(fun left right -> String.CompareOrdinal(left, right))
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
  (cancellation: CancellationToken)
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
    cancellation.ThrowIfCancellationRequested()
    let relative = name.Replace('/', Path.DirectorySeparatorChar)

    use leftFile =
      new FileStream(
        Path.Combine(original, relative),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read ||| FileShare.Delete
      )

    use rightFile =
      new FileStream(
        Path.Combine(rebuilt, relative),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read ||| FileShare.Delete
      )

    let leftBuffer = ArrayPool<byte>.Shared.Rent 65536
    let rightBuffer = ArrayPool<byte>.Shared.Rent 65536

    try
      let mutable equal = leftFile.Length = rightFile.Length
      let mutable remaining = leftFile.Length

      while equal && remaining > 0L do
        cancellation.ThrowIfCancellationRequested()
        let count = int(min remaining 65536L)
        leftFile.ReadExactly(Span(leftBuffer, 0, count))
        rightFile.ReadExactly(Span(rightBuffer, 0, count))

        equal <-
          ReadOnlySpan(leftBuffer, 0, count)
            .SequenceEqual(ReadOnlySpan(rightBuffer, 0, count))

        remaining <- remaining - int64 count

      if not equal then
        mismatches.Add $"{name} がバイト単位で一致しません"
    finally
      ArrayPool<byte>.Shared.Return leftBuffer
      ArrayPool<byte>.Shared.Return rightBuffer

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
              // 抽出段階も生成条件の一部である。段階が違えば成果物は当然変わるため、
              // 記録された段階で再生成しなければ決定性を検証したことにならない。
              Extraction =
                { Extractor.ExtractionOptions.defaults with
                    Tier = tierOf(manifest.Options.RequestedTier |> ValueOption.defaultValue manifest.Options.Tier) }
              AssumeEncoding = Encodings.tryParse manifest.Options.AssumedEncoding
              // 並列度は所要時間だけを変え、出力を変えないことをここで検証する。
              Jobs = 1
              // 既存の成果物が解析ルート配下にある場合、初回と同じ条件になるよう除外する。
              ExcludedPaths = excludedOutputPath rootFullPath originalOutputDirectory }

        let diagnostics = DiagnosticSink()

        match!
          buildIndex
            rootFullPath
            temporary
            repository
            options
            manifest.Options.RequestedTier
            true
            diagnostics
            (IndexProgress "never")
            Args.DefaultMemoryLimit
            Args.DefaultTemporaryLimit
            cancellation
        with
        | Error failure -> return Error(IndexFailure.message failure)
        | Ok(struct (rebuilt, _)) ->
          return
            Ok(
              compareArtifacts
                originalOutputDirectory
                (Manifest.generationIn manifest)
                temporary
                (Manifest.generationIn rebuilt)
                cancellation
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
      return
        writeManagementError "verify" ExitCode.MissingArtifact (Manifest.ManifestError.describe error) arguments.Json
    | Ok report ->

      let! determinism =
        task {
          if not arguments.Deterministic || not report.IsValid then
            return ValueNone
          else
            match arguments.RootPath with
            | ValueNone ->
              if not arguments.Json then
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

      let code =
        if not report.IsValid then
          ExitCode.MissingArtifact
        elif report.Issues.Length = 0 && determinismIssues.Length = 0 then
          ExitCode.Success
        else
          ExitCode.CompletedWithDiagnostics

      let issues =
        Array.append (report.Issues |> Array.map Verify.Issue.describe) determinismIssues

      if arguments.Json then
        writeJson(fun writer ->
          let samples =
            issues
            |> Array.truncate 32
            |> Array.map(fun issue -> struct ("verification", "", issue))

          writeManagementStatus writer "verify" code true issues.Length samples
          writer.WriteBoolean("valid", report.IsValid && determinismIssues.Length = 0)
          writer.WriteNumber("segmentsChecked", report.SegmentsChecked)
          writer.WriteNumber("bytesChecked", report.BytesChecked)
          writer.WriteStartArray "issues"

          for issue in issues |> Array.truncate 32 do
            writer.WriteStringValue(boundedDiagnostic issue)

          writer.WriteEndArray())
      else
        Terminal.outLine $"検証したセグメント: {report.SegmentsChecked} ({report.BytesChecked} バイト)"

        for issue in report.Issues do
          Terminal.resultLine $"問題: {Verify.Issue.describe issue}"

        for issue in determinismIssues do
          Terminal.resultLine $"問題: {issue}"

        if arguments.Deterministic && report.IsValid && determinismIssues.Length = 0 then
          Terminal.outLine "決定性: 二度の生成で成果物がバイト単位で一致しました"

        if report.IsValid && determinismIssues.Length = 0 then
          Terminal.outLine "整合性: 問題ありません"

      return code
  }
