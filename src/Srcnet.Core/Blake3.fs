/// BLAKE3 の実装。
///
/// 用途は内容ハッシュ（変更検出）、ノード ID の導出、成果物の完全性検証である。
/// docs/security.md 5 のとおり、これは暗号プリミティブの自作禁止とは区別される用途だが、
/// 独自実装である以上、公式テスト ベクターとの一致を維持することが採用条件である。
/// 仕様は BLAKE3 リファレンス実装に従う。
module Srcnet.Core.Blake3

open System
open System.Buffers.Binary
open System.Numerics

[<Literal>]
let BlockLength = 64

[<Literal>]
let ChunkLength = 1024

/// 既定の出力長（バイト）。XOF として任意長も出力できる。
[<Literal>]
let HashLength = 32

[<Literal>]
let private ChunkStartFlag = 1u

[<Literal>]
let private ChunkEndFlag = 2u

[<Literal>]
let private ParentFlag = 4u

[<Literal>]
let private RootFlag = 8u

/// 部分木スタックの深さ上限。入力長は 2^64 バイト未満なのでチャンク数は 2^54 未満に収まる。
[<Literal>]
let private MaxStackDepth = 54

[<Literal>]
let private Iv0 = 0x6A09E667u

[<Literal>]
let private Iv1 = 0xBB67AE85u

[<Literal>]
let private Iv2 = 0x3C6EF372u

[<Literal>]
let private Iv3 = 0xA54FF53Au

[<Literal>]
let private Iv4 = 0x510E527Fu

[<Literal>]
let private Iv5 = 0x9B05688Cu

[<Literal>]
let private Iv6 = 0x1F83D9ABu

[<Literal>]
let private Iv7 = 0x5BE0CD19u

let private iv =
  [| Iv0; Iv1; Iv2; Iv3; Iv4; Iv5; Iv6; Iv7 |]

let inline private g (a: byref<uint32>) (b: byref<uint32>) (c: byref<uint32>) (d: byref<uint32>) (mx: uint32) (my: uint32) =
  a <- a + b + mx
  d <- BitOperations.RotateRight(d ^^^ a, 16)
  c <- c + d
  b <- BitOperations.RotateRight(b ^^^ c, 12)
  a <- a + b + my
  d <- BitOperations.RotateRight(d ^^^ a, 8)
  c <- c + d
  b <- BitOperations.RotateRight(b ^^^ c, 7)

/// 圧縮関数。
///
/// 状態とメッセージ語をすべてローカル変数に置き、7 ラウンドを完全に展開する。
/// メッセージ語の置換 [2;6;3;10;7;0;4;13;1;11;12;5;9;14;15;8] は、各ラウンドの
/// 添字を静的に解決することで実行時の入れ替えを不要にしている。
///
/// この形は通常の F# idiom から意図が読み取りにくいが、配列添字による実装は
/// 境界検査と再読み込みのために約 5 倍遅く、内容ハッシュはインデックス生成の
/// hot path であるため、この展開を維持すること。計測は bench/Srcnet.Benchmarks にある。
let private compress
  (chainingValue: ReadOnlySpan<uint32>)
  (m: ReadOnlySpan<uint32>)
  (counter: uint64)
  (blockLength: uint32)
  (flags: uint32)
  (state: Span<uint32>)
  =
  let m0 = m[0]
  let m1 = m[1]
  let m2 = m[2]
  let m3 = m[3]
  let m4 = m[4]
  let m5 = m[5]
  let m6 = m[6]
  let m7 = m[7]
  let m8 = m[8]
  let m9 = m[9]
  let m10 = m[10]
  let m11 = m[11]
  let m12 = m[12]
  let m13 = m[13]
  let m14 = m[14]
  let m15 = m[15]

  let cv0 = chainingValue[0]
  let cv1 = chainingValue[1]
  let cv2 = chainingValue[2]
  let cv3 = chainingValue[3]
  let cv4 = chainingValue[4]
  let cv5 = chainingValue[5]
  let cv6 = chainingValue[6]
  let cv7 = chainingValue[7]

  let mutable s0 = cv0
  let mutable s1 = cv1
  let mutable s2 = cv2
  let mutable s3 = cv3
  let mutable s4 = cv4
  let mutable s5 = cv5
  let mutable s6 = cv6
  let mutable s7 = cv7
  let mutable s8 = Iv0
  let mutable s9 = Iv1
  let mutable s10 = Iv2
  let mutable s11 = Iv3
  let mutable s12 = uint32 counter
  let mutable s13 = uint32 (counter >>> 32)
  let mutable s14 = blockLength
  let mutable s15 = flags

  // ラウンド 1
  g &s0 &s4 &s8 &s12 m0 m1
  g &s1 &s5 &s9 &s13 m2 m3
  g &s2 &s6 &s10 &s14 m4 m5
  g &s3 &s7 &s11 &s15 m6 m7
  g &s0 &s5 &s10 &s15 m8 m9
  g &s1 &s6 &s11 &s12 m10 m11
  g &s2 &s7 &s8 &s13 m12 m13
  g &s3 &s4 &s9 &s14 m14 m15

  // ラウンド 2
  g &s0 &s4 &s8 &s12 m2 m6
  g &s1 &s5 &s9 &s13 m3 m10
  g &s2 &s6 &s10 &s14 m7 m0
  g &s3 &s7 &s11 &s15 m4 m13
  g &s0 &s5 &s10 &s15 m1 m11
  g &s1 &s6 &s11 &s12 m12 m5
  g &s2 &s7 &s8 &s13 m9 m14
  g &s3 &s4 &s9 &s14 m15 m8

  // ラウンド 3
  g &s0 &s4 &s8 &s12 m3 m4
  g &s1 &s5 &s9 &s13 m10 m12
  g &s2 &s6 &s10 &s14 m13 m2
  g &s3 &s7 &s11 &s15 m7 m14
  g &s0 &s5 &s10 &s15 m6 m5
  g &s1 &s6 &s11 &s12 m9 m0
  g &s2 &s7 &s8 &s13 m11 m15
  g &s3 &s4 &s9 &s14 m8 m1

  // ラウンド 4
  g &s0 &s4 &s8 &s12 m10 m7
  g &s1 &s5 &s9 &s13 m12 m9
  g &s2 &s6 &s10 &s14 m14 m3
  g &s3 &s7 &s11 &s15 m13 m15
  g &s0 &s5 &s10 &s15 m4 m0
  g &s1 &s6 &s11 &s12 m11 m2
  g &s2 &s7 &s8 &s13 m5 m8
  g &s3 &s4 &s9 &s14 m1 m6

  // ラウンド 5
  g &s0 &s4 &s8 &s12 m12 m13
  g &s1 &s5 &s9 &s13 m9 m11
  g &s2 &s6 &s10 &s14 m15 m10
  g &s3 &s7 &s11 &s15 m14 m8
  g &s0 &s5 &s10 &s15 m7 m2
  g &s1 &s6 &s11 &s12 m5 m3
  g &s2 &s7 &s8 &s13 m0 m1
  g &s3 &s4 &s9 &s14 m6 m4

  // ラウンド 6
  g &s0 &s4 &s8 &s12 m9 m14
  g &s1 &s5 &s9 &s13 m11 m5
  g &s2 &s6 &s10 &s14 m8 m12
  g &s3 &s7 &s11 &s15 m15 m1
  g &s0 &s5 &s10 &s15 m13 m3
  g &s1 &s6 &s11 &s12 m0 m10
  g &s2 &s7 &s8 &s13 m2 m6
  g &s3 &s4 &s9 &s14 m4 m7

  // ラウンド 7
  g &s0 &s4 &s8 &s12 m11 m15
  g &s1 &s5 &s9 &s13 m5 m0
  g &s2 &s6 &s10 &s14 m1 m9
  g &s3 &s7 &s11 &s15 m8 m6
  g &s0 &s5 &s10 &s15 m14 m10
  g &s1 &s6 &s11 &s12 m2 m12
  g &s2 &s7 &s8 &s13 m3 m4
  g &s3 &s4 &s9 &s14 m7 m13

  state[0] <- s0 ^^^ s8
  state[1] <- s1 ^^^ s9
  state[2] <- s2 ^^^ s10
  state[3] <- s3 ^^^ s11
  state[4] <- s4 ^^^ s12
  state[5] <- s5 ^^^ s13
  state[6] <- s6 ^^^ s14
  state[7] <- s7 ^^^ s15
  state[8] <- s8 ^^^ cv0
  state[9] <- s9 ^^^ cv1
  state[10] <- s10 ^^^ cv2
  state[11] <- s11 ^^^ cv3
  state[12] <- s12 ^^^ cv4
  state[13] <- s13 ^^^ cv5
  state[14] <- s14 ^^^ cv6
  state[15] <- s15 ^^^ cv7

let private readWords (bytes: ReadOnlySpan<byte>) (words: Span<uint32>) =
  for i in 0..15 do
    words[i] <- BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4))

/// 増分ハッシュ計算器。
///
/// 使い捨てにせず `Reset` で再利用することで、ファイルごとの割り当てを避けられる。
/// スレッド安全ではないため、ワーカーごとに 1 インスタンスを持つこと。
[<Sealed>]
type Hasher() =
  let chunkChainingValue = Array.zeroCreate<uint32> 8
  let chunkBlock = Array.zeroCreate<byte> BlockLength
  let stack = Array.zeroCreate<uint32> (MaxStackDepth * 8)
  let message = Array.zeroCreate<uint32> 16
  let state = Array.zeroCreate<uint32> 16
  let chunkCv = Array.zeroCreate<uint32> 8
  let parentCv = Array.zeroCreate<uint32> 8
  let outputChainingValue = Array.zeroCreate<uint32> 8
  let outputBlock = Array.zeroCreate<uint32> 16

  let mutable chunkCounter = 0UL
  let mutable chunkBlockLength = 0
  let mutable chunkBlocksCompressed = 0
  let mutable stackLength = 0

  do Array.blit iv 0 chunkChainingValue 0 8

  member private _.StartFlag = if chunkBlocksCompressed = 0 then ChunkStartFlag else 0u

  member private _.PendingChunkLength = chunkBlocksCompressed * BlockLength + chunkBlockLength

  /// 現在のチャンクの連鎖値を `chunkCv` へ書く。ハッシュ器の状態は変えない。
  member private this.ComputeChunkChainingValue() =
    readWords (ReadOnlySpan chunkBlock) (Span message)

    compress
      (ReadOnlySpan chunkChainingValue)
      (ReadOnlySpan message)
      chunkCounter
      (uint32 chunkBlockLength)
      (this.StartFlag ||| ChunkEndFlag)
      (Span state)

    Span(state, 0, 8).CopyTo(Span chunkCv)

  member private _.ResetChunk(counter: uint64) =
    Array.blit iv 0 chunkChainingValue 0 8
    System.Array.Clear chunkBlock
    chunkBlockLength <- 0
    chunkBlocksCompressed <- 0
    chunkCounter <- counter

  /// `chunkCv` の内容を部分木スタックへ積む。
  /// `totalChunks` の最下位ビットが 0 である間は、対になる完全な部分木が必ず
  /// スタック上にあるという不変条件を使い、対が揃うたびに親ノードへ畳み込む。
  member private _.PushChunkChainingValue(totalChunks: uint64) =
    Span(chunkCv).CopyTo(Span parentCv)
    let mutable remaining = totalChunks

    while remaining &&& 1UL = 0UL do
      stackLength <- stackLength - 1
      Span(stack, stackLength * 8, 8).CopyTo(Span(message, 0, 8))
      Span(parentCv).CopyTo(Span(message, 8, 8))
      compress (ReadOnlySpan iv) (ReadOnlySpan message) 0UL (uint32 BlockLength) ParentFlag (Span state)
      Span(state, 0, 8).CopyTo(Span parentCv)
      remaining <- remaining >>> 1

    Span(parentCv).CopyTo(Span(stack, stackLength * 8, 8))
    stackLength <- stackLength + 1

  member private _.UpdateChunk(input: ReadOnlySpan<byte>) =
    let mutable rest = input

    while rest.Length > 0 do
      if chunkBlockLength = BlockLength then
        readWords (ReadOnlySpan chunkBlock) (Span message)
        let startFlag = if chunkBlocksCompressed = 0 then ChunkStartFlag else 0u

        compress
          (ReadOnlySpan chunkChainingValue)
          (ReadOnlySpan message)
          chunkCounter
          (uint32 BlockLength)
          startFlag
          (Span state)

        Span(state, 0, 8).CopyTo(Span chunkChainingValue)
        chunkBlocksCompressed <- chunkBlocksCompressed + 1
        // 最終ブロックはゼロ埋めが前提のため、圧縮のたびに必ず消去する。
        System.Array.Clear chunkBlock
        chunkBlockLength <- 0

      let take = min (BlockLength - chunkBlockLength) rest.Length
      rest.Slice(0, take).CopyTo(Span(chunkBlock, chunkBlockLength, take))
      chunkBlockLength <- chunkBlockLength + take
      rest <- rest.Slice take

  /// 入力を追加する。入力の分割位置は結果に影響しない。
  member this.Update(input: ReadOnlySpan<byte>) =
    let mutable rest = input

    while rest.Length > 0 do
      if this.PendingChunkLength = ChunkLength then
        this.ComputeChunkChainingValue()
        let totalChunks = chunkCounter + 1UL
        this.PushChunkChainingValue totalChunks
        this.ResetChunk totalChunks

      let take = min (ChunkLength - this.PendingChunkLength) rest.Length
      this.UpdateChunk(rest.Slice(0, take))
      rest <- rest.Slice take

  /// これまでの入力に対するハッシュを `destination` の長さだけ書き出す。
  /// ハッシュ器の状態は変わらないため、続けて `Update` を呼べる。
  member this.Finish(destination: Span<byte>) =
    readWords (ReadOnlySpan chunkBlock) (Span outputBlock)
    Span(chunkChainingValue).CopyTo(Span outputChainingValue)
    let mutable outputCounter = chunkCounter
    let mutable outputBlockLength = uint32 chunkBlockLength
    let mutable outputFlags = this.StartFlag ||| ChunkEndFlag
    let mutable remaining = stackLength

    while remaining > 0 do
      remaining <- remaining - 1

      compress
        (ReadOnlySpan outputChainingValue)
        (ReadOnlySpan outputBlock)
        outputCounter
        outputBlockLength
        outputFlags
        (Span state)

      Span(stack, remaining * 8, 8).CopyTo(Span(outputBlock, 0, 8))
      Span(state, 0, 8).CopyTo(Span(outputBlock, 8, 8))
      Span(iv).CopyTo(Span outputChainingValue)
      outputCounter <- 0UL
      outputBlockLength <- uint32 BlockLength
      outputFlags <- ParentFlag

    let mutable written = 0
    let mutable blockCounter = 0UL

    while written < destination.Length do
      compress
        (ReadOnlySpan outputChainingValue)
        (ReadOnlySpan outputBlock)
        blockCounter
        outputBlockLength
        (outputFlags ||| RootFlag)
        (Span state)

      let take = min BlockLength (destination.Length - written)
      let mutable offset = 0

      while offset < take do
        let word = state[offset >>> 2]
        let size = min 4 (take - offset)

        if size = 4 then
          BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(written + offset, 4), word)
        else
          for byteIndex in 0 .. size - 1 do
            destination[written + offset + byteIndex] <- byte (word >>> (8 * byteIndex))

        offset <- offset + size

      written <- written + take
      blockCounter <- blockCounter + 1UL

  /// 状態を初期化して再利用可能にする。
  member this.Reset() =
    System.Array.Clear stack
    stackLength <- 0
    this.ResetChunk 0UL

/// 一度きりの計算。`destination` の長さだけ XOF 出力する。
let hashInto (input: ReadOnlySpan<byte>) (destination: Span<byte>) =
  let hasher = Hasher()
  hasher.Update input
  hasher.Finish destination

/// 既定長 32 バイトのハッシュ。
let hash (input: ReadOnlySpan<byte>) =
  let result = Array.zeroCreate<byte> HashLength
  hashInto input (Span result)
  result
