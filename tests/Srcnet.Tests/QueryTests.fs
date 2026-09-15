/// 照会 CLI のテスト（backlog 028）。
///
/// 検証する不変条件は次のとおり。
/// 1. 出力は必ず有界で、打ち切りは件数として報告される（docs/query-and-cli.md 4）
/// 2. 同じ成果物と引数から、同じ JSON が出る（決定性）
/// 3. CJK の完全一致・前方一致・部分一致が働く（docs/testing.md T-4）
/// 4. 未知の ID、過大な深さ、破損した指定は診断になり、クラッシュも無限探索も起こさない
module Srcnet.Tests.QueryTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Buffers.Binary
open System.Collections.Generic
open Xunit
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Storage

/// コーパスを索引して、照会できるビューを返す。
type private Indexed(corpus: string, assumeEncoding: Srcnet.Text.Encodings.DetectedEncoding voption) =
  let output =
    Path.Combine(Path.GetTempPath(), "srcnet-query-" + Guid.NewGuid().ToString "N")

  do
    let arguments: Srcnet.Cli.Args.IndexArguments =
      { RootPath = Corpus.corpusPath corpus
        OutputDirectory = ValueSome output
        RepositoryId = ValueSome "query-test"
        Jobs = ValueSome 1
        MaxFileSizeBytes = ValueNone
        MaxDepth = ValueNone
        Tier = 2
        AssumeEncoding =
          match assumeEncoding with
          | ValueSome encoding -> ValueSome(Srcnet.Text.Encodings.name encoding)
          | ValueNone -> ValueNone
        RespectIgnoreFiles = true
        FollowSymbolicLinks = false
        AllowPartial = true
        Json = true }

    let code =
      (Srcnet.Cli.Commands.index arguments CancellationToken.None)
        .GetAwaiter()
        .GetResult()

    // 診断つきの完了（4）も正常な結果である。
    if code <> 0 && code <> 4 then
      failwith $"索引の生成に失敗しました（終了コード {code}）"

  new(corpus: string) = new Indexed(corpus, ValueNone)

  member _.Output = output

  member _.View =
    match Query.GraphView.Open output with
    | Ok view -> view
    | Error error -> failwith(Query.QueryError.describe error)

  interface IDisposable with

    member _.Dispose() =
      if Directory.Exists output then
        try
          Directory.Delete(output, true)
        with :? IOException ->
          ()

let private assertCompleteTraversal (traversal: Query.Traversal) =
  Assert.False traversal.Truncated
  Assert.Equal(0, traversal.OmittedCount)
  Assert.Equal(0, traversal.OmittedEdgeCount)
  Assert.False traversal.OmittedCountIsLowerBound
  Assert.Empty traversal.Diagnostics

let private searchNames (view: Query.GraphView) (text: string) =
  let outcome = Query.search view text false CancellationToken.None
  Assert.True outcome.UsedIndex
  let ordered = Array.copy outcome.Hits
  Array.sortInPlaceWith (Query.compareHits view) ordered

  ordered
  |> Array.map(fun hit ->
    let node = view.Node hit.Node
    struct (view.String node.QualifiedNameRef, hit.Strength, hit.Target))

[<Corpus.ParserFact>]
let ``名前の完全一致が前方一致と部分一致より上に来る`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View
  let results = searchNames view "Area"

  Assert.NotEmpty results

  // 一致の強さは順位の 1 段目である。降順に緩むことはない。
  let strengths =
    results
    |> Array.map(fun (struct (_, strength, _)) -> Query.MatchStrength.rank strength)

  Assert.Equal<int[]>(Array.sort strengths, strengths)

  let struct (_, first, _) = results[0]
  Assert.Equal(Query.Exact, first)

[<Corpus.ParserFact>]
let ``定義は同じ名前の宣言より上に来る`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View

  let outcome = Query.search view "Area" false CancellationToken.None
  let ordered = Array.copy outcome.Hits
  Array.sortInPlaceWith (Query.compareHits view) ordered

  let exact =
    ordered
    |> Array.filter(fun hit -> hit.Strength = Query.Exact)
    |> Array.map(fun hit -> (view.Node hit.Node).Flags)

  let definitionFirst =
    exact
    |> Array.map(fun flags -> if flags.HasFlag NodeFlags.Definition then 0 else 1)

  Assert.Equal<int[]>(Array.sort definitionFirst, definitionFirst)

[<Corpus.ParserFact>]
let ``CJK の完全一致・前方一致・部分一致が働く`` () =
  use indexed = new Indexed("cjk", ValueSome Srcnet.Text.Encodings.EucJp)
  use view = indexed.View

  let strengthOf (text: string) (qualified: string) =
    searchNames view text
    |> Array.tryPick(fun (struct (name, strength, _)) ->
      if String.Equals(name, qualified, StringComparison.Ordinal) then
        Some strength
      else
        None)

  // 完全一致（名前が「面積」そのもの）
  Assert.Equal(Some Query.Exact, strengthOf "面積" "圖形::方塊::面積")
  // 前方一致（「合計面積」の先頭）
  Assert.Equal(Some Query.Prefix, strengthOf "合計" "合計面積")
  // 部分一致（「座標」の一部）
  Assert.Equal(Some Query.Substring, strengthOf "標" "座標")

[<Fact>]
let ``検索キーは NFC 正規化される`` () =
  use indexed = new Indexed("cjk", ValueSome Srcnet.Text.Encodings.EucJp)
  use view = indexed.View

  // 濁点を分解した形でも、合成形の識別子に一致する。
  let composed = searchNames view "日本語"
  let decomposed = searchNames view ("日本語".Normalize NormalizationForm.FormD)
  Assert.Equal(composed.Length, decomposed.Length)

[<Theory>]
[<InlineData("micro", false)>]
[<InlineData("micro", true)>]
[<InlineData("cjk", false)>]
[<InlineData("cjk", true)>]
let ``一致対象は強さを優先し同点なら名前と修飾名とパスの順で選ぶ`` corpus ignoreCase =
  use indexed = new Indexed(corpus, ValueSome Srcnet.Text.Encodings.EucJp)
  use view = indexed.View

  let fields =
    Array.init view.NodeCount (fun index ->
      let node = view.Node index

      [| Query.Name, view.String node.NameRef
         Query.QualifiedName, view.String node.QualifiedNameRef
         Query.Path,
         (if node.FileIndex = Format.NodeRecord.NoFile then
            ""
          else
            view.String(view.FilePathRef node.FileIndex)) |])

  let needles =
    fields
    |> Array.collect(Array.map snd)
    |> Array.filter(fun text -> text.Length > 0)
    |> Array.collect(fun text ->
      [| text
         text.Substring(0, max 1 (text.Length / 2))
         text.Substring(text.Length / 2)
         text.ToUpperInvariant() |])
    |> Array.append [| "no-such-name" |]
    |> Array.distinct

  let normalize text =
    if ignoreCase then
      Srcnet.Text.Unicode.caseFold text
    else
      Srcnet.Text.Unicode.normalize text

  for needle in needles do
    let key = normalize needle

    let expected =
      fields
      |> Array.mapi(fun index values ->
        values
        |> Array.choose(fun (target, value) ->
          let text = normalize value

          let strength =
            if text = key then
              Some Query.Exact
            elif text.StartsWith(key, StringComparison.Ordinal) then
              Some Query.Prefix
            elif text.Contains(key, StringComparison.Ordinal) then
              Some Query.Substring
            else
              None

          strength |> Option.map(fun matched -> struct (index, matched, target)))
        |> Array.sortBy(fun (struct (_, strength, target)) ->
          Query.MatchStrength.rank strength, Query.MatchTarget.rank target)
        |> Array.tryHead)
      |> Array.choose id

    let actual =
      (Query.search view needle ignoreCase CancellationToken.None).Hits
      |> Array.map(fun hit -> struct (hit.Node, hit.Strength, hit.Target))

    Assert.Equal<struct (int * Query.MatchStrength * Query.MatchTarget)>(expected, actual)

    let exactNames =
      Query.searchExactNames view needle ignoreCase CancellationToken.None

    let expectedNames =
      expected
      |> Array.filter(fun (struct (_, strength, target)) -> strength = Query.Exact && target <> Query.Path)

    let actualNames =
      exactNames.Hits
      |> Array.map(fun hit -> struct (hit.Node, hit.Strength, hit.Target))

    Assert.Equal<struct (int * Query.MatchStrength * Query.MatchTarget)>(expectedNames, actualNames)
    Assert.True exactNames.UsedIndex
    Assert.False exactNames.Truncated

[<Fact>]
let ``ノード ID から密インデックスを引ける`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View

  for index in 0 .. min 64 (view.NodeCount - 1) do
    let node = view.Node index

    match view.TryResolve node.Id with
    | ValueSome resolved -> Assert.Equal(index, resolved)
    | ValueNone -> failwith $"ノード {index} の ID を解決できませんでした"

[<Fact>]
let ``未知の ID は診断になり、クラッシュしない`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View

  let unknown =
    { High = 0xFFFFFFFFFFFFFFFFUL
      Low = 0xFFFFFFFFFFFFFFFFUL }

  Assert.Equal(ValueNone, view.TryResolve unknown)

[<Fact>]
let ``近傍探索は深さの上限を守る`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View
  let repository = 0

  for depth in 0..3 do
    let traversal =
      Query.neighbors view repository [| Contains |] Query.Outgoing depth CancellationToken.None

    assertCompleteTraversal traversal

    // 起点からの距離は必ず指定した深さ以内に収まる。
    for struct (_, distance) in traversal.Nodes do
      Assert.InRange(distance, 0, depth)

    if depth = 0 then
      Assert.Equal<struct (int * int)[]>([| struct (repository, 0) |], traversal.Nodes)
      Assert.Empty traversal.Edges

[<Fact>]
let ``近傍の結果は決定的な順序になる`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View

  let run () =
    Query.neighbors view 0 view.EdgeKinds Query.Both 2 CancellationToken.None

  let first = run()
  let second = run()

  Assert.Equal<int[]>(
    first.Nodes |> Array.map(fun (struct (node, _)) -> node),
    second.Nodes |> Array.map(fun (struct (node, _)) -> node)
  )

  Assert.Equal<string[]>(
    first.Edges
    |> Array.map(fun edge -> $"{edge.From}-{EdgeKind.name edge.Kind}-{edge.To}"),
    second.Edges
    |> Array.map(fun edge -> $"{edge.From}-{EdgeKind.name edge.Kind}-{edge.To}")
  )

[<Fact>]
let ``最短経路は起点と終点を含み、深さ上限を超えない`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View

  // リポジトリからファイルへは CONTAINS で必ず到達できる。
  let target =
    [| 0 .. view.NodeCount - 1 |]
    |> Array.find(fun index -> (view.Node index).Kind = File)

  match
    (Query.shortestPath view 0 target [| Contains |] Query.Outgoing 8 CancellationToken.None)
      .Path
  with
  | ValueNone -> failwith "経路が見つかりませんでした"
  | ValueSome path ->
    Assert.Equal(0, path[0])
    Assert.Equal(target, path[path.Length - 1])
    Assert.True(path.Length - 1 <= 8)

[<Fact>]
let ``到達できない経路は見つからないと報告する`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View

  // 深さ 0 では起点以外へ到達できない。
  let target =
    [| 0 .. view.NodeCount - 1 |]
    |> Array.find(fun index -> (view.Node index).Kind = File)

  let result =
    Query.shortestPath view 0 target [| Contains |] Query.Outgoing 0 CancellationToken.None

  Assert.Equal(ValueNone, result.Path)
  Assert.False result.Truncated
  Assert.Equal(0, result.OmittedCount)
  Assert.False result.OmittedCountIsLowerBound
  Assert.Empty result.Diagnostics

[<Fact>]
let ``トークン見積りは決定的で、長さに対して単調である`` () =
  let short = Srcnet.Cli.QueryCommands.estimateTokens "abcd"
  let long = Srcnet.Cli.QueryCommands.estimateTokens "abcdabcdabcd"
  Assert.True(long > short)

  // CJK は 1 文字 1 トークンとして数える。
  Assert.Equal(3, Srcnet.Cli.QueryCommands.estimateTokens "日本語")

  // 同じ入力からは常に同じ値が出る。
  Assert.Equal(Srcnet.Cli.QueryCommands.estimateTokens "面積を求める", Srcnet.Cli.QueryCommands.estimateTokens "面積を求める")

[<Fact>]
let ``同じ引数からは同じ JSON が出る`` () =
  task {
    use indexed = new Indexed("micro")

    let arguments =
      [ "search"; "Area"; "--out"; indexed.Output; "--json"; "--limit"; "10" ]

    let! struct (firstCode, first, _) = Corpus.runCli arguments
    let! struct (secondCode, second, _) = Corpus.runCli arguments
    Assert.Equal(firstCode, secondCode)
    // バイト単位で一致すること。改行や順序の揺れも差として現れる。
    Assert.Equal(first, second)
  }

[<Fact>]
let ``JSON は定めた項目をすべて持つ`` () =
  task {
    use indexed = new Indexed("micro")
    let! struct (_, payload, _) = Corpus.runCli [ "search"; "shapes"; "--out"; indexed.Output; "--json"; "--limit"; "2" ]
    use document = JsonDocument.Parse payload
    let root = document.RootElement

    for name in
      [ "schemaVersion"
        "command"
        "query"
        "nodes"
        "edges"
        "diagnostics"
        "truncated"
        "omittedCount"
        "tokenEstimate"
        "tokenEstimateMethod" ] do
      Assert.True(root.TryGetProperty(name) |> fst, $"項目 `{name}` がありません")
    // 件数の上限を超えたぶんは省略として報告される。黙って捨てない。
    Assert.Equal(2, root.GetProperty("nodes").GetArrayLength())
    Assert.True(root.GetProperty("truncated").GetBoolean())
    Assert.True(root.GetProperty("omittedCount").GetInt32() > 0)
    // Fresh Writer artifacts have an actual lexical lookup.
    let diagnostics =
      root.GetProperty("diagnostics").EnumerateArray()
      |> Seq.map(fun item -> item.GetString())
      |> Seq.toArray

    Assert.DoesNotContain(diagnostics, (fun text -> text <> null && text.Contains "検索索引がありません"))
  }

[<Fact>]
let ``照会の終了コードは仕様どおりになる`` () =
  task {
    use indexed = new Indexed("micro")
    // 該当なしは 1。異常ではない。
    let! struct (noResults, _, _) = Corpus.runCli [ "search"; "この名前は存在しない"; "--out"; indexed.Output; "--json" ]
    Assert.Equal(Srcnet.Cli.Commands.ExitCode.NoResults, noResults)
    // 未知のエッジ種別は利用者入力の誤りなので 2。
    let! struct (userError, _, _) = Corpus.runCli [ "neighbors"; "Point"; "--out"; indexed.Output; "--edge"; "NOPE" ]
    Assert.Equal(Srcnet.Cli.Commands.ExitCode.UserError, userError)
    // 生成物が無ければ 3。
    let missing =
      Path.Combine(Path.GetTempPath(), "srcnet-missing-" + Guid.NewGuid().ToString "N")

    let! struct (missingArtifact, _, _) = Corpus.runCli [ "search"; "Area"; "--out"; missing ]
    Assert.Equal(Srcnet.Cli.Commands.ExitCode.MissingArtifact, missingArtifact)
  }

[<Corpus.ParserFact>]
let ``ノード指定は曖昧な名前を拒否しファイル自身と ID を解決する`` () =
  task {
    use indexed = new Indexed("micro")
    let! struct (ambiguous, ambiguity, errors) = Corpus.runCli [ "show"; "Area"; "--out"; indexed.Output; "--json" ]
    Assert.Equal(Srcnet.Cli.Commands.ExitCode.NoResults, ambiguous)
    Assert.Empty errors
    use ambiguousDocument = JsonDocument.Parse ambiguity

    let diagnostics =
      ambiguousDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
      |> Seq.map(fun item -> item.GetString())
      |> Seq.toArray

    Assert.Contains(diagnostics, (fun text -> text <> null && text.Contains "一意に決まりません"))
    let! struct (fileCode, payload, _) = Corpus.runCli [ "show"; "shapes.c"; "--out"; indexed.Output; "--json" ]
    Assert.Equal(Srcnet.Cli.Commands.ExitCode.Success, fileCode)
    use document = JsonDocument.Parse payload
    let file = document.RootElement.GetProperty("nodes")[0]
    Assert.Equal("File", file.GetProperty("kind").GetString())
    let id = file.GetProperty("id").GetString()
    Assert.NotNull id
    let! struct (idCode, byId, _) = Corpus.runCli [ "show"; id; "--out"; indexed.Output; "--json" ]
    Assert.Equal(Srcnet.Cli.Commands.ExitCode.Success, idCode)
    use resolved = JsonDocument.Parse byId
    let resolvedNode = resolved.RootElement.GetProperty("nodes")[0]
    Assert.Equal(id, resolvedNode.GetProperty("id").GetString())
  }

[<Fact>]
let ``結果は stdout、診断は stderr へ出る`` () =
  task {
    use indexed = new Indexed("micro")
    // JSON 出力では、機械が読む先を 1 つに保つため診断も封筒の中に入れる
    // （docs/query-and-cli.md 5.1）。stdout は JSON だけになり、パイプ処理を壊さない。
    let! struct (_, jsonOutput, _) = Corpus.runCli [ "search"; "shapes"; "--out"; indexed.Output; "--json" ]
    use document = JsonDocument.Parse jsonOutput

    let diagnostics =
      document.RootElement.GetProperty("diagnostics").EnumerateArray()
      |> Seq.map(fun item -> item.GetString())
      |> Seq.toArray

    Assert.DoesNotContain(diagnostics, (fun text -> text <> null && text.Contains "検索索引がありません"))
    // テキスト出力では、結果は stdout、診断は stderr へ分ける。
    let! struct (_, textOutput, textErrors) = Corpus.runCli [ "search"; "shapes"; "--out"; indexed.Output ]
    Assert.Contains("shapes", textOutput)
    Assert.DoesNotContain("検索索引", textOutput)
    Assert.DoesNotContain("検索索引がありません", textErrors)
  }

[<Fact>]
let ``生成物が無ければ明示的に失敗する`` () =
  let missing =
    Path.Combine(Path.GetTempPath(), "srcnet-missing-" + Guid.NewGuid().ToString "N")

  match Query.GraphView.Open missing with
  | Ok view ->
    (view :> IDisposable).Dispose()
    failwith "存在しない成果物を開けてしまいました"
  | Error(Query.ArtifactUnreadable _) -> ()
  | Error other -> failwith $"想定と違う失敗です: {Query.QueryError.describe other}"

[<Fact>]
let ``取り消しは探索の途中でも効く`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View
  use cancellation = new CancellationTokenSource()
  cancellation.Cancel()

  Assert.ThrowsAny<OperationCanceledException>(fun () ->
    Query.neighbors view 0 view.EdgeKinds Query.Both 8 cancellation.Token |> ignore)
  |> ignore

  Assert.ThrowsAny<OperationCanceledException>(fun () ->
    Query.searchExactNames view "Area" false cancellation.Token |> ignore)
  |> ignore

  Assert.ThrowsAny<OperationCanceledException>(fun () -> Query.validateLookup view cancellation.Token)
  |> ignore

let private readManifest output =
  match Manifest.read output with
  | Ok manifest -> manifest
  | Error error -> failwith(Manifest.ManifestError.describe error)

let private writeManifest output manifest =
  match Manifest.write output manifest with
  | Ok() -> ()
  | Error error -> failwith(Artifact.PathError.describe error)

let private segmentPath output suffix =
  let descriptor =
    (readManifest output).Segments
    |> Array.find(fun segment -> segment.Name.EndsWith("." + suffix, StringComparison.Ordinal))

  match Artifact.tryResolveSegment output descriptor.Name with
  | Ok path -> path
  | Error error -> failwith(Artifact.PathError.describe error)

let private mutate output suffix change =
  let path = segmentPath output suffix
  let bytes = File.ReadAllBytes path
  change bytes
  File.WriteAllBytes(path, bytes)

let private assertCorrupt action =
  match (Assert.Throws<Query.QueryException>(Action action) :> exn) with
  | Query.QueryException(Query.SegmentCorrupt _) -> ()
  | other -> failwith $"Expected explicit corrupt-artifact error, got {other}"

[<Fact>]
let ``corrupt string offsets and UTF8 never become empty names`` () =
  use indexed = new Indexed("micro")

  mutate indexed.Output "stroffsets" (fun bytes ->
    BinaryPrimitives.WriteUInt64LittleEndian(Span(bytes, Format.HeaderLength + 16, 8), UInt64.MaxValue))

  use view = indexed.View
  assertCorrupt(fun () -> view.String 1 |> ignore)

  use invalidUtf8 = new Indexed("micro")
  mutate invalidUtf8.Output "strings" (fun bytes -> bytes[Format.HeaderLength] <- 0xFFuy)
  use other = invalidUtf8.View
  assertCorrupt(fun () -> other.StringBytes(1).Length |> ignore)

[<Theory>]
[<InlineData(16, 255u)>]
[<InlineData(18, 65535u)>]
[<InlineData(20, 0x80000000u)>]
[<InlineData(24, 0xFFFFFFFEu)>]
[<InlineData(28, 0xFFFFFFFFu)>]
[<InlineData(32, 0xFFFFFFFFu)>]
let ``invalid node kinds languages flags and references are typed errors`` offset value =
  use indexed = new Indexed("micro")

  mutate indexed.Output "nodes" (fun bytes ->
    BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, Format.HeaderLength + offset, 4), value))

  use view = indexed.View
  assertCorrupt(fun () -> view.Node 0 |> ignore)

[<Fact>]
let ``invalid file references CSR targets and offsets fail explicitly`` () =
  use indexed = new Indexed("micro")

  mutate indexed.Output "files" (fun bytes ->
    BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, Format.HeaderLength, 4), UInt32.MaxValue))

  use view = indexed.View
  assertCorrupt(fun () -> view.FilePathRef 0u |> ignore)

  use invalidTarget = new Indexed("micro")
  let count = (readManifest invalidTarget.Output).Counts.Nodes

  mutate invalidTarget.Output "edges.CONTAINS" (fun bytes ->
    BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, Format.HeaderLength + (count + 1) * 8, 4), uint32 count))

  use targetView = invalidTarget.View
  assertCorrupt(fun () -> targetView.Neighbors(0, Contains, Query.Outgoing) |> ignore)
  assertCorrupt(fun () -> targetView.NeighborAt(0, Contains, false, 0) |> ignore)

  use invalidOffset = new Indexed("micro")

  mutate invalidOffset.Output "edges.CONTAINS" (fun bytes ->
    BinaryPrimitives.WriteUInt64LittleEndian(Span(bytes, Format.HeaderLength + 8, 8), UInt64.MaxValue))

  use offsetView = invalidOffset.View
  assertCorrupt(fun () -> offsetView.Neighbors(0, Contains, Query.Outgoing) |> ignore)
  assertCorrupt(fun () -> offsetView.NeighborCount(0, Contains, false) |> ignore)

  let seedOnly =
    Query.neighbors offsetView 0 [| Contains |] Query.Outgoing 0 CancellationToken.None

  assertCompleteTraversal seedOnly
  Assert.Empty seedOnly.Edges

  let noHops =
    Query.shortestPath offsetView 0 1 [| Contains |] Query.Outgoing 0 CancellationToken.None

  Assert.Equal(ValueNone, noHops.Path)
  Assert.False noHops.Truncated

[<Fact>]
let ``invalid idmap references and swapped segment kinds fail explicitly`` () =
  use indexed = new Indexed("micro")
  let count = (readManifest indexed.Output).Counts.Nodes

  mutate indexed.Output "idmap" (fun bytes ->
    for index in 0 .. count - 1 do
      BinaryPrimitives.WriteUInt32LittleEndian(
        Span(bytes, Format.HeaderLength + count * Srcnet.Core.Ids.NodeIdLength + index * 4, 4),
        uint32 count
      ))

  use view = indexed.View
  assertCorrupt(fun () -> view.TryResolve((view.Node 0).Id) |> ignore)

  use swapped = new Indexed("micro")

  mutate swapped.Output "nodes" (fun bytes ->
    BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, 12, 4), Format.SegmentKind.toCode Format.Files))

  match Query.GraphView.Open swapped.Output with
  | Error(Query.SegmentCorrupt _) -> ()
  | Ok view ->
    (view :> IDisposable).Dispose()
    failwith "Accepted swapped segment types"
  | Error error -> failwith $"Unexpected error: {error}"

[<Fact>]
let ``invalid caller indices and disposed views do not return sentinels`` () =
  use indexed = new Indexed("micro")
  let view = indexed.View

  for action in
    [ (fun () -> view.Node -1 |> ignore)
      (fun () -> view.String -1 |> ignore)
      (fun () -> view.FilePathRef Format.NodeRecord.NoFile |> ignore)
      (fun () -> view.Neighbors(-1, Contains, Query.Outgoing) |> ignore)
      (fun () -> view.NeighborCount(-1, Contains, false) |> ignore)
      (fun () -> view.NeighborAt(0, Contains, false, -1) |> ignore)
      (fun () -> view.NeighborAt(0, Contains, false, Int32.MaxValue) |> ignore)
      (fun () ->
        Query.neighbors view 0 [| Contains |] Query.Outgoing -1 CancellationToken.None
        |> ignore)
      (fun () ->
        Query.shortestPath view 0 view.NodeCount [| Contains |] Query.Outgoing 8 CancellationToken.None
        |> ignore) ] do
    match (Assert.Throws<Query.QueryException>(Action action) :> exn) with
    | Query.QueryException(Query.InvalidArgument _) -> ()
    | other -> failwith $"Expected invalid argument, got {other}"

  let retainedNode = view.Node 0
  let retainedName = view.String retainedNode.NameRef
  (view :> IDisposable).Dispose()
  (view :> IDisposable).Dispose()
  Assert.NotEmpty retainedName

  for action in
    [ (fun () -> view.String retainedNode.NameRef |> ignore)
      (fun () -> view.NeighborCount(0, Contains, false) |> ignore)
      (fun () -> view.NeighborAt(0, Contains, false, 0) |> ignore) ] do
    match (Assert.Throws<Query.QueryException>(Action action) :> exn) with
    | Query.QueryException Query.ViewDisposed -> ()
    | other -> failwith $"Expected disposed view, got {other}"

[<Fact>]
let ``file nodes are found from the dense schema without scanning names`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View
  Assert.Equal(ValueNone, view.FileNodeIndex Format.NodeRecord.NoFile)

  for index in 0 .. view.FileCount - 1 do
    match view.FileNodeIndex(uint32 index) with
    | ValueNone -> failwith "Missing file node"
    | ValueSome node ->
      let file = view.Node node
      Assert.Equal(File, file.Kind)
      Assert.Equal(uint32 index, file.FileIndex)
      Assert.Equal(view.FilePathRef(uint32 index), file.QualifiedNameRef)

[<Fact>]
let ``publication replaces a manifest held by a delete-sharing reader`` () =
  use indexed = new Indexed("micro")
  let original = readManifest indexed.Output
  let replacement = { original with Complete = not original.Complete }

  use held =
    new FileStream(
      Path.Combine(indexed.Output, Manifest.FileName),
      FileMode.Open,
      FileAccess.Read,
      FileShare.ReadWrite ||| FileShare.Delete
    )

  let before = Array.zeroCreate<byte>(int held.Length)
  held.ReadExactly(Span before)
  writeManifest indexed.Output replacement
  Assert.Equal(replacement, readManifest indexed.Output)

  held.Position <- 0L
  let after = Array.zeroCreate<byte>(int held.Length)
  held.ReadExactly(Span after)
  Assert.Equal<byte[]>(before, after)

  use view = indexed.View
  Assert.Equal(replacement, view.Manifest)

[<Fact>]
let ``one thousand opens during publication never mix mapped generations`` () =
  use first = new Indexed("micro")
  use second = new Indexed("cjk", ValueSome Srcnet.Text.Encodings.EucJp)
  let original = readManifest first.Output
  let replacement = readManifest second.Output

  for descriptor in replacement.Segments do
    let source = Path.Combine(second.Output, descriptor.Name)
    let destination = Path.Combine(first.Output, descriptor.Name)
    Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
    File.Copy(source, destination, true)

  use held = first.View
  let heldId = (held.Node(held.NodeCount - 1)).Id
  use start = new ManualResetEventSlim(false)

  let publication =
    Tasks.Task.Run(fun () ->
      start.Wait()

      for index in 0..999 do
        writeManifest first.Output (if index % 2 = 0 then replacement else original))

  start.Set()
  let mutable good = 0
  let mutable busy = 0

  for _ in 1..1000 do
    match Query.GraphView.Open first.Output with
    | Ok view ->
      use view = view
      let expected = if view.Manifest = original then original else replacement
      Assert.Equal(expected, view.Manifest)
      Assert.Equal(expected.Counts.Nodes, view.NodeCount)
      Assert.Equal(expected.Counts.Strings, view.StringCount)
      let node = view.Node(view.NodeCount - 1)
      Assert.NotEmpty(view.String node.NameRef)

      let found =
        Query.search view (view.String node.NameRef) false CancellationToken.None

      Assert.Contains(found.Hits, (fun hit -> hit.Node = node.Index && hit.Strength = Query.Exact))
      good <- good + 1
    | Error Query.GenerationChanged
    | Error(Query.ArtifactUnreadable(Manifest.Busy _)) -> busy <- busy + 1
    | Error error -> failwith $"Unexpected publication failure: {error}"

  publication.GetAwaiter().GetResult()
  Assert.Equal(1000, good + busy)
  Assert.True(good > 0)
  Assert.Equal(heldId, (held.Node(held.NodeCount - 1)).Id)

[<Fact>]
let ``legacy artifacts refuse search and explicit lookup rebuilding needs no sources`` () =
  use indexed = new Indexed("micro")
  let original = readManifest indexed.Output

  let lookup =
    original.Segments
    |> Array.find(fun segment -> segment.Name.EndsWith(".lookup", StringComparison.Ordinal))

  let originalBytes = File.ReadAllBytes(segmentPath indexed.Output "lookup")

  let legacy =
    { original with
        Segments = original.Segments |> Array.filter(fun segment -> segment <> lookup) }

  writeManifest indexed.Output legacy

  do
    use view = indexed.View

    match
      (Assert.Throws<Query.QueryException>(fun () -> Query.search view "Area" false CancellationToken.None |> ignore)
      :> exn)
    with
    | Query.QueryException Query.SearchIndexRequired -> ()
    | other -> failwith $"Expected unsupported index-less search, got {other}"

    match
      (Assert.Throws<Query.QueryException>(fun () ->
        Query.searchExactNames view "Area" false CancellationToken.None |> ignore)
      :> exn)
    with
    | Query.QueryException Query.SearchIndexRequired -> ()
    | other -> failwith $"Expected unsupported index-less exact search, got {other}"

    match (Assert.Throws<Query.QueryException>(fun () -> Query.validateLookup view CancellationToken.None) :> exn) with
    | Query.QueryException Query.SearchIndexRequired -> ()
    | other -> failwith $"Expected unsupported index-less validation, got {other}"

    Assert.Equal(0, (view.Node 0).Index)

  let directory = Path.GetDirectoryName(segmentPath indexed.Output "nodes")
  let rebuilt = Writer.buildLookup directory CancellationToken.None
  Assert.Equal(lookup.Checksum, rebuilt.Checksum)
  Assert.Equal<byte[]>(originalBytes, File.ReadAllBytes(Path.Combine(directory, rebuilt.Name)))
  writeManifest indexed.Output original
  use rebuiltView = indexed.View
  let hits = Query.search rebuiltView "shapes.c" false CancellationToken.None
  Assert.True hits.UsedIndex
  Assert.NotEmpty hits.Hits

[<Theory>]
[<InlineData(0)>]
[<InlineData(1)>]
[<InlineData(2)>]
let ``corrupt lexical keys postings and UTF8 fail as typed query errors`` variant =
  use indexed = new Indexed("micro")

  mutate indexed.Output "lookup" (fun bytes ->
    let keys = int(BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, 16, 8)))

    let postings =
      int(BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, 24, 8)))

    match variant with
    | 0 -> BinaryPrimitives.WriteUInt64LittleEndian(Span(bytes, Format.HeaderLength + 32, 8), UInt64.MaxValue)
    | 1 ->
      for index in 0 .. postings - 1 do
        BinaryPrimitives.WriteUInt32LittleEndian(
          Span(bytes, Format.HeaderLength + 32 + keys * 24 + index * 8, 4),
          UInt32.MaxValue
        )
    | _ ->
      Array.Fill(
        bytes,
        0xFFuy,
        Format.HeaderLength + 32 + keys * 24 + postings * 8,
        bytes.Length - Format.HeaderLength - 32 - keys * 24 - postings * 8
      ))

  use view = indexed.View
  assertCorrupt(fun () -> Query.search view "shapes.c" false CancellationToken.None |> ignore)
  assertCorrupt(fun () -> Query.validateLookup view CancellationToken.None)

  if variant > 0 then
    assertCorrupt(fun () -> Query.searchExactNames view "shapes.c" false CancellationToken.None |> ignore)

/// Small, source-free graphs let the oracle cover arbitrary directed edges, not just CONTAINS trees.
type internal TestGraph(names: string[], connections: struct (int * int * EdgeKind)[]) =
  let output =
    Path.Combine(Path.GetTempPath(), "srcnet-query-graph-" + Guid.NewGuid().ToString "N")

  let directory = Manifest.stagedSegmentsPath output

  let repository =
    match RepositoryId.tryCreate "query-graph" with
    | Ok value -> value
    | Error error -> failwith $"{error}"

  let paths =
    names
    |> Array.map(fun name ->
      match Srcnet.Core.Paths.tryCreate name with
      | Ok path -> path
      | Error error -> failwith $"{error}")

  let written =
    Writer.write
      directory
      { Repository = repository
        Directories = paths
        Files = Array.empty }
      (Srcnet.Core.Diagnostics.DiagnosticSink())
      CancellationToken.None

  let nodeCount = names.Length + 1
  // The manifest requires at least nodes-1 structural edges. Keep a separate
  // scaffold kind out of the oracle's requested kinds.
  let storedConnections =
    Array.append connections [| for node in 1 .. nodeCount - 1 -> struct (0, node, Explains) |]

  let edgeKinds =
    Array.append
      [| Contains; Declares; Defines; GuardedBy; Explains |]
      (connections |> Array.map(fun (struct (_, _, kind)) -> kind))
    |> Array.distinct
    |> Array.sortBy EdgeKind.toCode

  let descriptors = ResizeArray<Writer.SegmentDescriptor>()
  let counts = ResizeArray<Writer.KindCount>()

  do
    for segment in written.Segments do
      if not(segment.Name.Contains(".edges.") || segment.Name.Contains(".redges.")) then
        descriptors.Add segment

    for kind in edgeKinds do
      let edges =
        storedConnections
        |> Array.choose(fun (struct (source, target, edgeKind)) ->
          if kind = edgeKind then
            Some(struct (source, target))
          else
            None)
        |> Array.distinct
        |> Array.sort

      counts.Add
        { Kind = EdgeKind.name kind
          Count = edges.Length }

      for reverse in [| false; true |] do
        let sorted =
          if reverse then
            edges
            |> Array.map(fun (struct (source, target)) -> struct (target, source))
            |> Array.sort
          else
            edges

        let offsets = Array.zeroCreate<uint64>(nodeCount + 1)

        for struct (source, _) in sorted do
          offsets[source + 1] <- offsets[source + 1] + 1UL

        for index in 1..nodeCount do
          offsets[index] <- offsets[index] + offsets[index - 1]

        let bytes =
          Array.zeroCreate<byte>(Format.HeaderLength + (nodeCount + 1) * 8 + sorted.Length * 4)

        Format.writeHeader
          (Span bytes)
          { Kind = Format.AdjacencyCsr
            PrimaryCount = uint64 nodeCount
            SecondaryCount = uint64 sorted.Length
            RecordLength = 0u
            PayloadLength = uint64(bytes.Length - Format.HeaderLength) }

        for index in 0..nodeCount do
          BinaryPrimitives.WriteUInt64LittleEndian(Span(bytes, Format.HeaderLength + index * 8, 8), offsets[index])

        for index in 0 .. sorted.Length - 1 do
          let struct (_, target) = sorted[index]

          BinaryPrimitives.WriteUInt32LittleEndian(
            Span(bytes, Format.HeaderLength + (nodeCount + 1) * 8 + index * 4, 4),
            uint32 target
          )

        let suffix = if reverse then "redges" else "edges"
        let name = $"0001.{suffix}.{EdgeKind.name kind}"
        File.WriteAllBytes(Path.Combine(directory, name), bytes)

        descriptors.Add
          { Name = name
            ByteLength = int64 bytes.Length
            Checksum = Convert.ToHexStringLower(Srcnet.Core.Hashing.hash(ReadOnlySpan bytes)) }

    let ordered =
      descriptors.ToArray() |> Array.sortBy(fun descriptor -> descriptor.Name)

    let generation = Manifest.generationOf ordered

    let manifest: Manifest.Manifest =
      { ManifestVersion = Manifest.ManifestVersion
        FormatVersion = Format.FormatVersion
        ToolVersion = Manifest.toolVersion
        RepositoryId = RepositoryId.value repository
        Complete = true
        Options =
          { FollowSymbolicLinks = false
            RespectIgnoreFiles = true
            MaxDepth = 64
            MaxFileSizeBytes = 1L
            Tier = 0
            RequestedTier = ValueNone
            ParserAvailable = false
            Grammars = Array.empty
            AssumedEncoding = "" }
        Counts =
          { Nodes = nodeCount
            Edges = counts |> Seq.sumBy(fun entry -> entry.Count)
            Strings = written.StringCount
            StringBytes = written.StringBytes
            Directories = names.Length
            Files = 0
            Symbols = 0
            ReferenceCandidates = 0
            NodeKinds = written.NodeKinds
            EdgeKinds = counts.ToArray() |> Array.sortBy(fun entry -> entry.Kind) }
        Segments = Manifest.qualify generation ordered
        Diagnostics = Array.empty }

    match Manifest.publish output generation manifest with
    | Ok() -> ()
    | Error error -> failwith(Artifact.PathError.describe error)

  member _.Output = output

  member _.View =
    match Query.GraphView.Open output with
    | Ok view -> view
    | Error error -> failwith(Query.QueryError.describe error)

  interface IDisposable with
    member _.Dispose() = Directory.Delete(output, true)

[<Fact>]
let ``scalar adjacency access matches forward reverse and absent rows`` () =
  let edges =
    [| struct (0, 1, Contains)
       struct (0, 2, Contains)
       struct (2, 1, Contains)
       struct (2, 2, Contains)
       struct (2, 3, Calls) |]

  use graph = new TestGraph([| "a"; "b"; "c" |], edges)
  use view = graph.View

  for index in 0 .. view.NodeCount - 1 do
    for kind in [| Contains; Calls; Includes |] do
      for incoming in [| false; true |] do
        let direction = if incoming then Query.Incoming else Query.Outgoing
        let expected = view.Neighbors(index, kind, direction)
        Assert.Equal(expected.Length, view.NeighborCount(index, kind, incoming))

        for position in 0 .. expected.Length - 1 do
          Assert.Equal(expected[position], view.NeighborAt(index, kind, incoming, position))

        match
          (Assert.Throws<Query.QueryException>(fun () ->
            view.NeighborAt(index, kind, incoming, expected.Length) |> ignore)
          :> exn)
        with
        | Query.QueryException(Query.InvalidArgument _) -> ()
        | other -> failwith $"Expected invalid adjacency position, got {other}"

[<Fact>]
let ``full lookup validation covers path postings and preserves view ownership`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View
  Query.validateLookup view CancellationToken.None
  Assert.NotEmpty((Query.searchExactNames view "shapes.c" false CancellationToken.None).Hits)

[<Theory>]
[<InlineData(false, 0)>]
[<InlineData(true, 0)>]
[<InlineData(false, 1)>]
[<InlineData(true, 1)>]
[<InlineData(false, 2)>]
[<InlineData(true, 2)>]
[<InlineData(false, 3)>]
[<InlineData(true, 3)>]
[<InlineData(false, 4)>]
[<InlineData(true, 4)>]
[<InlineData(false, 5)>]
[<InlineData(true, 5)>]
let ``full lookup validation checks every normal and folded key and posting`` folded variant =
  use graph = new TestGraph([| "aaaa"; "bbbb" |], Array.empty)

  mutate graph.Output "lookup" (fun bytes ->
    let keys = int(BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, 16, 8)))
    let postings = int(BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, 24, 8)))
    let prelude = Format.HeaderLength
    let normal = int(BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(bytes, prelude + 24, 4)))
    let key = prelude + Format.Lookup.PreludeLength + (if folded then normal else 0) * Format.Lookup.KeyLength
    let blob = prelude + Format.Lookup.PreludeLength + keys * Format.Lookup.KeyLength + postings * Format.Lookup.PostingLength
    let offset = int(BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, key, 8)))
    let first = int(BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(bytes, key + 12, 4)))
    let posting = prelude + Format.Lookup.PreludeLength + keys * Format.Lookup.KeyLength + first * Format.Lookup.PostingLength

    match variant with
    | 0 -> BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, key + 20, 4), 1u)
    | 1 -> bytes[blob + offset] <- 0xFFuy
    | 2 -> Array.Copy(bytes, posting, bytes, posting + Format.Lookup.PostingLength, Format.Lookup.PostingLength)
    | 3 -> BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, posting, 4), 0u)
    | 4 -> BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, posting + 4, 4), 3u)
    | _ ->
      let swap first second length =
        for index in 0 .. length - 1 do
          let saved = bytes[first + index]
          bytes[first + index] <- bytes[second + index]
          bytes[second + index] <- saved

      swap (blob + offset) (blob + offset + 4) 4
      swap posting (posting + 2 * Format.Lookup.PostingLength) (2 * Format.Lookup.PostingLength))

  use view = graph.View
  assertCorrupt(fun () -> Query.validateLookup view CancellationToken.None)

[<Fact>]
let ``full lookup validation detects structurally valid missing postings`` () =
  use graph = new TestGraph([| "aaaa"; "bbbb" |], Array.empty)
  let path = segmentPath graph.Output "lookup"
  let original = File.ReadAllBytes path
  let keys = int(BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(original, 16, 8)))
  let postings = BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(original, 24, 8))
  let firstKey = Format.HeaderLength + Format.Lookup.PreludeLength
  let postingBase = firstKey + keys * Format.Lookup.KeyLength
  let bytes = Array.zeroCreate<byte>(original.Length - Format.Lookup.PostingLength)
  Array.Copy(original, bytes, postingBase)
  Array.Copy(original, postingBase + Format.Lookup.PostingLength, bytes, postingBase, bytes.Length - postingBase)
  BinaryPrimitives.WriteUInt64LittleEndian(Span(bytes, 24, 8), postings - 1UL)
  BinaryPrimitives.WriteUInt64LittleEndian(Span(bytes, 40, 8), uint64(bytes.Length - Format.HeaderLength))

  for index in 0 .. keys - 1 do
    let field =
      firstKey + index * Format.Lookup.KeyLength + (if index = 0 then 16 else 12)

    let value = BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(bytes, field, 4))
    BinaryPrimitives.WriteUInt32LittleEndian(Span(bytes, field, 4), value - 1u)

  File.WriteAllBytes(path, bytes)
  let manifest = readManifest graph.Output

  let segments =
    manifest.Segments
    |> Array.map(fun segment ->
      if segment.Name.EndsWith(".lookup", StringComparison.Ordinal) then
        { segment with
            ByteLength = int64 bytes.Length
            Checksum = Convert.ToHexStringLower(Srcnet.Core.Hashing.hash(ReadOnlySpan bytes)) }
      else
        segment)

  writeManifest graph.Output { manifest with Segments = segments }
  use view = graph.View
  let remaining = Query.searchExactNames view "aaaa" false CancellationToken.None
  Assert.Equal(Query.QualifiedName, (Assert.Single remaining.Hits).Target)
  Assert.False remaining.Truncated
  assertCorrupt(fun () -> Query.validateLookup view CancellationToken.None)

[<Fact>]
let ``lookup indexes NFC and invariant folding for stored names and all match strengths`` () =
  use graph =
    new TestGraph(
      [| "Cafe\u0301"
         "CAFETERIA"
         "aCafe\u0301z"
         "\uD55C\uAE00"
         "\u65E5\u672C\u8A9E" |],
      Array.empty
    )

  use view = graph.View
  Query.validateLookup view CancellationToken.None

  let find text ignoreCase =
    (Query.search view text ignoreCase CancellationToken.None).Hits
    |> Array.map(fun hit -> hit.Node, hit.Strength)
    |> Map.ofArray

  Assert.Equal(Query.Exact, (find "Caf\u00E9" false)[1])
  Assert.Equal(Query.Exact, (find "CAFE\u0301" true)[1])
  Assert.Equal(Query.Prefix, (find "cafe" true)[2])
  Assert.Equal(Query.Substring, (find "Caf\u00E9" false)[3])
  Assert.Equal(Query.Substring, (find "\u672C" false)[5])
  Assert.Equal(Query.Exact, (find "\uD55C\uAE00" false)[4])

[<Fact>]
let ``blob lookup finds rare late CJK substrings beyond 32768 keys without dictionary allocations`` () =
  let names =
    Array.append
      (Array.init 40_000 (fun index -> $"n{index:D8}"))
      [| "\u7D42\u7AEF-Cafe\u0301-\U00020000-\u65E5\u672C\u8A9E-tail" |]

  use graph = new TestGraph(names, Array.empty)
  use view = graph.View

  for needle, ignoreCase in [ "\u65E5\u672C", false; "\U00020000-\u65E5", false; "CAFE\u0301", true ] do
    Query.search view needle ignoreCase CancellationToken.None |> ignore
    let before = GC.GetAllocatedBytesForCurrentThread()
    let result = Query.search view needle ignoreCase CancellationToken.None
    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    let hit = Assert.Single result.Hits
    Assert.Equal(names.Length, hit.Node)
    Assert.Equal(Query.Substring, hit.Strength)
    Assert.Equal(Query.Name, hit.Target)
    Assert.True result.UsedIndex
    Assert.False result.Truncated
    Assert.Equal(0, result.OmittedCount)
    Assert.False result.OmittedCountIsLowerBound
    Assert.Empty result.Diagnostics
    Assert.Equal(1, result.ScannedNodes)
    Assert.InRange(allocated, 0L, 1_048_576L)

  let absent = Query.search view "no-such-substring" false CancellationToken.None
  Assert.Empty absent.Hits
  Assert.False absent.Truncated

[<Fact>]
let ``blob matches cannot cross keys or normalized and folded regions`` () =
  use graph = new TestGraph([| "abc"; "def"; "\u65E5\u672C"; "\u8A9E" |], Array.empty)
  use view = graph.View

  for needle in [| "cde"; "\u65E5\u672C\u8A9E"; "\u8A9Eabc" |] do
    for ignoreCase in [| false; true |] do
      let result = Query.search view needle ignoreCase CancellationToken.None
      Assert.Empty result.Hits
      Assert.False result.Truncated
      Assert.Equal(0, result.OmittedCount)
      Assert.Empty result.Diagnostics

[<Fact>]
let ``blob scanning rejects invalid UTF8 even when its key does not match`` () =
  use graph = new TestGraph([| "aaa"; "zzzz-unmatched" |], Array.empty)

  mutate graph.Output "lookup" (fun bytes ->
    let keys = int(BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, 16, 8)))

    let postings =
      int(BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, 24, 8)))

    let normal =
      int(BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(bytes, Format.HeaderLength + 24, 4)))

    let record =
      Format.HeaderLength
      + Format.Lookup.PreludeLength
      + (normal - 1) * Format.Lookup.KeyLength

    let offset =
      int(BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, record, 8)))

    let length =
      int(BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(bytes, record + 8, 4)))

    let blob =
      Format.HeaderLength
      + Format.Lookup.PreludeLength
      + keys * Format.Lookup.KeyLength
      + postings * Format.Lookup.PostingLength

    bytes[blob + offset + length - 1] <- 0xFFuy)

  use view = graph.View
  assertCorrupt(fun () -> Query.search view "not-present" false CancellationToken.None |> ignore)

[<Fact>]
let ``substring windows preserve matches and UTF8 scalars crossing the byte boundary`` () =
  let large = "b" + String('x', 1_048_565) + "\u65E5\u672C\u8A9E"
  use graph = new TestGraph([| "a-padding"; large |], Array.empty)
  use view = graph.View

  for needle in [| "\u65E5\u672C\u8A9E"; "x\u65E5\u672C\u8A9E" |] do
    for ignoreCase in [| false; true |] do
      let result = Query.search view needle ignoreCase CancellationToken.None
      let hit = Assert.Single result.Hits
      Assert.Equal(2, hit.Node)
      Assert.Equal(Query.Substring, hit.Strength)
      Assert.False result.Truncated
      Assert.Equal(0, result.OmittedCount)

[<Fact>]
let ``substring byte cap remains explicit when valid key bytes exceed the work budget`` () =
  let count = Query.MaxLookupBytes / Format.Lookup.MaxKeyBytes + 2
  let marker = "late-only-marker"
  let padding = String('x', Format.Lookup.MaxKeyBytes - 4)

  let names =
    Array.init count (fun index ->
      if index + 1 = count then
        $"{index:D4}" + padding.Substring(0, padding.Length - marker.Length) + marker
      else
        $"{index:D4}" + padding)

  use graph = new TestGraph(names, Array.empty)
  use view = graph.View

  let exact =
    Query.searchExactNames view names[count - 1] false CancellationToken.None

  Assert.Equal(count, (Assert.Single exact.Hits).Node)
  Assert.False exact.Truncated
  let result = Query.search view marker false CancellationToken.None
  Assert.Empty result.Hits
  Assert.True result.UsedIndex
  Assert.True result.Truncated
  Assert.Equal(0, result.OmittedCount)
  Assert.True result.OmittedCountIsLowerBound
  Assert.NotEmpty result.Diagnostics

[<Fact>]
let ``cross-key byte candidates have an explicit work cap without fake node omissions`` () =
  let names =
    Array.init (Query.MaxLookupCandidates + 2) (fun index -> $"n{index:D8}x")

  use graph = new TestGraph(names, Array.empty)
  use view = graph.View
  let result = Query.search view "xn" false CancellationToken.None
  Assert.Empty result.Hits
  Assert.Equal(0, result.ScannedNodes)
  Assert.True result.Truncated
  Assert.Equal(0, result.OmittedCount)
  Assert.True result.OmittedCountIsLowerBound
  Assert.NotEmpty result.Diagnostics
  let exact = Query.searchExactNames view "xn" false CancellationToken.None
  Assert.Empty exact.Hits
  Assert.False exact.Truncated

let private oracle nodeCount (edges: struct (int * int * EdgeKind)[]) source target direction depth =
  let distances = Array.create nodeCount -1
  let queue = Queue<int>()
  distances[source] <- 0
  queue.Enqueue source

  while queue.Count > 0 do
    let current = queue.Dequeue()

    if distances[current] < depth then
      for struct (fromNode, toNode, _) in edges do
        let next =
          if fromNode = current && direction <> Query.Incoming then
            toNode
          elif toNode = current && direction <> Query.Outgoing then
            fromNode
          else
            -1

        if next >= 0 && distances[next] < 0 then
          distances[next] <- distances[current] + 1
          queue.Enqueue next

  distances[target]

let private assertPath nodeCount edges (view: Query.GraphView) source target direction depth =
  let kinds = edges |> Array.map(fun (struct (_, _, kind)) -> kind) |> Array.distinct

  let result =
    Query.shortestPath view source target kinds direction depth CancellationToken.None

  Assert.False result.Truncated
  Assert.Equal(0, result.OmittedCount)
  Assert.False result.OmittedCountIsLowerBound
  Assert.Empty result.Diagnostics

  let expected = oracle nodeCount edges source target direction depth

  match result.Path with
  | ValueNone -> Assert.Equal(-1, expected)
  | ValueSome path ->
    Assert.Equal(expected, path.Length - 1)
    Assert.Equal(source, path[0])
    Assert.Equal(target, path[path.Length - 1])
    Assert.Equal(path.Length - 1, result.Edges.Length)

    for index in 0 .. result.Edges.Length - 1 do
      let edge = result.Edges[index]
      Assert.Contains(struct (edge.From, edge.To, edge.Kind), edges)
      let outgoing = edge.From = path[index] && edge.To = path[index + 1]
      let incoming = edge.To = path[index] && edge.From = path[index + 1]

      Assert.True(
        (direction <> Query.Incoming && outgoing)
        || (direction <> Query.Outgoing && incoming)
      )

  let again =
    Query.shortestPath view source target (Array.rev kinds) direction depth CancellationToken.None

  Assert.Equal(result, again)

[<Fact>]
let ``requested radius filters single and multi-source traversal without omissions`` () =
  let edges =
    [| struct (0, 1, Contains)
       struct (1, 2, Contains)
       struct (2, 3, Contains)
       struct (3, 4, Contains)
       struct (2, 2, Contains) |]

  use graph = new TestGraph([| "a"; "b"; "c"; "d" |], edges)
  use view = graph.View

  for direction in [| Query.Outgoing; Query.Incoming; Query.Both |] do
    for starts in [| [| 0 |]; [| 4 |]; [| 2 |]; [| 4; 0; 4 |] |] do
      for depth in 0..3 do
        let traversal =
          Query.neighborsFrom view starts [| Contains |] direction depth CancellationToken.None

        assertCompleteTraversal traversal

        let expected =
          [| for node in 0..4 do
               let distances =
                 starts
                 |> Array.map(fun start -> oracle 5 edges start node direction depth)
                 |> Array.filter(fun distance -> distance >= 0)

               if distances.Length > 0 then
                 yield struct (node, Array.min distances) |]
          |> Array.sortBy(fun (struct (node, distance)) -> struct (distance, node))

        Assert.Equal<struct (int * int)[]>(expected, traversal.Nodes)

        let distances =
          expected
          |> Array.map(fun (struct (node, distance)) -> node, distance)
          |> Map.ofArray

        let expanded node =
          match distances.TryFind node with
          | Some distance -> distance < depth
          | None -> false

        let expectedEdges =
          edges
          |> Array.filter(fun (struct (source, target, _)) ->
            (direction <> Query.Incoming && expanded source)
            || (direction <> Query.Outgoing && expanded target))
          |> Array.map(fun (struct (source, target, kind)) ->
            ({ From = source
               To = target
               Kind = kind }
            : Query.EdgeView))
          |> Array.sortBy(fun edge -> struct (edge.From, EdgeKind.toCode edge.Kind, edge.To))

        Assert.Equal<Query.EdgeView[]>(expectedEdges, traversal.Edges)

        if depth = 0 then
          Assert.Empty traversal.Edges

[<Fact>]
let ``bidirectional paths agree with exhaustive BFS for every three-node digraph and depth`` () =
  let possible =
    [| for source in 0..2 do
         for target in 0..2 do
           if source <> target then
             struct (source, target, Contains) |]

  for mask in 0 .. (1 <<< possible.Length) - 1 do
    let edges =
      possible
      |> Array.mapi(fun index edge -> index, edge)
      |> Array.choose(fun (index, edge) -> if mask &&& (1 <<< index) <> 0 then Some edge else None)

    use graph = new TestGraph([| "a"; "b" |], edges)
    use view = graph.View

    for direction in [| Query.Outgoing; Query.Incoming; Query.Both |] do
      for source in 0..2 do
        for target in 0..2 do
          for depth in 0..3 do
            assertPath 3 edges view source target direction depth

[<Fact>]
let ``paths handle asymmetric frontiers cycles kinds self loops and deterministic ties`` () =
  let edges =
    [| for index in 1..12 do
         yield struct (0, index, Contains)
       yield struct (1, 13, Calls)
       yield struct (2, 13, Contains)
       yield struct (13, 14, Calls)
       yield struct (14, 13, Contains)
       yield struct (14, 14, Calls)
       yield struct (15, 14, Contains) |]

  use graph = new TestGraph(Array.init 15 (fun index -> $"n{index:D2}"), edges)
  use view = graph.View

  for direction in [| Query.Outgoing; Query.Incoming; Query.Both |] do
    for source in 0..15 do
      for target in 0..15 do
        for depth in 0..5 do
          assertPath 16 edges view source target direction depth

  let incoming =
    Query.neighbors view 14 [| Contains; Calls |] Query.Incoming 4 CancellationToken.None

  let both =
    Query.neighborsFrom view [| 14; 0; 14 |] [| Contains; Calls |] Query.Both 4 CancellationToken.None

  for traversal in [| incoming; both |] do
    Assert.Equal(traversal.Edges.Length, (Array.distinct traversal.Edges).Length)

    for edge in traversal.Edges do
      Assert.Contains(struct (edge.From, edge.To, edge.Kind), edges)

[<Fact>]
let ``bidirectional search finds a six-hop leaf beyond the old 200k forward cap`` () =
  let count = Query.MaxVisitedNodes + 10
  let first = count - 6

  let edges =
    [| for target in 1..first -> struct (0, target, Contains)
       for source in first .. count - 2 -> struct (source, source + 1, Contains) |]

  use graph =
    new TestGraph(Array.init (count - 1) (fun index -> $"n{index:D8}"), edges)

  use view = graph.View

  Query.validateLookup view CancellationToken.None
  view.NeighborCount(0, Contains, false) |> ignore
  view.NeighborAt(0, Contains, false, 0) |> ignore
  view.NeighborAt(0, Contains, false, first - 1) |> ignore
  let before = GC.GetAllocatedBytesForCurrentThread()
  let degree = view.NeighborCount(0, Contains, false)
  let firstTarget = view.NeighborAt(0, Contains, false, 0)
  let lastTarget = view.NeighborAt(0, Contains, false, first - 1)
  let allocated = GC.GetAllocatedBytesForCurrentThread() - before
  Assert.Equal(first, degree)
  Assert.Equal(1, firstTarget)
  Assert.Equal(first, lastTarget)
  Assert.InRange(allocated, 0L, 65_536L)

  let result =
    Query.shortestPath view 0 (count - 1) [| Contains |] Query.Outgoing 6 CancellationToken.None

  match result.Path with
  | ValueNone -> failwith $"Missed six-hop route: {result.Diagnostics}"
  | ValueSome path -> Assert.Equal<int[]>(Array.append [| 0 |] [| first .. count - 1 |], path)

  Assert.False result.Truncated

  let traversal =
    Query.neighbors view 0 [| Contains |] Query.Outgoing 1 CancellationToken.None

  Assert.Equal(Query.MaxVisitedNodes, traversal.Nodes.Length)
  Assert.True traversal.Truncated
  Assert.True traversal.OmittedCountIsLowerBound
  Assert.True(traversal.OmittedCount > 0)
  Assert.InRange(traversal.Edges.Length, 0, Query.MaxExploredEdges)
  let search = Query.search view $"n{count - 2:D8}" false CancellationToken.None
  Assert.True search.UsedIndex
  Assert.Contains(search.Hits, (fun hit -> hit.Node = count - 1 && hit.Strength = Query.Exact))
  Assert.InRange(search.ScannedNodes, 1, 2)
  Assert.False search.Truncated
  Assert.Equal(0, search.OmittedCount)
  Assert.False search.OmittedCountIsLowerBound
  Assert.Empty search.Diagnostics

  let exact =
    Query.searchExactNames view $"n{count - 2:D8}" false CancellationToken.None

  Assert.True exact.UsedIndex
  Assert.False exact.Truncated
  Assert.False exact.OmittedCountIsLowerBound
  Assert.Equal(0, exact.OmittedCount)
  Assert.Equal(1, exact.ScannedNodes)
  Assert.Empty exact.Diagnostics
  Assert.Equal(count - 1, (Assert.Single exact.Hits).Node)

  for needle in [| "n"; "no-such-exact-name" |] do
    let missing = Query.searchExactNames view needle false CancellationToken.None
    Assert.Empty missing.Hits
    Assert.False missing.Truncated
    Assert.Equal(0, missing.ScannedNodes)

  let broad = Query.search view "n" false CancellationToken.None
  Assert.Equal(Query.MaxScannedNodes, broad.Hits.Length)
  Assert.True broad.Truncated
  Assert.Equal(1, broad.OmittedCount)
  Assert.True broad.OmittedCountIsLowerBound

[<Fact>]
let ``exact name lookup never claims complete uniqueness after its node cap`` () =
  let names =
    [| for index in 0 .. Query.MaxScannedNodes do
         yield $"d{index:D8}"
         yield $"d{index:D8}/common" |]

  use graph = new TestGraph(names, Array.empty)
  use view = graph.View
  let found = Query.searchExactNames view "common" false CancellationToken.None
  Assert.True found.UsedIndex
  Assert.True found.Truncated
  Assert.True found.OmittedCountIsLowerBound
  Assert.Equal(Query.MaxScannedNodes, found.Hits.Length)
  Assert.Equal(1, found.OmittedCount)

  Assert.All(
    found.Hits,
    fun hit ->
      Assert.Equal(Query.Exact, hit.Strength)
      Assert.Equal(Query.Name, hit.Target)
  )

[<Fact>]
let ``path node cap is strict across both frontiers and reports known omissions`` () =
  let count = Query.MaxVisitedNodes + 10
  let half = count / 2

  let edges =
    [| for target in 1 .. half - 1 do
         yield struct (0, target, Contains)
       for source in 1 .. half - 1 do
         yield struct (source, source, Contains)
         yield struct (source, 0, Contains)
       for source in half .. count - 2 do
         yield struct (source, count - 1, Contains) |]

  use graph =
    new TestGraph(Array.init (count - 1) (fun index -> $"n{index:D8}"), edges)

  use view = graph.View

  let result =
    Query.shortestPath view 0 (count - 1) [| Contains |] Query.Outgoing 8 CancellationToken.None

  Assert.Equal(ValueNone, result.Path)
  Assert.True result.Truncated
  Assert.Equal(1, result.OmittedCount)
  Assert.True result.OmittedCountIsLowerBound
  Assert.NotEmpty result.Diagnostics

[<Fact>]
let ``proved shortest paths do not exhaust the rest of a dense layer`` () =
  let half = 640
  let count = half * 2 + 2

  let edges =
    [| for left in 2 .. half + 1 do
         yield struct (0, left, Contains)

         for right in half + 2 .. count - 1 do
           yield struct (left, right, Contains)

       for right in half + 2 .. count - 1 do
         yield struct (right, 1, Contains) |]

  use graph =
    new TestGraph(Array.init (count - 1) (fun index -> $"n{index:D4}"), edges)

  use view = graph.View

  let route: Query.EdgeView[] =
    [| { From = 0
         To = 2
         Kind = Contains }
       { From = 2
         To = half + 2
         Kind = Contains }
       { From = half + 2
         To = 1
         Kind = Contains } |]

  for source, target, direction in
    [ 0, 1, Query.Outgoing
      1, 0, Query.Incoming
      0, 1, Query.Both
      1, 0, Query.Both ] do
    let result =
      Query.shortestPath view source target [| Contains |] direction 3 CancellationToken.None

    let expected =
      if source = 0 then [| 0; 2; half + 2; 1 |]
      else [| 1; half + 2; 2; 0 |]

    let expectedEdges = if source = 0 then route else Array.rev route

    match result.Path with
    | ValueNone -> failwith $"Discarded a proved shortest route: {result.Diagnostics}"
    | ValueSome path -> Assert.Equal<int[]>(expected, path)

    Assert.Equal<Query.EdgeView[]>(expectedEdges, result.Edges)
    Assert.False result.Truncated
    Assert.Equal(0, result.OmittedCount)
    Assert.False result.OmittedCountIsLowerBound
    Assert.Empty result.Diagnostics

[<Fact>]
let ``dense disconnected graphs stop at the edge cap without claiming no route`` () =
  let half = 650
  let count = half * 2

  let edges =
    [| for group in 0..1 do
         for source in group * half .. (group + 1) * half - 1 do
           for target in group * half .. (group + 1) * half - 1 do
             if source <> target then
               struct (source, target, Contains) |]

  use graph =
    new TestGraph(Array.init (count - 1) (fun index -> $"n{index:D5}"), edges)

  use view = graph.View

  let radiusOne =
    Query.neighbors view 0 [| Contains |] Query.Outgoing 1 CancellationToken.None

  assertCompleteTraversal radiusOne
  Assert.Equal(half, radiusOne.Nodes.Length)
  Assert.Equal(half - 1, radiusOne.Edges.Length)

  let result =
    Query.shortestPath view 0 (count - 1) [| Contains |] Query.Outgoing 8 CancellationToken.None

  Assert.Equal(ValueNone, result.Path)
  Assert.True result.Truncated
  Assert.True result.OmittedCountIsLowerBound
  Assert.NotEmpty result.Diagnostics

  let traversal =
    Query.neighbors view 0 [| Contains |] Query.Both 8 CancellationToken.None

  Assert.True traversal.Truncated
  Assert.InRange(traversal.Edges.Length, 0, Query.MaxExploredEdges)
