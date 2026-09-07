/// 抽出のための復号。
///
/// 抽出器は UTF-8 のバイト列だけを扱う。判定した符号化から UTF-8 へ変換する経路を
/// ここへ集約し、変換できない符号化を「不明のまま抽出しない」と明示できるようにする。
/// docs/platform-and-i18n.md 2、docs/storage.md 5 を参照。
module Srcnet.Text.Decoding

open System
open System.Text

/// 復号の結果。
type DecodeOutcome =
  /// 入力がそのまま UTF-8 である。`Offset` は BOM を除いた開始位置。
  | AlreadyUtf8 of offset: int
  /// 変換した。`Length` は変換後のバイト数。
  | Converted of length: int
  /// この符号化に対応する復号器を持たない。抽出は行わない。
  | Unsupported
  /// テキストとして扱わない。
  | NotText

let private utf16Le = UnicodeEncoding(false, false, false) :> Encoding

let private utf16Be = UnicodeEncoding(true, false, false) :> Encoding

/// BOM の長さ。判定側と同じ規則を使う。
let private bomLength (encoding: Encodings.DetectedEncoding) (source: ReadOnlySpan<byte>) =
  match encoding with
  | Encodings.Utf8WithBom when
    source.Length >= 3
    && source[0] = 0xEFuy
    && source[1] = 0xBBuy
    && source[2] = 0xBFuy
    ->
    3
  | Encodings.Utf16Le when source.Length >= 2 && source[0] = 0xFFuy && source[1] = 0xFEuy -> 2
  | Encodings.Utf16Be when source.Length >= 2 && source[0] = 0xFEuy && source[1] = 0xFFuy -> 2
  | _ -> 0

/// UTF-8 へ変換したときに必要な最大バイト数。
///
/// UTF-16 の 1 コード単位は UTF-8 で最大 3 バイトになる。サロゲート対は 4 バイトだが
/// 2 コード単位を消費するため、この上限を超えない。レガシー符号化は 1 バイトが
/// 最大 3 バイトへ広がる（半角カタカナ）ため、そちらの上限を採る。
let maxUtf8Bytes (sourceLength: int) =
  max (sourceLength / 2 * 3) (LegacyEncodings.maxUtf8Bytes sourceLength) + 3

/// 変換なしで UTF-8 として扱えるか。
let isUtf8 (encoding: Encodings.DetectedEncoding) =
  encoding = Encodings.Utf8 || encoding = Encodings.Utf8WithBom

/// 復号できる符号化か。判定結果だけで決まり、内容には依存しない。
let isSupported (encoding: Encodings.DetectedEncoding) =
  match encoding with
  | Encodings.Utf8
  | Encodings.Utf8WithBom
  | Encodings.Utf16Le
  | Encodings.Utf16Be
  | Encodings.Undetermined -> true
  | Encodings.ShiftJis
  | Encodings.EucJp
  | Encodings.Iso2022Jp
  | Encodings.Gb18030
  | Encodings.Big5
  | Encodings.EucKr -> LegacyEncodings.isSupported encoding
  | Encodings.Binary -> false

/// 先頭の BOM の長さ。BOM がなければ 0。
let bomLengthOf (encoding: Encodings.DetectedEncoding) (source: ReadOnlySpan<byte>) = bomLength encoding source

/// `source` を UTF-8 へ復号し、必要なら `destination` へ書き出す。
///
/// `destination` は `maxUtf8Bytes source.Length` 以上の長さが必要である。
/// 不正なバイト列は置換文字にして継続する。復号の失敗で走査を止めない。
let toUtf8
  (encoding: Encodings.DetectedEncoding)
  (source: ReadOnlySpan<byte>)
  (destination: Span<byte>)
  : DecodeOutcome =
  match encoding with
  | Encodings.Binary -> NotText
  | Encodings.Utf8
  | Encodings.Utf8WithBom -> AlreadyUtf8(bomLength encoding source)
  | Encodings.Utf16Le
  | Encodings.Utf16Be
  | Encodings.Undetermined ->
    let offset = bomLength encoding source
    let body = source.Slice offset

    if body.Length = 0 then
      Converted 0
    else
      let sourceEncoding =
        if encoding = Encodings.Utf16Le then utf16Le
        elif encoding = Encodings.Utf16Be then utf16Be
        else Encoding.UTF8

      // 奇数バイトの末尾も含め、不正な列は標準の U+FFFD で置換する。
      let charCount = sourceEncoding.GetCharCount body
      let characters = Array.zeroCreate<char> charCount
      sourceEncoding.GetChars(body, Span characters) |> ignore
      let written = Encoding.UTF8.GetBytes(ReadOnlySpan characters, destination)
      Converted written
  | Encodings.ShiftJis
  | Encodings.EucJp
  | Encodings.Iso2022Jp
  | Encodings.Gb18030
  | Encodings.Big5
  | Encodings.EucKr ->
    // 変換表は同梱している。表を持たない構成では `Unsupported` へ落ちる。
    let written = LegacyEncodings.decode encoding source destination
    if written < 0 then Unsupported else Converted written

/// 復号済みの UTF-8 バイト列から、改行種別によらない論理的な行数を数える。
///
/// 判定時にバイト単位で数えられない符号化（UTF-16）のために用意する。
/// 走査時と同じ規則（CR、LF、CRLF のいずれも 1 行の区切り）で数える。
let countLines (source: ReadOnlySpan<byte>) =
  if source.Length = 0 then
    0
  else
    let mutable terminators = 0
    let mutable index = 0

    while index < source.Length do
      let b = source[index]

      if b = 0x0Auy then
        terminators <- terminators + 1
        index <- index + 1
      elif b = 0x0Duy then
        terminators <- terminators + 1
        // CRLF は 1 つの区切りとして数える。
        if index + 1 < source.Length && source[index + 1] = 0x0Auy then
          index <- index + 2
        else
          index <- index + 1
      else
        index <- index + 1

    let last = source[source.Length - 1]

    if last = 0x0Auy || last = 0x0Duy then
      terminators
    else
      terminators + 1
