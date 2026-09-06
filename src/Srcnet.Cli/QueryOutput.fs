/// Bounded query serialization. Count the encoded envelope, not an estimate of its nodes.
module internal Srcnet.Cli.QueryOutput

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Storage
open Srcnet.Text

[<Literal>]
let TokenEstimateMethod = "ascii/4 + cjk*1 + other/2"

[<Struct>]
type private Tokens = { Ascii: int64; Cjk: int64; Other: int64 }

let private zero = { Ascii = 0L; Cjk = 0L; Other = 0L }

let private add left right =
  { Ascii = left.Ascii + right.Ascii
    Cjk = left.Cjk + right.Cjk
    Other = left.Other + right.Other }

let private count (text: string) =
  let mutable ascii = 0L
  let mutable cjk = 0L
  let mutable other = 0L
  let mutable runes = text.EnumerateRunes()

  while runes.MoveNext() do
    let scalar = runes.Current.Value
    if scalar < 128 then ascii <- ascii + 1L
    elif Words.isCjkScalar scalar then cjk <- cjk + 1L
    else other <- other + 1L

  { Ascii = ascii; Cjk = cjk; Other = other }

let private estimate tokens =
  int ((tokens.Ascii + 3L) / 4L + tokens.Cjk + (tokens.Other + 1L) / 2L)

let estimateTokens text = estimate (count text)

[<Struct>]
type Candidate =
  { Index: int
    Distance: int
    Match: string
    MatchedOn: string }

[<NoEquality; NoComparison>]
type Request =
  { Kind: string
    Limits: Args.QueryLimits
    Json: bool
    WriteQuery: Utf8JsonWriter -> unit
    Cancellation: CancellationToken }

[<NoEquality; NoComparison>]
type Response =
  { Candidates: Candidate[]
    Edges: Query.EdgeView[]
    TotalNodes: int
    OmittedEdges: int
    Truncated: bool
    LowerBound: bool
    Diagnostics: string[]
    IncludeDistance: bool
    WriteSummary: Utf8JsonWriter -> unit
    Summary: string }

type private CachedNode =
  { Node: Query.NodeView
    Json: string
    Text: string }

let private writeEmptyArrays (writer: Utf8JsonWriter) =
  writer.WriteStartArray "nodes"
  writer.WriteEndArray()
  writer.WriteStartArray "edges"
  writer.WriteEndArray()

let private writeOmissions
  (writer: Utf8JsonWriter)
  (nodes: int64)
  (edges: int64)
  (diagnostics: int)
  truncated
  lowerBound
  =
  writer.WriteBoolean("truncated", truncated)
  writer.WriteNumber("omittedCount", nodes + edges + int64 diagnostics)
  writer.WriteNumber("omittedNodeCount", nodes)
  writer.WriteNumber("omittedEdgeCount", edges)
  writer.WriteNumber("omittedDiagnosticCount", diagnostics)
  writer.WriteBoolean("omittedCountIsLowerBound", lowerBound)

let writeError (kind: string) (code: int) (message: string) (json: bool) (requestedBudget: int) =
  let kind =
    match kind with
    | "search" | "show" | "neighbors" | "path" | "context" | "export" -> kind
    | _ -> "query"

  let budget =
    if requestedBudget >= Args.MinBudget && requestedBudget <= Args.MaxBudget then requestedBudget
    else Args.DefaultBudget

  let runes = message.EnumerateRunes() |> Seq.toArray
  let mutable kept = min runes.Length Args.MaxQueryScalars

  let shortened () =
    if kept = runes.Length then message
    else
      let text = StringBuilder()
      let head = kept / 2
      for index in 0 .. head - 1 do text.Append(runes[index].ToString()) |> ignore
      text.Append '\u2026' |> ignore
      for index in runes.Length - (kept - head) .. runes.Length - 1 do
        text.Append(runes[index].ToString()) |> ignore
      text.ToString()

  let render () =
    let diagnostic = shortened ()
    if not json then Sanitize.forTerminal diagnostic + Terminal.Newline
    else
      let mutable tokens = 0
      let mutable payload = ""
      let mutable stable = false
      while not stable do
        payload <-
          Commands.renderJson false (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("schemaVersion", Commands.SchemaVersion)
            writer.WriteString("command", kind)
            writer.WriteStartObject "query"
            writer.WriteString("kind", kind)
            writer.WriteEndObject()
            writeEmptyArrays writer
            writer.WriteStartArray "diagnostics"
            writer.WriteStringValue diagnostic
            writer.WriteEndArray()
            writer.WriteNumber("exitCode", code)
            writeOmissions writer 0L 0L (if kept < runes.Length then 1 else 0) (kept < runes.Length) false
            writer.WriteBoolean("diagnosticsTruncated", kept < runes.Length)
            writer.WriteNumber("tokenEstimate", tokens)
            writer.WriteString("tokenEstimateMethod", TokenEstimateMethod)
            writer.WriteEndObject())
          + Terminal.Newline
        let measured = estimateTokens payload
        stable <- measured = tokens
        tokens <- measured
      payload

  let mutable output = render ()
  while estimateTokens output > budget && kept > 0 do
    kept <- kept / 2
    output <- render ()

  if estimateTokens output > budget then invalidOp "The minimum query budget cannot hold an error envelope"
  if json then Terminal.out output else Terminal.errLine(output.TrimEnd '\n')
  code

let private writeNode
  (writer: Utf8JsonWriter)
  (node: Query.NodeView)
  (name: string)
  (qualifiedName: string)
  (path: string)
  (candidate: Candidate)
  includeDistance
  =
  writer.WriteStartObject()
  writer.WriteString("id", NodeId.toString node.Id)
  writer.WriteString("kind", NodeKind.name node.Kind)
  writer.WriteString("name", name)
  writer.WriteString("qualifiedName", qualifiedName)
  writer.WriteString("path", path)
  writer.WriteString("language", Language.name node.Language)
  writer.WriteStartArray "lines"
  writer.WriteNumberValue node.StartLine
  writer.WriteNumberValue node.EndLine
  writer.WriteEndArray()
  writer.WriteNumber("ordinal", node.Ordinal)
  writer.WriteStartArray "flags"
  for flag in Commands.flagNames node.Flags do writer.WriteStringValue flag
  writer.WriteEndArray()
  if includeDistance then writer.WriteNumber("distance", candidate.Distance)
  if candidate.Match <> "" then writer.WriteString("match", candidate.Match)
  if candidate.MatchedOn <> "" then writer.WriteString("matchedOn", candidate.MatchedOn)
  writer.WriteEndObject()

let private writeEdge (writer: Utf8JsonWriter) source target kind =
  writer.WriteStartObject()
  writer.WriteString("from", NodeId.toString source)
  writer.WriteString("to", NodeId.toString target)
  writer.WriteString("kind", EdgeKind.name kind)
  writer.WriteString("confidence", Confidence.name Extracted)
  writer.WriteEndObject()

let emit (view: Query.GraphView) (request: Request) (response: Response) (exitCode: int) =
  let cached = ResizeArray<CachedNode>()
  let jsonPrefix = ResizeArray<Tokens>()
  let textPrefix = ResizeArray<Tokens>()
  jsonPrefix.Add zero
  textPrefix.Add zero
  let mutable exhausted = false
  let mutable decodedBytes = 0L

  // A scalar consumes at most eight UTF-8 bytes per estimated token. Reject an
  // oversized prefix node before decoding, rather than allocating its whole name.
  while not exhausted && cached.Count < min request.Limits.Limit response.Candidates.Length do
    request.Cancellation.ThrowIfCancellationRequested()
    let candidate = response.Candidates[cached.Count]
    let node = view.Node candidate.Index
    let pathRef =
      if node.Kind = Directory then node.QualifiedNameRef
      elif node.FileIndex = Format.NodeRecord.NoFile then 0
      else view.FilePathRef node.FileIndex
    let bytes =
      int64 (view.StringBytes node.NameRef).Length
      + int64 (view.StringBytes node.QualifiedNameRef).Length
      + int64 (view.StringBytes pathRef).Length

    if bytes > int64 request.Limits.Budget * 8L - decodedBytes then exhausted <- true
    else
      decodedBytes <- decodedBytes + bytes
      let name = view.String node.NameRef
      let qualified = view.String node.QualifiedNameRef
      let path = view.String pathRef
      let payload = Commands.renderJson false (fun writer ->
        writeNode writer node name qualified path candidate response.IncludeDistance)
      let location =
        if path = "" then ""
        elif node.StartLine > 0 then $" {path}:{node.StartLine}"
        else $" {path}"
      let flags =
        match Commands.flagNames node.Flags with
        | [||] -> ""
        | values -> " [" + String.Join(",", values) + "]"
      let distance = if response.IncludeDistance then $" (深さ {candidate.Distance})" else ""
      let matched = if candidate.Match = "" then "" else $" <{candidate.Match}>"
      let text =
        Terminal.fitToWidth $"{NodeKind.name node.Kind} {qualified}{location}{flags}{matched}{distance}"
        + Terminal.Newline
      cached.Add { Node = node; Json = payload; Text = text }
      jsonPrefix.Add(add jsonPrefix[jsonPrefix.Count - 1] (count payload))
      textPrefix.Add(add textPrefix[textPrefix.Count - 1] (count text))
      let used =
        if request.Json then jsonPrefix[jsonPrefix.Count - 1]
        else textPrefix[textPrefix.Count - 1]
      if estimate used > request.Limits.Budget then exhausted <- true

  let nodes = cached.ToArray()
  let positions = Dictionary<int, int>(nodes.Length)
  for index in 0 .. nodes.Length - 1 do positions.Add(nodes[index].Node.Index, index)

  let edgeCounts = Array.zeroCreate<int> (nodes.Length + 1)
  let edgeBytes = Array.zeroCreate<int64> (nodes.Length + 1)
  let edgeLengths = Dictionary<EdgeKind, int64>()
  let emptyId: NodeId = { High = 0UL; Low = 0UL }
  for index in 0 .. response.Edges.Length - 1 do
    if index &&& 0x3FFF = 0 then request.Cancellation.ThrowIfCancellationRequested()
    let edge = response.Edges[index]
    match positions.TryGetValue edge.From, positions.TryGetValue edge.To with
    | (true, source), (true, target) ->
      let required = max source target + 1
      let length =
        match edgeLengths.TryGetValue edge.Kind with
        | true, length -> length
        | false, _ ->
          let text = Commands.renderJson false (fun writer -> writeEdge writer emptyId emptyId edge.Kind)
          let length = int64 text.Length
          edgeLengths.Add(edge.Kind, length)
          length
      edgeCounts[required] <- edgeCounts[required] + 1
      edgeBytes[required] <- edgeBytes[required] + length
    | _ -> ()
  for index in 1 .. nodes.Length do
    edgeCounts[index] <- edgeCounts[index] + edgeCounts[index - 1]
    edgeBytes[index] <- edgeBytes[index] + edgeBytes[index - 1]

  let omissions kept =
    let omittedNodes = max 0L (int64 response.TotalNodes - int64 kept)
    let omittedEdges = int64 response.OmittedEdges + int64 response.Edges.Length - int64 edgeCounts[kept]
    struct (omittedNodes, omittedEdges, response.Truncated || omittedNodes > 0L || omittedEdges > 0L)

  let renderJson (kept: int) (tokens: int) (includeData: bool) =
    Commands.renderJson false (fun writer ->
      writer.WriteStartObject()
      writer.WriteNumber("schemaVersion", Commands.SchemaVersion)
      writer.WriteString("command", request.Kind)
      writer.WriteStartObject "query"
      writer.WriteString("kind", request.Kind)
      writer.WriteNumber("limit", request.Limits.Limit)
      writer.WriteNumber("budget", request.Limits.Budget)
      request.WriteQuery writer
      writer.WriteEndObject()
      response.WriteSummary writer
      writer.WriteStartArray "nodes"
      if includeData then
        for index in 0 .. kept - 1 do writer.WriteRawValue(nodes[index].Json, skipInputValidation = true)
      writer.WriteEndArray()
      writer.WriteStartArray "edges"
      if includeData then
        for index in 0 .. response.Edges.Length - 1 do
          if index &&& 0x3FFF = 0 then request.Cancellation.ThrowIfCancellationRequested()
          let edge = response.Edges[index]
          match positions.TryGetValue edge.From, positions.TryGetValue edge.To with
          | (true, source), (true, target) when source < kept && target < kept ->
            let sourceNode = nodes[source].Node
            let targetNode = nodes[target].Node
            writeEdge writer sourceNode.Id targetNode.Id edge.Kind
          | _ -> ()
      writer.WriteEndArray()
      writer.WriteStartArray "diagnostics"
      for diagnostic in response.Diagnostics do writer.WriteStringValue diagnostic
      writer.WriteEndArray()
      let struct (omittedNodes, omittedEdges, truncated) = omissions kept
      writeOmissions writer omittedNodes omittedEdges 0 truncated response.LowerBound
      writer.WriteNumber("exitCode", exitCode)
      writer.WriteNumber("tokenEstimate", tokens)
      writer.WriteString("tokenEstimateMethod", TokenEstimateMethod)
      writer.WriteEndObject())
    + Terminal.Newline

  let footer kept =
    let result = StringBuilder()
    result.Append(Sanitize.forTerminal response.Summary).Append($" / 表示: {kept} 件").Append('\n') |> ignore
    for diagnostic in response.Diagnostics do
      result.Append(Sanitize.forTerminal diagnostic).Append('\n') |> ignore
    let struct (omittedNodes, omittedEdges, truncated) = omissions kept
    if truncated then
      let qualifier = if response.LowerBound then "少なくとも " else ""
      result.Append($"打ち切り: {qualifier}{omittedNodes} ノード / {omittedEdges} エッジを省略しました\n") |> ignore
    result.ToString()

  let measure kept =
    if not request.Json then estimate (add textPrefix[kept] (count (footer kept)))
    else
      let commaBytes = int64 (max 0 (kept - 1) + max 0 (edgeCounts[kept] - 1))
      let content = add jsonPrefix[kept] { zero with Ascii = edgeBytes[kept] + commaBytes }
      let mutable tokens = 0
      let mutable stable = false
      while not stable do
        let measured = estimate (add content (count (renderJson kept tokens false)))
        stable <- measured = tokens
        tokens <- measured
      tokens

  if measure 0 > request.Limits.Budget then
    writeError request.Kind Commands.ExitCode.UserError
      "照会条件と診断が出力予算に収まりません。--budget を増やしてください"
      request.Json request.Limits.Budget
  else
    // A prefix retains ranking and path order. Induced edges only grow with it.
    let mutable low = 0
    let mutable high = nodes.Length
    while low < high do
      let middle = low + (high - low + 1) / 2
      if measure middle <= request.Limits.Budget then low <- middle
      else high <- middle - 1

    request.Cancellation.ThrowIfCancellationRequested()
    if request.Json then
      let tokens = measure low
      let payload = renderJson low tokens true
      if estimateTokens payload <> tokens || tokens > request.Limits.Budget then
        invalidOp "Query serialization exceeded its measured budget"
      Terminal.out payload
    else
      let output = StringBuilder()
      for index in 0 .. low - 1 do output.Append nodes[index].Text |> ignore
      let errors = footer low
      if estimateTokens (output.ToString() + errors) > request.Limits.Budget then
        invalidOp "Text query serialization exceeded its measured budget"
      Terminal.out (output.ToString())
      if errors <> "" then Terminal.errLine(errors.TrimEnd '\n')
    exitCode
