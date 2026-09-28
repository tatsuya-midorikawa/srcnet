module Srcnet.Tests.QueryCliTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Xunit
open Srcnet.Cli

type private Workspace() =
  let directory =
    Path.Combine(Path.GetTempPath(), "srcnet-query-contract-" + Guid.NewGuid().ToString("N"))

  do Directory.CreateDirectory directory |> ignore
  member _.Path = directory

  interface IDisposable with
    member _.Dispose() = Directory.Delete(directory, true)

let private index output corpus =
  task {
    let! struct (code, _, errors) =
      Corpus.runCli
        [ "index"
          Corpus.corpusPath corpus
          "--out"
          output
          "--tier"
          "2"
          "--jobs"
          "1" ]

    Assert.True(code = 0 || code = 4, errors)
  }

let private fileIds output =
  task {
    let! struct (code, json, errors) = Corpus.runCli [ "show"; "shapes.c"; "--out"; output; "--json" ]
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
    [ "schemaVersion"
      "query"
      "nodes"
      "edges"
      "diagnostics"
      "truncated"
      "omittedCount"
      "omittedNodeCount"
      "omittedEdgeCount"
      "omittedDiagnosticCount"
      "omittedCountIsLowerBound" ] do
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
    |> Seq.map(fun node -> node.GetProperty("id").GetString())
    |> Set.ofSeq

  for edge in root.GetProperty("edges").EnumerateArray() do
    Assert.True(ids.Contains(edge.GetProperty("from").GetString()), "dangling from")
    Assert.True(ids.Contains(edge.GetProperty("to").GetString()), "dangling to")

[<Theory>]
[<InlineData("index", 2)>]
[<InlineData("stats", 3)>]
[<InlineData("verify", 3)>]
let ``management failures return one bounded JSON result`` command expected =
  task {
    use workspace = new Workspace()
    let missing = Path.Combine(workspace.Path, "missing")

    for options, exitCode in [ [], expected; [ "--bogus" ], 2 ] do
      let arguments =
        if command = "index" then
          [ command; missing; "--json" ]
        else
          [ command; "--out"; missing; "--json" ]

      let! struct (code, json, errors) = Corpus.runCli(arguments @ options)
      Assert.Equal(exitCode, code)
      Assert.Equal("", errors)
      use document = JsonDocument.Parse json
      let result = document.RootElement
      Assert.Equal(command, result.GetProperty("command").GetString())
      Assert.Equal(code, result.GetProperty("exitCode").GetInt32())
      Assert.False(result.GetProperty("hasResult").GetBoolean())
      Assert.InRange(result.GetProperty("diagnosticDetails").GetArrayLength(), 1, 32)
      Assert.InRange(json.Length, 1, 16384)
  }

[<Fact>]
let ``management success diagnostics and corruption preserve JSON contracts`` () =
  task {
    use workspace = new Workspace()
    let source = Path.Combine(workspace.Path, "source")
    let output = Path.Combine(workspace.Path, "index")
    Directory.CreateDirectory source |> ignore
    File.WriteAllText(Path.Combine(source, "sample.c"), "int sample;\n")

    for tier, extra, expected in [ "0", [], 0; "1", [ "--max-file-size=1" ], 4 ] do
      let! struct (code, json, errors) =
        Corpus.runCli([ "index"; source; "--out"; output; "--tier"; tier; "--json" ] @ extra)

      Assert.Equal(expected, code)
      Assert.Equal("", errors)
      use indexed = JsonDocument.Parse json
      Assert.Equal(code, indexed.RootElement.GetProperty("exitCode").GetInt32())
      Assert.True(indexed.RootElement.GetProperty("hasResult").GetBoolean())
      Assert.True(indexed.RootElement.TryGetProperty("complete") |> fst)

      if expected = 4 then
        Assert.NotEmpty(indexed.RootElement.GetProperty("diagnosticDetails").EnumerateArray())

      for command in [ "stats"; "verify" ] do
        let! struct (resultCode, resultJson, resultErrors) = Corpus.runCli [ command; "--out"; output; "--json" ]
        Assert.Equal(0, resultCode)
        Assert.Equal("", resultErrors)
        use result = JsonDocument.Parse resultJson
        Assert.Equal(resultCode, result.RootElement.GetProperty("exitCode").GetInt32())
        Assert.True(result.RootElement.GetProperty("hasResult").GetBoolean())

    File.WriteAllText(Path.Combine(output, "manifest.json"), "{")

    for command in [ "stats"; "verify" ] do
      let! struct (code, json, errors) = Corpus.runCli [ command; "--out"; output; "--json" ]
      Assert.Equal(3, code)
      Assert.Equal("", errors)
      use result = JsonDocument.Parse json
      Assert.Equal(code, result.RootElement.GetProperty("exitCode").GetInt32())
      Assert.False(result.RootElement.GetProperty("hasResult").GetBoolean())
  }

[<Fact>]
let ``freshness detects same size edits without modifying the index`` () =
  task {
    use workspace = new Workspace()
    let source = Path.Combine(workspace.Path, "source")
    let output = Path.Combine(workspace.Path, "index")
    Directory.CreateDirectory source |> ignore
    let path = Path.Combine(source, "日本語 中文 한국어.c")
    File.WriteAllText(path, "int before;\n")
    File.WriteAllText(Path.Combine(source, "other.c"), "int other;\n")
    let! struct (indexed, _, _) = Corpus.runCli [ "index"; source; "--out"; output; "--tier"; "0" ]
    Assert.Equal(0, indexed)
    let manifest = File.ReadAllBytes(Path.Combine(output, "manifest.json"))
    let timestamp = File.GetLastWriteTimeUtc path

    for expected in [ "unchanged"; "changed"; "deleted" ] do
      if expected = "changed" then
        File.WriteAllText(path, "int after_;\n")
        File.SetLastWriteTimeUtc(path, timestamp)
      elif expected = "deleted" then
        File.Delete path

      let! struct (code, json, errors) =
        Corpus.runCli [ "freshness"; "日本語 中文 한국어.c"; "--root"; source; "--out"; output; "--json" ]

      Assert.Equal((if expected = "unchanged" then 0 else 4), code)
      Assert.Equal("", errors)
      use result = JsonDocument.Parse json
      let observed = result.RootElement.GetProperty("files")[0]
      Assert.Equal(expected, observed.GetProperty("status").GetString())
      Assert.Equal(1, result.RootElement.GetProperty("uncheckedFiles").GetInt32())
      Assert.NotEqual<string>("", result.RootElement.GetProperty("generation").GetString())

    Assert.Equal<byte>(manifest, File.ReadAllBytes(Path.Combine(output, "manifest.json")))
  }

[<Fact>]
let ``index progress stays on stderr and does not change artifacts`` () =
  task {
    use workspace = new Workspace()
    let source = Corpus.corpusPath "micro"

    let! struct (code, json, progress) =
      Corpus.runCli
        [ "index"
          source
          "--out"
          workspace.Path
          "--tier"
          "0"
          "--progress"
          "always"
          "--json" ]

    Assert.Equal(0, code)
    use result = JsonDocument.Parse json
    Assert.Equal("index", result.RootElement.GetProperty("command").GetString())

    for phase in [ "discovery/extraction"; "sort"; "write"; "publish"; "finished" ] do
      Assert.Contains("progress " + phase, progress)

    Assert.DoesNotContain(string(char 27), progress, StringComparison.Ordinal)
    Assert.DoesNotContain("\r", progress, StringComparison.Ordinal)
    Assert.InRange(progress.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, 5, 6)
    let manifest = File.ReadAllBytes(Path.Combine(workspace.Path, "manifest.json"))

    let! struct (silentCode, silentJson, silentErrors) =
      Corpus.runCli
        [ "index"
          source
          "--out"
          workspace.Path
          "--tier"
          "0"
          "--progress"
          "never"
          "--json" ]

    Assert.Equal(0, silentCode)
    Assert.Equal("", silentErrors)
    Assert.Equal(json, silentJson)
    Assert.Equal<byte>(manifest, File.ReadAllBytes(Path.Combine(workspace.Path, "manifest.json")))
  }

[<Theory>]
[<InlineData("0")>]
[<InlineData("1")>]
[<InlineData("2")>]
let ``extraction coverage survives reopening and sums to file counts`` tier =
  task {
    use workspace = new Workspace()

    let! struct (code, json, errors) =
      Corpus.runCli
        [ "index"
          Corpus.corpusPath "micro"
          "--out"
          workspace.Path
          "--tier"
          tier
          "--json" ]

    Assert.True(code = 0 || code = 4, errors)
    Assert.Equal("", errors)
    use indexed = JsonDocument.Parse json
    let coverage = indexed.RootElement.GetProperty("extractionCoverage")
    let! struct (statsCode, statsJson, _) = Corpus.runCli [ "stats"; "--out"; workspace.Path; "--json" ]
    Assert.Equal(0, statsCode)
    use stats = JsonDocument.Parse statsJson
    Assert.Equal(coverage.GetRawText(), stats.RootElement.GetProperty("extractionCoverage").GetRawText())
    let mutable files = 0

    for language in coverage.EnumerateArray() do
      let count = language.GetProperty("files").GetInt32()
      files <- files + count

      let tiers =
        [ "tier2"; "tier1"; "notExtracted" ]
        |> List.sumBy(fun key -> language.GetProperty(key).GetInt32())

      Assert.Equal(count, tiers)

      for reason in language.GetProperty("reasons").EnumerateArray() do
        Assert.InRange(reason.GetProperty("examples").GetArrayLength(), 0, 2)

    Assert.Equal(indexed.RootElement.GetProperty("files").GetInt32(), files)

    let! struct (verified, verification, verificationErrors) =
      Corpus.runCli
        [ "verify"
          Corpus.corpusPath "micro"
          "--out"
          workspace.Path
          "--deterministic"
          "--json" ]

    Assert.True((verified = 0), verification + verificationErrors)
  }

[<Fact>]
let ``extraction coverage distinguishes unsupported syntax and remains optional`` () =
  task {
    use workspace = new Workspace()
    let source = Path.Combine(workspace.Path, "source")
    let output = Path.Combine(workspace.Path, "index")
    Directory.CreateDirectory source |> ignore
    File.WriteAllText(Path.Combine(source, "sample.py"), "def sample():\n    pass\n")
    let! struct (code, json, _) = Corpus.runCli [ "index"; source; "--out"; output; "--tier"; "2"; "--json" ]
    Assert.True(code = 0 || code = 4)
    use result = JsonDocument.Parse json
    let language = result.RootElement.GetProperty("extractionCoverage")[0]
    Assert.False(language.GetProperty("syntaxSupported").GetBoolean())
    let reason = language.GetProperty("reasons")[0]
    Assert.Equal("syntax-unsupported", reason.GetProperty("reason").GetString())
    let path = Path.Combine(output, "manifest.json")

    let manifest =
      System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText path).AsObject()

    let original = manifest["extraction"].DeepClone()
    manifest.Remove("extraction") |> ignore
    File.WriteAllText(path, manifest.ToJsonString())
    let! struct (legacyCode, legacyJson, _) = Corpus.runCli [ "stats"; "--out"; output; "--json" ]
    Assert.Equal(0, legacyCode)
    use legacy = JsonDocument.Parse legacyJson
    Assert.False(legacy.RootElement.GetProperty("coverageAvailable").GetBoolean())
    manifest["extraction"] <- original
    let firstRow = original.AsArray()[0]
    firstRow["count"] <- System.Text.Json.Nodes.JsonValue.Create 100
    File.WriteAllText(path, manifest.ToJsonString())
    let! struct (invalidCode, invalidJson, _) = Corpus.runCli [ "stats"; "--out"; output; "--json" ]
    Assert.Equal(3, invalidCode)
    use invalid = JsonDocument.Parse invalidJson
    Assert.False(invalid.RootElement.GetProperty("hasResult").GetBoolean())
  }

[<Fact>]
let ``freshness bounds reads accepts moved roots and rejects linked sources`` () =
  task {
    use workspace = new Workspace()
    let original = Path.Combine(workspace.Path, "original")
    let moved = Path.Combine(workspace.Path, "moved source")
    let output = Path.Combine(workspace.Path, "index")
    Directory.CreateDirectory original |> ignore
    File.WriteAllText(Path.Combine(original, "small.c"), "int small;\n")
    File.WriteAllText(Path.Combine(original, "growing.c"), "int grow;\n")
    let! struct (indexed, _, _) = Corpus.runCli [ "index"; original; "--out"; output; "--tier"; "0" ]
    Assert.Equal(0, indexed)
    Directory.Move(original, moved)
    let growing = Path.Combine(moved, "growing.c")

    do
      use stream = File.OpenWrite growing
      stream.SetLength(FreshnessCommands.MaxBytes + 1L)

    let! struct (code, json, errors) =
      Corpus.runCli
        [ "freshness"
          "small.c"
          "growing.c"
          "new.c"
          "--root"
          moved
          "--out"
          output
          "--json" ]

    Assert.Equal(4, code)
    Assert.Equal("", errors)
    use result = JsonDocument.Parse json
    let files = result.RootElement.GetProperty("files")
    Assert.Equal("unchanged", files[0].GetProperty("status").GetString())
    Assert.Equal("byte-limit", files[1].GetProperty("reason").GetString())
    Assert.Equal("not-indexed", files[2].GetProperty("reason").GetString())
    Assert.InRange(result.RootElement.GetProperty("bytesRead").GetInt64(), 1L, 100L)

    if not(OperatingSystem.IsWindows()) then
      File.Delete growing
      File.CreateSymbolicLink(growing, Path.Combine(moved, "small.c")) |> ignore

      let! struct (linkedCode, linkedJson, _) =
        Corpus.runCli [ "freshness"; "growing.c"; "--root"; moved; "--out"; output; "--json" ]

      Assert.Equal(4, linkedCode)
      use linked = JsonDocument.Parse linkedJson
      let observation = linked.RootElement.GetProperty("files")[0]
      Assert.Equal("link-rejected", observation.GetProperty("reason").GetString())

    for path in [ "../outside.c"; "/outside.c"; "nested/file:stream" ] do
      let! struct (rejected, rejectedJson, _) =
        Corpus.runCli [ "freshness"; path; "--root"; moved; "--out"; output; "--json" ]

      Assert.Equal(2, rejected)
      use rejection = JsonDocument.Parse rejectedJson
      Assert.Equal(2, rejection.RootElement.GetProperty("exitCode").GetInt32())
  }

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
          command
          @ [ "--out"
              workspace.Path
              "--json"
              "--budget"
              string budget
              "--limit"
              string limit ]

        let! struct (code, json, errors) = Corpus.runCli arguments
        Assert.Equal(0, code)
        Assert.Equal("", errors)
        assertBudget budget limit json

    let arguments =
      command
      @ [ "--out"; workspace.Path; "--json"; "--budget"; "512"; "--limit"; "3" ]

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
        Corpus.runCli(command @ [ "--out"; workspace.Path; "--json"; "--budget"; "256" ])

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
      Corpus.runCli
        [ "context"
          "shapes"
          "Area"
          "--out"
          workspace.Path
          "--budget"
          "256"
          "--limit"
          "30" ]

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
      fun node ->
        node.GetProperty("kind").GetString() = "File"
        && node.GetProperty("path").GetString() = "shapes.c"
    )

    let! struct (_, shallow, _) =
      Corpus.runCli
        [ "context"
          "injection_target"
          "--out"
          workspace.Path
          "--json"
          "--depth"
          "0"
          "--limit"
          "100" ]

    let! struct (_, deep, _) =
      Corpus.runCli
        [ "context"
          "injection_target"
          "--out"
          workspace.Path
          "--json"
          "--depth"
          "2"
          "--limit"
          "100" ]

    use first = JsonDocument.Parse shallow
    use second = JsonDocument.Parse deep

    Assert.All(
      first.RootElement.GetProperty("nodes").EnumerateArray(),
      fun node -> Assert.Equal(0, node.GetProperty("distance").GetInt32())
    )

    Assert.Contains(
      second.RootElement.GetProperty("nodes").EnumerateArray(),
      fun node -> node.GetProperty("distance").GetInt32() = 2
    )
  }

[<Fact>]
let ``CJK and escaped metadata use the same serialized token count`` () =
  task {
    use workspace = new Workspace()
    do! index workspace.Path "cjk"

    for query in [ "\u9762\u7a4d"; "\ud55c\uad6d\uc5b4"; "\ud83d\ude00\"<tag>\n" ] do
      let! struct (code, json, errors) =
        Corpus.runCli
          [ "search"
            query
            "--out"
            workspace.Path
            "--json"
            "--budget"
            "512"
            "--limit"
            "5" ]

      Assert.True(code = 0 || code = 1, errors)
      Assert.Equal("", errors)
      assertBudget 512 5 json
  }

[<Theory>]
[<InlineData(64L)>]
[<InlineData(4096L)>]
let ``external sort remains deterministic across multiple merge boundaries`` bufferBytes =
  use workspace = new Workspace()
  let budget = Srcnet.Storage.ExternalSort.TemporaryBudget 65536L
  let values = Array.init 2000 (fun index -> (1999 - index) % 317)

  do
    use sorter =
      new Srcnet.Storage.ExternalSort.Sorter<int>(
        workspace.Path,
        bufferBytes,
        budget,
        compare,
        (fun writer value -> writer.Write value),
        (fun reader -> reader.ReadInt32()),
        CancellationToken.None
      )

    for value in values do
      sorter.Add(value, 16L)

    let actual = sorter.Read() |> Seq.toArray
    Assert.Equal<int>(Array.sort values, actual)
    Assert.Equal<int>(actual, sorter.Read() |> Seq.toArray)
    Assert.InRange(budget.Peak, 8000L, 65536L)

  Assert.Equal(0L, budget.Used)
  Assert.Empty(Directory.EnumerateFileSystemEntries workspace.Path)

[<Fact>]
let ``spilled input produces the same graph bytes as array input`` () =
  task {
    use workspace = new Workspace()
    let root = Corpus.corpusPath "micro"
    let options = Srcnet.Discovery.Walk.WalkOptions.defaults
    let diagnostics = Srcnet.Core.Diagnostics.DiagnosticSink()
    let! discovered = Srcnet.Discovery.Walk.run root options diagnostics CancellationToken.None

    let convert (file: Srcnet.Discovery.Walk.DiscoveredFile) : Srcnet.Storage.Writer.FileInput =
      { Path = file.Path
        SizeBytes = file.SizeBytes
        Language = file.Language
        EncodingCode = Srcnet.Text.Encodings.toCode file.Encoding
        Flags = file.Flags
        LineCount = file.LineCount
        ContentHash = file.Hash
        Symbols =
          file.Extraction.Symbols
          |> Array.map(fun symbol ->
            { Srcnet.Storage.Writer.SymbolInput.Kind = symbol.Kind
              Name = symbol.Name
              QualifiedName = symbol.QualifiedName
              Parent = symbol.Parent
              Ordinal = symbol.Ordinal
              StartLine = symbol.StartLine
              EndLine = symbol.EndLine
              StartByte = symbol.StartByte
              EndByte = symbol.EndByte
              Flags = symbol.Flags })
        References =
          file.Extraction.References
          |> Array.map(fun reference ->
            { Srcnet.Storage.Writer.ReferenceInput.Source = reference.Source
              Kind = reference.Kind
              Target = reference.Target
              Qualifier = reference.Qualifier
              Language = reference.Language
              StartByte = reference.StartByte
              EndByte = reference.EndByte
              Line = reference.Line
              Stage = reference.Stage
              Confidence = reference.Confidence }) }

    let repository =
      match Srcnet.Core.Ids.RepositoryId.tryCreate "spill-test" with
      | Ok repository -> repository
      | Error error -> failwith(string error)

    let input: Srcnet.Storage.Writer.IndexInput =
      { Repository = repository
        Directories = discovered.Directories
        Files = discovered.Files |> Array.map convert }

    let baseline =
      Srcnet.Storage.Writer.write (Path.Combine(workspace.Path, "array")) input diagnostics CancellationToken.None

    for buffer in [ 128L; 65536L ] do
      let budget = Srcnet.Storage.ExternalSort.TemporaryBudget 16777216L

      do
        use spill =
          new Srcnet.Storage.Spill.Input(workspace.Path, buffer, budget, CancellationToken.None)

        for path in Array.rev input.Directories do
          spill.AddDirectory path

        for file in Array.rev input.Files do
          spill.AddFile file

        let source = spill.Finish repository

        let result =
          Srcnet.Storage.Writer.writeSource
            (Path.Combine(workspace.Path, "spill"))
            source
            diagnostics
            CancellationToken.None

        Assert.Equal<Srcnet.Storage.Writer.SegmentDescriptor>(baseline.Segments, result.Segments)

      Assert.Equal(0L, budget.Used)
  }

[<Fact>]
let ``segmented reader preserves cross boundary bytes and rejects unlisted parts`` () =
  use workspace = new Workspace()
  let path = Path.Combine(workspace.Path, "data.strings")
  let payload = Array.init 257 (fun index -> byte(index % 251))
  let header = Array.zeroCreate<byte> Srcnet.Storage.Format.HeaderLength

  Srcnet.Storage.Format.writeHeader
    (Span header)
    { Kind = Srcnet.Storage.Format.Strings
      PrimaryCount = 1UL
      SecondaryCount = uint64 payload.Length
      RecordLength = 0u
      PayloadLength = uint64 payload.Length }

  let chunk = 37
  let allowed = Collections.Generic.HashSet<string>(StringComparer.Ordinal)

  for index in 0 .. (payload.Length - 1) / chunk do
    let part = Srcnet.Storage.Reader.partPath path index

    let bytes =
      payload[index * chunk .. min (payload.Length - 1) ((index + 1) * chunk - 1)]

    File.WriteAllBytes(part, (if index = 0 then Array.append header bytes else bytes))
    allowed.Add(Path.GetFullPath part) |> ignore

  match Srcnet.Storage.Reader.MappedSegment.Open(path, allowed) with
  | Error error -> failwith(Srcnet.Storage.Reader.OpenError.describe error)
  | Ok segment ->
    use segment = segment
    Assert.Equal<byte>(payload, segment.Data.Slice(0, payload.Length).ToArray())
    Assert.Equal<byte>(payload[32..78], segment.Data.Slice(32, 47).ToArray())
    Assert.Equal(payload[37], segment.Data[37])

  allowed.Remove(Path.GetFullPath(Srcnet.Storage.Reader.partPath path 1))
  |> ignore

  match Srcnet.Storage.Reader.MappedSegment.Open(path, allowed) with
  | Error _ -> ()
  | Ok segment ->
    (segment :> IDisposable).Dispose()
    failwith "Unlisted segment part was accepted"

[<Fact>]
let ``split published segments remain queryable and verify every part`` () =
  task {
    use workspace = new Workspace()
    do! index workspace.Path "micro"

    let manifest =
      match Srcnet.Storage.Manifest.read workspace.Path with
      | Ok manifest -> manifest
      | Error error -> failwith(Srcnet.Storage.Manifest.ManifestError.describe error)

    let generation =
      Srcnet.Storage.Manifest.generationIn manifest |> ValueOption.defaultValue ""

    let directory = Path.Combine(workspace.Path, "segments", generation)

    let originals =
      manifest.Segments
      |> Array.map(fun segment ->
        { segment with
            Name = Path.GetFileName segment.Name })

    let split =
      Srcnet.Storage.Writer.splitSegments
        directory
        512
        (Srcnet.Storage.ExternalSort.TemporaryBudget 16777216L)
        originals
        CancellationToken.None

    Assert.True(split.Length > originals.Length)

    let updated =
      { manifest with
          Segments = Srcnet.Storage.Manifest.qualify generation split }

    match Srcnet.Storage.Manifest.write workspace.Path updated with
    | Error error -> failwith(string error)
    | Ok() -> ()

    for command in [ [ "verify" ]; [ "stats" ]; [ "search"; "shapes.c" ]; [ "show"; "shapes.c" ] ] do
      let! struct (code, json, errors) = Corpus.runCli(command @ [ "--out"; workspace.Path; "--json" ])
      Assert.True((code = 0), json + errors)
      use document = JsonDocument.Parse json
      Assert.Equal(code, document.RootElement.GetProperty("exitCode").GetInt32())

    let part =
      split
      |> Array.find(fun segment -> segment.Name.Contains(".part-", StringComparison.Ordinal))

    let partPath = Path.Combine(directory, part.Name)
    let bytes = File.ReadAllBytes partPath
    bytes[0] <- bytes[0] ^^^ 1uy
    File.WriteAllBytes(partPath, bytes)
    let! struct (broken, report, _) = Corpus.runCli [ "verify"; "--out"; workspace.Path; "--json" ]
    Assert.Equal(3, broken)
    use corruption = JsonDocument.Parse report
    Assert.False(corruption.RootElement.GetProperty("valid").GetBoolean())
  }

[<Fact>]
let ``index resource failures preserve published data and remove staging`` () =
  task {
    use workspace = new Workspace()
    let source = Corpus.corpusPath "micro"

    let! struct (success, _, _) =
      Corpus.runCli
        [ "index"
          source
          "--out"
          workspace.Path
          "--tier"
          "0"
          "--memory-limit=256MiB"
          "--json" ]

    Assert.Equal(0, success)
    let manifest = File.ReadAllBytes(Path.Combine(workspace.Path, "manifest.json"))

    for option in [ "--temp-limit=1"; "--memory-limit=1MiB" ] do
      let! struct (code, json, errors) =
        Corpus.runCli [ "index"; source; "--out"; workspace.Path; "--tier"; "2"; option; "--json" ]

      Assert.Equal(2, code)
      Assert.Equal("", errors)
      use result = JsonDocument.Parse json
      Assert.False(result.RootElement.GetProperty("hasResult").GetBoolean())
      Assert.Equal<byte>(manifest, File.ReadAllBytes(Path.Combine(workspace.Path, "manifest.json")))
      Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".staging")))

    let! struct (verified, json, _) = Corpus.runCli [ "verify"; "--out"; workspace.Path; "--json" ]
    Assert.True((verified = 0), json)
  }

[<Fact>]
let ``streaming discovery cancellation stops every worker`` () =
  task {
    use cancellation = new CancellationTokenSource()
    let diagnostics = Srcnet.Core.Diagnostics.DiagnosticSink()

    let pending =
      Srcnet.Discovery.Walk.runTo
        (Corpus.corpusPath "micro")
        Srcnet.Discovery.Walk.WalkOptions.defaults
        diagnostics
        ignore
        ignore
        (fun _ -> cancellation.Cancel())
        cancellation.Token

    let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> pending :> System.Threading.Tasks.Task)
    return ()
  }

[<Fact>]
let ``agent context stays separate and legacy migration preserves graph data`` () =
  task {
    use workspace = new Workspace()
    let source = Corpus.corpusPath "micro"
    let executable = Environment.ProcessPath
    let! struct (indexed, _, _) = Corpus.runCli [ "index"; source; "--out"; workspace.Path; "--tier"; "0"; "--json" ]
    Assert.Equal(0, indexed)
    let manifestPath = Path.Combine(workspace.Path, "manifest.json")
    let original = File.ReadAllBytes manifestPath

    let! struct (written, writeJson, _) =
      Corpus.runCli
        [ "agent-context"
          "--out"
          workspace.Path
          "--write"
          "--root"
          source
          "--executable"
          executable
          "--json" ]

    Assert.True((written = 0), writeJson)
    Assert.Equal<byte>(original, File.ReadAllBytes manifestPath)

    let! struct (verified, verifiedJson, _) =
      Corpus.runCli [ "verify"; source; "--out"; workspace.Path; "--deterministic"; "--json" ]

    Assert.True((verified = 0), verifiedJson)

    let legacy =
      System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText manifestPath).AsObject()

    legacy["SRCNET"] <- System.Text.Json.Nodes.JsonValue.Create executable
    legacy["SOURCE_ROOT"] <- System.Text.Json.Nodes.JsonValue.Create source
    legacy["INDEX_DIR"] <- System.Text.Json.Nodes.JsonValue.Create workspace.Path
    File.WriteAllText(manifestPath, legacy.ToJsonString())
    File.Delete(Path.Combine(workspace.Path, "agent-context.json"))
    let beforeRead = File.ReadAllBytes manifestPath
    let! struct (readCode, readJson, _) = Corpus.runCli [ "agent-context"; "--out"; workspace.Path; "--json" ]
    Assert.Equal(0, readCode)
    use read = JsonDocument.Parse readJson
    Assert.Equal("legacy-manifest", read.RootElement.GetProperty("source").GetString())
    Assert.Equal("not-inferred", read.RootElement.GetProperty("executableTrust").GetString())
    Assert.Equal<byte>(beforeRead, File.ReadAllBytes manifestPath)

    let! struct (migrated, migrationJson, _) =
      Corpus.runCli
        [ "agent-context"
          "--out"
          workspace.Path
          "--migrate"
          "--root"
          source
          "--executable"
          executable
          "--json" ]

    Assert.True((migrated = 0), migrationJson)
    Assert.Equal<byte>(original, File.ReadAllBytes manifestPath)
    File.WriteAllText(Path.Combine(workspace.Path, "agent-context.json"), "{")
    let! struct (invalid, invalidJson, _) = Corpus.runCli [ "agent-context"; "--out"; workspace.Path; "--json" ]
    Assert.Equal(2, invalid)
    use invalidResult = JsonDocument.Parse invalidJson
    Assert.False(invalidResult.RootElement.GetProperty("hasResult").GetBoolean())
    Assert.Equal<byte>(original, File.ReadAllBytes manifestPath)
  }

[<Fact>]
let ``agent context reports relocated bindings and refuses lossy migrations`` () =
  task {
    use workspace = new Workspace()
    let source = Path.Combine(workspace.Path, "source 日本語 中文 한국어")
    let output = Path.Combine(workspace.Path, "index 空白")
    let moved = Path.Combine(workspace.Path, "moved 索引")
    Directory.CreateDirectory source |> ignore
    File.WriteAllText(Path.Combine(source, "sample.c"), "int sample;\n")
    let untrusted = Path.Combine(workspace.Path, "untrusted executable")
    File.WriteAllText(untrusted, "this is not an executable and must never run")
    let! struct (indexed, _, _) = Corpus.runCli [ "index"; source; "--out"; output; "--tier"; "0"; "--json" ]
    Assert.Equal(0, indexed)

    let! struct (written, _, _) =
      Corpus.runCli
        [ "agent-context"
          "--out"
          output
          "--write"
          "--root"
          source
          "--executable"
          untrusted
          "--json" ]

    Assert.Equal(0, written)
    Directory.Move(output, moved)
    let! struct (code, json, _) = Corpus.runCli [ "agent-context"; "--out"; moved; "--json" ]
    Assert.Equal(4, code)
    use result = JsonDocument.Parse json
    Assert.False(result.RootElement.GetProperty("indexBindingMatches").GetBoolean())
    Assert.Equal(moved, result.RootElement.GetProperty("selectedIndexDirectory").GetString())
    Assert.Equal("not-inferred", result.RootElement.GetProperty("executableTrust").GetString())
    let manifestPath = Path.Combine(moved, "manifest.json")

    let manifest =
      System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText manifestPath).AsObject()

    manifest["extra"] <- System.Text.Json.Nodes.JsonValue.Create "preserve me"
    File.WriteAllText(manifestPath, manifest.ToJsonString())
    let before = File.ReadAllBytes manifestPath

    let! struct (rejected, rejectedJson, _) =
      Corpus.runCli
        [ "agent-context"
          "--out"
          moved
          "--migrate"
          "--root"
          source
          "--executable"
          untrusted
          "--json" ]

    Assert.Equal(2, rejected)
    use rejection = JsonDocument.Parse rejectedJson
    Assert.False(rejection.RootElement.GetProperty("hasResult").GetBoolean())
    Assert.Equal<byte>(before, File.ReadAllBytes manifestPath)
  }

[<Fact>]
let ``management diagnostic samples stay bounded while totals remain exact`` () =
  task {
    use workspace = new Workspace()
    let source = Path.Combine(workspace.Path, "source")
    let output = Path.Combine(workspace.Path, "index")
    Directory.CreateDirectory source |> ignore

    for index in 0..99 do
      File.WriteAllText(Path.Combine(source, $"sample_{index}.c"), "int sample;\n")

    let! struct (code, json, errors) =
      Corpus.runCli
        [ "index"
          source
          "--out"
          output
          "--tier"
          "1"
          "--max-file-size=1"
          "--json" ]

    Assert.Equal(4, code)
    Assert.Equal("", errors)
    use document = JsonDocument.Parse json
    let result = document.RootElement
    Assert.Equal(100, result.GetProperty("diagnostics").GetInt32())
    Assert.Equal(32, result.GetProperty("diagnosticDetails").GetArrayLength())
    Assert.Equal(68, result.GetProperty("omittedDiagnosticCount").GetInt32())
    Assert.True(result.GetProperty("diagnosticsTruncated").GetBoolean())
    let language = result.GetProperty("extractionCoverage")[0]
    let reason = language.GetProperty("reasons")[0]
    Assert.Equal("file-size-limit", reason.GetProperty("reason").GetString())
    Assert.Equal(100, reason.GetProperty("count").GetInt32())
  }

[<Fact>]
let ``freshness reports access denial and enforces public input limits`` () =
  task {
    use workspace = new Workspace()
    let source = Path.Combine(workspace.Path, "source")
    let output = Path.Combine(workspace.Path, "index")
    Directory.CreateDirectory source |> ignore
    let file = Path.Combine(source, "private.c")
    File.WriteAllText(file, "int private_value;\n")
    let! struct (indexed, _, _) = Corpus.runCli [ "index"; source; "--out"; output; "--tier"; "0"; "--json" ]
    Assert.Equal(0, indexed)

    if not(OperatingSystem.IsWindows()) then
      let original = File.GetUnixFileMode file

      try
        File.SetUnixFileMode(file, enum<UnixFileMode> 0)

        let! struct (code, json, _) =
          Corpus.runCli [ "freshness"; "private.c"; "--root"; source; "--out"; output; "--json" ]

        Assert.Equal(4, code)
        use document = JsonDocument.Parse json
        let observation = document.RootElement.GetProperty("files")[0]
        Assert.Equal("unreadable", observation.GetProperty("status").GetString())
        Assert.Equal("access-denied", observation.GetProperty("reason").GetString())
      finally
        File.SetUnixFileMode(file, original)

    for paths in [ Array.empty; Array.create 33 "private.c"; [| "../private.c" |] ] do
      let! code =
        FreshnessCommands.run
          { RootPath = source
            OutputDirectory = ValueSome output
            Paths = paths
            Json = true }
          CancellationToken.None

      Assert.Equal(2, code)

    use cancellation = new CancellationTokenSource()
    cancellation.Cancel()

    let pending =
      FreshnessCommands.run
        { RootPath = source
          OutputDirectory = ValueSome output
          Paths = [| "private.c" |]
          Json = true }
        cancellation.Token

    let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> pending :> System.Threading.Tasks.Task)
    return ()
  }

[<Fact>]
let ``null query text and null or empty keyword arrays are rejected before opening artifacts`` () =
  use workspace = new Workspace()

  let limits: Args.QueryLimits =
    { Limit = Args.DefaultLimit
      Budget = Args.DefaultBudget }

  let output = ValueSome workspace.Path

  Assert.Equal(
    Commands.ExitCode.UserError,
    QueryCommands.search
      { OutputDirectory = output
        RootPath = ValueNone
        Text = null
        IgnoreCase = false
        Limits = limits
        Json = true }
      CancellationToken.None
  )

  Assert.Equal(
    Commands.ExitCode.UserError,
    QueryCommands.show
      { OutputDirectory = output
        RootPath = ValueNone
        Node = null
        Limits = limits
        Json = true }
      CancellationToken.None
  )

  Assert.Equal(
    Commands.ExitCode.UserError,
    QueryCommands.neighbors
      { OutputDirectory = output
        RootPath = ValueNone
        Node = null
        Edges = Array.empty
        Direction = "both"
        Depth = 1
        Limits = limits
        Json = true }
      CancellationToken.None
  )

  Assert.Equal(
    Commands.ExitCode.UserError,
    QueryCommands.path
      { OutputDirectory = output
        RootPath = ValueNone
        From = "from"
        To = null
        Edges = Array.empty
        Direction = "both"
        Depth = 1
        Limits = limits
        Json = true }
      CancellationToken.None
  )

  for keywords in [| null; Array.empty; [| null |] |] do
    Assert.Equal(
      Commands.ExitCode.UserError,
      QueryCommands.context
        { OutputDirectory = output
          RootPath = ValueNone
          Keywords = keywords
          Depth = 1
          Limits = limits
          Json = true }
        CancellationToken.None
    )

[<Fact>]
let ``error diagnostics do not materialize unbounded scalar arrays`` () =
  QueryCommands.writeError "search" Commands.ExitCode.UserError "warmup" true
  |> ignore

  let message = String('x', 1_000_000)
  let before = GC.GetAllocatedBytesForCurrentThread()

  let code =
    QueryCommands.writeError "search" Commands.ExitCode.UserError message true

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

[<Fact>]
let ``default extraction retains the requested tier for deterministic verification`` () =
  task {
    use workspace = new Workspace()
    let source = Path.Combine(workspace.Path, "source")
    Directory.CreateDirectory source |> ignore
    File.WriteAllText(Path.Combine(source, "sample.c"), "int value;\n")
    let! struct (code, json, errors) = Corpus.runCli [ "index"; source; "--json" ]
    Assert.True(code = 0 || code = 4, errors)
    use result = JsonDocument.Parse json

    use manifest =
      JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "_srcnet", "manifest.json")))

    Assert.Equal(
      2,
      manifest.RootElement
        .GetProperty("options")
        .GetProperty("requestedTier")
        .GetInt32()
    )

    Assert.Equal(
      result.RootElement.GetProperty("tier").GetInt32(),
      manifest.RootElement.GetProperty("options").GetProperty("tier").GetInt32()
    )

    let! struct (verified, report, verifyErrors) = Corpus.runCli [ "verify"; source; "--deterministic"; "--json" ]
    Assert.True((verified = 0), report + verifyErrors)
  }

[<Fact>]
let ``option-shaped query text does not change error output options`` () =
  task {
    use workspace = new Workspace()

    let! struct (code, output, errors) =
      Corpus.runCli [ "context"; "--out"; workspace.Path; "--"; "--json"; "--budget=256" ]

    Assert.Equal(3, code)
    Assert.Equal("", output)
    Assert.NotEqual<string>("", errors)
  }

[<Fact>]
let ``deterministic verification requires its source before opening artifacts`` () =
  task {
    let! struct (code, json, errors) = Corpus.runCli [ "verify"; "--deterministic"; "--json" ]
    Assert.Equal(2, code)
    Assert.Equal("", errors)
    use document = JsonDocument.Parse json
    Assert.Equal("verify", document.RootElement.GetProperty("command").GetString())
  }

[<Fact>]
let ``corrupt artifacts use the documented verification exit code`` () =
  task {
    use workspace = new Workspace()
    do! index workspace.Path "micro"

    use manifest =
      JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.Path, "manifest.json")))

    let segments = manifest.RootElement.GetProperty("segments")
    let name = segments[0].GetProperty("name").GetString()

    let path =
      Path.Combine(workspace.Path, name.Replace('/', Path.DirectorySeparatorChar))

    let bytes = File.ReadAllBytes path
    bytes[bytes.Length - 1] <- bytes[bytes.Length - 1] ^^^ 1uy
    File.WriteAllBytes(path, bytes)
    let! struct (code, json, errors) = Corpus.runCli [ "verify"; "--out"; workspace.Path; "--json" ]
    Assert.Equal(3, code)
    Assert.Equal("", errors)
    use document = JsonDocument.Parse json
    Assert.False(document.RootElement.GetProperty("valid").GetBoolean())
  }

[<Fact>]
let ``a held writer lease rejects indexing before reading source ignore rules`` () =
  task {
    use source = new Workspace()
    use output = new Workspace()

    File.WriteAllText(
      Path.Combine(source.Path, ".gitignore"),
      "#" + String('x', Srcnet.Discovery.Ignore.MaxLineBytes + 1)
    )

    match Srcnet.Storage.Manifest.acquireWriter output.Path with
    | Error error -> failwith(Srcnet.Storage.Artifact.PathError.describe error)
    | Ok lease ->
      use _lease = lease
      let! struct (code, _, errors) = Corpus.runCli [ "index"; source.Path; "--out"; output.Path; "--tier"; "0" ]
      Assert.Equal(2, code)
      Assert.DoesNotContain("ignore-file-unreadable", errors)
      Assert.False(Directory.Exists(Srcnet.Storage.Manifest.stagingPath output.Path))
  }

[<Theory>]
[<InlineData("stats")>]
[<InlineData("verify")>]
let ``artifact errors sanitize untrusted metadata before terminal output`` command =
  task {
    use workspace = new Workspace()
    do! index workspace.Path "micro"
    let path = Path.Combine(workspace.Path, "manifest.json")
    let original = File.ReadAllText path
    File.WriteAllText(path, original.Replace("\"Repository\"", "\"\\u001b[31mUnknown\""))
    let! struct (code, output, errors) = Corpus.runCli [ command; "--out"; workspace.Path ]
    Assert.Equal(3, code)
    Assert.Equal("", output)
    Assert.False(errors.Contains '\u001b')
    Assert.True(errors.Contains '\uFFFD')
  }
