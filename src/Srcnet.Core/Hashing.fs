/// 内容ハッシュ、ノード ID の導出、成果物のチェックサムに使うハッシュ。
///
/// 検証済みの platform 実装を使う。同一条件の benchmark（macOS / arm64、Release、
/// InProcessEmitToolchain）で、自前の BLAKE3 実装は BCL の SHA-256 に対して
/// 4 KiB で 5.05 倍、64 KiB で 5.66 倍、1 MiB で 5.62 倍、16 MiB で 5.62 倍遅く、
/// 「自前実装は BCL を上回ることを採用条件とする」開発規約を満たさない。
/// SIMD 実装が baseline を上回るまで、本番経路では `Blake3` を使用しない。
/// docs/decisions.md ADR-8 を参照。
///
/// 用途は変更検出と完全性検証であり、鍵付き認証ではない。
module Srcnet.Core.Hashing

open System
open System.Security.Cryptography

/// ダイジェスト長（バイト）。
[<Literal>]
let HashLength = 32

/// アルゴリズムの識別子。成果物とログで使う。
[<Literal>]
let AlgorithmName = "sha256"

/// 逐次ハッシュ計算器。
///
/// 使い捨てにせず再利用することで、入力ごとの割り当てを避けられる。
/// スレッド安全ではないため、ワーカーごとに 1 インスタンスを持つこと。
[<Sealed>]
type Hasher() =
  let hasher = IncrementalHash.CreateHash HashAlgorithmName.SHA256
  let scratch = Array.zeroCreate<byte> HashLength

  member _.Update(input: ReadOnlySpan<byte>) = hasher.AppendData input

  /// これまでの入力のダイジェストを書き出し、次の入力に備えて状態を初期化する。
  member _.Finish(destination: Span<byte>) = hasher.GetHashAndReset destination |> ignore

  /// 途中まで与えた入力を捨てる。
  member _.Reset() = hasher.GetHashAndReset(Span scratch) |> ignore

  interface IDisposable with

    member _.Dispose() = hasher.Dispose()

/// 一度きりの計算。小さな入力では逐次計算より相互運用の往復が少なく速い。
let hashInto (input: ReadOnlySpan<byte>) (destination: Span<byte>) =
  SHA256.HashData(input, destination) |> ignore

/// 既定長 32 バイトのハッシュ。
let hash (input: ReadOnlySpan<byte>) =
  let result = Array.zeroCreate<byte> HashLength
  hashInto input (Span result)
  result
