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
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Xunit
open Srcnet.Cli

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
let private indexCorpus (corpus: string) (output: string) =
  let arguments: Args.IndexArguments =
    { RootPath = Corpus.corpusPath corpus
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
      Json = true }

  let code = (Commands.index arguments CancellationToken.None).GetAwaiter().GetResult()
  if code <> 0 && code <> 4 then failwith $"索引の生成に失敗しました（終了コード {code}）"

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

  Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml (exportArguments workspace.Path file) CancellationToken.None)
  Assert.True(File.Exists file)

  let payload = dataBlock (File.ReadAllText file)
  use document = JsonDocument.Parse payload
  Assert.True(document.RootElement.GetProperty("aggregated").GetBoolean())

  // 集約ではディレクトリとリポジトリだけを残す。シンボルは載せない。
  let kinds =
    document.RootElement.GetProperty("nodes").EnumerateArray()
    |> Seq.map (fun node -> node.GetProperty("kind").GetString())
    |> Seq.distinct
    |> Seq.toArray

  Assert.All(kinds, fun kind -> Assert.True(kind = "Repository" || kind = "Directory"))

[<Fact>]
let ``起点を指定するとその周辺を出す`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "graph.html"

  let arguments =
    { exportArguments workspace.Path file with Query = "point_area" }

  Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml arguments CancellationToken.None)

  use document = JsonDocument.Parse(dataBlock (File.ReadAllText file))
  let root = document.RootElement
  Assert.False(root.GetProperty("aggregated").GetBoolean())
  Assert.True(root.GetProperty("nodes").GetArrayLength() > 1)
  // 起点は必ず結果に含まれる。
  Assert.True(root.GetProperty("seedIndex").GetInt32() >= 0)

[<Fact>]
let ``同じ引数からはバイト単位に同じ HTML が出る`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let first = workspace.File "a.html"
  let second = workspace.File "b.html"

  let run file =
    ExportCommands.exportHtml { exportArguments workspace.Path file with Query = "Point" } CancellationToken.None
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
        Query = "injection_target"
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
    |> Seq.map (fun node -> node.GetProperty("qualifiedName").GetString())
    |> Seq.toArray

  Assert.Contains(names, fun name -> name <> null && name.Contains "</script>")

[<Fact>]
let ``CJK の名前とパスが文字化けせずに載る`` () =
  use workspace = new Workspace()
  indexCorpus "cjk" workspace.Path
  let file = workspace.File "graph.html"

  let arguments = { exportArguments workspace.Path file with Query = "面積" }
  Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml arguments CancellationToken.None)

  let html = File.ReadAllText file
  // 置換文字が現れたら復号か書き出しが壊れている。
  Assert.DoesNotContain("\uFFFD", html)

  use document = JsonDocument.Parse(dataBlock html)

  let names =
    document.RootElement.GetProperty("nodes").EnumerateArray()
    |> Seq.map (fun node -> node.GetProperty("qualifiedName").GetString())
    |> Seq.toArray

  Assert.Contains(names, fun name -> name <> null && name.Contains "面積")

[<Fact>]
let ``上限を超えると打ち切りとして表示される`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "graph.html"

  let arguments =
    { exportArguments workspace.Path file with
        Query = "point_area"
        Depth = 3
        MaxNodes = 2 }

  Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml arguments CancellationToken.None)

  use document = JsonDocument.Parse(dataBlock (File.ReadAllText file))
  let root = document.RootElement
  Assert.Equal(2, root.GetProperty("nodes").GetArrayLength())
  Assert.True(root.GetProperty("truncated").GetBoolean())
  Assert.True(root.GetProperty("omittedCount").GetInt32() > 0)

  // 打ち切りは診断としても現れる。黙って捨てない。
  let diagnostics =
    root.GetProperty("diagnostics").EnumerateArray()
    |> Seq.map (fun item -> item.GetString())
    |> Seq.toArray

  Assert.Contains(diagnostics, fun text -> text <> null && text.Contains "省略")

[<Fact>]
let ``起点が見つからなければ既存のファイルを保持したまま失敗する`` () =
  use workspace = new Workspace()
  indexCorpus "micro" workspace.Path
  let file = workspace.File "graph.html"
  File.WriteAllText(file, "既存の内容", UTF8Encoding false)

  let arguments =
    { exportArguments workspace.Path file with Query = "この名前は存在しない" }

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
  ExportCommands.exportHtml (exportArguments workspace.Path file) CancellationToken.None |> ignore
  let html = File.ReadAllText file

  // CDN、外部スクリプト、外部スタイル、画像取得のいずれも含まない。
  for forbidden in [ "http://"; "https://"; "//cdn"; "<link"; "src=" ] do
    Assert.DoesNotContain(forbidden, html)
