module Srcnet.Benchmarks.HotPathBenchmarks

open System
open BenchmarkDotNet.Attributes
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Discovery
open Srcnet.Text

[<MemoryDiagnoser>]
type IgnoreBenchmarks() =
  let mutable rules = Array.empty<Ignore.RuleSet>
  let path = [| "src"; "deep"; "\u65e5\u672c\u8a9e"; "nested"; "source.c" |]

  [<Params("basename", "anchored", "recursive")>]
  member val PatternKind = "basename" with get, set

  [<GlobalSetup>]
  member this.Setup() =
    let patterns =
      match this.PatternKind with
      | "basename" -> [ for index in 0 .. 127 -> $"*.cache{index}" ]
      | "anchored" -> [ for index in 0 .. 127 -> $"src/build{index}/*.c" ]
      | "recursive" -> [ "src/**/missing?.c"; "src/**/\u65e5\u672c\u8a9e/*.c" ]
      | other -> invalidArg "PatternKind" other
    rules <- [| Ignore.parse 0 patterns |]

  [<Benchmark>]
  member _.Match() = Ignore.decide rules path false

[<MemoryDiagnoser>]
type LineCountBenchmarks() =
  let mutable payload = Array.empty<byte>

  [<Params(65536, 1048576)>]
  member val SizeBytes = 0 with get, set

  [<GlobalSetup>]
  member this.Setup() =
    let text = Text.Encoding.UTF8.GetBytes "int main() { return 0; }\r\n\u65e5\u672c\u8a9e\u306e\u884c\r"
    payload <- Array.init this.SizeBytes (fun index -> text[index % text.Length])

  [<Benchmark>]
  member _.Count() = Decoding.countLines(ReadOnlySpan payload)

[<MemoryDiagnoser>]
type LanguageLookupBenchmarks() =
  let mutable code = 0us

  [<Benchmark>]
  member _.Lookup() =
    code <- (code + 1us) % 31us
    Language.ofCode code

[<MemoryDiagnoser>]
type NodeIdConstructionBenchmarks() =
  let builder = new NodeIdBuilder()
  let repository =
    match RepositoryId.tryCreate "benchmark" with
    | Ok value -> value
    | Error error -> invalidOp (RepositoryId.describe error)
  let mutable ordinal = 0u

  [<Benchmark>]
  member _.Construct() =
    ordinal <- ordinal + 1u
    builder.Compute(Function, repository, Srcnet.Core.Paths.root, "Item", ordinal)

  [<GlobalCleanup>]
  member _.Cleanup() = (builder :> IDisposable).Dispose()

[<MemoryDiagnoser>]
type NodeIdComparisonBenchmarks() =
  let ids =
    let random = Random 23
    Array.init 4096 (fun _ ->
      { High = uint64 (random.NextInt64()); Low = uint64 (random.NextInt64()) })
  let mutable position = 0

  [<Benchmark(Baseline = true)>]
  member _.Structural() =
    position <- (position + 1) % ids.Length
    compare ids[position] ids[(position + 1) % ids.Length]

  [<Benchmark>]
  member _.Words() =
    position <- (position + 1) % ids.Length
    NodeId.compare ids[position] ids[(position + 1) % ids.Length]
