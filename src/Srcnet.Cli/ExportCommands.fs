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
  { Index: int
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
    Weight: int }

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

let private flagNames (flags: NodeFlags) =
  flagTable
  |> Array.filter (fun (flag, _) -> flags.HasFlag flag)
  |> Array.map snd

let private toExport (view: Query.GraphView) (index: int) (distance: int) (weight: int) =
  let node = view.Node index

  { Index = index
    Id = node.Id
    Kind = node.Kind
    Language = node.Language
    Name = view.String node.NameRef
    QualifiedName = view.String node.QualifiedNameRef
    Path = view.String(view.FilePathRef node.FileIndex)
    StartLine = node.StartLine
    EndLine = node.EndLine
    Flags = node.Flags
    Distance = distance
    Weight = weight }

/// 起点なしの概要。`CONTAINS` グラフをディレクトリ単位へ集約する。
///
/// 全ノードを渡すとブラウザーが持たない。ディレクトリとリポジトリだけを残し、
/// 各ディレクトリが直接含む件数を重みにする。
let private aggregate (view: Query.GraphView) (maxNodes: int) (cancellation: CancellationToken) =
  let selected = List<int>()
  let weights = Dictionary<int, int>()

  for index in 0 .. view.NodeCount - 1 do
    if index &&& 0xFFFF = 0 then cancellation.ThrowIfCancellationRequested()
    let node = view.Node index

    match node.Kind with
    | Repository
    | Directory ->
      selected.Add index
      weights[index] <- view.Neighbors(index, Contains, Query.Outgoing).Length
    | _ -> ()

  // 密インデックスの昇順は、リポジトリ → ディレクトリの論理パス順に一致する。
  let total = selected.Count
  let kept = selected |> Seq.truncate maxNodes |> Seq.toArray
  let keptSet = HashSet<int>(kept)

  let nodes =
    kept
    |> Array.map (fun index ->
      let depth = (view.String (view.Node index).QualifiedNameRef).Split('/').Length
      toExport view index (if (view.Node index).Kind = Repository then 0 else depth) weights[index])

  let edges = List<Query.EdgeView>()

  for index in kept do
    for target in view.Neighbors(index, Contains, Query.Outgoing) do
      if keptSet.Contains target then
        edges.Add { From = index; To = target; Kind = Contains }

  struct (nodes, edges.ToArray(), total)

/// 起点を中心にした部分グラフ。深さと件数の両方で範囲を限る。
let private aroundSeed
  (view: Query.GraphView)
  (seed: int)
  (depth: int)
  (maxNodes: int)
  (cancellation: CancellationToken)
  =
  let traversal = Query.neighbors view seed view.EdgeKinds Query.Both depth cancellation
  let total = traversal.Nodes.Length
  let kept = traversal.Nodes |> Array.truncate maxNodes
  let keptSet = HashSet<int>(kept |> Array.map (fun (struct (index, _)) -> index))

  let nodes =
    kept |> Array.map (fun (struct (index, distance)) -> toExport view index distance 1)

  let edges =
    traversal.Edges
    |> Array.filter (fun edge -> keptSet.Contains edge.From && keptSet.Contains edge.To)

  struct (nodes, edges, total)

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
  (total: int)
  (omitted: int)
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
  writer.WriteNumber("totalNodes", total)
  writer.WriteBoolean("truncated", omitted > 0)
  writer.WriteNumber("omittedCount", omitted)

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

    for name in flagNames node.Flags do
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
let private writeAtomically (destination: string) (payload: string) =
  let temporary = destination + ".tmp"

  try
    match Path.GetDirectoryName destination with
    | null -> ()
    | parent -> if parent <> "" then Directory.CreateDirectory parent |> ignore

    // 既存の項目がリンクだと `FileMode.Create` はリンク先を上書きしてしまう。
    File.Delete temporary

    do
      use stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
      let bytes = Encoding.UTF8.GetBytes payload
      stream.Write(ReadOnlySpan bytes)
      stream.Flush()

    File.Move(temporary, destination, true)
    Ok()
  with
  | :? IOException as ex ->
    (try File.Delete temporary with :? IOException -> ())
    Error ex.Message
  | :? UnauthorizedAccessException ->
    (try File.Delete temporary with :? IOException -> ())
    Error "書き込む権限がありません"

let private openView (explicitOutput: string voption) (rootPath: string voption) =
  let outputDirectory =
    match explicitOutput with
    | ValueSome directory -> Path.GetFullPath directory
    | ValueNone ->
      let root =
        match rootPath with
        | ValueSome path -> Path.GetFullPath path
        | ValueNone -> Directory.GetCurrentDirectory()

      Path.Combine(Path.TrimEndingDirectorySeparator root, Args.DefaultOutputDirectoryName)

  struct (outputDirectory, Query.GraphView.Open outputDirectory)

/// `srcnet export html` の本体。
let exportHtml (arguments: Args.ExportArguments) (cancellation: CancellationToken) : int =
  match template.Value with
  | ValueNone ->
    Terminal.errLine "HTML テンプレートが実行ファイルに含まれていません"
    Commands.ExitCode.InternalError
  | ValueSome page ->

  let struct (outputDirectory, opened) = openView arguments.OutputDirectory arguments.RootPath

  match opened with
  | Error error ->
    Terminal.errLine (Query.QueryError.describe error)
    Commands.ExitCode.MissingArtifact
  | Ok view ->

  use view = view
  let diagnostics = List<string>()

  // 起点の決定。`--node` は ID か完全一致の名前、`--query` は検索の最上位。
  let seed =
    if arguments.Node <> "" then
      match NodeId.tryParse arguments.Node with
      | ValueSome id ->
        match view.TryResolve id with
        | ValueSome index -> Ok(ValueSome index)
        | ValueNone -> Error $"ノードが見つかりません: {Sanitize.forTerminal arguments.Node}"
      | ValueNone ->
        let outcome = Query.search view arguments.Node false cancellation

        let exact =
          outcome.Hits
          |> Array.filter (fun hit ->
            hit.Strength = Query.Exact && (hit.Target = Query.Name || hit.Target = Query.QualifiedName))

        match exact with
        | [||] -> Error $"ノードが見つかりません: {Sanitize.forTerminal arguments.Node}"
        | many ->
          let ordered = Array.copy many
          Array.sortInPlaceWith (Query.compareHits view) ordered

          if ordered.Length > 1 then
            diagnostics.Add $"`{arguments.Node}` は {ordered.Length} 件に一致しました。最上位を起点にしています"

          Ok(ValueSome ordered[0].Node)
    elif arguments.Query <> "" then
      let outcome = Query.search view arguments.Query false cancellation
      let ordered = Array.copy outcome.Hits
      Array.sortInPlaceWith (Query.compareHits view) ordered

      if ordered.Length = 0 then Error $"一致するノードがありません: {Sanitize.forTerminal arguments.Query}"
      else
        if ordered.Length > 1 then
          diagnostics.Add $"`{arguments.Query}` は {ordered.Length} 件に一致しました。最上位を起点にしています"

        Ok(ValueSome ordered[0].Node)
    else Ok ValueNone

  match seed with
  | Error message ->
    // 起点が決まらないまま不完全な HTML を残さない。既存のファイルもそのままにする。
    Terminal.errLine message
    Commands.ExitCode.NoResults
  | Ok resolved ->

  let struct (nodes, edges, total) =
    match resolved with
    | ValueSome index -> aroundSeed view index arguments.Depth arguments.MaxNodes cancellation
    | ValueNone ->
      diagnostics.Add "起点の指定がないため、CONTAINS グラフをディレクトリ単位へ集約しました"
      aggregate view arguments.MaxNodes cancellation

  let omitted = max 0 (total - nodes.Length)

  if omitted > 0 then
    diagnostics.Add $"上限 {arguments.MaxNodes} を超えたため {omitted} ノードを省略しました"

  // ソース本文は埋め込まない。成果物を共有したときにコードを複製しないためである。
  let payload =
    renderData
      view
      (if resolved.IsSome then (if arguments.Node <> "" then "node" else "query") else "overview")
      (if arguments.Node <> "" then arguments.Node else arguments.Query)
      arguments.Depth
      resolved.IsNone
      (match resolved with
       | ValueSome index -> index
       | ValueNone -> -1)
      nodes
      edges
      total
      omitted
      (diagnostics.ToArray())

  let destination =
    match arguments.File with
    | ValueSome file -> Path.GetFullPath file
    | ValueNone -> Path.Combine(outputDirectory, DefaultFileName)

  match writeAtomically destination (page.Replace(DataPlaceholder, payload)) with
  | Error message ->
    Terminal.errLine $"HTML を書き出せませんでした: {message}"
    Commands.ExitCode.UserError
  | Ok() ->

  if arguments.Json then
    Commands.writeJson (fun writer ->
      writer.WriteString("command", "export")
      writer.WriteString("format", "html")
      writer.WriteString("file", destination)
      writer.WriteNumber("nodes", nodes.Length)
      writer.WriteNumber("edges", edges.Length)
      writer.WriteNumber("totalNodes", total)
      writer.WriteBoolean("aggregated", resolved.IsNone)
      writer.WriteBoolean("truncated", omitted > 0)
      writer.WriteNumber("omittedCount", omitted)
      writer.WriteStartArray "diagnostics"

      for diagnostic in diagnostics do
        writer.WriteStringValue diagnostic

      writer.WriteEndArray())
  else
    Terminal.resultLine destination
    Terminal.errLine $"ノード {nodes.Length} / エッジ {edges.Length}（成果物全体 {total} ノード）"

    for diagnostic in diagnostics do
      Terminal.errLine diagnostic

  Commands.ExitCode.Success
