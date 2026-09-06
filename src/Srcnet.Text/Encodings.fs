/// 文字エンコーディングの決定的な判定。
///
/// 大規模リポジトリには非 UTF-8 のファイルが実在する。判定は統計的推定ではなく
/// 固定の構造規則で行い、実行ごとに結果が揺れてはならない。
/// docs/platform-and-i18n.md 2 を参照。
module Srcnet.Text.Encodings

open System
open System.Text
open System.Text.Unicode

/// 判定に用いる先頭バイト数。値を固定することが決定性の前提であり、
/// ファイル サイズや利用可能メモリで変えてはならない。
[<Literal>]
let DetectionPrefixBytes = 65536

/// 判定結果の符号化。`Undetermined` は置換文字での復号と診断の対象になる。
type DetectedEncoding =
  | Utf8
  | Utf8WithBom
  | Utf16Le
  | Utf16Be
  | ShiftJis
  | EucJp
  | Iso2022Jp
  | Gb18030
  | Big5
  | EucKr
  | Binary
  | Undetermined

/// 成果物とログで使う安定した符号。列挙順の変更で値が動いてはならない。
let toCode encoding =
  match encoding with
  | Utf8 -> 1us
  | Utf8WithBom -> 2us
  | Utf16Le -> 3us
  | Utf16Be -> 4us
  | ShiftJis -> 5us
  | EucJp -> 6us
  | Iso2022Jp -> 7us
  | Gb18030 -> 8us
  | Big5 -> 9us
  | EucKr -> 10us
  | Binary -> 11us
  | Undetermined -> 12us

let name encoding =
  match encoding with
  | Utf8 -> "UTF-8"
  | Utf8WithBom -> "UTF-8-BOM"
  | Utf16Le -> "UTF-16LE"
  | Utf16Be -> "UTF-16BE"
  | ShiftJis -> "Shift_JIS"
  | EucJp -> "EUC-JP"
  | Iso2022Jp -> "ISO-2022-JP"
  | Gb18030 -> "GB18030"
  | Big5 -> "Big5"
  | EucKr -> "EUC-KR"
  | Binary -> "binary"
  | Undetermined -> "undetermined"

/// 名前から符号化を引く。利用者が明示した符号化を受け取るために使う。
/// 比較は序数で行い、ロケールに依存させない。
let tryParse (text: string) =
  let all =
    [| Utf8; Utf8WithBom; Utf16Le; Utf16Be; ShiftJis; EucJp; Iso2022Jp; Gb18030; Big5; EucKr |]

  let normalized = text.Replace("_", "-").ToUpperInvariant()

  all
  |> Array.tryFind (fun encoding -> (name encoding).ToUpperInvariant().Replace("_", "-") = normalized)
  |> function
    | Some encoding -> ValueSome encoding
    | None -> ValueNone

[<Struct>]
type Detection =
  { Encoding: DetectedEncoding
    /// BOM の長さ（バイト）。BOM がなければ 0。
    BomLength: int
    /// 構造規則を満たす候補が複数あった。ADR-5 に従い曖昧さを隠さない。
    Ambiguous: bool
    /// 構造規則を満たした候補の一覧。`Ambiguous` でなければ空。
    /// 判定順の先頭を確定値として扱わせないため、候補を捨てずに呼び出し側へ渡す。
    Candidates: DetectedEncoding[] }

let private bomLengthOf (bytes: ReadOnlySpan<byte>) =
  if bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then struct (3, ValueSome Utf8WithBom)
  elif bytes.Length >= 2 && bytes[0] = 0xFFuy && bytes[1] = 0xFEuy then struct (2, ValueSome Utf16Le)
  elif bytes.Length >= 2 && bytes[0] = 0xFEuy && bytes[1] = 0xFFuy then struct (2, ValueSome Utf16Be)
  else struct (0, ValueNone)

/// 先頭を切り出した結果、末尾に不完全な多バイト列が残り得る。
/// 判定では末尾 3 バイトまでの不完全列を失敗とみなさない。
[<Literal>]
let private MaxIncompleteTail = 3

let inline private isContinuation (b: byte) (low: byte) (high: byte) = b >= low && b <= high

/// 先頭バイトから、その UTF-8 列に必要な総バイト数を返す。
/// 単独では出現し得ないバイト（`0x80`〜`0xC1`、`0xF5`〜`0xFF`）は 0 を返す。
let private sequenceLength (lead: byte) =
  if lead >= 0xC2uy && lead <= 0xDFuy then 2
  elif lead >= 0xE0uy && lead <= 0xEFuy then 3
  elif lead >= 0xF0uy && lead <= 0xF4uy then 4
  else 0

/// `bytes` が「妥当な UTF-8 列の途中まで」であるかを判定する。
///
/// 単に長さが足りないだけでは不十分で、既に読めている継続バイトが
/// overlong、surrogate、範囲外 scalar を作らない範囲に収まっている必要がある。
let private isIncompleteSequence (bytes: ReadOnlySpan<byte>) =
  let lead = bytes[0]
  let required = sequenceLength lead

  if required = 0 || bytes.Length >= required then false
  elif bytes.Length = 1 then true
  else
    // 第 2 バイトの許容範囲は先頭バイトで狭まる。ここを緩めると overlong と
    // surrogate を UTF-8 として受理してしまう。
    let low =
      if lead = 0xE0uy then 0xA0uy
      elif lead = 0xF0uy then 0x90uy
      else 0x80uy

    let high =
      if lead = 0xEDuy then 0x9Fuy
      elif lead = 0xF4uy then 0x8Fuy
      else 0xBFuy

    if not (isContinuation bytes[1] low high) then false
    elif bytes.Length = 2 then true
    else isContinuation bytes[2] 0x80uy 0xBFuy

/// prefix 末尾に残った不完全な UTF-8 列の長さ。不完全列でなければ 0。
let private incompleteTailLength (bytes: ReadOnlySpan<byte>) =
  let mutable length = 0
  let mutable back = 1

  while length = 0 && back <= MaxIncompleteTail && back <= bytes.Length do
    if isIncompleteSequence (bytes.Slice(bytes.Length - back, back)) then length <- back
    back <- back + 1

  length

/// prefix が UTF-8 として妥当か。
///
/// 元ファイルが prefix より長い場合に限り、末尾の**不完全列**を許容する。
/// 機械的に末尾を削ると `0xFF` のような常に不正なバイトまで見逃すため、
/// 削る対象が実際に妥当な列の途中であることを確認する。
let private isValidUtf8 (bytes: ReadOnlySpan<byte>) (truncated: bool) =
  if Utf8.IsValid bytes then true
  elif not truncated then false
  else
    let tail = incompleteTailLength bytes
    tail > 0 && Utf8.IsValid(bytes.Slice(0, bytes.Length - tail))

let private containsNul (bytes: ReadOnlySpan<byte>) = bytes.IndexOf 0uy >= 0

// --- レガシー符号化の構造検証 ---------------------------------------------
// いずれも「バイト列がその符号化の文法に適合するか」だけを見る。文字表は引かない。
// 適合しても実際にその符号化である保証はないため、複数適合は Ambiguous として報告する。

let private isShiftJis (bytes: ReadOnlySpan<byte>) =
  let mutable i = 0
  let mutable ok = true
  let mutable multiByte = false

  while ok && i < bytes.Length do
    let b = bytes[i]

    if b <= 0x7Fuy then i <- i + 1
    elif b >= 0xA1uy && b <= 0xDFuy then
      multiByte <- true
      i <- i + 1
    elif (b >= 0x81uy && b <= 0x9Fuy) || (b >= 0xE0uy && b <= 0xFCuy) then
      if i + 1 >= bytes.Length then
        i <- bytes.Length // 末尾の不完全列は許容する
      else
        let t = bytes[i + 1]

        if (t >= 0x40uy && t <= 0x7Euy) || (t >= 0x80uy && t <= 0xFCuy) then
          multiByte <- true
          i <- i + 2
        else ok <- false
    else ok <- false

  ok && multiByte

let private isEucJp (bytes: ReadOnlySpan<byte>) =
  let mutable i = 0
  let mutable ok = true
  let mutable multiByte = false

  while ok && i < bytes.Length do
    let b = bytes[i]

    if b <= 0x7Fuy then i <- i + 1
    elif b = 0x8Euy then
      if i + 1 >= bytes.Length then i <- bytes.Length
      elif bytes[i + 1] >= 0xA1uy && bytes[i + 1] <= 0xDFuy then
        multiByte <- true
        i <- i + 2
      else ok <- false
    elif b = 0x8Fuy then
      if i + 2 >= bytes.Length then i <- bytes.Length
      elif
        bytes[i + 1] >= 0xA1uy && bytes[i + 1] <= 0xFEuy && bytes[i + 2] >= 0xA1uy && bytes[i + 2] <= 0xFEuy
      then
        multiByte <- true
        i <- i + 3
      else ok <- false
    elif b >= 0xA1uy && b <= 0xFEuy then
      if i + 1 >= bytes.Length then i <- bytes.Length
      elif bytes[i + 1] >= 0xA1uy && bytes[i + 1] <= 0xFEuy then
        multiByte <- true
        i <- i + 2
      else ok <- false
    else ok <- false

  ok && multiByte

/// ISO-2022-JP はエスケープ シーケンスで一意に識別できるため、他候補より先に判定する。
let private isIso2022Jp (bytes: ReadOnlySpan<byte>) =
  let mutable i = 0
  let mutable hasEscape = false
  let mutable sevenBit = true

  while i < bytes.Length do
    if bytes[i] >= 0x80uy then
      sevenBit <- false
      i <- bytes.Length
    else
      if bytes[i] = 0x1Buy && i + 2 < bytes.Length then
        let b1 = bytes[i + 1]
        let b2 = bytes[i + 2]

        let known =
          (b1 = byte '$' && (b2 = byte '@' || b2 = byte 'B' || b2 = byte 'A'))
          || (b1 = byte '(' && (b2 = byte 'B' || b2 = byte 'J' || b2 = byte 'I'))

        if known then hasEscape <- true

      i <- i + 1

  sevenBit && hasEscape

let private isGb18030 (bytes: ReadOnlySpan<byte>) =
  let mutable i = 0
  let mutable ok = true
  let mutable multiByte = false

  while ok && i < bytes.Length do
    let b = bytes[i]

    if b <= 0x7Fuy then i <- i + 1
    elif b >= 0x81uy && b <= 0xFEuy then
      if i + 1 >= bytes.Length then i <- bytes.Length
      else
        let t = bytes[i + 1]

        if t >= 0x30uy && t <= 0x39uy then
          // 4 バイト列。第 3 バイトは 0x81..0xFE、第 4 バイトは 0x30..0x39。
          if i + 3 >= bytes.Length then i <- bytes.Length
          elif bytes[i + 2] >= 0x81uy && bytes[i + 2] <= 0xFEuy && bytes[i + 3] >= 0x30uy && bytes[i + 3] <= 0x39uy then
            multiByte <- true
            i <- i + 4
          else ok <- false
        elif (t >= 0x40uy && t <= 0x7Euy) || (t >= 0x80uy && t <= 0xFEuy) then
          multiByte <- true
          i <- i + 2
        else ok <- false
    else ok <- false

  ok && multiByte

let private isBig5 (bytes: ReadOnlySpan<byte>) =
  let mutable i = 0
  let mutable ok = true
  let mutable multiByte = false

  while ok && i < bytes.Length do
    let b = bytes[i]

    if b <= 0x7Fuy then i <- i + 1
    elif b >= 0x81uy && b <= 0xFEuy then
      if i + 1 >= bytes.Length then i <- bytes.Length
      else
        let t = bytes[i + 1]

        if (t >= 0x40uy && t <= 0x7Euy) || (t >= 0xA1uy && t <= 0xFEuy) then
          multiByte <- true
          i <- i + 2
        else ok <- false
    else ok <- false

  ok && multiByte

let private isEucKr (bytes: ReadOnlySpan<byte>) =
  let mutable i = 0
  let mutable ok = true
  let mutable multiByte = false

  while ok && i < bytes.Length do
    let b = bytes[i]

    if b <= 0x7Fuy then i <- i + 1
    elif b >= 0x81uy && b <= 0xFEuy then
      if i + 1 >= bytes.Length then i <- bytes.Length
      else
        let t = bytes[i + 1]

        if (t >= 0x41uy && t <= 0x5Auy) || (t >= 0x61uy && t <= 0x7Auy) || (t >= 0x81uy && t <= 0xFEuy) then
          multiByte <- true
          i <- i + 2
        else ok <- false
    else ok <- false

  ok && multiByte

/// 適合検査の固定順序。この順序が判定の決定性を与えるため、変更は成果物の非互換変更である。
/// 関数値は byref 型を引数に取れないため、表ではなく明示的な分岐で表現する。
let private legacyOrder = [| ShiftJis; EucJp; Gb18030; Big5; EucKr |]

let private validateLegacy (candidate: DetectedEncoding) (bytes: ReadOnlySpan<byte>) =
  match candidate with
  | ShiftJis -> isShiftJis bytes
  | EucJp -> isEucJp bytes
  | Gb18030 -> isGb18030 bytes
  | Big5 -> isBig5 bytes
  | EucKr -> isEucKr bytes
  | Utf8
  | Utf8WithBom
  | Utf16Le
  | Utf16Be
  | Iso2022Jp
  | Binary
  | Undetermined -> false

/// 先頭 `DetectionPrefixBytes` から符号化を判定する。
/// `totalLength` は元ファイルの全長で、prefix が切り詰められたかの判断にのみ使う。
let detect (prefix: ReadOnlySpan<byte>) (totalLength: int64) : Detection =
  let struct (bomLength, bomEncoding) = bomLengthOf prefix

  match bomEncoding with
  | ValueSome encoding ->
    { Encoding = encoding
      BomLength = bomLength
      Ambiguous = false
      Candidates = Array.empty }
  | ValueNone ->

  if prefix.Length = 0 then
    { Encoding = Utf8
      BomLength = 0
      Ambiguous = false
      Candidates = Array.empty }
  elif containsNul prefix then
    { Encoding = Binary
      BomLength = 0
      Ambiguous = false
      Candidates = Array.empty }
  else

  let truncated = int64 prefix.Length < totalLength

  // ISO-2022-JP は 7 ビットのみで構成されるため、UTF-8 としても常に妥当になる。
  // エスケープ シーケンスで一意に識別できるこちらを先に判定しなければ、
  // すべて UTF-8 と誤判定される。
  if isIso2022Jp prefix then
    { Encoding = Iso2022Jp
      BomLength = 0
      Ambiguous = false
      Candidates = Array.empty }
  elif Ascii.IsValid prefix || isValidUtf8 prefix truncated then
    { Encoding = Utf8
      BomLength = 0
      Ambiguous = false
      Candidates = Array.empty }
  else

  let mutable chosen = ValueNone
  let mutable matches = 0
  let mutable matched = 0u

  for index in 0 .. legacyOrder.Length - 1 do
    let candidate = legacyOrder[index]

    if validateLegacy candidate prefix then
      matches <- matches + 1
      matched <- matched ||| (1u <<< index)

      if chosen.IsNone then chosen <- ValueSome candidate

  match chosen with
  | ValueSome encoding ->
    { Encoding = encoding
      BomLength = 0
      Ambiguous = matches > 1
      // 候補が 1 つのときだけ確定とみなせる。配列の割り当ては曖昧なときに限る。
      Candidates =
        if matches > 1 then
          legacyOrder
          |> Array.mapi (fun index candidate -> struct (index, candidate))
          |> Array.choose (fun (struct (index, candidate)) ->
            if matched &&& (1u <<< index) <> 0u then Some candidate else None)
        else Array.empty }
  | ValueNone ->
    { Encoding = Undetermined
      BomLength = 0
      Ambiguous = false
      Candidates = Array.empty }
