/// Query commands share one bounded serializer and one artifact/error boundary.
module Srcnet.Cli.QueryCommands

open System
open System.Collections
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.Json
open System.Threading
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Storage
open Srcnet.Text

let TokenEstimateMethod = QueryOutput.TokenEstimateMethod
let estimateTokens text = QueryOutput.estimateTokens text

let writeError (kind: string) (code: int) (message: string) (json: bool) =
  QueryOutput.writeError kind code message json Args.DefaultBudget

let writeArgumentError (arguments: string[]) code message =
  let kind = if arguments.Length = 0 then "query" else arguments[0]
  let json = arguments |> Array.exists ((=) "--json")
  let mutable budget = Args.DefaultBudget
  for index in 0 .. arguments.Length - 1 do
    let value =
      if arguments[index].StartsWith("--budget=", StringComparison.Ordinal) then
        ValueSome(arguments[index].Substring 9)
      elif arguments[index] = "--budget" && index + 1 < arguments.Length then
        ValueSome arguments[index + 1]
      else ValueNone
    match value with
    | ValueSome text ->
      match Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
      | true, value when value >= Args.MinBudget && value <= Args.MaxBudget -> budget <- value
      | _ -> budget <- Args.DefaultBudget
    | ValueNone -> ()
  QueryOutput.writeError kind code message json budget

type Resolution =
  | ResolvedNode of index: int
  | AmbiguousNode of candidates: int[]
  | IncompleteNode of candidates: int[]
  | MissingNode

let internal resolveNode (view: Query.GraphView) (text: string) cancellation =
  match NodeId.tryParse text with
  | ValueSome id ->
    match view.TryResolve id with
    | ValueSome index -> ResolvedNode index
    | ValueNone -> MissingNode
  | ValueNone ->
    let outcome = Query.searchExactNames view text false cancellation
    let exact =
      outcome.Hits
      |> Array.filter (fun hit ->
        hit.Strength = Query.Exact && (hit.Target = Query.Name || hit.Target = Query.QualifiedName))
    Array.sortInPlaceWith (Query.compareHits view) exact
    let candidates = exact |> Array.map (fun hit -> hit.Node)
    if outcome.Truncated then IncompleteNode candidates
    else
      match candidates with
      | [||] -> MissingNode
      | [| node |] -> ResolvedNode node
      | nodes -> AmbiguousNode nodes

let private candidate index distance : QueryOutput.Candidate =
  { Index = index; Distance = distance; Match = ""; MatchedOn = "" }

let private hitCandidate (hit: Query.SearchHit) =
  { candidate hit.Node 0 with
      Match = Query.MatchStrength.name hit.Strength
      MatchedOn = Query.MatchTarget.name hit.Target }

let private response candidates edges total diagnostics : QueryOutput.Response =
  { Candidates = candidates
    Edges = edges
    TotalNodes = total
    OmittedEdges = 0
    Truncated = false
    LowerBound = false
    Diagnostics = diagnostics
    IncludeDistance = false
    WriteSummary = ignore
    Summary = $"候補: {total} 件" }

let private request kind (limits: Args.QueryLimits) json cancellation query : QueryOutput.Request =
  { Kind = kind
    Limits =
      { Limit = if limits.Limit = 0 then Args.DefaultLimit else limits.Limit
        Budget = if limits.Budget = 0 then Args.DefaultBudget else limits.Budget }
    Json = json
    WriteQuery = query
    Cancellation = cancellation }

let private withView
  (request: QueryOutput.Request)
  output
  root
  (texts: string[])
  (depth: int voption)
  (build: Query.GraphView -> QueryOutput.Response * int)
  =
  let limits = request.Limits
  let invalid =
    if limits.Limit < 1 || limits.Limit > Args.MaxLimit then
      ValueSome $"--limit は 1..{Args.MaxLimit} で指定してください"
    elif limits.Budget < Args.MinBudget || limits.Budget > Args.MaxBudget then
      ValueSome $"--budget は {Args.MinBudget}..{Args.MaxBudget} で指定してください"
    elif depth |> ValueOption.exists (fun value -> value < 0 || value > Args.MaxQueryDepth) then
      ValueSome $"--depth は 0..{Args.MaxQueryDepth} で指定してください"
    elif isNull(box texts) then
      ValueSome "照会文字列に null は指定できません"
    elif texts.Length = 0 then
      ValueSome "照会文字列が指定されていません"
    elif texts.Length > Args.MaxContextKeywords then
      ValueSome $"キーワードは {Args.MaxContextKeywords} 個までです"
    else
      texts
      |> Array.tryPick (fun text ->
        Args.validateQueryText "<query>" text |> ValueOption.toOption |> Option.map Args.ParseError.describe)
      |> ValueOption.ofOption

  let fail code message = QueryOutput.writeError request.Kind code message request.Json limits.Budget

  match invalid with
  | ValueSome message -> fail Commands.ExitCode.UserError message
  | ValueNone ->
    let located =
      try Ok(Commands.locateArtifact output root)
      with
      | :? ArgumentException as error -> Error error.Message
      | :? NotSupportedException as error -> Error error.Message
      | :? PathTooLongException as error -> Error error.Message
    match located with
    | Error message -> fail Commands.ExitCode.UserError message
    | Ok directory ->
      let describe error =
        (Query.QueryError.describe error).Replace(directory, "<artifact>", StringComparison.Ordinal).Replace('\\', '/')
      try
        request.Cancellation.ThrowIfCancellationRequested()
        match Query.GraphView.Open directory with
        | Error error -> fail Commands.ExitCode.MissingArtifact (describe error)
        | Ok graph ->
          use view = graph
          let result, code = build view
          QueryOutput.emit view request result code
      with
      | Query.QueryException error -> fail Commands.ExitCode.MissingArtifact (describe error)
      | :? OperationCanceledException -> fail Commands.ExitCode.Interrupted "中断しました"

let private takeRanked limit compareValues (values: 'T[]) =
  let result =
    if limit <= 0 then Array.empty
    elif values.Length <= limit then Array.copy values
    else
      let order = Comparer<'T>.Create(fun left right -> compareValues right left)
      let queue = PriorityQueue<'T, 'T>(order)
      for value in values do
        if queue.Count < limit then queue.Enqueue(value, value)
        elif compareValues value (queue.Peek()) < 0 then
          queue.Dequeue() |> ignore
          queue.Enqueue(value, value)
      Array.init queue.Count (fun _ -> queue.Dequeue())
  Array.sortInPlaceWith compareValues result
  result

let private compareCandidates (view: Query.GraphView) (left: QueryOutput.Candidate) (right: QueryOutput.Candidate) =
  let distance = compare left.Distance right.Distance
  if distance <> 0 then distance
  else
    let leftNode = view.Node left.Index
    let rightNode = view.Node right.Index
    let kind = compare (NodeKind.toCode leftNode.Kind) (NodeKind.toCode rightNode.Kind)
    if kind <> 0 then kind else compare leftNode.Id rightNode.Id

let private resolveKinds (view: Query.GraphView) (requested: string[]) =
  if requested.Length = 0 then Ok view.EdgeKinds
  else
    let mutable error = ValueNone
    let kinds = HashSet<EdgeKind>()
    for text in requested do
      match EdgeKind.all |> Array.tryFind (fun kind -> EdgeKind.name kind = text.ToUpperInvariant()) with
      | Some kind -> kinds.Add kind |> ignore
      | None -> if error.IsNone then error <- ValueSome $"未知のエッジ種別です: {text}"
    match error with
    | ValueSome message -> Error message
    | ValueNone -> Ok(kinds |> Seq.sortBy EdgeKind.toCode |> Seq.toArray)

let private resolveTraversal view edges direction =
  match resolveKinds view edges, Query.Direction.tryParse direction with
  | Error message, _ -> Error message
  | _, ValueNone -> Error $"未知の向きです: {direction}"
  | Ok kinds, ValueSome direction -> Ok(kinds, direction)

let private failure message code =
  response Array.empty Array.empty 0 [| message |], code

let private resolveRequired view text cancellation =
  match resolveNode view text cancellation with
  | ResolvedNode index -> Ok index
  | MissingNode -> Error(failure $"ノードが見つかりません: {text}" Commands.ExitCode.NoResults)
  | AmbiguousNode candidates ->
    let outputs = candidates |> Array.map (fun index -> candidate index 0)
    Error(
      response outputs Array.empty candidates.Length [| $"`{text}` は一意に決まりません。NodeId を指定してください" |],
      Commands.ExitCode.NoResults)
  | IncompleteNode candidates ->
    let outputs = candidates |> Array.map (fun index -> candidate index 0)
    let result =
      { response outputs Array.empty (candidates.Length + 1)
          [| "名前の照会が打ち切られ、一意性を確認できません。NodeId を指定してください" |] with
          Truncated = true
          LowerBound = true }
    Error(result, Commands.ExitCode.CompletedWithDiagnostics)

let private writeRequestedEdges (writer: Utf8JsonWriter) (edges: string[]) =
  writer.WriteStartArray "edgeKinds"
  for edge in edges |> Array.map (fun text -> text.ToUpperInvariant()) |> Array.distinct |> Array.sort do
    writer.WriteStringValue edge
  writer.WriteEndArray()

let search (arguments: Args.SearchArguments) cancellation =
  let req =
    request "search" arguments.Limits arguments.Json cancellation (fun writer ->
      writer.WriteString("text", Unicode.normalize arguments.Text)
      writer.WriteBoolean("ignoreCase", arguments.IgnoreCase))
  withView req arguments.OutputDirectory arguments.RootPath [| arguments.Text |] ValueNone (fun view ->
    let result = Query.search view arguments.Text arguments.IgnoreCase cancellation
    let hits = takeRanked req.Limits.Limit (Query.compareHits view) result.Hits
    let total = result.Hits.Length + result.OmittedCount
    let output =
      { response (Array.map hitCandidate hits) Array.empty total result.Diagnostics with
          Truncated = result.Truncated
          LowerBound = result.OmittedCountIsLowerBound
          WriteSummary = fun writer ->
            writer.WriteBoolean("usedIndex", result.UsedIndex)
            writer.WriteNumber("totalMatches", total)
          Summary = $"一致: {total} 件" }
    let code =
      if result.Truncated then Commands.ExitCode.CompletedWithDiagnostics
      elif total = 0 then Commands.ExitCode.NoResults
      else Commands.ExitCode.Success
    output, code)

let show (arguments: Args.ShowArguments) cancellation =
  let req =
    request "show" arguments.Limits arguments.Json cancellation (fun writer ->
      writer.WriteString("node", Unicode.normalize arguments.Node))
  withView req arguments.OutputDirectory arguments.RootPath [| arguments.Node |] ValueNone (fun view ->
    match resolveRequired view arguments.Node cancellation with
    | Error result -> result
    | Ok index ->
      let parents = Query.neighbors view index [| Contains |] Query.Incoming 1 cancellation
      let selected = ResizeArray<QueryOutput.Candidate>()
      let seen = HashSet<int>()
      let add index distance =
        if seen.Add index then selected.Add(candidate index distance)
      add index 0
      let fileIndex = (view.Node index).FileIndex
      let owner =
        if fileIndex = Format.NodeRecord.NoFile then ValueNone
        else view.FileNodeIndex fileIndex
      match owner with
      | ValueSome file -> add file 1
      | ValueNone -> ()
      let containing =
        parents.Nodes
        |> Array.filter (fun (struct (node, _)) -> not (seen.Contains node))
        |> Array.map (fun (struct (node, distance)) -> candidate node distance)
      let ranked = takeRanked req.Limits.Limit (compareCandidates view) containing
      for parent in ranked do add parent.Index parent.Distance
      let extraOwner =
        match owner with
        | ValueSome file when not (parents.Nodes |> Array.exists (fun (struct (node, _)) -> node = file)) -> 1
        | _ -> 0
      let total =
        if parents.Truncated then max selected.Count (parents.Nodes.Length + parents.OmittedCount)
        else parents.Nodes.Length + extraOwner
      { response (selected.ToArray()) parents.Edges total parents.Diagnostics with
          Truncated = parents.Truncated
          LowerBound = parents.OmittedCountIsLowerBound
          OmittedEdges = parents.OmittedEdgeCount },
      (if parents.Truncated then Commands.ExitCode.CompletedWithDiagnostics else Commands.ExitCode.Success))

let neighbors (arguments: Args.NeighborsArguments) cancellation =
  let req =
    request "neighbors" arguments.Limits arguments.Json cancellation (fun writer ->
      writer.WriteString("node", Unicode.normalize arguments.Node)
      writer.WriteNumber("depth", arguments.Depth)
      writer.WriteString("direction", arguments.Direction)
      writeRequestedEdges writer arguments.Edges)
  withView req arguments.OutputDirectory arguments.RootPath [| arguments.Node |] (ValueSome arguments.Depth) (fun view ->
    match resolveTraversal view arguments.Edges arguments.Direction with
    | Error message -> failure message Commands.ExitCode.UserError
    | Ok(kinds, direction) ->
      match resolveRequired view arguments.Node cancellation with
      | Error result -> result
      | Ok index ->
        let found = Query.neighbors view index kinds direction arguments.Depth cancellation
        let candidates = found.Nodes |> Array.map (fun (struct (node, distance)) -> candidate node distance)
        let selected = takeRanked req.Limits.Limit (compareCandidates view) candidates
        let total = found.Nodes.Length + found.OmittedCount
        { response selected found.Edges total found.Diagnostics with
            IncludeDistance = true
            Truncated = found.Truncated
            LowerBound = found.OmittedCountIsLowerBound
            OmittedEdges = found.OmittedEdgeCount
            WriteSummary = fun writer -> writer.WriteNumber("totalReachable", total)
            Summary = $"到達: {total} 件" },
        (if found.Truncated then Commands.ExitCode.CompletedWithDiagnostics else Commands.ExitCode.Success))

let path (arguments: Args.PathArguments) cancellation =
  let req =
    request "path" arguments.Limits arguments.Json cancellation (fun writer ->
      writer.WriteString("from", Unicode.normalize arguments.From)
      writer.WriteString("to", Unicode.normalize arguments.To)
      writer.WriteNumber("depth", arguments.Depth)
      writer.WriteString("direction", arguments.Direction)
      writeRequestedEdges writer arguments.Edges)
  withView req arguments.OutputDirectory arguments.RootPath [| arguments.From; arguments.To |] (ValueSome arguments.Depth) (fun view ->
    match resolveTraversal view arguments.Edges arguments.Direction with
    | Error message -> failure message Commands.ExitCode.UserError
    | Ok(kinds, direction) ->
      match resolveRequired view arguments.From cancellation with
      | Error result -> result
      | Ok source ->
        match resolveRequired view arguments.To cancellation with
        | Error result -> result
        | Ok target ->
          let found = Query.shortestPath view source target kinds direction arguments.Depth cancellation
          let foundPath = found.Path
          let indices = foundPath |> ValueOption.defaultValue Array.empty
          let candidates = indices |> Array.mapi (fun distance node -> candidate node distance)
          let output =
            { response candidates found.Edges (indices.Length + found.OmittedCount) found.Diagnostics with
                IncludeDistance = true
                Truncated = found.Truncated
                LowerBound = found.OmittedCountIsLowerBound
                WriteSummary = fun writer ->
                  writer.WriteBoolean("found", foundPath.IsSome)
                  writer.WriteBoolean("complete", not found.Truncated)
                  writer.WriteNumber("length", max 0 (indices.Length - 1))
                Summary =
                  if foundPath.IsSome then $"経路: {indices.Length - 1} ホップ"
                  elif found.Truncated then "探索の上限に達したため経路を確定できませんでした"
                  else $"深さ {arguments.Depth} 以内に経路が見つかりませんでした" }
          output,
          (if found.Truncated then Commands.ExitCode.CompletedWithDiagnostics
           elif foundPath.IsSome then Commands.ExitCode.Success
           else Commands.ExitCode.NoResults))

let context (arguments: Args.ContextArguments) cancellation =
  let req =
    request "context" arguments.Limits arguments.Json cancellation (fun writer ->
      writer.WriteStartArray "keywords"
      for keyword in arguments.Keywords do writer.WriteStringValue(Unicode.normalize keyword)
      writer.WriteEndArray()
      writer.WriteNumber("depth", arguments.Depth))
  withView req arguments.OutputDirectory arguments.RootPath arguments.Keywords (ValueSome arguments.Depth) (fun view ->
    if arguments.Keywords.Length = 0 then
      failure "キーワードが指定されていません" Commands.ExitCode.UserError
    else
      let coreLimit = if arguments.Depth = 0 then req.Limits.Limit else max 1 (req.Limits.Limit / 2)
      let known = BitArray(view.NodeCount)
      let mutable knownCount = 0
      let mark node =
        if not known[node] then
          known[node] <- true
          knownCount <- knownCount + 1
      let diagnostics = ResizeArray<string>()
      let addDiagnostics messages =
        for message in messages do
          if not (diagnostics.Contains message) then diagnostics.Add message
      let mutable totalLowerBound = 0
      let mutable truncated = false
      let mutable lowerBound = false
      let mutable matchedKeywords = 0
      let ranked =
        arguments.Keywords
        |> Array.map (fun keyword ->
          cancellation.ThrowIfCancellationRequested()
          let found = Query.search view keyword false cancellation
          for hit in found.Hits do mark hit.Node
          let total = found.Hits.Length + found.OmittedCount
          totalLowerBound <- max totalLowerBound total
          if total > 0 then matchedKeywords <- matchedKeywords + 1
          truncated <- truncated || found.Truncated
          lowerBound <- lowerBound || found.OmittedCountIsLowerBound || found.OmittedCount > 0
          addDiagnostics found.Diagnostics
          takeRanked coreLimit (Query.compareHits view) found.Hits)
      let totalMatches = max knownCount totalLowerBound
      let core = ResizeArray<QueryOutput.Candidate>()
      let selected = HashSet<int>()
      let mutable position = 0
      let mutable more = true
      while core.Count < coreLimit && more do
        more <- false
        for hits in ranked do
          if position < hits.Length then
            more <- true
            let hit = hits[position]
            if core.Count < coreLimit && selected.Add hit.Node then core.Add(hitCandidate hit)
        position <- position + 1

      let mutable edges = Array.empty
      let mutable omittedEdges = 0
      let mutable extra = Array.empty
      if arguments.Depth > 0 && core.Count > 0 then
        let starts = core |> Seq.map (fun value -> value.Index) |> Seq.toArray
        let nearby = Query.neighborsFrom view starts view.EdgeKinds Query.Both arguments.Depth cancellation
        let candidates = ResizeArray<QueryOutput.Candidate>()
        for struct (node, distance) in nearby.Nodes do
          mark node
          if not (selected.Contains node) then candidates.Add(candidate node distance)
        totalLowerBound <- max totalLowerBound (nearby.Nodes.Length + nearby.OmittedCount)
        truncated <- truncated || nearby.Truncated
        lowerBound <- lowerBound || nearby.OmittedCountIsLowerBound || nearby.OmittedCount > 0
        addDiagnostics nearby.Diagnostics
        edges <- nearby.Edges
        omittedEdges <- nearby.OmittedEdgeCount
        extra <- takeRanked (req.Limits.Limit - core.Count) (compareCandidates view) (candidates.ToArray())

      let candidates = Array.append (core.ToArray()) extra
      let total = max knownCount totalLowerBound
      { response candidates edges total (diagnostics.ToArray()) with
          IncludeDistance = true
          Truncated = truncated
          LowerBound = lowerBound
          OmittedEdges = omittedEdges
          WriteSummary = fun writer ->
            writer.WriteNumber("totalMatches", totalMatches)
            writer.WriteNumber("matchedKeywords", matchedKeywords)
            writer.WriteNumber("seedLimit", coreLimit)
          Summary = $"一致: {totalMatches} 件 / キーワード: {matchedKeywords}/{arguments.Keywords.Length}" },
      (if truncated then Commands.ExitCode.CompletedWithDiagnostics
       elif totalMatches = 0 then Commands.ExitCode.NoResults
       else Commands.ExitCode.Success))
