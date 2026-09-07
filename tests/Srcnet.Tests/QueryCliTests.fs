module Srcnet.Tests.QueryCliTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Xunit
open Srcnet.Cli

type private Workspace() =
  let directory = Path.Combine(Path.GetTempPath(), "srcnet-query-contract-" + Guid.NewGuid().ToString("N"))
  do Directory.CreateDirectory directory |> ignore
  member _.Path = directory
  interface IDisposable with
    member _.Dispose() = Directory.Delete(directory, true)

let private index output corpus =
  task {
    let! struct (code, _, errors) =
      Corpus.runCli [ "index"; Corpus.corpusPath corpus; "--out"; output; "--tier"; "2"; "--jobs"; "1" ]
    Assert.True(code = 0 || code = 4, errors)
  }

let private fileIds output =
  task {
    let! struct (code, json, errors) =
      Corpus.runCli [ "show"; "shapes.c"; "--out"; output; "--json" ]
    Assert.Equal(0, code)
    Assert.Equal("", errors)
    use document = JsonDocument.Parse json
    let nodes = document.RootElement.GetProperty("nodes")
    let file = nodes[0].GetProperty("id").GetString()
    let edge = document.RootElement.GetProperty("edges")[0]
    let parent = edge.GetProperty("from").GetString()
    return file, parent
  }

let private assertBudget budget limit (json: string) =
  use document = JsonDocument.Parse json
  let root = document.RootElement
  for property in
    [ "schemaVersion"; "query"; "nodes"; "edges"; "diagnostics"; "truncated"; "omittedCount"
      "omittedNodeCount"; "omittedEdgeCount"; "omittedDiagnosticCount"; "omittedCountIsLowerBound" ] do
    Assert.True(root.TryGetProperty(property) |> fst, property)
  Assert.Equal(QueryCommands.estimateTokens json, root.GetProperty("tokenEstimate").GetInt32())
  Assert.InRange(root.GetProperty("tokenEstimate").GetInt32(), 0, budget)
  Assert.InRange(root.GetProperty("nodes").GetArrayLength(), 0, limit)
  let omitted =
    root.GetProperty("omittedNodeCount").GetInt64()
    + root.GetProperty("omittedEdgeCount").GetInt64()
    + root.GetProperty("omittedDiagnosticCount").GetInt64()
  Assert.Equal(omitted, root.GetProperty("omittedCount").GetInt64())
  if root.GetProperty("truncated").GetBoolean() then
    Assert.True(omitted > 0L || root.GetProperty("omittedCountIsLowerBound").GetBoolean())
  let ids =
    root.GetProperty("nodes").EnumerateArray()
    |> Seq.map (fun node -> node.GetProperty("id").GetString())
    |> Set.ofSeq
  for edge in root.GetProperty("edges").EnumerateArray() do
    Assert.True(ids.Contains(edge.GetProperty("from").GetString()), "dangling from")
    Assert.True(ids.Contains(edge.GetProperty("to").GetString()), "dangling to")

[<Theory>]
[<InlineData("search")>]
[<InlineData("show")>]
[<InlineData("neighbors")>]
[<InlineData("path")>]
[<InlineData("context")>]
let ``every query bounds the complete serialized envelope`` kind =
  task {
    use workspace = new Workspace()
    do! index workspace.Path "micro"
    let! file, parent = fileIds workspace.Path
    let command =
      match kind with
      | "search" -> [ kind; "shapes" ]
      | "show" -> [ kind; file ]
      | "neighbors" -> [ kind; file; "--depth"; "3" ]
      | "path" -> [ kind; parent; file; "--direction"; "out"; "--depth"; "16" ]
      | _ -> [ kind; "shapes"; "Area"; "--depth"; "2" ]
    for budget in [ 256; 512; 1024 ] do
      for limit in [ 1; 3; 10 ] do
        let arguments =
          command @ [ "--out"; workspace.Path; "--json"; "--budget"; string budget; "--limit"; string limit ]
        let! struct (code, json, errors) = Corpus.runCli arguments
        Assert.Equal(0, code)
        Assert.Equal("", errors)
        assertBudget budget limit json
    let arguments = command @ [ "--out"; workspace.Path; "--json"; "--budget"; "512"; "--limit"; "3" ]
    let! struct (_, first, _) = Corpus.runCli arguments
    let! struct (_, second, _) = Corpus.runCli arguments
    Assert.Equal(first, second)
  }

[<Fact>]
let ``valid small budgets also bound errors and invalid options produce JSON`` () =
  task {
    use workspace = new Workspace()
    do! index workspace.Path "micro"
    let! file, _ = fileIds workspace.Path
    for command, expected in
      [ [ "show"; "ffffffffffffffffffffffffffffffff" ], 1
        [ "neighbors"; file; "--edge"; "NOPE" ], 2
        [ "neighbors"; file; "--direction"; "sideways" ], 2
        [ "neighbors"; file; "--depth"; "17" ], 2
        [ "search"; "shapes"; "--limit"; "10001" ], 2 ] do
      let! struct (code, json, errors) =
        Corpus.runCli (command @ [ "--out"; workspace.Path; "--json"; "--budget"; "256" ])
      Assert.Equal(expected, code)
      Assert.Equal("", errors)
      assertBudget 256 Args.DefaultLimit json
      use document = JsonDocument.Parse json
      Assert.NotEmpty(document.RootElement.GetProperty("diagnostics").EnumerateArray())

    let! struct (code, json, errors) =
      Corpus.runCli [ "show"; file; "--out"; workspace.Path; "--json"; "--budget"; "1" ]
    Assert.Equal(2, code)
    Assert.Equal("", errors)
    assertBudget Args.DefaultBudget Args.DefaultLimit json
  }

[<Fact>]
let ``text output includes diagnostics within its budget`` () =
  task {
    use workspace = new Workspace()
    do! index workspace.Path "micro"
    let! struct (code, output, errors) =
      Corpus.runCli [ "context"; "shapes"; "Area"; "--out"; workspace.Path; "--budget"; "256"; "--limit"; "30" ]
    Assert.Equal(0, code)
    Assert.InRange(QueryCommands.estimateTokens(output + errors), 1, 256)
    Assert.NotEqual<string>("", errors)
  }

[<Corpus.ParserFact>]
let ``show includes the owning file and context follows its requested depth`` () =
  task {
    use workspace = new Workspace()
    do! index workspace.Path "micro"
    let! struct (searchCode, matches, _) =
      Corpus.runCli [ "search"; "point_area"; "--out"; workspace.Path; "--json"; "--limit"; "1" ]
    Assert.Equal(0, searchCode)
    use matched = JsonDocument.Parse matches
    let matchedNode = matched.RootElement.GetProperty("nodes")[0]
    let id = matchedNode.GetProperty("id").GetString()
    let! struct (code, json, _) = Corpus.runCli [ "show"; id; "--out"; workspace.Path; "--json" ]
    Assert.Equal(0, code)
    use document = JsonDocument.Parse json
    Assert.Contains(
      document.RootElement.GetProperty("nodes").EnumerateArray(),
      fun node -> node.GetProperty("kind").GetString() = "File" && node.GetProperty("path").GetString() = "shapes.c")

    let! struct (_, shallow, _) =
      Corpus.runCli [ "context"; "injection_target"; "--out"; workspace.Path; "--json"; "--depth"; "0"; "--limit"; "100" ]
    let! struct (_, deep, _) =
      Corpus.runCli [ "context"; "injection_target"; "--out"; workspace.Path; "--json"; "--depth"; "2"; "--limit"; "100" ]
    use first = JsonDocument.Parse shallow
    use second = JsonDocument.Parse deep
    Assert.All(first.RootElement.GetProperty("nodes").EnumerateArray(),
      fun node -> Assert.Equal(0, node.GetProperty("distance").GetInt32()))
    Assert.Contains(second.RootElement.GetProperty("nodes").EnumerateArray(),
      fun node -> node.GetProperty("distance").GetInt32() = 2)
  }

[<Fact>]
let ``CJK and escaped metadata use the same serialized token count`` () =
  task {
    use workspace = new Workspace()
    do! index workspace.Path "cjk"
    for query in [ "\u9762\u7a4d"; "\ud55c\uad6d\uc5b4"; "\ud83d\ude00\"<tag>\n" ] do
      let! struct (code, json, errors) =
        Corpus.runCli [ "search"; query; "--out"; workspace.Path; "--json"; "--budget"; "512"; "--limit"; "5" ]
      Assert.True(code = 0 || code = 1, errors)
      Assert.Equal("", errors)
      assertBudget 512 5 json
  }

[<Fact>]
let ``null query text and null or empty keyword arrays are rejected before opening artifacts`` () =
  use workspace = new Workspace()
  let limits: Args.QueryLimits = { Limit = Args.DefaultLimit; Budget = Args.DefaultBudget }
  let output = ValueSome workspace.Path
  Assert.Equal(
    Commands.ExitCode.UserError,
    QueryCommands.search
      { OutputDirectory = output; RootPath = ValueNone; Text = null
        IgnoreCase = false; Limits = limits; Json = true }
      CancellationToken.None)
  Assert.Equal(
    Commands.ExitCode.UserError,
    QueryCommands.show
      { OutputDirectory = output; RootPath = ValueNone; Node = null; Limits = limits; Json = true }
      CancellationToken.None)
  Assert.Equal(
    Commands.ExitCode.UserError,
    QueryCommands.neighbors
      { OutputDirectory = output; RootPath = ValueNone; Node = null; Edges = Array.empty
        Direction = "both"; Depth = 1; Limits = limits; Json = true }
      CancellationToken.None)
  Assert.Equal(
    Commands.ExitCode.UserError,
    QueryCommands.path
      { OutputDirectory = output; RootPath = ValueNone; From = "from"; To = null
        Edges = Array.empty; Direction = "both"; Depth = 1; Limits = limits; Json = true }
      CancellationToken.None)
  for keywords in [| null; Array.empty; [| null |] |] do
    Assert.Equal(
      Commands.ExitCode.UserError,
      QueryCommands.context
        { OutputDirectory = output; RootPath = ValueNone; Keywords = keywords
          Depth = 1; Limits = limits; Json = true }
        CancellationToken.None)

[<Fact>]
let ``error diagnostics do not materialize unbounded scalar arrays`` () =
  QueryCommands.writeError "search" Commands.ExitCode.UserError "warmup" true |> ignore
  let message = String('x', 1_000_000)
  let before = GC.GetAllocatedBytesForCurrentThread()
  let code = QueryCommands.writeError "search" Commands.ExitCode.UserError message true
  let allocated = GC.GetAllocatedBytesForCurrentThread() - before
  Assert.Equal(Commands.ExitCode.UserError, code)
  Assert.InRange(allocated, 0L, 1_048_576L)

[<Fact>]
let ``long error diagnostics preserve Unicode scalar boundaries and report omissions`` () =
  task {
    let command = "x" + String.replicate 6000 "\ud83d\ude00"
    let! struct (code, json, errors) = Corpus.runCli [ command; "--json"; "--budget"; "256" ]
    Assert.Equal(Commands.ExitCode.UserError, code)
    Assert.Equal("", errors)
    assertBudget 256 0 json
    use document = JsonDocument.Parse json
    let root = document.RootElement
    Assert.True(root.GetProperty("diagnosticsTruncated").GetBoolean())
    Assert.Equal(1, root.GetProperty("omittedDiagnosticCount").GetInt32())
    let diagnostic = (root.GetProperty("diagnostics")[0]).GetString()
    Assert.DoesNotContain("\uFFFD", diagnostic)
    Assert.EndsWith("\ud83d\ude00", diagnostic)
  }
