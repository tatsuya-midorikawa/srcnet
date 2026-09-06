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
      (Srcnet.Cli.Commands.index arguments CancellationToken.None).GetAwaiter().GetResult()

    // 診断つきの完了（4）も正常な結果である。
    if code <> 0 && code <> 4 then failwith $"索引の生成に失敗しました（終了コード {code}）"

  new(corpus: string) = new Indexed(corpus, ValueNone)

  member _.Output = output

  member _.View =
    match Query.GraphView.Open output with
    | Ok view -> view
    | Error error -> failwith (Query.QueryError.describe error)

  interface IDisposable with

    member _.Dispose() =
      if Directory.Exists output then
        try
          Directory.Delete(output, true)
        with :? IOException ->
          ()

let private searchNames (view: Query.GraphView) (text: string) =
  let outcome = Query.search view text false CancellationToken.None
  let ordered = Array.copy outcome.Hits
  Array.sortInPlaceWith (Query.compareHits view) ordered

  ordered
  |> Array.map (fun hit ->
    let node = view.Node hit.Node
    struct (view.String node.QualifiedNameRef, hit.Strength, hit.Target))

[<Fact>]
let ``名前の完全一致が前方一致と部分一致より上に来る`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View
  let results = searchNames view "Area"

  Assert.NotEmpty results

  // 一致の強さは順位の 1 段目である。降順に緩むことはない。
  let strengths = results |> Array.map (fun (struct (_, strength, _)) -> Query.MatchStrength.rank strength)
  Assert.Equal<int[]>(Array.sort strengths, strengths)

  let struct (_, first, _) = results[0]
  Assert.Equal(Query.Exact, first)

[<Fact>]
let ``定義は同じ名前の宣言より上に来る`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View

  let outcome = Query.search view "Area" false CancellationToken.None
  let ordered = Array.copy outcome.Hits
  Array.sortInPlaceWith (Query.compareHits view) ordered

  let exact =
    ordered
    |> Array.filter (fun hit -> hit.Strength = Query.Exact)
    |> Array.map (fun hit -> (view.Node hit.Node).Flags)

  let definitionFirst =
    exact
    |> Array.map (fun flags -> if flags.HasFlag NodeFlags.Definition then 0 else 1)

  Assert.Equal<int[]>(Array.sort definitionFirst, definitionFirst)

[<Fact>]
let ``CJK の完全一致・前方一致・部分一致が働く`` () =
  use indexed = new Indexed("cjk", ValueSome Srcnet.Text.Encodings.EucJp)
  use view = indexed.View

  let strengthOf (text: string) (qualified: string) =
    searchNames view text
    |> Array.tryPick (fun (struct (name, strength, _)) ->
      if String.Equals(name, qualified, StringComparison.Ordinal) then Some strength else None)

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
         Query.Path, view.String(view.FilePathRef node.FileIndex) |])

  let needles =
    fields
    |> Array.collect (Array.map snd)
    |> Array.filter (fun text -> text.Length > 0)
    |> Array.collect (fun text ->
      [| text
         text.Substring(0, max 1 (text.Length / 2))
         text.Substring(text.Length / 2)
         text.ToUpperInvariant() |])
    |> Array.append [| "no-such-name" |]
    |> Array.distinct

  let normalize text =
    if ignoreCase then Srcnet.Text.Unicode.caseFold text
    else Srcnet.Text.Unicode.normalize text

  for needle in needles do
    let key = normalize needle

    let expected =
      fields
      |> Array.mapi (fun index values ->
        values
        |> Array.choose (fun (target, value) ->
          let text = normalize value

          let strength =
            if text = key then Some Query.Exact
            elif text.StartsWith(key, StringComparison.Ordinal) then Some Query.Prefix
            elif text.Contains(key, StringComparison.Ordinal) then Some Query.Substring
            else None

          strength |> Option.map (fun matched -> struct (index, matched, target)))
        |> Array.sortBy (fun (struct (_, strength, target)) ->
          Query.MatchStrength.rank strength, Query.MatchTarget.rank target)
        |> Array.tryHead)
      |> Array.choose id

    let actual =
      (Query.search view needle ignoreCase CancellationToken.None).Hits
      |> Array.map (fun hit -> struct (hit.Node, hit.Strength, hit.Target))

    Assert.Equal<struct (int * Query.MatchStrength * Query.MatchTarget)>(expected, actual)

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
  let unknown = { High = 0xFFFFFFFFFFFFFFFFUL; Low = 0xFFFFFFFFFFFFFFFFUL }
  Assert.Equal(ValueNone, view.TryResolve unknown)

[<Fact>]
let ``近傍探索は深さの上限を守る`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View
  let repository = 0

  for depth in 0..3 do
    let traversal =
      Query.neighbors view repository [| Contains |] Query.Outgoing depth CancellationToken.None

    // 起点からの距離は必ず指定した深さ以内に収まる。
    for struct (_, distance) in traversal.Nodes do
      Assert.InRange(distance, 0, depth)

[<Fact>]
let ``近傍の結果は決定的な順序になる`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View

  let run () =
    Query.neighbors view 0 view.EdgeKinds Query.Both 2 CancellationToken.None

  let first = run ()
  let second = run ()

  Assert.Equal<int[]>(
    first.Nodes |> Array.map (fun (struct (node, _)) -> node),
    second.Nodes |> Array.map (fun (struct (node, _)) -> node)
  )

  Assert.Equal<string[]>(
    first.Edges |> Array.map (fun edge -> $"{edge.From}-{EdgeKind.name edge.Kind}-{edge.To}"),
    second.Edges |> Array.map (fun edge -> $"{edge.From}-{EdgeKind.name edge.Kind}-{edge.To}")
  )

[<Fact>]
let ``最短経路は起点と終点を含み、深さ上限を超えない`` () =
  use indexed = new Indexed("micro")
  use view = indexed.View

  // リポジトリからファイルへは CONTAINS で必ず到達できる。
  let target =
    [| 0 .. view.NodeCount - 1 |]
    |> Array.find (fun index -> (view.Node index).Kind = File)

  match Query.shortestPath view 0 target [| Contains |] Query.Outgoing 8 CancellationToken.None with
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
    |> Array.find (fun index -> (view.Node index).Kind = File)

  Assert.Equal(ValueNone, Query.shortestPath view 0 target [| Contains |] Query.Outgoing 0 CancellationToken.None)

[<Fact>]
let ``トークン見積りは決定的で、長さに対して単調である`` () =
  let short = Srcnet.Cli.QueryCommands.estimateTokens "abcd"
  let long = Srcnet.Cli.QueryCommands.estimateTokens "abcdabcdabcd"
  Assert.True(long > short)

  // CJK は 1 文字 1 トークンとして数える。
  Assert.Equal(3, Srcnet.Cli.QueryCommands.estimateTokens "日本語")

  // 同じ入力からは常に同じ値が出る。
  Assert.Equal(
    Srcnet.Cli.QueryCommands.estimateTokens "面積を求める",
    Srcnet.Cli.QueryCommands.estimateTokens "面積を求める"
  )

[<Fact>]
let ``予算は件数とトークンの両方で出力を抑える`` () =
  let budget = Srcnet.Cli.QueryCommands.Budget(2, 1000)
  Assert.True(budget.TryAdd 10)
  Assert.True(budget.TryAdd 10)
  // 件数の上限に達したら受け付けない。
  Assert.False(budget.TryAdd 10)
  Assert.True budget.Truncated
  Assert.Equal(1, budget.Omitted)

  let tight = Srcnet.Cli.QueryCommands.Budget(100, 15)
  Assert.True(tight.TryAdd 10)
  // トークン予算を超えるものは受け付けない。
  Assert.False(tight.TryAdd 10)
  Assert.Equal(1, tight.Omitted)

/// 実際の CLI を起動して標準出力を取り込む。
///
/// `Terminal` は標準出力のストリームを直接持つため、プロセス内で差し替えられない。
/// 「同じ引数から同じバイト列が出る」ことは成果物の外部仕様なので、実物を起動して確かめる。
let private runCli (arguments: string list) =
  let dll =
    let candidates =
      Directory.EnumerateFiles(Path.Combine(Corpus.root.Value, "src", "Srcnet.Cli", "bin"), "srcnet.dll", SearchOption.AllDirectories)
      |> Seq.sortByDescending (fun path -> File.GetLastWriteTimeUtc path)
      |> Seq.toArray

    match candidates with
    | [||] -> failwith "srcnet.dll が見つかりません。先に build してください"
    | _ -> candidates[0]

  let info = Diagnostics.ProcessStartInfo("dotnet")
  info.ArgumentList.Add dll

  for argument in arguments do
    info.ArgumentList.Add argument

  info.RedirectStandardOutput <- true
  info.RedirectStandardError <- true
  info.StandardOutputEncoding <- Text.UTF8Encoding false

  use process' = Diagnostics.Process.Start info
  let output = process'.StandardOutput.ReadToEnd()
  let errors = process'.StandardError.ReadToEnd()

  // 応答しない場合に永久に待たない。上限に達したら殺して失敗させる。
  if not (process'.WaitForExit 120_000) then
    process'.Kill true
    failwith "CLI が時間内に終了しませんでした"

  struct (process'.ExitCode, output, errors)

[<Fact>]
let ``同じ引数からは同じ JSON が出る`` () =
  use indexed = new Indexed("micro")

  let arguments =
    [ "search"; "Area"; "--out"; indexed.Output; "--json"; "--limit"; "10" ]

  let struct (firstCode, first, _) = runCli arguments
  let struct (secondCode, second, _) = runCli arguments

  Assert.Equal(firstCode, secondCode)
  // バイト単位で一致すること。改行や順序の揺れも差として現れる。
  Assert.Equal(first, second)

[<Fact>]
let ``JSON は定めた項目をすべて持つ`` () =
  use indexed = new Indexed("micro")

  let struct (_, payload, _) =
    runCli [ "search"; "Area"; "--out"; indexed.Output; "--json"; "--limit"; "2" ]

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

  // 検索索引がないことを診断で明示する。全走査へ黙って退行しない。
  let diagnostics =
    root.GetProperty("diagnostics").EnumerateArray()
    |> Seq.map (fun item -> item.GetString())
    |> Seq.toArray

  Assert.Contains(diagnostics, fun text -> text <> null && text.Contains "検索索引")

[<Fact>]
let ``照会の終了コードは仕様どおりになる`` () =
  use indexed = new Indexed("micro")

  // 該当なしは 1。異常ではない。
  let struct (noResults, _, _) =
    runCli [ "search"; "この名前は存在しない"; "--out"; indexed.Output; "--json" ]

  Assert.Equal(Srcnet.Cli.Commands.ExitCode.NoResults, noResults)

  // 未知のエッジ種別は利用者入力の誤りなので 2。
  let struct (userError, _, _) =
    runCli [ "neighbors"; "Point"; "--out"; indexed.Output; "--edge"; "NOPE" ]

  Assert.Equal(Srcnet.Cli.Commands.ExitCode.UserError, userError)

  // 生成物が無ければ 3。
  let missing = Path.Combine(Path.GetTempPath(), "srcnet-missing-" + Guid.NewGuid().ToString "N")
  let struct (missingArtifact, _, _) = runCli [ "search"; "Area"; "--out"; missing ]
  Assert.Equal(Srcnet.Cli.Commands.ExitCode.MissingArtifact, missingArtifact)

[<Fact>]
let ``ノード指定は曖昧な名前を拒否しファイル自身と ID を解決する`` () =
  use indexed = new Indexed("micro")

  let struct (ambiguous, _, diagnostics) =
    runCli [ "show"; "Area"; "--out"; indexed.Output; "--json" ]

  Assert.Equal(Srcnet.Cli.Commands.ExitCode.NoResults, ambiguous)
  Assert.Contains("一意に決まりません", diagnostics)

  let struct (fileCode, payload, _) =
    runCli [ "show"; "shapes.c"; "--out"; indexed.Output; "--json" ]

  Assert.Equal(Srcnet.Cli.Commands.ExitCode.Success, fileCode)
  use document = JsonDocument.Parse payload
  let file = document.RootElement.GetProperty("nodes")[0]
  Assert.Equal("File", file.GetProperty("kind").GetString())
  let id = file.GetProperty("id").GetString()
  Assert.NotNull id

  let struct (idCode, byId, _) = runCli [ "show"; id; "--out"; indexed.Output; "--json" ]
  Assert.Equal(Srcnet.Cli.Commands.ExitCode.Success, idCode)
  use resolved = JsonDocument.Parse byId
  let resolvedNode = resolved.RootElement.GetProperty("nodes")[0]
  Assert.Equal(id, resolvedNode.GetProperty("id").GetString())

[<Fact>]
let ``結果は stdout、診断は stderr へ出る`` () =
  use indexed = new Indexed("micro")

  // JSON 出力では、機械が読む先を 1 つに保つため診断も封筒の中に入れる
  // （docs/query-and-cli.md 5.1）。stdout は JSON だけになり、パイプ処理を壊さない。
  let struct (_, jsonOutput, _) =
    runCli [ "search"; "Area"; "--out"; indexed.Output; "--json" ]

  use document = JsonDocument.Parse jsonOutput

  let diagnostics =
    document.RootElement.GetProperty("diagnostics").EnumerateArray()
    |> Seq.map (fun item -> item.GetString())
    |> Seq.toArray

  Assert.Contains(diagnostics, fun text -> text <> null && text.Contains "検索索引")

  // テキスト出力では、結果は stdout、診断は stderr へ分ける。
  let struct (_, textOutput, textErrors) =
    runCli [ "search"; "Area"; "--out"; indexed.Output ]

  Assert.Contains("Area", textOutput)
  Assert.DoesNotContain("検索索引", textOutput)
  Assert.Contains("検索索引", textErrors)

[<Fact>]
let ``生成物が無ければ明示的に失敗する`` () =
  let missing = Path.Combine(Path.GetTempPath(), "srcnet-missing-" + Guid.NewGuid().ToString "N")

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
