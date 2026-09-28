/// 対話的な HTML グラフの出力（backlog 029）。
///
/// 検証する不変条件は次のとおり。
/// 1. 単一ファイルで完結し、外部への参照を持たない
/// 2. リポジトリ由来の名前が HTML / JavaScript の文脈へ漏れない
/// 3. 同じ成果物と引数からバイト単位に同じ HTML が出る
/// 4. 上限超過は集約または打ち切りとして表示され、黙って捨てない
/// 5. 起点が決まらない場合、不完全な HTML を成功扱いで残さない
module Srcnet.Tests.ExportTests

open System
open System.Buffers.Binary
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Xunit
open Srcnet.Cli
open Srcnet.Storage
open Srcnet.Core.Graph
open Srcnet.Core.Ids

/// テストごとに使い捨てる出力先。
type private Workspace() =
  let path =
    Path.Combine(Path.GetTempPath(), "srcnet-export-" + Guid.NewGuid().ToString "N")

  do Directory.CreateDirectory path |> ignore

  member _.Path = path

  member _.File(name: string) = Path.Combine(path, name)

  interface IDisposable with

    member _.Dispose() =
      if Directory.Exists path then
        try
          Directory.Delete(path, true)
        with :? IOException ->
          ()

/// コーパスを索引して、出力先を返す。
let private indexPath (root: string) (output: string) =
  let arguments: Args.IndexArguments =
    { RootPath = root
      OutputDirectory = ValueSome output
      RepositoryId = ValueSome "export-test"
      Jobs = ValueSome 1
      MaxFileSizeBytes = ValueNone
      MaxDepth = ValueNone
      Tier = 2
      AssumeEncoding = ValueSome "EUC-JP"
      RespectIgnoreFiles = true
      FollowSymbolicLinks = false
      AllowPartial = true
      Progress = "never"
      MemoryLimit = Args.DefaultMemoryLimit
      TemporaryLimit = Args.DefaultTemporaryLimit
      Json = true }

  let code =
    (Commands.index arguments CancellationToken.None).GetAwaiter().GetResult()

  if code <> 0 && code <> 4 then
    failwith $"索引の生成に失敗しました（終了コード {code}）"

let private indexCorpus corpus output =
  indexPath (Corpus.corpusPath corpus) output

let private exportArguments (output: string) (file: string) : Args.ExportArguments =
  { OutputDirectory = ValueSome output
    RootPath = ValueNone
    File = ValueSome file
    Query = ""
    Node = ""
    Depth = 2
    MaxNodes = Args.DefaultMaxNodes
    Json = false }

/// 埋め込んだデータ ブロックを取り出す。
let private dataBlock (html: string) =
  let matched =
    Regex.Match(html, "<script id=\"srcnet-data\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline)

  Assert.True(matched.Success, "データ ブロックが見つかりません")
  matched.Groups[1].Value

[<Fact>]
let ``起点なしではディレクトリ単位へ集約した概要を出す`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "graph.html"

  Assert.Equal(
    Commands.ExitCode.Success,
    ExportCommands.exportHtml (exportArguments workspace.Path file) CancellationToken.None
  )

  Assert.True(File.Exists file)

  let payload = dataBlock(File.ReadAllText file)
  use document = JsonDocument.Parse payload
  Assert.True(document.RootElement.GetProperty("aggregated").GetBoolean())

  // 集約ではディレクトリとリポジトリだけを残す。シンボルは載せない。
  let kinds =
    document.RootElement.GetProperty("nodes").EnumerateArray()
    |> Seq.map(fun node -> node.GetProperty("kind").GetString())
    |> Seq.distinct
    |> Seq.toArray

  Assert.All(kinds, (fun kind -> Assert.True(kind = "Repository" || kind = "Directory")))

[<Fact>]
let ``deterministic verify は既定の HTML だけを索引比較から除外する`` () =
  task {
    use workspace = new Workspace()
    let source = Corpus.corpusPath "micro"

    let! struct (indexed, _, errors) =
      Corpus.runCli [ "index"; source; "--out"; workspace.Path; "--tier"; "0"; "--jobs"; "1" ]

    Assert.True((indexed = Commands.ExitCode.Success), errors)
    let file = workspace.File ExportCommands.DefaultFileName

    Assert.Equal(
      Commands.ExitCode.Success,
      ExportCommands.exportHtml
        { exportArguments workspace.Path file with
            File = ValueNone }
        CancellationToken.None
    )

    let original = File.ReadAllBytes file

    let arguments: Args.VerifyArguments =
      { OutputDirectory = ValueSome workspace.Path
        RootPath = ValueSome source
        Deterministic = true
        Json = true }

    let! verified = Commands.verify arguments CancellationToken.None
    Assert.Equal(Commands.ExitCode.Success, verified)
    Assert.Equal<byte[]>(original, File.ReadAllBytes file)

    let custom = workspace.File "custom.html"

    Assert.Equal(
      Commands.ExitCode.Success,
      ExportCommands.exportHtml (exportArguments workspace.Path custom) CancellationToken.None
    )

    let customBytes = File.ReadAllBytes custom
    let! withUnexpectedFile = Commands.verify arguments CancellationToken.None
    Assert.Equal(Commands.ExitCode.CompletedWithDiagnostics, withUnexpectedFile)
    Assert.Equal<byte[]>(original, File.ReadAllBytes file)
    Assert.Equal<byte[]>(customBytes, File.ReadAllBytes custom)
  }

[<Fact>]
let ``起点を指定するとその周辺を出す`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "graph.html"

  let arguments =
    { exportArguments workspace.Path file with
        Query = "shapes.c" }

  Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml arguments CancellationToken.None)

  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
  let root = document.RootElement
  Assert.False(root.GetProperty("aggregated").GetBoolean())
  Assert.True(root.GetProperty("nodes").GetArrayLength() > 1)
  // 起点は必ず結果に含まれる。
  Assert.True(root.GetProperty("seedIndex").GetInt32() >= 0)

[<Corpus.ParserFact>]
let ``曖昧なノード名では順位が最上位の候補を起点とし診断を残す`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path

  use view =
    match Query.GraphView.Open workspace.Path with
    | Ok view -> view
    | Error error -> failwith(Query.QueryError.describe error)

  let candidates =
    (Query.search view "Area" false CancellationToken.None).Hits
    |> Array.filter(fun hit ->
      hit.Strength = Query.Exact
      && (hit.Target = Query.Name || hit.Target = Query.QualifiedName))

  Assert.True(candidates.Length > 1)
  Array.sortInPlaceWith (Query.compareHits view) candidates
  let expectedNode = view.Node candidates[0].Node
  let expected = Srcnet.Core.Ids.NodeId.toString expectedNode.Id
  let file = workspace.File "ambiguous.html"

  Assert.Equal(
    Commands.ExitCode.Success,
    ExportCommands.exportHtml
      { exportArguments workspace.Path file with
          Node = "Area" }
      CancellationToken.None
  )

  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
  let root = document.RootElement
  let seed = root.GetProperty("nodes")[root.GetProperty("seedIndex").GetInt32()]
  Assert.Equal(expected, seed.GetProperty("id").GetString())

  let diagnostics =
    root.GetProperty("diagnostics").EnumerateArray()
    |> Seq.map(fun item -> item.GetString())
    |> Seq.toArray

  Assert.Contains(diagnostics, (fun text -> text <> null && text.Contains "最上位"))

  let byId = workspace.File "by-id.html"

  Assert.Equal(
    Commands.ExitCode.Success,
    ExportCommands.exportHtml
      { exportArguments workspace.Path byId with
          Node = expected }
      CancellationToken.None
  )

  use resolved = JsonDocument.Parse(dataBlock(File.ReadAllText byId))
  Assert.DoesNotContain("最上位", resolved.RootElement.GetProperty("diagnostics").GetRawText())

[<Fact>]
let ``同じ引数からはバイト単位に同じ HTML が出る`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let first = workspace.File "a.html"
  let second = workspace.File "b.html"

  let run file =
    ExportCommands.exportHtml
      { exportArguments workspace.Path file with
          Query = "Point" }
      CancellationToken.None
    |> ignore

  run first
  run second

  Assert.Equal<byte[]>(File.ReadAllBytes first, File.ReadAllBytes second)

[<Fact>]
let ``HTML 特殊文字を含む名前がスクリプトとして解釈されない`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "graph.html"

  let arguments =
    { exportArguments workspace.Path file with
        Query = "injection.c"
        Depth = 2 }

  Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml arguments CancellationToken.None)
  let html = File.ReadAllText file
  let payload = dataBlock html

  // データ ブロックの中に生の `<` は現れない。`</script>` で閉じることができない。
  Assert.DoesNotContain("<", payload)

  // 値としては失われていない。復号すれば元の文字列が読める。
  use document = JsonDocument.Parse payload

  let names =
    document.RootElement.GetProperty("nodes").EnumerateArray()
    |> Seq.map(fun node -> node.GetProperty("qualifiedName").GetString())
    |> Seq.toArray

  Assert.Contains(names, (fun name -> name <> null && name.Contains "</script>"))

[<Corpus.ParserFact>]
let ``CJK の名前とパスが文字化けせずに載る`` () =
  use workspace = new Workspace()
  indexCorpus "cjk" workspace.Path
  let file = workspace.File "graph.html"

  let arguments =
    { exportArguments workspace.Path file with
        Query = "面積" }

  Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml arguments CancellationToken.None)

  let html = File.ReadAllText file
  // 置換文字が現れたら復号か書き出しが壊れている。
  Assert.DoesNotContain("\uFFFD", html)

  use document = JsonDocument.Parse(dataBlock html)

  let names =
    document.RootElement.GetProperty("nodes").EnumerateArray()
    |> Seq.map(fun node -> node.GetProperty("qualifiedName").GetString())
    |> Seq.toArray

  Assert.Contains(names, (fun name -> name <> null && name.Contains "面積"))

[<Fact>]
let ``上限を超えると打ち切りとして表示される`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "graph.html"

  let arguments =
    { exportArguments workspace.Path file with
        Query = "shapes.c"
        Depth = 3
        MaxNodes = 2 }

  Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml arguments CancellationToken.None)

  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
  let root = document.RootElement
  Assert.Equal(2, root.GetProperty("nodes").GetArrayLength())
  Assert.True(root.GetProperty("truncated").GetBoolean())
  Assert.True(root.GetProperty("omittedCount").GetInt32() > 0)

  // 打ち切りは診断としても現れる。黙って捨てない。
  let diagnostics =
    root.GetProperty("diagnostics").EnumerateArray()
    |> Seq.map(fun item -> item.GetString())
    |> Seq.toArray

  Assert.Contains(diagnostics, (fun text -> text <> null && text.Contains "省略"))

[<Fact>]
let ``起点が見つからなければ既存のファイルを保持したまま失敗する`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "graph.html"
  File.WriteAllText(file, "既存の内容", UTF8Encoding false)

  let arguments =
    { exportArguments workspace.Path file with
        Query = "この名前は存在しない" }

  Assert.Equal(Commands.ExitCode.NoResults, ExportCommands.exportHtml arguments CancellationToken.None)
  // 不完全な HTML で上書きしない。
  Assert.Equal("既存の内容", File.ReadAllText file)

[<Fact>]
let ``生成物がなければ明示的に失敗する`` () =
  use workspace = new Workspace()
  let missing = Path.Combine(workspace.Path, "absent")
  let file = workspace.File "graph.html"

  Assert.Equal(
    Commands.ExitCode.MissingArtifact,
    ExportCommands.exportHtml (exportArguments missing file) CancellationToken.None
  )

  Assert.False(File.Exists file)

[<Fact>]
let ``HTML は外部への参照を持たない`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "graph.html"

  ExportCommands.exportHtml (exportArguments workspace.Path file) CancellationToken.None
  |> ignore

  let html = File.ReadAllText file

  // CDN、外部スクリプト、外部スタイル、画像取得のいずれも含まない。
  for forbidden in [ "http://"; "https://"; "//cdn"; "<link"; "src=" ] do
    Assert.DoesNotContain(forbidden, html)

let private openView output =
  match Query.GraphView.Open output with
  | Ok view -> view
  | Error error -> failwith(Query.QueryError.describe error)

[<Fact>]
let ``全体件数と集約候補と表示上限による省略を区別する`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  use view = openView workspace.Path

  let candidates =
    [| 0 .. view.NodeCount - 1 |]
    |> Array.filter(fun index -> let kind = (view.Node index).Kind in kind = Directory || kind = Repository)

  Assert.True(candidates.Length > 1)
  let file = workspace.File "overview.html"

  let args =
    { exportArguments workspace.Path file with
        MaxNodes = 1 }

  Assert.Equal(0, ExportCommands.exportHtml args CancellationToken.None)
  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
  let root = document.RootElement
  Assert.Equal(view.NodeCount, root.GetProperty("totalNodes").GetInt32())
  Assert.Equal(candidates.Length, root.GetProperty("candidateNodes").GetInt32())
  Assert.Equal(view.NodeCount - candidates.Length, root.GetProperty("groupedNodeCount").GetInt32())
  Assert.Equal(candidates.Length - 1, root.GetProperty("omittedCount").GetInt32())
  Assert.False(root.GetProperty("omittedCountIsLowerBound").GetBoolean())
  Assert.False(root.GetProperty("traversalTruncated").GetBoolean())
  Assert.True(root.GetProperty("truncated").GetBoolean())

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``概要の走査予算は表示しないエッジも数え重みを保持する`` exceedsBudget =
  let directoryCount = 640
  let names = Array.init directoryCount (fun index -> $"d{index:D4}")

  let allConnections =
    [| for source in 0..directoryCount do
         for target in 0..directoryCount do
           if source <> target then
             yield struct (source, target, Contains) |]

  Assert.True(allConnections.Length > Query.MaxExploredEdges)

  let connections =
    if exceedsBudget then
      allConnections
    else
      Array.take Query.MaxExploredEdges allConnections

  use graph = new QueryTests.TestGraph(names, connections)
  use workspace = new Workspace()
  let file = workspace.File "bounded-overview.html"

  Assert.Equal(
    Commands.ExitCode.Success,
    ExportCommands.exportHtml
      { exportArguments graph.Output file with
          MaxNodes = 1 }
      CancellationToken.None
  )

  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
  let root = document.RootElement
  let nodes = root.GetProperty("nodes")
  Assert.Equal(1, nodes.GetArrayLength())
  Assert.Equal(directoryCount, nodes[0].GetProperty("weight").GetInt32())
  Assert.Equal(directoryCount + 1, root.GetProperty("totalNodes").GetInt32())
  Assert.Equal(directoryCount + 1, root.GetProperty("candidateNodes").GetInt32())
  Assert.Equal(0, root.GetProperty("groupedNodeCount").GetInt32())
  Assert.Equal(directoryCount, root.GetProperty("omittedCount").GetInt32())
  Assert.Equal(Query.MaxExploredEdges, root.GetProperty("omittedEdgeCount").GetInt32())
  Assert.Equal(exceedsBudget, root.GetProperty("traversalTruncated").GetBoolean())
  Assert.Equal(exceedsBudget, root.GetProperty("omittedCountIsLowerBound").GetBoolean())
  Assert.True(root.GetProperty("truncated").GetBoolean())
  Assert.Empty(root.GetProperty("edges").EnumerateArray())

  if exceedsBudget then
    Assert.Contains(
      root.GetProperty("diagnostics").EnumerateArray(),
      fun diagnostic -> diagnostic.GetString().Contains "エッジ走査"
    )

[<Fact>]
let ``ディレクトリのパスは所属ファイルではなく論理パスである`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "paths.html"
  Assert.Equal(0, ExportCommands.exportHtml (exportArguments workspace.Path file) CancellationToken.None)
  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))

  let directories =
    document.RootElement.GetProperty("nodes").EnumerateArray()
    |> Seq.filter(fun node -> node.GetProperty("kind").GetString() = "Directory")
    |> Seq.toArray

  Assert.NotEmpty directories

  for node in directories do
    let logical = node.GetProperty("qualifiedName").GetString()
    Assert.False(String.IsNullOrEmpty logical)
    Assert.Equal(logical, node.GetProperty("path").GetString())

[<Fact>]
let ``深さゼロは起点だけの正常な範囲指定であり探索打ち切りではない`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  use view = openView workspace.Path
  let seed = (view.Node 0).Id |> NodeId.toString

  let traversal =
    Query.neighbors view 0 view.EdgeKinds Query.Both 0 CancellationToken.None

  Assert.False traversal.Truncated
  Assert.Equal(0, traversal.OmittedCount)
  let file = workspace.File "depth-zero.html"

  let args =
    { exportArguments workspace.Path file with
        Node = seed
        Depth = 0
        MaxNodes = 1 }

  Assert.Equal(0, ExportCommands.exportHtml args CancellationToken.None)
  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
  let root = document.RootElement
  Assert.Equal(view.NodeCount, root.GetProperty("totalNodes").GetInt32())
  Assert.Equal(1, root.GetProperty("candidateNodes").GetInt32())
  Assert.Equal(1, root.GetProperty("nodes").GetArrayLength())
  Assert.False(root.GetProperty("truncated").GetBoolean())
  Assert.False(root.GetProperty("traversalTruncated").GetBoolean())
  Assert.Equal(0, root.GetProperty("edges").GetArrayLength())
  Assert.Equal(traversal.OmittedCountIsLowerBound, root.GetProperty("omittedCountIsLowerBound").GetBoolean())
  Assert.Equal(traversal.OmittedCount, root.GetProperty("omittedCount").GetInt32())
  Assert.Equal(traversal.OmittedEdgeCount, root.GetProperty("omittedEdgeCount").GetInt32())

  let diagnostics =
    root.GetProperty("diagnostics").EnumerateArray()
    |> Seq.map _.GetString()
    |> Seq.toArray

  for diagnostic in traversal.Diagnostics do
    Assert.Contains(diagnostic, diagnostics)

[<Fact>]
let ``近傍の省略数は探索と表示上限を含みエッジは実際の向きで重複しない`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  use view = openView workspace.Path
  let seed = (view.Node 0).Id |> NodeId.toString

  let traversal =
    Query.neighbors view 0 view.EdgeKinds Query.Both 2 CancellationToken.None

  let file = workspace.File "capped.html"

  let args =
    { exportArguments workspace.Path file with
        Node = seed
        Depth = 2
        MaxNodes = 3 }

  Assert.Equal(0, ExportCommands.exportHtml args CancellationToken.None)
  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
  let root = document.RootElement
  let nodes = root.GetProperty("nodes").EnumerateArray() |> Seq.toArray
  let edges = root.GetProperty("edges").EnumerateArray() |> Seq.toArray
  Assert.Equal(view.NodeCount, root.GetProperty("totalNodes").GetInt32())
  Assert.Equal(traversal.Nodes.Length, root.GetProperty("candidateNodes").GetInt32())

  Assert.Equal(
    traversal.Nodes.Length - nodes.Length + traversal.OmittedCount,
    root.GetProperty("omittedCount").GetInt32()
  )

  Assert.Equal(
    traversal.Edges.Length - edges.Length + traversal.OmittedEdgeCount,
    root.GetProperty("omittedEdgeCount").GetInt32()
  )

  Assert.Equal(ExportCommands.MaxExportEdges, root.GetProperty("maxExportEdges").GetInt32())
  let identities = edges |> Array.map _.GetRawText()
  Assert.Equal(identities.Length, (Array.distinct identities).Length)

  let indices =
    nodes
    |> Array.map(fun node ->
      match NodeId.tryParse(node.GetProperty("id").GetString()) with
      | ValueSome id ->
        match view.TryResolve id with
        | ValueSome index -> index
        | ValueNone -> failwith "Unknown exported ID"
      | ValueNone -> failwith "Invalid exported ID")

  for edge in edges do
    let kind =
      view.EdgeKinds
      |> Array.tryFind(fun kind -> EdgeKind.name kind = edge.GetProperty("kind").GetString())

    match kind with
    | None -> failwith "Unknown exported edge kind"
    | Some kind ->
      Assert.Contains(
        indices[edge.GetProperty("to").GetInt32()],
        view.Neighbors(indices[edge.GetProperty("from").GetInt32()], kind, Query.Outgoing)
      )

[<Fact>]
let ``不正な文字列オフセットでは既存出力を保持し新規出力も一時ファイルも残さない`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path

  let nameRef =
    use view = openView workspace.Path
    (view.Node 0).NameRef

  let manifest =
    match Manifest.read workspace.Path with
    | Ok manifest -> manifest
    | Error error -> failwith(Manifest.ManifestError.describe error)

  let offsets =
    match
      manifest.Segments
      |> Array.tryFind(fun segment -> segment.Name.EndsWith(".stroffsets", StringComparison.Ordinal))
    with
    | Some segment -> Path.Combine(workspace.Path, segment.Name)
    | None -> failwith "String offsets segment missing"

  let bytes = File.ReadAllBytes offsets
  BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(Format.HeaderLength + int nameRef * 8, 8), UInt64.MaxValue)
  File.WriteAllBytes(offsets, bytes)
  let existing = workspace.File "existing.html"
  File.WriteAllText(existing, "keep")

  for file in [ existing; workspace.File "new.html" ] do
    Assert.Equal(
      Commands.ExitCode.MissingArtifact,
      ExportCommands.exportHtml (exportArguments workspace.Path file) CancellationToken.None
    )

  Assert.Equal("keep", File.ReadAllText existing)
  Assert.False(File.Exists(workspace.File "new.html"))
  Assert.Empty(Directory.GetFiles(workspace.Path, "*.tmp"))

[<Fact>]
let ``絵文字とCJKと引用符を含む論理パスが安全に往復する`` () =
  use source = new Workspace()
  use output = new Workspace()
  let name = "日本語-简体中文-繁體中文-한국어-😀-&'.c"
  File.WriteAllText(source.File name, "int unicode_symbol(void) { return 1; }\n", UTF8Encoding false)
  indexPath source.Path output.Path
  let file = output.File "unicode.html"

  Assert.Equal(
    0,
    ExportCommands.exportHtml
      { exportArguments output.Path file with
          Query = name }
      CancellationToken.None
  )

  let html = File.ReadAllText file
  let payload = dataBlock html
  Assert.DoesNotContain("<", payload)
  Assert.DoesNotContain("\uFFFD", html)
  use document = JsonDocument.Parse payload

  let paths =
    document.RootElement.GetProperty("nodes").EnumerateArray()
    |> Seq.map(fun node -> node.GetProperty("path").GetString())
    |> Seq.toArray

  Assert.Contains(name, paths)

[<Fact>]
let ``同時出力は固有の一時ファイルを使い他の一時ファイルを変更しない`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "shared.html"
  File.WriteAllText(file + ".tmp", "unrelated")

  let run () =
    ExportCommands.exportHtml (exportArguments workspace.Path file) CancellationToken.None

  let tasks =
    Array.init 2 (fun _ -> System.Threading.Tasks.Task.Run<int>(Func<int> run))

  let results = System.Threading.Tasks.Task.WhenAll(tasks).GetAwaiter().GetResult()
  Assert.All(results, (fun code -> Assert.Equal(0, code)))
  let first = File.ReadAllBytes file
  Assert.Equal(0, run())
  Assert.Equal<byte[]>(first, File.ReadAllBytes file)
  Assert.Equal("unrelated", File.ReadAllText(file + ".tmp"))
  Assert.Equal<string[]>([| file + ".tmp" |], Directory.GetFiles(workspace.Path, "*.tmp"))

[<Fact>]
let ``削除共有の読み手が残る HTML を旧内容を保ったまま置き換える`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "held.html"
  let arguments = exportArguments workspace.Path file
  Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml arguments CancellationToken.None)
  let original = File.ReadAllBytes file

  use reader =
    new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

  Assert.Equal(
    Commands.ExitCode.Success,
    ExportCommands.exportHtml { arguments with MaxNodes = 1 } CancellationToken.None
  )

  let current = File.ReadAllBytes file
  Assert.False(ReadOnlySpan(original).SequenceEqual(ReadOnlySpan current))
  let snapshot = Array.zeroCreate<byte> original.Length
  reader.ReadExactly(Span snapshot)
  Assert.Equal<byte[]>(original, snapshot)
  Assert.Empty(Directory.GetFiles(workspace.Path, "*.tmp"))

[<Fact>]
let ``長い出力ファイル名でも一時ファイル名の上限を超えず置き換える`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path

  for name in [ String('a', 220) + ".html"; String.replicate 80 "界" + ".html" ] do
    let file = workspace.File name
    File.WriteAllText(file, "keep")

    Assert.Equal(
      Commands.ExitCode.Success,
      ExportCommands.exportHtml (exportArguments workspace.Path file) CancellationToken.None
    )

    use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
    Assert.NotEmpty(document.RootElement.GetProperty("nodes").EnumerateArray())

  Assert.Empty(Directory.GetFiles(workspace.Path, "*.tmp"))

[<Fact>]
let ``取り消し済みの出力は既存ファイルを変更しない`` () =
  use workspace = new Workspace()
  let file = workspace.File "cancelled.html"
  File.WriteAllText(file, "keep")
  use cancellation = new CancellationTokenSource()
  cancellation.Cancel()

  Assert.ThrowsAny<OperationCanceledException>(fun () ->
    ExportCommands.exportHtml (exportArguments workspace.Path file) cancellation.Token
    |> ignore)
  |> ignore

  Assert.Equal("keep", File.ReadAllText file)
  Assert.Empty(Directory.GetFiles(workspace.Path, "*.tmp"))

[<Fact>]
let ``方向コントロールと診断領域が単独で埋め込まれM6属性を捏造しない`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "controls.html"
  Assert.Equal(0, ExportCommands.exportHtml (exportArguments workspace.Path file) CancellationToken.None)
  let html = File.ReadAllText file
  Assert.Contains("id=\"direction\"", html)
  Assert.Contains("aria-describedby=\"directionReference\"", html)
  let footer = Regex.Match(html, "<footer id=\"status\"[\\s\\S]*?</footer>")
  Assert.True footer.Success

  for id in [ "counts"; "truncation"; "diagnostics" ] do
    Assert.Contains($"id=\"{id}\"", footer.Value)

  use document = JsonDocument.Parse(dataBlock html)

  for node in document.RootElement.GetProperty("nodes").EnumerateArray() do
    Assert.False(node.TryGetProperty("community") |> fst)
    Assert.False(node.TryGetProperty("centrality") |> fst)
    Assert.False(node.TryGetProperty("hub") |> fst)

[<Corpus.ParserFact>]
let ``密な近傍はノード上限内でもエッジ上限で打ち切る`` () =
  use source = new Workspace()
  use output = new Workspace()
  let code = StringBuilder()

  for from in 0..229 do
    code.Append($"int f{from:D3}(void) {{ return 0; }}\n") |> ignore

  File.WriteAllText(source.File "dense.c", code.ToString(), UTF8Encoding false)
  indexPath source.Path output.Path

  let manifest =
    match Manifest.read output.Path with
    | Ok value -> value
    | Error error -> failwith(Manifest.ManifestError.describe error)
  // M3 の解決器に依存せず、対称な完全 CALLS グラフを正規の CSR 形式で用意する。
  let count = manifest.Counts.Nodes
  Assert.Equal(232, count)
  let edgeCount = 230 * 229
  let payloadLength = (count + 1) * 8 + edgeCount * 4
  let bytes = Array.zeroCreate<byte>(Format.HeaderLength + payloadLength)

  Format.writeHeader
    (bytes.AsSpan(0, Format.HeaderLength))
    { Kind = Format.AdjacencyCsr
      PrimaryCount = uint64 count
      SecondaryCount = uint64 edgeCount
      RecordLength = 0u
      PayloadLength = uint64 payloadLength }

  let mutable offset = 0

  for index in 0..count do
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(Format.HeaderLength + index * 8, 8), uint64 offset)

    if index >= 2 && index < count then
      for target in 2 .. count - 1 do
        if target <> index then
          BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(Format.HeaderLength + (count + 1) * 8 + offset * 4, 4),
            uint32 target
          )

          offset <- offset + 1

  let forward =
    match
      manifest.Segments
      |> Array.tryFind(fun item -> item.Name.EndsWith(".edges.CONTAINS", StringComparison.Ordinal))
    with
    | Some value -> value.Name
    | None -> failwith "CONTAINS segment missing"

  let added =
    [| forward.Replace(".edges.CONTAINS", ".edges.CALLS")
       forward.Replace(".edges.CONTAINS", ".redges.CALLS") |]
    |> Array.map(fun name ->
      File.WriteAllBytes(Path.Combine(output.Path, name), bytes)

      ({ Name = name
         ByteLength = int64 bytes.Length
         Checksum = Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData bytes) }
      : Writer.SegmentDescriptor))

  let updated =
    { manifest with
        Segments = Array.append manifest.Segments added |> Array.sortBy _.Name
        Counts =
          { manifest.Counts with
              Edges = manifest.Counts.Edges + edgeCount
              EdgeKinds =
                Array.append manifest.Counts.EdgeKinds [| { Kind = "CALLS"; Count = edgeCount } |]
                |> Array.sortBy _.Kind } }

  match Manifest.write output.Path updated with
  | Ok() -> ()
  | Error error -> failwith(Artifact.PathError.describe error)

  use view = openView output.Path

  let traversal =
    Query.neighbors view 0 view.EdgeKinds Query.Both 4 CancellationToken.None

  Assert.True(traversal.Edges.Length > ExportCommands.MaxExportEdges)
  Assert.True(traversal.Nodes.Length < Args.DefaultMaxNodes)
  let file = output.File "dense.html"

  let args =
    { exportArguments output.Path file with
        Node = NodeId.toString (view.Node 0).Id
        Depth = 4 }

  Assert.Equal(0, ExportCommands.exportHtml args CancellationToken.None)
  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
  let root = document.RootElement
  Assert.Equal(ExportCommands.MaxExportEdges, root.GetProperty("edges").GetArrayLength())
  Assert.Equal(traversal.Nodes.Length, root.GetProperty("nodes").GetArrayLength())
  Assert.Equal(traversal.OmittedCount, root.GetProperty("omittedCount").GetInt32())

  Assert.Equal(
    traversal.Edges.Length - ExportCommands.MaxExportEdges
    + traversal.OmittedEdgeCount,
    root.GetProperty("omittedEdgeCount").GetInt32()
  )

  Assert.True(root.GetProperty("truncated").GetBoolean())

[<Fact>]
let ``出力リンクはリンク先も既存リンクも変更せず拒否する`` () =
  if not(OperatingSystem.IsWindows()) then
    use workspace = new Workspace()
    indexCorpus "micro" workspace.Path
    let target = workspace.File "target.html"
    let link = workspace.File "link.html"
    File.WriteAllText(target, "keep")
    File.CreateSymbolicLink(link, target) |> ignore

    Assert.Equal(
      Commands.ExitCode.UserError,
      ExportCommands.exportHtml (exportArguments workspace.Path link) CancellationToken.None
    )

    Assert.Equal("keep", File.ReadAllText target)
    Assert.True(Artifact.isLink link)
    Assert.Empty(Directory.GetFiles(workspace.Path, "*.tmp"))

[<Fact>]
let ``直接呼び出しの不正な引数は成果物を開く前に拒否する`` () =
  use workspace = new Workspace()
  let file = workspace.File "preserved.html"
  File.WriteAllText(file, "keep")

  let arguments =
    { exportArguments (workspace.File "missing") file with
        Json = true }

  let invalid =
    [| { arguments with Depth = -1 }
       { arguments with
           Depth = Args.MaxQueryDepth + 1 }
       { arguments with
           Depth = Int32.MaxValue }
       { arguments with MaxNodes = -1 }
       { arguments with
           MaxNodes = Args.MaxExportNodes + 1 }
       { arguments with
           MaxNodes = Int32.MaxValue }
       { arguments with
           Node = "node"
           Query = "query" }
       { arguments with
           Node = String('x', Args.MaxQueryScalars + 1) }
       { arguments with
           Query = String('x', Args.MaxQueryScalars + 1) }
       { arguments with
           Query = String(char 0xD800, 1) }
       { arguments with
           Node = String(char 0xD800, 1) }
       { arguments with Query = null }
       { arguments with Node = null }
       { arguments with File = ValueSome "" }
       { arguments with
           RootPath = ValueSome "" }
       { arguments with
           OutputDirectory = ValueSome "" } |]

  for index in 0 .. invalid.Length - 1 do
    let actual = ExportCommands.exportHtml invalid[index] CancellationToken.None
    Assert.True((actual = Commands.ExitCode.UserError), $"Invalid argument case {index} returned {actual}")
    Assert.Equal("keep", File.ReadAllText file)

  Assert.Empty(Directory.GetFiles(workspace.Path, "*.tmp"))

[<Fact>]
let ``直接呼び出しでもノード上限ゼロは既定値で深さゼロは保持する`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let standard = workspace.File "default.html"
  let zero = workspace.File "zero.html"
  let arguments = exportArguments workspace.Path standard
  Assert.Equal(0, ExportCommands.exportHtml arguments CancellationToken.None)

  Assert.Equal(
    0,
    ExportCommands.exportHtml
      { arguments with
          File = ValueSome zero
          MaxNodes = 0 }
      CancellationToken.None
  )

  Assert.Equal<byte[]>(File.ReadAllBytes standard, File.ReadAllBytes zero)
  let file = workspace.File "depth.html"

  let rootId =
    use view = openView workspace.Path
    NodeId.toString (view.Node 0).Id

  Assert.Equal(
    0,
    ExportCommands.exportHtml
      { arguments with
          File = ValueSome file
          Node = rootId
          Depth = 0
          MaxNodes = 0 }
      CancellationToken.None
  )

  use document = JsonDocument.Parse(dataBlock(File.ReadAllText file))
  Assert.Equal(0, document.RootElement.GetProperty("query").GetProperty("depth").GetInt32())
  Assert.Equal(1, document.RootElement.GetProperty("nodes").GetArrayLength())

[<Corpus.ParserFact>]
let ``名前検索が打ち切られたとき既知の完全一致を勝手に起点にしない`` () =
  use source = new Workspace()
  use output = new Workspace()

  let code =
    String.replicate (Query.MaxScannedNodes + 1) "int f(void) { return 0; }\n"

  File.WriteAllText(source.File "lookup.c", code, UTF8Encoding false)
  indexPath source.Path output.Path

  let id =
    use view = openView output.Path
    let found = Query.searchExactNames view "f" false CancellationToken.None
    Assert.True(found.Truncated)
    Assert.Equal(Query.MaxScannedNodes, found.Hits.Length)
    let exact = found.Hits |> Array.tryFind(fun hit -> hit.Strength = Query.Exact)

    match exact with
    | Some hit -> NodeId.toString (view.Node hit.Node).Id
    | None -> failwith "The fixture must include a returned exact match"

  let file = output.File "incomplete.html"
  File.WriteAllText(file, "keep")

  let arguments =
    { exportArguments output.Path file with
        Node = "f"
        Depth = 0 }

  Assert.Equal(Commands.ExitCode.NoResults, ExportCommands.exportHtml arguments CancellationToken.None)
  Assert.Equal("keep", File.ReadAllText file)
  Assert.Empty(Directory.GetFiles(output.Path, "*.tmp"))
  Assert.Equal(0, ExportCommands.exportHtml { arguments with Node = id } CancellationToken.None)
