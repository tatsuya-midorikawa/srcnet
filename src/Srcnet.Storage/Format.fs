/// セグメントのオンディスク形式。
///
/// 固定長レコードと 64 バイト境界の整列を前提にし、可変長データは blob へのオフセットで
/// 参照する。全ファイルの先頭にマジック番号と形式版を置き、非互換版は読まずに失敗させる。
/// docs/storage.md 3 を参照。
module Srcnet.Storage.Format

open System
open System.Buffers.Binary
open Srcnet.Core

/// セグメント形式の版。レイアウトを変えるたびに増やす。
/// 版が異なる成果物は読まずに拒否する。docs/requirements.md NFR-14 を参照。
///
/// 2: 内容ハッシュとノード ID のハッシュを SHA-256 へ変更（ADR-8 の見直し）。
[<Literal>]
let FormatVersion = 2u

/// ヘッダー長。ペイロードの先頭を 64 バイト境界に揃えるためにこの値を選ぶ。
[<Literal>]
let HeaderLength = 64

/// 固定長レコードのバイト長。キャッシュ ラインに合わせる。
[<Literal>]
let RecordLength = 64

let magic = "SRCNETSG"B

type SegmentKind =
  | Nodes
  | Files
  | Strings
  | StringOffsets
  | AdjacencyCsr
  | IdMap

module SegmentKind =

  let toCode kind =
    match kind with
    | Nodes -> 1u
    | Files -> 2u
    | Strings -> 3u
    | StringOffsets -> 4u
    | AdjacencyCsr -> 5u
    | IdMap -> 6u

  let ofCode code =
    match code with
    | 1u -> ValueSome Nodes
    | 2u -> ValueSome Files
    | 3u -> ValueSome Strings
    | 4u -> ValueSome StringOffsets
    | 5u -> ValueSome AdjacencyCsr
    | 6u -> ValueSome IdMap
    | _ -> ValueNone

/// セグメント ヘッダー。すべての整数はリトル エンディアン。
/// 対象は x64 と arm64 のみで、いずれもリトル エンディアンである。
[<Struct>]
type Header =
  { Kind: SegmentKind
    /// レコード数、ノード数、文字列数など、そのセグメントの主要な件数。
    PrimaryCount: uint64
    /// CSR の隣接要素数、文字列 blob のバイト長など、従属する件数。
    SecondaryCount: uint64
    /// 固定長レコードのバイト長。可変長セグメントでは 0。
    RecordLength: uint32
    PayloadLength: uint64 }

type FormatError =
  | TooShort
  | BadMagic
  | UnsupportedVersion of found: uint32
  | UnknownSegmentKind of code: uint32
  | LengthMismatch of declared: uint64 * actual: int64
  | InvalidCount of field: string * found: uint64
  | InvalidRecordLength of found: uint32 * expected: uint32
  | PayloadMismatch of declared: uint64 * expected: uint64

module FormatError =

  let describe error =
    match error with
    | TooShort -> "セグメントが短すぎます"
    | BadMagic -> "セグメントのマジック番号が一致しません"
    | UnsupportedVersion found -> $"セグメント形式版 {found} は未対応です (対応版 {FormatVersion})"
    | UnknownSegmentKind code -> $"未知のセグメント種別コード {code} です"
    | LengthMismatch(declared, actual) -> $"宣言された長さ {declared} と実際の長さ {actual} が一致しません"
    | InvalidCount(field, found) -> $"{field} の値 {found} が扱える範囲を超えています"
    | InvalidRecordLength(found, expected) -> $"レコード長が {expected} ではなく {found} です"
    | PayloadMismatch(declared, expected) ->
      $"宣言された payload 長 {declared} が、件数から求まる {expected} と一致しません"

let writeHeader (destination: Span<byte>) (header: Header) =
  if destination.Length < HeaderLength then
    invalidArg (nameof destination) "ヘッダーには 64 バイト必要です"

  destination.Slice(0, HeaderLength).Clear()
  ReadOnlySpan(magic).CopyTo destination
  BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), FormatVersion)
  BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), SegmentKind.toCode header.Kind)
  BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(16, 8), header.PrimaryCount)
  BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(24, 8), header.SecondaryCount)
  BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(32, 4), header.RecordLength)
  BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(40, 8), header.PayloadLength)

/// セグメント種別ごとの不変条件。
///
/// 件数とレコード長から求まる必要 payload 長を先に検証しなければ、破損した件数のまま
/// `Span.Slice` や反復処理へ進み、範囲外アクセスや巨大ループを引き起こす。
/// 乗算前に件数を上限で抑えることで、算術 overflow も同時に排除する。
/// docs/security.md C-6 を参照。
let private checkInvariants (kind: SegmentKind) (header: Header) =
  // payload はファイル長で抑えられているため、件数が payload 長を超えることはない。
  // 先に件数を抑えてから乗算すれば、以降の算術は uint64 の範囲に必ず収まる。
  let limit = uint64 Int32.MaxValue

  if header.PrimaryCount > limit then Error(InvalidCount("primaryCount", header.PrimaryCount))
  elif header.SecondaryCount > limit then Error(InvalidCount("secondaryCount", header.SecondaryCount))
  else

  let inline expect (recordLength: uint32) (payloadLength: uint64) (secondary: uint64 voption) =
    if header.RecordLength <> recordLength then
      Error(InvalidRecordLength(header.RecordLength, recordLength))
    elif header.PayloadLength <> payloadLength then
      Error(PayloadMismatch(header.PayloadLength, payloadLength))
    else
      match secondary with
      | ValueSome expected when header.SecondaryCount <> expected ->
        Error(InvalidCount("secondaryCount", header.SecondaryCount))
      | ValueSome _
      | ValueNone -> Ok header

  match kind with
  | Nodes
  | Files -> expect (uint32 RecordLength) (header.PrimaryCount * uint64 RecordLength) (ValueSome 0UL)
  | Strings -> expect 0u header.SecondaryCount ValueNone
  | StringOffsets -> expect 8u ((header.PrimaryCount + 1UL) * 8UL) ValueNone
  | AdjacencyCsr -> expect 0u ((header.PrimaryCount + 1UL) * 8UL + header.SecondaryCount * 4UL) ValueNone
  | IdMap ->
    let recordLength = uint32 (Ids.NodeIdLength + 4)
    expect recordLength (header.PrimaryCount * uint64 recordLength) (ValueSome 0UL)

/// ヘッダーを読み、形式版と宣言された長さを検証する。
/// 破損を検出した場合は部分的に読み進めず失敗させる。docs/security.md C-6 を参照。
let tryReadHeader (source: ReadOnlySpan<byte>) (fileLength: int64) : Result<Header, FormatError> =
  if source.Length < HeaderLength then Error TooShort
  elif not (source.Slice(0, 8).SequenceEqual(ReadOnlySpan magic)) then Error BadMagic
  else

  let version = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, 4))

  if version <> FormatVersion then Error(UnsupportedVersion version)
  else

  let kindCode = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4))

  match SegmentKind.ofCode kindCode with
  | ValueNone -> Error(UnknownSegmentKind kindCode)
  | ValueSome kind ->
    let payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(40, 8))

    if uint64 fileLength <> uint64 HeaderLength + payloadLength then
      Error(LengthMismatch(payloadLength, fileLength))
    else
      checkInvariants
        kind
        { Kind = kind
          PrimaryCount = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(16, 8))
          SecondaryCount = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24, 8))
          RecordLength = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(32, 4))
          PayloadLength = payloadLength }

// --- レコード レイアウト -----------------------------------------------------
// いずれも 64 バイト固定長。フィールドを追加する場合は予約領域を使い、
// 既存フィールドのオフセットを動かしてはならない。

module NodeRecord =

  [<Literal>]
  let IdOffset = 0

  [<Literal>]
  let KindOffset = 16

  [<Literal>]
  let LanguageOffset = 18

  [<Literal>]
  let FlagsOffset = 20

  [<Literal>]
  let FileIndexOffset = 24

  [<Literal>]
  let NameOffset = 28

  [<Literal>]
  let QualifiedNameOffset = 32

  [<Literal>]
  let StartLineOffset = 36

  [<Literal>]
  let EndLineOffset = 40

  [<Literal>]
  let OrdinalOffset = 44

  [<Literal>]
  let StartByteOffset = 48

  [<Literal>]
  let EndByteOffset = 56

  /// ファイルに属さないノードを表す `fileIndex`。
  [<Literal>]
  let NoFile = 0xFFFFFFFFu

module FileRecord =

  [<Literal>]
  let PathOffset = 0

  [<Literal>]
  let LanguageOffset = 4

  [<Literal>]
  let EncodingOffset = 6

  [<Literal>]
  let FlagsOffset = 8

  [<Literal>]
  let LineCountOffset = 12

  [<Literal>]
  let SizeOffset = 16

  [<Literal>]
  let ContentHashOffset = 24

  [<Literal>]
  let ContentHashLength = 32
