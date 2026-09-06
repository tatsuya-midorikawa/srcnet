/// 照会コマンドの実装。
///
/// AI エージェントはシェル経由でこの層を一次界面として使う（docs/query-and-cli.md 7）。
/// したがって出力は必ず有界で、打ち切りは黙って行わず件数として報告する。
///
/// 成果物全体を managed object へ展開しない。`Query.GraphView` が memory-mapped の
/// セグメントを保持し、触れたページだけを読む。
module Srcnet.Cli.QueryCommands

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Storage
open Srcnet.Text

/// トークン数の決定的な近似の説明。特定のモデルのトークナイザには依存しない
/// （docs/query-and-cli.md 4）。実際のトークナイザとは 2 割程度ずれ得る。
[<Literal>]
let TokenEstimateMethod = "ascii/4 + cjk*1 + other/2"

/// 文字ではなくスカラー値で数える。サロゲート対を 2 文字と数えない。
let estimateTokens (text: string) =
  let mutable ascii = 0
  let mutable cjk = 0
  let mutable other = 0
  let mutable enumerator = text.EnumerateRunes()

  while enumerator.MoveNext() do
    let scalar = enumerator.Current.Value

    if scalar < 128 then ascii <- ascii + 1
    elif Words.isCjkScalar scalar then cjk <- cjk + 1
    else other <- other + 1

  (ascii + 3) / 4 + cjk + (other + 1) / 2

/// 出力を組み立てながら予算を守るための状態。
///
/// 予算超過時は順位の下位から切り詰める。捨てた件数を必ず持ち回る。
[<Sealed>]
type Budget(limit: int, tokens: int) =
  let mutable used = 0
  let mutable count = 0
  let mutable omitted = 0

  member _.Count = count
  member _.Omitted = omitted
  member _.UsedTokens = used
  member _.Truncated = omitted > 0

  /// 1 件を受け入れられるか。受け入れる場合だけ使用量を加算する。
  member _.TryAdd(cost: int) =
    if count >= limit || used + cost > tokens then
      omitted <- omitted + 1
      false
    else
      count <- count + 1
      used <- used + cost
      true

  /// 予算に関わらず加算する。包絡部分など、省略できない出力に使う。
  member _.Charge(cost: int) = used <- used + cost

/// 出力に載せる 1 ノード分の情報。
[<Struct>]
type NodeOutput =
  { Node: Query.NodeView
    Name: string
    QualifiedName: string
    Path: string
    Distance: int
    Match: string
    MatchedOn: string }

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

let private describe (view: Query.GraphView) (index: int) (distance: int) =
  let node = view.Node index

  { Node = node
    Name = view.String node.NameRef
    QualifiedName = view.String node.QualifiedNameRef
    Path = view.String(view.FilePathRef node.FileIndex)
    Distance = distance
    Match = ""
    MatchedOn = "" }

/// 1 ノード分の出力が使うトークン数の見積り。
/// ID、種別、行範囲などの定型部分をおおよそ 24 トークンとみなす。
let private nodeCost (output: NodeOutput) =
  24 + estimateTokens output.Name + estimateTokens output.QualifiedName + estimateTokens output.Path

/// 1 エッジ分の出力が使うトークン数の見積り。ID 2 つと種別で概ね一定になる。
[<Literal>]
let private EdgeCost = 26

let private writeNode (writer: Utf8JsonWriter) (output: NodeOutput) (includeDistance: bool) =
  // 構造体のフィールドを重ねてたどると複製が起きる。1 度だけ束縛してから使う。
  let node = output.Node
  writer.WriteStartObject()
  writer.WriteString("id", NodeId.toString node.Id)
  writer.WriteString("kind", NodeKind.name node.Kind)
  writer.WriteString("name", output.Name)
  writer.WriteString("qualifiedName", output.QualifiedName)
  writer.WriteString("path", output.Path)
  writer.WriteString("language", Language.name node.Language)
  writer.WriteStartArray "lines"
  writer.WriteNumberValue node.StartLine
  writer.WriteNumberValue node.EndLine
  writer.WriteEndArray()
  writer.WriteNumber("ordinal", node.Ordinal)
  writer.WriteStartArray "flags"

  for name in flagNames node.Flags do
    writer.WriteStringValue name

  writer.WriteEndArray()

  if includeDistance then writer.WriteNumber("distance", output.Distance)
  if output.Match <> "" then writer.WriteString("match", output.Match)
  if output.MatchedOn <> "" then writer.WriteString("matchedOn", output.MatchedOn)

  writer.WriteEndObject()

let private writeTextNode (output: NodeOutput) (includeDistance: bool) =
  let node = output.Node

  let location =
    if output.Path = "" then ""
    elif node.StartLine > 0 then $" {output.Path}:{node.StartLine}"
    else $" {output.Path}"

  let flags =
    match flagNames node.Flags with
    | [||] -> ""
    | names -> " [" + String.Join(",", names) + "]"

  let distance = if includeDistance then $" (深さ {output.Distance})" else ""
  let matched = if output.Match = "" then "" else $" <{output.Match}>"

  Terminal.outLine
    (Terminal.fitToWidth
      $"{NodeKind.name node.Kind} {Sanitize.forTerminal output.QualifiedName}{location}{flags}{matched}{distance}")

/// 打ち切りと省略件数を報告する。黙って捨てない（docs/query-and-cli.md 4）。
let private reportTruncation (budget: Budget) =
  if budget.Truncated then
    Terminal.errLine $"出力の上限に達したため {budget.Omitted} 件を省略しました"

/// 成果物の位置を決めて開く。
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

  Query.GraphView.Open outputDirectory

/// 辿るエッジ種別を決める。指定がなければ成果物が持つすべての種別を使う。
let private resolveEdgeKinds (view: Query.GraphView) (requested: string[]) =
  if requested.Length = 0 then Ok(view.EdgeKinds)
  else
    let resolved = ResizeArray<EdgeKind>()
    let mutable unknown = ValueNone

    for name in requested do
      match EdgeKind.all |> Array.tryFind (fun kind -> EdgeKind.name kind = name.ToUpperInvariant()) with
      | Some kind -> if not (resolved.Contains kind) then resolved.Add kind
      | None -> if unknown.IsNone then unknown <- ValueSome name

    match unknown with
    | ValueSome name -> Error name
    | ValueNone ->
      // 成果物が持たない種別は空の隣接として扱う。要求そのものは誤りではない。
      Ok(resolved.ToArray() |> Array.sortBy EdgeKind.toCode)

/// ノードの指定を解決する。
///
/// 16 進 32 桁は ID として扱い、それ以外は完全一致の検索で解決する。一意に決まらない
/// 場合は候補を返す。推測で 1 つに絞らない（docs/graph-model.md 3）。
type Resolution =
  | ResolvedNode of index: int
  | AmbiguousNode of candidates: int[]
  | MissingNode

let private resolveNode (view: Query.GraphView) (text: string) (cancellation: CancellationToken) =
  match NodeId.tryParse text with
  | ValueSome id ->
    match view.TryResolve id with
    | ValueSome index -> ResolvedNode index
    | ValueNone -> MissingNode
  | ValueNone ->
    let outcome = Query.search view text false cancellation

    // ノードの指定は「そのノード自身の名前」で行う。所属パスの一致まで拾うと、
    // ファイル名を渡したときにそのファイルの全シンボルが候補になってしまう。
    let exact =
      outcome.Hits
      |> Array.filter (fun hit ->
        hit.Strength = Query.Exact && (hit.Target = Query.Name || hit.Target = Query.QualifiedName))

    match exact with
    | [||] -> MissingNode
    | [| single |] -> ResolvedNode single.Node
    | many ->
      let ordered = Array.copy many
      Array.sortInPlaceWith (Query.compareHits view) ordered
      AmbiguousNode(ordered |> Array.map (fun hit -> hit.Node))

let private reportAmbiguous (view: Query.GraphView) (text: string) (candidates: int[]) =
  Terminal.errLine $"`{Sanitize.forTerminal text}` は一意に決まりません。候補:"

  for index in candidates |> Array.truncate 10 do
    let node = view.Node index
    Terminal.errLine $"  {node.Id} {NodeKind.name node.Kind} {Sanitize.forTerminal(view.String node.QualifiedNameRef)}"

/// 検索索引がまだ無いことを伝える診断。全走査へ黙って退行させない（backlog 028）。
let private indexDiagnostics (outcome: Query.SearchOutcome) =
  let messages = ResizeArray<string>()

  if not outcome.UsedIndex then
    messages.Add $"検索索引がないため {outcome.ScannedNodes} 件のノードを全走査しました。索引は M5 で追加します"

  if outcome.Truncated then
    messages.Add $"走査するノード数の上限 {Query.MaxScannedNodes} に達したため打ち切りました"

  messages.ToArray()

/// 照会結果の共通部分。すべてのコマンドで同じ形にする。
let private writeCommon
  (writer: Utf8JsonWriter)
  (diagnostics: string[])
  (budget: Budget)
  =
  writer.WriteStartArray "diagnostics"

  for diagnostic in diagnostics do
    writer.WriteStringValue diagnostic

  writer.WriteEndArray()
  writer.WriteBoolean("truncated", budget.Truncated)
  writer.WriteNumber("omittedCount", budget.Omitted)
  writer.WriteNumber("tokenEstimate", budget.UsedTokens)
  writer.WriteString("tokenEstimateMethod", TokenEstimateMethod)

let private writeNodes (writer: Utf8JsonWriter) (outputs: NodeOutput seq) (includeDistance: bool) =
  writer.WriteStartArray "nodes"

  for output in outputs do
    writeNode writer output includeDistance

  writer.WriteEndArray()

let private writeEdges (view: Query.GraphView) (writer: Utf8JsonWriter) (edges: Query.EdgeView seq) =
  writer.WriteStartArray "edges"

  for edge in edges do
    let source = view.Node edge.From
    let target = view.Node edge.To
    writer.WriteStartObject()
    writer.WriteString("from", NodeId.toString source.Id)
    writer.WriteString("to", NodeId.toString target.Id)
    writer.WriteString("kind", EdgeKind.name edge.Kind)
    // M2 の成果物のエッジはすべて抽出時に確定した事実である。解決は M3。
    writer.WriteString("confidence", Confidence.name Extracted)
    writer.WriteEndObject()

  writer.WriteEndArray()

let private diagnosticsExit (diagnostics: string[]) (empty: bool) =
  if empty then Commands.ExitCode.NoResults
  elif diagnostics.Length > 0 then Commands.ExitCode.Success
  else Commands.ExitCode.Success

// --- search ------------------------------------------------------------------

let search (arguments: Args.SearchArguments) (cancellation: CancellationToken) : int =
  match openView arguments.OutputDirectory arguments.RootPath with
  | Error error ->
    Terminal.errLine (Query.QueryError.describe error)
    Commands.ExitCode.MissingArtifact
  | Ok view ->

  use view = view
  let outcome = Query.search view arguments.Text arguments.IgnoreCase cancellation
  let ordered = Array.copy outcome.Hits
  Array.sortInPlaceWith (Query.compareHits view) ordered

  let budget = Budget(arguments.Limits.Limit, arguments.Limits.Budget)
  let accepted = ResizeArray<NodeOutput>()

  for hit in ordered do
    let output =
      { describe view hit.Node 0 with
          Match = Query.MatchStrength.name hit.Strength
          MatchedOn = Query.MatchTarget.name hit.Target }

    if budget.TryAdd(nodeCost output) then accepted.Add output

  let diagnostics = indexDiagnostics outcome

  if arguments.Json then
    Commands.writeJson (fun writer ->
      writer.WriteString("command", "search")
      writer.WriteStartObject "query"
      writer.WriteString("kind", "search")
      writer.WriteString("text", arguments.Text)
      writer.WriteBoolean("ignoreCase", arguments.IgnoreCase)
      writer.WriteNumber("limit", arguments.Limits.Limit)
      writer.WriteNumber("budget", arguments.Limits.Budget)
      writer.WriteEndObject()
      writer.WriteBoolean("usedIndex", outcome.UsedIndex)
      writer.WriteNumber("totalMatches", ordered.Length)
      writeNodes writer accepted false
      writer.WriteStartArray "edges"
      writer.WriteEndArray()
      writeCommon writer diagnostics budget)
  else
    for output in accepted do
      writeTextNode output false

    Terminal.errLine $"一致: {ordered.Length} 件 / 表示: {accepted.Count} 件"

    for diagnostic in diagnostics do
      Terminal.errLine diagnostic

    reportTruncation budget

  diagnosticsExit diagnostics (ordered.Length = 0)

// --- show --------------------------------------------------------------------

let show (arguments: Args.ShowArguments) (cancellation: CancellationToken) : int =
  match openView arguments.OutputDirectory arguments.RootPath with
  | Error error ->
    Terminal.errLine (Query.QueryError.describe error)
    Commands.ExitCode.MissingArtifact
  | Ok view ->

  use view = view

  match resolveNode view arguments.Node cancellation with
  | MissingNode ->
    Terminal.errLine $"ノードが見つかりません: {Sanitize.forTerminal arguments.Node}"
    Commands.ExitCode.NoResults
  | AmbiguousNode candidates ->
    reportAmbiguous view arguments.Node candidates
    Commands.ExitCode.NoResults
  | ResolvedNode index ->

  let output = describe view index 0
  let budget = Budget(arguments.Limits.Limit, arguments.Limits.Budget)
  budget.Charge(nodeCost output)

  // 所属ファイルと親（`CONTAINS` の逆向き）を添える。定義位置だけを返し、本文は含めない。
  let parents =
    view.Neighbors(index, Contains, Query.Incoming)
    |> Array.map (fun parent -> describe view parent 1)

  for parent in parents do
    budget.Charge(nodeCost parent)

  if arguments.Json then
    Commands.writeJson (fun writer ->
      writer.WriteString("command", "show")
      writer.WriteStartObject "query"
      writer.WriteString("kind", "show")
      writer.WriteString("node", arguments.Node)
      writer.WriteEndObject()
      writeNodes writer (Array.append [| output |] parents) false
      writer.WriteStartArray "edges"

      let target = output.Node

      for parent in parents do
        let source = parent.Node
        writer.WriteStartObject()
        writer.WriteString("from", NodeId.toString source.Id)
        writer.WriteString("to", NodeId.toString target.Id)
        writer.WriteString("kind", EdgeKind.name Contains)
        writer.WriteString("confidence", Confidence.name Extracted)
        writer.WriteEndObject()

      writer.WriteEndArray()
      writeCommon writer Array.empty budget)
  else
    writeTextNode output false

    for parent in parents do
      let node = parent.Node

      Terminal.outLine
        (Terminal.fitToWidth $"  含まれる: {NodeKind.name node.Kind} {Sanitize.forTerminal parent.QualifiedName}")

  Commands.ExitCode.Success

// --- neighbors ---------------------------------------------------------------

let neighbors (arguments: Args.NeighborsArguments) (cancellation: CancellationToken) : int =
  match openView arguments.OutputDirectory arguments.RootPath with
  | Error error ->
    Terminal.errLine (Query.QueryError.describe error)
    Commands.ExitCode.MissingArtifact
  | Ok view ->

  use view = view

  match resolveEdgeKinds view arguments.Edges with
  | Error name ->
    Terminal.errLine $"未知のエッジ種別です: {Sanitize.forTerminal name}"
    Commands.ExitCode.UserError
  | Ok kinds ->

  match Query.Direction.tryParse arguments.Direction with
  | ValueNone ->
    Terminal.errLine $"未知の向きです: {Sanitize.forTerminal arguments.Direction}"
    Commands.ExitCode.UserError
  | ValueSome direction ->

  match resolveNode view arguments.Node cancellation with
  | MissingNode ->
    Terminal.errLine $"ノードが見つかりません: {Sanitize.forTerminal arguments.Node}"
    Commands.ExitCode.NoResults
  | AmbiguousNode candidates ->
    reportAmbiguous view arguments.Node candidates
    Commands.ExitCode.NoResults
  | ResolvedNode index ->

  let traversal = Query.neighbors view index kinds direction arguments.Depth cancellation
  let budget = Budget(arguments.Limits.Limit, arguments.Limits.Budget)
  let accepted = ResizeArray<NodeOutput>()
  let kept = HashSet<int>()

  for struct (node, distance) in traversal.Nodes do
    let output = describe view node distance

    if budget.TryAdd(nodeCost output) then
      accepted.Add output
      kept.Add node |> ignore

  // 両端が残ったエッジだけを出す。片側だけのエッジは参照先を持たない。
  let edges =
    traversal.Edges
    |> Array.filter (fun edge -> kept.Contains edge.From && kept.Contains edge.To)

  for _ in edges do
    budget.Charge EdgeCost

  let diagnostics =
    if traversal.Truncated then
      [| $"訪問ノード数の上限 {Query.MaxVisitedNodes} に達したため探索を打ち切りました" |]
    else Array.empty

  if arguments.Json then
    Commands.writeJson (fun writer ->
      writer.WriteString("command", "neighbors")
      writer.WriteStartObject "query"
      writer.WriteString("kind", "neighbors")
      writer.WriteString("node", arguments.Node)
      writer.WriteNumber("depth", arguments.Depth)
      writer.WriteString("direction", Query.Direction.name direction)
      writer.WriteStartArray "edgeKinds"

      for kind in kinds do
        writer.WriteStringValue(EdgeKind.name kind)

      writer.WriteEndArray()
      writer.WriteEndObject()
      writer.WriteNumber("totalReachable", traversal.Nodes.Length)
      writeNodes writer accepted true
      writeEdges view writer edges
      writeCommon writer diagnostics budget)
  else
    for output in accepted do
      writeTextNode output true

    Terminal.errLine $"到達: {traversal.Nodes.Length} 件 / 表示: {accepted.Count} 件"

    for diagnostic in diagnostics do
      Terminal.errLine diagnostic

    reportTruncation budget

  if traversal.Nodes.Length <= 1 then Commands.ExitCode.NoResults else Commands.ExitCode.Success

// --- path --------------------------------------------------------------------

let path (arguments: Args.PathArguments) (cancellation: CancellationToken) : int =
  match openView arguments.OutputDirectory arguments.RootPath with
  | Error error ->
    Terminal.errLine (Query.QueryError.describe error)
    Commands.ExitCode.MissingArtifact
  | Ok view ->

  use view = view

  match resolveEdgeKinds view arguments.Edges with
  | Error name ->
    Terminal.errLine $"未知のエッジ種別です: {Sanitize.forTerminal name}"
    Commands.ExitCode.UserError
  | Ok kinds ->

  match Query.Direction.tryParse arguments.Direction with
  | ValueNone ->
    Terminal.errLine $"未知の向きです: {Sanitize.forTerminal arguments.Direction}"
    Commands.ExitCode.UserError
  | ValueSome direction ->

  let resolve (text: string) =
    match resolveNode view text cancellation with
    | ResolvedNode index -> Ok index
    | AmbiguousNode candidates ->
      reportAmbiguous view text candidates
      Error()
    | MissingNode ->
      Terminal.errLine $"ノードが見つかりません: {Sanitize.forTerminal text}"
      Error()

  match resolve arguments.From, resolve arguments.To with
  | Error(), _
  | _, Error() -> Commands.ExitCode.NoResults
  | Ok source, Ok target ->

  let found = Query.shortestPath view source target kinds direction arguments.Depth cancellation
  let budget = Budget(arguments.Limits.Limit, arguments.Limits.Budget)

  let outputs =
    match found with
    | ValueNone -> Array.empty
    | ValueSome indices -> indices |> Array.mapi (fun distance index -> describe view index distance)

  let accepted = ResizeArray<NodeOutput>()

  for output in outputs do
    if budget.TryAdd(nodeCost output) then accepted.Add output

  if arguments.Json then
    Commands.writeJson (fun writer ->
      writer.WriteString("command", "path")
      writer.WriteStartObject "query"
      writer.WriteString("kind", "path")
      writer.WriteString("from", arguments.From)
      writer.WriteString("to", arguments.To)
      writer.WriteNumber("depth", arguments.Depth)
      writer.WriteString("direction", Query.Direction.name direction)
      writer.WriteEndObject()
      writer.WriteBoolean("found", outputs.Length > 0)
      writer.WriteNumber("length", max 0 (outputs.Length - 1))
      writeNodes writer accepted true
      writer.WriteStartArray "edges"
      writer.WriteEndArray()
      writeCommon writer Array.empty budget)
  else if outputs.Length = 0 then
    Terminal.errLine $"深さ {arguments.Depth} 以内に経路が見つかりませんでした"
  else
    for output in accepted do
      writeTextNode output true

    reportTruncation budget

  if outputs.Length = 0 then Commands.ExitCode.NoResults else Commands.ExitCode.Success

// --- context -----------------------------------------------------------------

let context (arguments: Args.ContextArguments) (cancellation: CancellationToken) : int =
  match openView arguments.OutputDirectory arguments.RootPath with
  | Error error ->
    Terminal.errLine (Query.QueryError.describe error)
    Commands.ExitCode.MissingArtifact
  | Ok view ->

  use view = view
  let budget = Budget(arguments.Limits.Limit, arguments.Limits.Budget)
  let accepted = ResizeArray<NodeOutput>()
  let seen = HashSet<int>()
  let diagnostics = ResizeArray<string>()
  let mutable totalMatches = 0

  // 語ごとに順位付けし、上位から交互に採る。1 語が予算を食い尽くさないようにする。
  let ranked =
    arguments.Keywords
    |> Array.map (fun keyword ->
      let outcome = Query.search view keyword false cancellation
      let ordered = Array.copy outcome.Hits
      Array.sortInPlaceWith (Query.compareHits view) ordered
      totalMatches <- totalMatches + ordered.Length

      for diagnostic in indexDiagnostics outcome do
        if not (diagnostics.Contains diagnostic) then diagnostics.Add diagnostic

      ordered)

  let mutable position = 0
  let mutable exhausted = false

  while not exhausted do
    exhausted <- true

    for hits in ranked do
      if position < hits.Length then
        exhausted <- false
        let hit = hits[position]

        if seen.Add hit.Node then
          let output =
            { describe view hit.Node 0 with
                Match = Query.MatchStrength.name hit.Strength
                MatchedOn = Query.MatchTarget.name hit.Target }

          if budget.TryAdd(nodeCost output) then accepted.Add output

    position <- position + 1

  // 採用したノードの 1 ホップ近傍を、残った予算の範囲で足す。
  let edges = ResizeArray<Query.EdgeView>()
  let core = accepted |> Seq.map (fun output -> (output.Node).Index) |> Seq.toArray

  if arguments.Depth > 0 then
    for index in core do
      for kind in view.EdgeKinds do
        for next in view.Neighbors(index, kind, Query.Both) do
          if seen.Add next then
            let output = describe view next 1

            if budget.TryAdd(nodeCost output) then
              accepted.Add output
              edges.Add { From = index; To = next; Kind = kind }

  let orderedEdges = edges.ToArray()

  Array.sortInPlaceWith
    (fun (left: Query.EdgeView) (right: Query.EdgeView) ->
      let byFrom = compare left.From right.From

      if byFrom <> 0 then byFrom
      else
        let byKind = compare (EdgeKind.toCode left.Kind) (EdgeKind.toCode right.Kind)
        if byKind <> 0 then byKind else compare left.To right.To)
    orderedEdges

  for _ in orderedEdges do
    budget.Charge EdgeCost

  if arguments.Json then
    Commands.writeJson (fun writer ->
      writer.WriteString("command", "context")
      writer.WriteStartObject "query"
      writer.WriteString("kind", "context")
      writer.WriteStartArray "keywords"

      for keyword in arguments.Keywords do
        writer.WriteStringValue keyword

      writer.WriteEndArray()
      writer.WriteNumber("depth", arguments.Depth)
      writer.WriteNumber("budget", arguments.Limits.Budget)
      writer.WriteEndObject()
      writer.WriteNumber("totalMatches", totalMatches)
      writeNodes writer accepted true
      writeEdges view writer orderedEdges
      writeCommon writer (diagnostics.ToArray()) budget)
  else
    for output in accepted do
      writeTextNode output true

    Terminal.errLine $"一致: {totalMatches} 件 / 表示: {accepted.Count} 件"

    for diagnostic in diagnostics do
      Terminal.errLine diagnostic

    reportTruncation budget

  if accepted.Count = 0 then Commands.ExitCode.NoResults else Commands.ExitCode.Success
