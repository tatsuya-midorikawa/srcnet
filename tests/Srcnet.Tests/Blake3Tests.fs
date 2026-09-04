/// BLAKE3 実装の既知解テスト。
///
/// テスト ベクターは BLAKE3 公式リポジトリ (BLAKE3-team/BLAKE3, CC0-1.0 OR Apache-2.0) の
/// `test_vectors/test_vectors.json` から抜粋した。入力は 251 バイト周期の
/// 0, 1, ..., 250, 0, 1, ... という列で、期待値は既定長 32 バイト出力の 16 進表記である。
/// チャンク境界 (1024) と部分木の畳み込みを通る長さを選んでいる。
module Srcnet.Tests.Blake3Tests

open System
open Xunit
open Srcnet.Core

let private vectors: struct (int * string)[] =
  [|
    struct (0, "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262")
    struct (1, "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c1510225d0f592e213")
    struct (2, "7b7015bb92cf0b318037702a6cdd81dee41224f734684c2c122cd6359cb1ee63")
    struct (3, "e1be4d7a8ab5560aa4199eea339849ba8e293d55ca0a81006726d184519e647f")
    struct (63, "e9bc37a594daad83be9470df7f7b3798297c3d834ce80ba85d6e207627b7db7b")
    struct (64, "4eed7141ea4a5cd4b788606bd23f46e212af9cacebacdc7d1f4c6dc7f2511b98")
    struct (65, "de1e5fa0be70df6d2be8fffd0e99ceaa8eb6e8c93a63f2d8d1c30ecb6b263dee")
    struct (1023, "10108970eeda3eb932baac1428c7a2163b0e924c9a9e25b35bba72b28f70bd11")
    struct (1024, "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7")
    struct (1025, "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c8cb7fb814b8444")
    struct (2048, "e776b6028c7cd22a4d0ba182a8bf62205d2ef576467e838ed6f2529b85fba24a")
    struct (2049, "5f4d72f40d7a5f82b15ca2b2e44b1de3c2ef86c426c95c1af0b6879522563030")
    struct (3072, "b98cb0ff3623be03326b373de6b9095218513e64f1ee2edd2525c7ad1e5cffd2")
    struct (4096, "015094013f57a5277b59d8475c0501042c0b642e531b0a1c8f58d2163229e969")
    struct (8192, "aae792484c8efe4f19e2ca7d371d8c467ffb10748d8a5a1ae579948f718a2a63")
    struct (16384, "f875d6646de28985646f34ee13be9a576fd515f76b5b0a26bb324735041ddde4")
    struct (31744, "62b6960e1a44bcc1eb1a611a8d6235b6b4b78f32e7abc4fb4c6cdcce94895c47")
  |]

let private inputOfLength length =
  Array.init length (fun index -> byte (index % 251))

[<Fact>]
let ``既定長ハッシュが公式テスト ベクターと一致する`` () =
  for struct (length, expected) in vectors do
    let input = inputOfLength length
    let actual = Convert.ToHexStringLower(Blake3.hash (ReadOnlySpan input))
    Assert.Equal(expected, actual)

[<Fact>]
let ``入力の分割位置がハッシュに影響しない`` () =
  for struct (length, expected) in vectors do
    let input = inputOfLength length
    let hasher = Blake3.Hasher()
    let mutable offset = 0
    let mutable step = 1

    while offset < length do
      let take = min step (length - offset)
      hasher.Update(ReadOnlySpan(input, offset, take))
      offset <- offset + take
      // 1 バイト刻みからチャンクを跨ぐ長さまで、境界条件を網羅する刻み幅を巡回させる。
      step <- (step * 3 + 7) % 1500 + 1

    let digest = Array.zeroCreate<byte> Blake3.HashLength
    hasher.Finish(Span digest)
    Assert.Equal(expected, Convert.ToHexStringLower digest)

[<Fact>]
let ``Reset 後に再利用しても結果が変わらない`` () =
  let hasher = Blake3.Hasher()

  for struct (length, expected) in vectors do
    hasher.Reset()
    hasher.Update(ReadOnlySpan(inputOfLength length))
    let digest = Array.zeroCreate<byte> Blake3.HashLength
    hasher.Finish(Span digest)
    Assert.Equal(expected, Convert.ToHexStringLower digest)

[<Fact>]
let ``Finish は状態を変えないので繰り返し呼べる`` () =
  let hasher = Blake3.Hasher()
  hasher.Update(ReadOnlySpan(inputOfLength 5000))
  let first = Array.zeroCreate<byte> Blake3.HashLength
  let second = Array.zeroCreate<byte> Blake3.HashLength
  hasher.Finish(Span first)
  hasher.Finish(Span second)
  Assert.Equal<byte[]>(first, second)

[<Fact>]
let ``XOF 出力の先頭 32 バイトは既定長ハッシュに一致する`` () =
  let input = inputOfLength 4096
  let extended = Array.zeroCreate<byte> 131
  Blake3.hashInto (ReadOnlySpan input) (Span extended)
  let standard = Blake3.hash (ReadOnlySpan input)
  Assert.Equal<byte[]>(standard, extended[0..31])
