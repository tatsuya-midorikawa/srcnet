/// `srcnet export html` の実装。
///
/// 人間が抽出結果の妥当性、ハブ、孤立ノード、意外な接続を確認するための表示を作る。
/// AI の一次界面は 028 の JSON CLI であり、こちらは監査・探索用である（backlog 029）。
///
/// 生成物は単一の自己完結 HTML で、外部 CDN・ネットワーク通信・テレメトリを使わない。
/// 全体エクスポートは提供しない。上限を超える入力はディレクトリ単位へ集約するか、
/// 明示的に打ち切ってブラウザーをフリーズさせない。
module Srcnet.Cli.ExportCommands

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Storage
open Srcnet.Text

/// 埋め込みデータの構造の版。破壊的変更時に増やす。
[<Literal>]
let DataSchemaVersion = 1

/// テンプレート内のデータの差し込み位置。
[<Literal>]
let private DataPlaceholder = "{{DATA}}"

[<Literal>]
let private TemplateResource = "Srcnet.Cli.Assets.graph.html"

/// 既定の出力ファイル名。
[<Literal>]
let DefaultFileName = "graph.html"

/// Canvas の描画負荷をノード数と独立に制限する。
[<Literal>]
let MaxExportEdges = 50_000

[<Literal>]
let private MaxExportTextBytes = 8 * 1024 * 1024

[<Literal>]
let private MaxExportPageBytes = 64 * 1024 * 1024

let private template =
  lazy
    (let assembly = typeof<Srcnet.Cli.Args.Command>.Assembly

     match assembly.GetManifestResourceStream TemplateResource with
     | null -> ValueNone
     | stream ->
       use stream = stream
       use reader = new StreamReader(stream, UTF8Encoding false)
       ValueSome(reader.ReadToEnd()))

/// 出力に載せる 1 ノード分。
[<Struct>]
type private ExportNode =
  {
    Index: int
    Id: NodeId
    Kind: NodeKind
    Language: Language
    Name: string
    QualifiedName: string
    Path: string
    StartLine: int
    EndLine: int
    Flags: NodeFlags
    Distance: int
    /// 集約したノードがまとめている件数。集約していない場合は 1。
    Weight: int
  }

let private boundedStrings (view: Query.GraphView) (cancellation: CancellationToken) =
  let mutable remaining = MaxExportTextBytes

  fun index ->
    cancellation.ThrowIfCancellationRequested()
    let bytes = view.StringBytes index

    if bytes.Length > remaining then
      raise(IOException $"HTML の文字列データが上限 {MaxExportTextBytes} バイトを超えました。表示ノード数を減らしてください")

    remaining <- remaining - bytes.Length
    Encoding.UTF8.GetString bytes

let private toExport (view: Query.GraphView) (read: int -> string) (index: int) (distance: int) (weight: int) =
  let node = view.Node index

  { Index = index
    Id = node.Id
    Kind = node.Kind
    Language = node.Language
    Name = read node.NameRef
    QualifiedName = read node.QualifiedNameRef
    Path =
      if node.Kind = Directory then read node.QualifiedNameRef
      elif node.FileIndex = Format.NodeRecord.NoFile then ""
      else read(view.FilePathRef node.FileIndex)
    StartLine = node.StartLine
    EndLine = node.EndLine
    Flags = node.Flags
    Distance = distance
    Weight = weight }

type private Selection =
  { Nodes: ExportNode[]
    Edges: Query.EdgeView[]
    CandidateNodes: int
    GroupedNodes: int
    OmittedCount: int
    OmittedEdgeCount: int
    TraversalTruncated: bool
    LowerBound: bool
    Diagnostics: string[] }

/// 起点なしの概要。`CONTAINS` グラフをディレクトリ単位へ集約する。
///
/// 全ノードを渡すとブラウザーが持たない。ディレクトリとリポジトリだけを残し、
/// 各ディレクトリが直接含む件数を重みにする。
let private aggregate (view: Query.GraphView) (maxNodes: int) (cancellation: CancellationToken) =
  let selected = List<int>()
  let mutable total = 0

  for index in 0 .. view.NodeCount - 1 do
    if index &&& 0xFFFF = 0 then
      cancellation.ThrowIfCancellationRequested()

    let node = view.Node index

    match node.Kind with
    | Repository
    | Directory ->
      total <- total + 1

      if selected.Count < maxNodes then
        selected.Add index
    | _ -> ()

  // 密インデックスの昇順は、リポジトリ → ディレクトリの論理パス順に一致する。
  let kept = selected.ToArray()
  let keptSet = HashSet<int>(kept)
  let read = boundedStrings view cancellation

  let nodes =
    kept
    |> Array.map(fun index ->
      let node = toExport view read index 0 (view.NeighborCount(index, Contains, false))

      { node with
          Distance =
            if node.Kind = Repository then
              0
            else
              node.QualifiedName.Split('/').Length })

  let edges = List<Query.EdgeView>()
  let mutable omittedEdges = 0
  let mutable examined = 0
  let mutable truncated = false
  let mutable index = 0

  while index < view.NodeCount && not truncated do
    if index &&& 0xFFFF = 0 then
      cancellation.ThrowIfCancellationRequested()

    let kind = (view.Node index).Kind

    if kind = Repository || kind = Directory then
      let count = view.NeighborCount(index, Contains, false)
      let mutable position = 0

      while position < count && not truncated do
        if examined >= Query.MaxExploredEdges then
          truncated <- true
        else
          if examined &&& 1023 = 0 then
            cancellation.ThrowIfCancellationRequested()

          let target = view.NeighborAt(index, Contains, false, position)
          examined <- examined + 1
          position <- position + 1
          let targetKind = (view.Node target).Kind

          if targetKind = Directory || targetKind = Repository then
            if
              keptSet.Contains index
              && keptSet.Contains target
              && edges.Count < MaxExportEdges
            then
              edges.Add
                { From = index
                  To = target
                  Kind = Contains }
            else
              omittedEdges <- omittedEdges + 1

    index <- index + 1

  { Nodes = nodes
    Edges = edges.ToArray()
    CandidateNodes = total
    GroupedNodes = view.NodeCount - total
    OmittedCount = total - nodes.Length
    OmittedEdgeCount = omittedEdges
    TraversalTruncated = truncated
    LowerBound = truncated
    Diagnostics =
      if truncated then
        [| $"概要のエッジ走査が上限 {Query.MaxExploredEdges} 件に達しました。省略エッジ数は下限です" |]
      else
        Array.empty }

/// 起点を中心にした部分グラフ。深さと件数の両方で範囲を限る。
let private aroundSeed
  (view: Query.GraphView)
  (seed: int)
  (depth: int)
  (maxNodes: int)
  (cancellation: CancellationToken)
  =
  let traversal =
    Query.neighbors view seed view.EdgeKinds Query.Both depth cancellation

  let total = traversal.Nodes.Length
  let kept = traversal.Nodes |> Array.truncate maxNodes
  let keptSet = HashSet<int>(kept |> Array.map(fun (struct (index, _)) -> index))
  let read = boundedStrings view cancellation

  let nodes =
    kept
    |> Array.map(fun (struct (index, distance)) -> toExport view read index distance 1)

  let edges = List<Query.EdgeView>()
  let mutable omittedEdges = traversal.OmittedEdgeCount

  for edge in traversal.Edges do
    if
      keptSet.Contains edge.From
      && keptSet.Contains edge.To
      && edges.Count < MaxExportEdges
    then
      edges.Add edge
    else
      omittedEdges <- omittedEdges + 1

  { Nodes = nodes
    Edges = edges.ToArray()
    CandidateNodes = total
    GroupedNodes = 0
    OmittedCount = total - nodes.Length + traversal.OmittedCount
    OmittedEdgeCount = omittedEdges
    TraversalTruncated = traversal.Truncated
    LowerBound = traversal.OmittedCountIsLowerBound
    Diagnostics = traversal.Diagnostics }

/// データを JSON にする。
///
/// エンコーダーは CJK を素通しにする一方で、`<`、`>`、`&`、`'`、`"`、`+` は必ず
/// `\uXXXX` へ逃がす。これにより、リポジトリ由来の名前に `</script>` や引用符が
/// 含まれていても、埋め込んだ `<script type="application/json">` を閉じることができない。
let private renderData
  (view: Query.GraphView)
  (queryKind: string)
  (queryValue: string)
  (depth: int)
  (aggregated: bool)
  (seedIndex: int)
  (nodes: ExportNode[])
  (edges: Query.EdgeView[])
  (selection: Selection)
  (searchTruncated: bool)
  (searchOmitted: int)
  (searchLowerBound: bool)
  (diagnostics: string[])
  =
  let positionOf = Dictionary<int, int>()

  for position in 0 .. nodes.Length - 1 do
    positionOf[nodes[position].Index] <- position

  use buffer = new MemoryStream()

  // 改行を明示しないと OS 既定の改行が使われ、生成物が OS 依存になる。
  use writer =
    new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false, NewLine = "\n", Encoder = Commands.jsonEncoder))

  writer.WriteStartObject()
  writer.WriteNumber("schemaVersion", DataSchemaVersion)
  writer.WriteString("repository", view.Manifest.RepositoryId)
  writer.WriteString("toolVersion", view.Manifest.ToolVersion)
  writer.WriteStartObject "query"
  writer.WriteString("kind", queryKind)
  writer.WriteString("value", queryValue)
  writer.WriteNumber("depth", depth)
  writer.WriteEndObject()
  writer.WriteBoolean("aggregated", aggregated)
  writer.WriteNumber("totalNodes", view.NodeCount)
  writer.WriteNumber("candidateNodes", selection.CandidateNodes)
  writer.WriteNumber("groupedNodeCount", selection.GroupedNodes)
  writer.WriteNumber("maxExportEdges", MaxExportEdges)
  writer.WriteBoolean("traversalTruncated", selection.TraversalTruncated)
  writer.WriteBoolean("searchTruncated", searchTruncated)
  writer.WriteNumber("searchOmittedCount", searchOmitted)
  writer.WriteBoolean("searchOmittedCountIsLowerBound", searchLowerBound)

  writer.WriteBoolean(
    "truncated",
    searchTruncated
    || selection.TraversalTruncated
    || selection.OmittedCount > 0
    || selection.OmittedEdgeCount > 0
  )
  // 検索の未返却ヒットと近傍の省略は重なり得る。加算して正確な件数と見せない。
  writer.WriteNumber("omittedCount", max selection.OmittedCount searchOmitted)
  writer.WriteBoolean("omittedCountIsLowerBound", selection.LowerBound || searchLowerBound || searchOmitted > 0)
  writer.WriteNumber("omittedEdgeCount", selection.OmittedEdgeCount)

  writer.WriteNumber(
    "seedIndex",
    match positionOf.TryGetValue seedIndex with
    | true, position -> position
    | false, _ -> -1
  )

  writer.WriteStartArray "nodes"

  for node in nodes do
    writer.WriteStartObject()
    writer.WriteString("id", NodeId.toString node.Id)
    writer.WriteString("kind", NodeKind.name node.Kind)
    writer.WriteString("name", node.Name)
    writer.WriteString("qualifiedName", node.QualifiedName)
    writer.WriteString("path", node.Path)
    writer.WriteString("language", Language.name node.Language)
    writer.WriteStartArray "lines"
    writer.WriteNumberValue node.StartLine
    writer.WriteNumberValue node.EndLine
    writer.WriteEndArray()
    writer.WriteNumber("distance", node.Distance)
    writer.WriteNumber("weight", node.Weight)
    writer.WriteStartArray "flags"

    for name in Commands.flagNames node.Flags do
      writer.WriteStringValue name

    writer.WriteEndArray()
    writer.WriteEndObject()

  writer.WriteEndArray()
  writer.WriteStartArray "edges"

  for edge in edges do
    match positionOf.TryGetValue edge.From, positionOf.TryGetValue edge.To with
    | (true, from), (true, target) ->
      writer.WriteStartObject()
      writer.WriteNumber("from", from)
      writer.WriteNumber("to", target)
      writer.WriteString("kind", EdgeKind.name edge.Kind)
      writer.WriteEndObject()
    | _ -> ()

  writer.WriteEndArray()
  writer.WriteStartArray "diagnostics"

  for diagnostic in diagnostics do
    writer.WriteStringValue diagnostic

  writer.WriteEndArray()
  writer.WriteEndObject()
  writer.Flush()
  Encoding.UTF8.GetString(buffer.ToArray())

/// HTML を原子的に置き換える。
///
/// 途中で失敗したときに、不完全な HTML を成功扱いで残さない。既存のファイルは
/// 書き上がるまで保持する。
let private rejectLinks (destination: string) =
  let paths =
    match Path.GetDirectoryName destination with
    | null -> [| destination |]
    | parent -> [| destination; parent |]

  for current in paths do
    let attributes =
      try
        ValueSome(File.GetAttributes current)
      with
      | :? FileNotFoundException -> ValueNone
      | :? DirectoryNotFoundException -> ValueNone

    match attributes with
    | ValueSome value when value.HasFlag FileAttributes.ReparsePoint ->
      raise(IOException $"リンク経由の出力は許可されません: {current}")
    | _ -> ()

let private writeAtomically (destination: string) (payload: string) (cancellation: CancellationToken) =
  if Encoding.UTF8.GetByteCount payload > MaxExportPageBytes then
    raise(IOException $"HTML が上限 {MaxExportPageBytes} バイトを超えました。表示ノード数を減らしてください")

  rejectLinks destination
  cancellation.ThrowIfCancellationRequested()

  let directory =
    match Path.GetDirectoryName destination with
    | null
    | "" -> "."
    | parent -> parent

  // Appending a suffix to a valid destination can exceed the file-name limit.
  let temporary =
    Path.Combine(directory, "srcnet-" + Guid.NewGuid().ToString("N") + ".tmp")

  let mutable owned = false

  try
    Directory.CreateDirectory directory |> ignore

    do
      use stream =
        new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)

      owned <- true
      let bytes = Encoding.UTF8.GetBytes payload
      stream.Write(ReadOnlySpan bytes)
      stream.Flush true

    cancellation.ThrowIfCancellationRequested()
    rejectLinks destination
    Artifact.replaceFile temporary destination
    owned <- false
  finally
    if owned then
      try
        File.Delete temporary
      with
      | :? IOException as ex -> Terminal.diagnosticLine $"一時ファイルを削除できません: {ex.Message}"
      | :? UnauthorizedAccessException as ex -> Terminal.diagnosticLine $"一時ファイルを削除できません: {ex.Message}"

/// `srcnet export html` の本体。
let private exportHtmlCore (arguments: Args.ExportArguments) (cancellation: CancellationToken) : int =
  let fail code message =
    QueryCommands.writeError "export" code message arguments.Json

  cancellation.ThrowIfCancellationRequested()

  match template.Value with
  | ValueNone -> fail Commands.ExitCode.InternalError "HTML テンプレートが実行ファイルに含まれていません"
  | ValueSome page ->

    let outputDirectory =
      Commands.locateArtifact arguments.OutputDirectory arguments.RootPath

    match Query.GraphView.Open outputDirectory with
    | Error error -> fail Commands.ExitCode.MissingArtifact (Query.QueryError.describe error)
    | Ok view ->

      use view = view
      let diagnostics = List<string>()
      let mutable searchTruncated = false
      let mutable searchOmitted = 0
      let mutable searchLowerBound = false

      // 起点の決定。`--node` は ID か完全一致の名前、`--query` は検索の最上位。
      let seed =
        if arguments.Node <> "" then
          match QueryCommands.resolveNode view arguments.Node cancellation with
          | QueryCommands.MissingNode -> Error $"ノードが見つかりません: {Sanitize.forTerminal arguments.Node}"
          | QueryCommands.ResolvedNode index -> Ok(ValueSome index)
          | QueryCommands.IncompleteNode _ -> Error "名前の照会が打ち切られ、一意性を確認できません。NodeId を指定してください"
          | QueryCommands.AmbiguousNode candidates ->
            diagnostics.Add $"`{arguments.Node}` は {candidates.Length} 件に一致しました。最上位を起点にしています"
            Ok(ValueSome candidates[0])
        elif arguments.Query <> "" then
          let text = arguments.Query

          let outcome = Query.search view text false cancellation
          searchTruncated <- outcome.Truncated
          searchOmitted <- outcome.OmittedCount
          searchLowerBound <- outcome.OmittedCountIsLowerBound
          diagnostics.AddRange outcome.Diagnostics

          if not outcome.UsedIndex then
            diagnostics.Add "検索索引を使わない有界検索の結果です"

          if outcome.Truncated then
            diagnostics.Add "起点検索が打ち切られました。返された候補内の最上位であり、未返却の候補があります"

          let ordered = Array.copy outcome.Hits

          Array.sortInPlaceWith (Query.compareHits view) ordered

          if ordered.Length = 0 then
            let detail = String.Join(" / ", diagnostics)
            Error $"一致するノードがありません: {Sanitize.forTerminal text}。{detail}"
          else
            if ordered.Length > 1 then
              diagnostics.Add $"`{text}` は返された {ordered.Length} 件に一致しました。最上位を起点にしています"

            Ok(ValueSome ordered[0].Node)
        else
          Ok ValueNone

      match seed with
      | Error message ->
        // 起点が決まらないまま不完全な HTML を残さない。既存のファイルもそのままにする。
        fail Commands.ExitCode.NoResults message
      | Ok resolved ->

        let selection =
          match resolved with
          | ValueSome index -> aroundSeed view index arguments.Depth arguments.MaxNodes cancellation
          | ValueNone ->
            diagnostics.Add "起点の指定がないため、CONTAINS グラフをディレクトリ単位へ集約しました"
            aggregate view arguments.MaxNodes cancellation

        let nodes, edges = selection.Nodes, selection.Edges
        diagnostics.AddRange selection.Diagnostics
        let omitted = max selection.OmittedCount searchOmitted
        let lowerBound = selection.LowerBound || searchLowerBound || searchOmitted > 0

        let truncated =
          searchTruncated
          || selection.TraversalTruncated
          || omitted > 0
          || selection.OmittedEdgeCount > 0

        if omitted > 0 then
          diagnostics.Add $"省略ノード数: {omitted}（下限: {lowerBound}）。表示ノード上限: {arguments.MaxNodes}"

        if selection.OmittedEdgeCount > 0 then
          diagnostics.Add $"省略エッジ数: {selection.OmittedEdgeCount}。表示エッジ上限: {MaxExportEdges}"

        // ソース本文は埋め込まない。成果物を共有したときにコードを複製しないためである。
        let payload =
          renderData
            view
            (if resolved.IsSome then
               (if arguments.Node <> "" then "node" else "query")
             else
               "overview")
            (if arguments.Node <> "" then
               arguments.Node
             else
               arguments.Query)
            arguments.Depth
            resolved.IsNone
            (match resolved with
             | ValueSome index -> index
             | ValueNone -> -1)
            nodes
            edges
            selection
            searchTruncated
            searchOmitted
            searchLowerBound
            (diagnostics.ToArray())

        let destination =
          match arguments.File with
          | ValueSome file -> Path.GetFullPath file
          | ValueNone -> Path.Combine(outputDirectory, DefaultFileName)

        writeAtomically destination (page.Replace(DataPlaceholder, payload)) cancellation

        if arguments.Json then
          Commands.writeJson(fun writer ->
            writer.WriteString("command", "export")
            writer.WriteString("format", "html")
            writer.WriteString("file", destination)
            writer.WriteNumber("nodes", nodes.Length)
            writer.WriteNumber("edges", edges.Length)
            writer.WriteNumber("totalNodes", view.NodeCount)
            writer.WriteNumber("candidateNodes", selection.CandidateNodes)
            writer.WriteNumber("groupedNodeCount", selection.GroupedNodes)
            writer.WriteBoolean("aggregated", resolved.IsNone)
            writer.WriteBoolean("truncated", truncated)
            writer.WriteBoolean("traversalTruncated", selection.TraversalTruncated)
            writer.WriteBoolean("searchTruncated", searchTruncated)
            writer.WriteNumber("searchOmittedCount", searchOmitted)
            writer.WriteBoolean("searchOmittedCountIsLowerBound", searchLowerBound)
            writer.WriteNumber("maxExportEdges", MaxExportEdges)
            writer.WriteNumber("omittedCount", omitted)
            writer.WriteNumber("omittedEdgeCount", selection.OmittedEdgeCount)
            writer.WriteBoolean("omittedCountIsLowerBound", lowerBound)
            writer.WriteStartArray "diagnostics"

            for diagnostic in diagnostics do
              writer.WriteStringValue diagnostic

            writer.WriteEndArray())
        else
          Terminal.resultLine destination
          Terminal.errLine $"ノード {nodes.Length} / エッジ {edges.Length}（成果物全体 {view.NodeCount} ノード）"

          for diagnostic in diagnostics do
            Terminal.diagnosticLine diagnostic

        Commands.ExitCode.Success

let private validateArguments (arguments: Args.ExportArguments) =
  if arguments.Depth < 0 || arguments.Depth > Args.MaxQueryDepth then
    ValueSome $"--depth は 0..{Args.MaxQueryDepth} で指定してください"
  elif arguments.MaxNodes < 0 || arguments.MaxNodes > Args.MaxExportNodes then
    ValueSome $"--max-nodes は 1..{Args.MaxExportNodes} で指定してください（0 は既定値）"
  elif isNull(box arguments.Query) || isNull(box arguments.Node) then
    ValueSome "--query と --node に null は指定できません"
  elif arguments.Query <> "" && arguments.Node <> "" then
    ValueSome "--query と --node は同時には指定できません"
  else
    let textError =
      [| "--query", arguments.Query; "--node", arguments.Node |]
      |> Array.tryPick(fun (name, text) ->
        if text = "" then
          None
        else
          Args.validateQueryText name text
          |> ValueOption.toOption
          |> Option.map Args.ParseError.describe)

    match textError with
    | Some message -> ValueSome message
    | None ->
      [| "--out", arguments.OutputDirectory
         "--root", arguments.RootPath
         "--file", arguments.File |]
      |> Array.tryPick(fun (name, path) ->
        match path with
        | ValueSome value when String.IsNullOrWhiteSpace value -> Some $"{name} に空のパスは指定できません"
        | _ -> None)
      |> ValueOption.ofOption

let exportHtml (arguments: Args.ExportArguments) (cancellation: CancellationToken) : int =
  try
    cancellation.ThrowIfCancellationRequested()

    match validateArguments arguments with
    | ValueSome message -> QueryCommands.writeError "export" Commands.ExitCode.UserError message arguments.Json
    | ValueNone ->
      let validated =
        if arguments.MaxNodes = 0 then
          { arguments with
              MaxNodes = Args.DefaultMaxNodes }
        else
          arguments

      exportHtmlCore validated cancellation
  with
  | Query.QueryException error ->
    QueryCommands.writeError "export" Commands.ExitCode.MissingArtifact (Query.QueryError.describe error) arguments.Json
  | :? IOException as ex ->
    QueryCommands.writeError "export" Commands.ExitCode.UserError $"HTML を書き出せませんでした: {ex.Message}" arguments.Json
  | :? UnauthorizedAccessException as ex ->
    QueryCommands.writeError "export" Commands.ExitCode.UserError $"HTML を書き出せませんでした: {ex.Message}" arguments.Json
  | :? ArgumentException as ex ->
    QueryCommands.writeError "export" Commands.ExitCode.UserError ex.Message arguments.Json
