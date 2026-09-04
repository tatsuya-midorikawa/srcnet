/// 内容ハッシュと符号化判定のベンチマーク。
///
/// 内容ハッシュはインデックス生成の支配的要因になると想定している領域の 1 つで、
/// BCL の SHA-256（ハードウェア アクセラレーション有効）を baseline とする。
/// 自前実装は「代表 workload で候補を上回ること」を採用条件とするため、
/// この比較は採用根拠そのものである。docs/performance.md 6 を参照。
module Srcnet.Benchmarks.HashBenchmarks

open System
open System.Security.Cryptography
open BenchmarkDotNet.Attributes
open Srcnet.Core
open Srcnet.Text

[<MemoryDiagnoser>]
type ContentHashBenchmarks() =
  let mutable payload = Array.empty<byte>
  let hasher = Blake3.Hasher()
  let digest = Array.zeroCreate<byte> Blake3.HashLength

  /// 代表値（小さなソース ファイル）と上限値（巨大な生成ファイル）を分けて測る。
  [<Params(4096, 65536, 1048576, 16777216)>]
  member val SizeBytes = 0 with get, set

  [<GlobalSetup>]
  member this.Setup() =
    payload <- Array.init this.SizeBytes (fun index -> byte (index % 251))

  [<Benchmark(Baseline = true)>]
  member _.Sha256() =
    SHA256.HashData(ReadOnlySpan payload, Span(digest)) |> ignore

  [<Benchmark>]
  member _.Blake3() =
    hasher.Reset()
    hasher.Update(ReadOnlySpan payload)
    hasher.Finish(Span digest)

[<MemoryDiagnoser>]
type EncodingDetectionBenchmarks() =
  let mutable payload = Array.empty<byte>

  [<Params(4096, 65536)>]
  member val SizeBytes = 0 with get, set

  /// UTF-8 の妥当性検査は SIMD 化された経路を通る。ASCII 主体と CJK 主体で
  /// 経路が変わるため、両方を測る。
  [<Params("ascii", "cjk")>]
  member val Content = "ascii" with get, set

  [<GlobalSetup>]
  member this.Setup() =
    let unit = if this.Content = "ascii" then "int main(void) { return 0; }\n" else "日本語のコメント行です。\n"
    let builder = Text.StringBuilder()

    while Text.Encoding.UTF8.GetByteCount(builder.ToString()) < this.SizeBytes do
      builder.Append unit |> ignore

    payload <- Text.Encoding.UTF8.GetBytes(builder.ToString())

  [<Benchmark>]
  member _.Detect() =
    Encodings.detect (ReadOnlySpan payload) (int64 payload.Length) |> ignore
