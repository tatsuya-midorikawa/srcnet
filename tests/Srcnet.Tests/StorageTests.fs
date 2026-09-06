/// セグメント形式と文字列表のテスト。
module Srcnet.Tests.StorageTests

open System
open Xunit
open Srcnet.Storage

/// 種別ごとの不変条件を満たすヘッダー。`Nodes` は 64 バイト固定長で、
/// payload 長は件数 × レコード長に一致し、従属件数を持たない。
let private header: Format.Header =
  { Kind = Format.Nodes
    PrimaryCount = 1234UL
    SecondaryCount = 0UL
    RecordLength = 64u
    PayloadLength = 1234UL * 64UL }

[<Fact>]
let ``ヘッダーは往復する`` () =
  let buffer = Array.zeroCreate<byte> Format.HeaderLength
  Format.writeHeader (Span buffer) header
  let fileLength = int64 Format.HeaderLength + int64 header.PayloadLength

  match Format.tryReadHeader (ReadOnlySpan buffer) fileLength with
  | Ok parsed -> Assert.Equal(header, parsed)
  | Error error -> failwith(Format.FormatError.describe error)

[<Fact>]
let ``マジック番号が違うセグメントは拒否される`` () =
  let buffer = Array.zeroCreate<byte> Format.HeaderLength
  Format.writeHeader (Span buffer) header
  buffer[0] <- 0x00uy
  let fileLength = int64 Format.HeaderLength + int64 header.PayloadLength
  Assert.Equal(Error Format.BadMagic, Format.tryReadHeader (ReadOnlySpan buffer) fileLength)

[<Fact>]
let ``非互換な形式版は読まずに拒否される`` () =
  let buffer = Array.zeroCreate<byte> Format.HeaderLength
  Format.writeHeader (Span buffer) header
  Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(Span(buffer, 8, 4), 999u)
  let fileLength = int64 Format.HeaderLength + int64 header.PayloadLength
  Assert.Equal(Error(Format.UnsupportedVersion 999u), Format.tryReadHeader (ReadOnlySpan buffer) fileLength)

[<Fact>]
let ``宣言された長さと実際の長さの不一致を検出する`` () =
  let buffer = Array.zeroCreate<byte> Format.HeaderLength
  Format.writeHeader (Span buffer) header

  match Format.tryReadHeader (ReadOnlySpan buffer) 10L with
  | Error(Format.LengthMismatch(declared, actual)) ->
    Assert.Equal(header.PayloadLength, declared)
    Assert.Equal(10L, actual)
  | other -> failwith $"長さの不一致を検出できませんでした: {other}"

[<Fact>]
let ``短すぎるセグメントは拒否される`` () =
  Assert.Equal(Error Format.TooShort, Format.tryReadHeader (ReadOnlySpan(Array.zeroCreate<byte> 10)) 10L)

[<Fact>]
let ``未知のセグメント種別は拒否される`` () =
  let buffer = Array.zeroCreate<byte> Format.HeaderLength
  Format.writeHeader (Span buffer) header
  Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(Span(buffer, 12, 4), 200u)
  let fileLength = int64 Format.HeaderLength + int64 header.PayloadLength
  Assert.Equal(Error(Format.UnknownSegmentKind 200u), Format.tryReadHeader (ReadOnlySpan buffer) fileLength)

[<Fact>]
let ``セグメント種別コードは往復する`` () =
  let all =
    [ Format.Nodes
      Format.Files
      Format.Strings
      Format.StringOffsets
      Format.AdjacencyCsr
      Format.IdMap
      Format.References
      Format.LexicalLookup ]

  for kind in all do
    Assert.Equal(ValueSome kind, Format.SegmentKind.ofCode(Format.SegmentKind.toCode kind))

// --- 文字列表 ---

[<Fact>]
let ``空文字列は常に索引 0 である`` () =
  let table = Strings.StringTable()
  Assert.Equal(0, table.Intern "")
  Assert.Equal(1, table.Count)

[<Fact>]
let ``同じ文字列は同じ索引を返す`` () =
  let table = Strings.StringTable()
  let first = table.Intern "kernel/sched"
  let second = table.Intern "kernel/sched"
  Assert.Equal(first, second)
  Assert.Equal(2, table.Count)

[<Fact>]
let ``オフセットは累積和になり、末尾は blob 長に一致する`` () =
  let table = Strings.StringTable()
  table.Intern "abc" |> ignore
  table.Intern "日本語" |> ignore
  let offsets = table.Offsets()
  Assert.Equal(table.Count + 1, offsets.Length)
  Assert.Equal(0UL, offsets[0])
  Assert.Equal(0UL, offsets[1])
  Assert.Equal(3UL, offsets[2])
  // 日本語は UTF-8 で 9 バイト。
  Assert.Equal(12UL, offsets[3])
  Assert.Equal(uint64 table.TotalBytes, offsets[table.Count])

[<Fact>]
let ``CJK 文字列のバイト長を正しく数える`` () =
  let table = Strings.StringTable()
  let index = table.Intern "日本語"
  Assert.Equal(9, table.ByteLength index)

[<Fact>]
let ``unpaired surrogates cannot be silently replaced in the string table`` () =
  let table = Strings.StringTable()

  Assert.Throws<Text.EncoderFallbackException>(fun () -> table.Intern(String(char 0xD800, 1)) |> ignore)
  |> ignore

  Assert.Equal(1, table.Count)
  Assert.Equal(0L, table.TotalBytes)

[<Fact>]
let ``lookup format rejects undersized and overflowed table declarations`` () =
  for count, postings, bytes in
    [ 1UL, 1UL, 63UL
      uint64 UInt32.MaxValue, 0UL, 32UL
      0UL, uint64 UInt32.MaxValue, 32UL ] do
    let buffer = Array.zeroCreate<byte> Format.HeaderLength

    Format.writeHeader
      (Span buffer)
      { Kind = Format.LexicalLookup
        PrimaryCount = count
        SecondaryCount = postings
        RecordLength = 24u
        PayloadLength = bytes }

    match Format.tryReadHeader (ReadOnlySpan buffer) (int64 Format.HeaderLength + int64 bytes) with
    | Error _ -> ()
    | Ok _ -> failwith "Accepted invalid lookup table lengths"
