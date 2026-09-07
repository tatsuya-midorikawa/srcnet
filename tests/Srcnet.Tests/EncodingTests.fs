/// エンコーディング判定のテスト。docs/testing.md T-4 のレガシー符号化項目に対応する。
module Srcnet.Tests.EncodingTests

open System
open Xunit
open Srcnet.Text

let private detect (bytes: byte[]) =
  Encodings.detect (ReadOnlySpan bytes) (int64 bytes.Length)

[<Fact>]
let ``空のファイルは UTF-8 とみなす`` () =
  let detection = detect [||]
  Assert.Equal(Encodings.Utf8, detection.Encoding)
  Assert.Equal(0, detection.BomLength)

[<Fact>]
let ``BOM を優先して判定する`` () =
  Assert.Equal(Encodings.Utf8WithBom, (detect [| 0xEFuy; 0xBBuy; 0xBFuy; 0x61uy |]).Encoding)
  Assert.Equal(Encodings.Utf16Le, (detect [| 0xFFuy; 0xFEuy; 0x61uy; 0x00uy |]).Encoding)
  Assert.Equal(Encodings.Utf16Be, (detect [| 0xFEuy; 0xFFuy; 0x00uy; 0x61uy |]).Encoding)

[<Fact>]
let ``BOM の長さを返す`` () =
  Assert.Equal(3, (detect [| 0xEFuy; 0xBBuy; 0xBFuy |]).BomLength)
  Assert.Equal(2, (detect [| 0xFFuy; 0xFEuy |]).BomLength)

[<Fact>]
let ``妥当な UTF-8 は UTF-8 と判定する`` () =
  let bytes = Text.Encoding.UTF8.GetBytes "日本語のコメント"
  Assert.Equal(Encodings.Utf8, (detect bytes).Encoding)

[<Fact>]
let ``NUL を含むファイルはバイナリと判定する`` () =
  Assert.Equal(Encodings.Binary, (detect [| 0x7Fuy; 0x45uy; 0x4Cuy; 0x46uy; 0x00uy; 0x01uy |]).Encoding)

[<Fact>]
let ``Shift_JIS の構造を認識する`` () =
  // 「日本語」を CP932 で符号化したバイト列。UTF-8 としては不正。
  let bytes = [| 0x93uy; 0xFAuy; 0x96uy; 0x7Cuy; 0x8Cuy; 0xEAuy |]
  Assert.NotEqual(Encodings.Utf8, (detect bytes).Encoding)
  Assert.NotEqual(Encodings.Undetermined, (detect bytes).Encoding)

[<Fact>]
let ``ISO-2022-JP はエスケープ シーケンスで一意に識別される`` () =
  // ESC $ B ... ESC ( B
  let bytes =
    [| 0x1Buy
       0x24uy
       0x42uy
       0x46uy
       0x7Cuy
       0x4Buy
       0x5Cuy
       0x1Buy
       0x28uy
       0x42uy |]

  Assert.Equal(Encodings.Iso2022Jp, (detect bytes).Encoding)

[<Fact>]
let ``ISO-2022-JP はエスケープがなければ選ばれない`` () =
  let bytes = Text.Encoding.ASCII.GetBytes "plain ascii"
  Assert.Equal(Encodings.Utf8, (detect bytes).Encoding)

[<Fact>]
let ``GB18030 の 4 バイト列を認識する`` () =
  let bytes = [| 0x81uy; 0x30uy; 0x81uy; 0x30uy |]
  Assert.Equal(Encodings.Gb18030, (detect bytes).Encoding)

[<Fact>]
let ``どの規則にも適合しないバイト列は判定不能になる`` () =
  // 0xFF は先頭バイトとしてどのレガシー符号化にも現れない。
  let bytes = [| 0x41uy; 0xFFuy; 0xFFuy; 0x42uy |]
  Assert.Equal(Encodings.Undetermined, (detect bytes).Encoding)

[<Fact>]
let ``複数の符号化に適合する場合は曖昧と報告する`` () =
  // 0x81 0x81 は Shift_JIS / GB18030 / Big5 / EUC-KR のいずれの文法にも適合する。
  let detection = detect [| 0x81uy; 0x81uy |]
  Assert.True detection.Ambiguous

[<Fact>]
let ``判定は同じ入力に対して常に同じ結果を返す`` () =
  let bytes = [| 0x93uy; 0xFAuy; 0x96uy; 0x7Cuy |]
  let first = detect bytes

  for _ in 1..16 do
    let repeated = detect bytes
    Assert.Equal(first.Encoding, repeated.Encoding)
    Assert.Equal(first.Ambiguous, repeated.Ambiguous)

[<Fact>]
let ``先頭を切り出した場合、末尾の不完全な UTF-8 列を失敗としない`` () =
  let full = Text.Encoding.UTF8.GetBytes "あいうえお"
  // 最後の 1 バイトを落とし、元の全長を伝える。
  let prefix = full[0 .. full.Length - 2]
  let detection = Encodings.detect (ReadOnlySpan prefix) (int64 full.Length)
  Assert.Equal(Encodings.Utf8, detection.Encoding)

[<Fact>]
let ``符号化コードは往復する`` () =
  let all =
    [ Encodings.Utf8
      Encodings.Utf8WithBom
      Encodings.Utf16Le
      Encodings.Utf16Be
      Encodings.ShiftJis
      Encodings.EucJp
      Encodings.Iso2022Jp
      Encodings.Gb18030
      Encodings.Big5
      Encodings.EucKr
      Encodings.Binary
      Encodings.Undetermined ]

  let codes = all |> List.map Encodings.toCode
  Assert.Equal(codes.Length, List.distinct codes |> List.length)

// --- 打ち切られた prefix 末尾の扱い（backlogs/completed/009） ---

/// 元ファイルが prefix より長いことを検出器へ伝える。
let private detectTruncated (bytes: byte[]) =
  Encodings.detect (ReadOnlySpan bytes) (int64 bytes.Length + 1L)

[<Fact>]
let ``prefix 末尾で途切れた妥当な多バイト列は許容する`` () =
  // 2〜4 バイト列のそれぞれについて、途中で切れた形をすべて許容しなければならない。
  let sequences =
    [ Text.Encoding.UTF8.GetBytes "é" // 2 バイト
      Text.Encoding.UTF8.GetBytes "あ" // 3 バイト
      Text.Encoding.UTF8.GetBytes "𠮷" ] // 4 バイト

  for sequence in sequences do
    for keep in 1 .. sequence.Length - 1 do
      let bytes =
        Array.append (Text.Encoding.ASCII.GetBytes "abc") sequence[0 .. keep - 1]

      Assert.Equal(Encodings.Utf8, (detectTruncated bytes).Encoding)

[<Fact>]
let ``prefix 末尾にある常に不正なバイトは UTF-8 として受理しない`` () =
  // 機械的に末尾 3 バイトを削ると、これらを見逃して UTF-8 と誤判定する。
  for invalid in [ 0x80uy; 0xC0uy; 0xC1uy; 0xF5uy; 0xF8uy; 0xFEuy; 0xFFuy ] do
    let bytes = Array.append (Text.Encoding.ASCII.GetBytes "abc") [| invalid |]
    Assert.NotEqual(Encodings.Utf8, (detectTruncated bytes).Encoding)

[<Fact>]
let ``prefix 末尾の overlong と surrogate は不完全列として許容しない`` () =
  // E0 A0..BF / ED 80..9F / F0 90..BF / F4 80..8F の範囲外は、
  // 続きが何であっても妥当な scalar にならない。
  let rejected =
    [ [| 0xE0uy; 0x9Fuy |] // overlong
      [| 0xEDuy; 0xA0uy |] // surrogate
      [| 0xF0uy; 0x8Fuy |] // overlong
      [| 0xF4uy; 0x90uy |] ] // 範囲外 scalar

  for sequence in rejected do
    let bytes = Array.append (Text.Encoding.ASCII.GetBytes "abc") sequence
    Assert.NotEqual(Encodings.Utf8, (detectTruncated bytes).Encoding)

[<Fact>]
let ``prefix 境界の前後をずらしても判定は一貫する`` () =
  // 不正バイトの位置を prefix 末尾から動かしても、UTF-8 と判定してはならない。
  for offset in 0..6 do
    let bytes =
      Array.concat
        [ Text.Encoding.ASCII.GetBytes(String('a', 8))
          [| 0xFFuy |]
          Text.Encoding.ASCII.GetBytes(String('b', offset)) ]

    Assert.NotEqual(Encodings.Utf8, (detectTruncated bytes).Encoding)

[<Fact>]
let ``曖昧な判定は候補を捨てずに返す`` () =
  // ADR-5 に従い、判定順の先頭を確定値として扱わせない。
  let detection = detect [| 0x81uy; 0x81uy |]
  Assert.True detection.Ambiguous
  Assert.True(detection.Candidates.Length > 1)
  Assert.Contains(Encodings.ShiftJis, detection.Candidates)

[<Fact>]
let ``確定した判定は候補を持たない`` () =
  let detection = detect(Text.Encoding.UTF8.GetBytes "日本語")
  Assert.False detection.Ambiguous
  Assert.Empty detection.Candidates

[<Fact>]
let ``レガシー符号化の不完全列は prefix を切り出した場合だけ候補になる`` () =
  let cases =
    [ Encodings.ShiftJis, [| 0x82uy; 0xA0uy; 0x82uy |]
      Encodings.EucJp, [| 0xA4uy; 0xA2uy; 0x8Euy |]
      Encodings.EucJp, [| 0xA4uy; 0xA2uy; 0x8Fuy; 0xA1uy |]
      Encodings.Gb18030, [| 0x81uy; 0x30uy; 0x81uy; 0x30uy; 0x81uy; 0x30uy |]
      Encodings.Big5, [| 0xA4uy; 0x40uy; 0xA4uy |]
      Encodings.EucKr, [| 0xB0uy; 0xA1uy; 0xB0uy |] ]

  let includes encoding (detection: Encodings.Detection) =
    detection.Encoding = encoding || Array.contains encoding detection.Candidates

  for encoding, bytes in cases do
    Assert.False(includes encoding (detect bytes), Encodings.name encoding)
    Assert.True(includes encoding (detectTruncated bytes), Encodings.name encoding)

[<Fact>]
let ``レガシー符号化の prefix 末尾も既読の不正バイトを見逃さない`` () =
  let cases =
    [ Encodings.EucJp, [| 0xA4uy; 0xA2uy; 0x8Fuy; 0x20uy |]
      Encodings.Gb18030, [| 0x81uy; 0x30uy; 0x81uy; 0x30uy; 0x81uy; 0x30uy; 0x20uy |] ]

  for encoding, bytes in cases do
    let detection = detectTruncated bytes
    Assert.NotEqual(encoding, detection.Encoding)
    Assert.DoesNotContain(encoding, detection.Candidates)

[<Fact>]
let ``ISO-2022-JP でない文字集合の指示子を日本語と誤判定しない`` () =
  let bytes = [| 0x1Buy; byte '$'; byte 'A'; byte 'a' |]
  Assert.Equal(Encodings.Utf8, (detect bytes).Encoding)

let private decode encoding (source: byte[]) =
  let destination = Array.zeroCreate<byte>(Decoding.maxUtf8Bytes source.Length)

  match Decoding.toUtf8 encoding (ReadOnlySpan source) (Span destination) with
  | Decoding.Converted count -> Text.Encoding.UTF8.GetString(destination, 0, count)
  | outcome -> failwith $"変換されませんでした: {outcome}"

[<Fact>]
let ``UTF-16 の奇数バイトと孤立サロゲートを欠落させず置換する`` () =
  let cases =
    [ Encodings.Utf16Le, [| 0xFFuy; 0xFEuy; 0x61uy; 0uy; 0xFFuy |]
      Encodings.Utf16Be, [| 0xFEuy; 0xFFuy; 0uy; 0x61uy; 0xFFuy |]
      Encodings.Utf16Le, [| 0xFFuy; 0xFEuy; 0x61uy; 0uy; 0uy; 0xD8uy |]
      Encodings.Utf16Be, [| 0xFEuy; 0xFFuy; 0uy; 0x61uy; 0xD8uy; 0uy |] ]

  for encoding, source in cases do
    Assert.Equal("a\uFFFD", decode encoding source)

[<Fact>]
let ``復号時は指定した符号化の BOM だけを除く`` () =
  Assert.Equal(0, Decoding.bomLengthOf Encodings.Utf8WithBom (ReadOnlySpan(Text.Encoding.UTF8.GetBytes "abc")))
  Assert.Equal(0, Decoding.bomLengthOf Encodings.Utf16Le (ReadOnlySpan [| 0xFEuy; 0xFFuy |]))
  Assert.Equal(0, Decoding.bomLengthOf Encodings.Utf16Be (ReadOnlySpan [| 0xFFuy; 0xFEuy |]))
  Assert.Equal(3, Decoding.bomLengthOf Encodings.Utf8WithBom (ReadOnlySpan [| 0xEFuy; 0xBBuy; 0xBFuy |]))

[<Fact>]
let ``判定不能の内容は UTF-8 の置換復号で残す`` () =
  let source = [| byte 'a'; 0xFFuy; 0x0Auy; byte 'b' |]
  Assert.Equal(Encodings.Undetermined, (detect source).Encoding)
  Assert.True(Decoding.isSupported Encodings.Undetermined)
  Assert.Equal("a\uFFFD\nb", decode Encodings.Undetermined source)

[<Fact>]
let ``ISO-2022-JP のローマ字と半角カタカナのモードを復号する`` () =
  let source =
    [| 0x1Buy
       byte '('
       byte 'J'
       0x5Cuy
       0x7Euy
       0x1Buy
       byte '('
       byte 'I'
       0x31uy
       0x21uy
       0x1Buy
       byte '('
       byte 'B'
       0x5Cuy
       0x7Euy |]

  Assert.Equal("\u00A5\u203E\uFF71\uFF61\\~", decode Encodings.Iso2022Jp source)

[<Fact>]
let ``ISO-2022-JP の漢字とカタカナのモードでも空白と制御文字を保持する`` () =
  let jisPrefix = [| 0x1Buy; byte '$'; byte 'B'; 0x24uy; 0x22uy |]
  let kanaPrefix = [| 0x1Buy; byte '('; byte 'I'; 0x31uy |]
  let reset = [| 0x1Buy; byte '('; byte 'B' |]

  let controls =
    Array.append (Array.filter ((<>) 0x1Buy) [| 0uy .. 0x20uy |]) [| 0x7Fuy |]

  for separator in [ [| 0x0Auy |]; controls ] do
    let whitespace = Text.Encoding.ASCII.GetString separator
    let jis = Array.concat [ jisPrefix; separator; [| 0x24uy; 0x23uy |]; reset ]
    let kana = Array.concat [ kanaPrefix; separator; [| 0x32uy |]; reset ]
    Assert.Equal("あ" + whitespace + "ぃ", decode Encodings.Iso2022Jp jis)
    Assert.Equal("\uFF71" + whitespace + "\uFF72", decode Encodings.Iso2022Jp kana)

[<Fact>]
let ``レガシー符号化の不正バイトは次の改行や ASCII を消費しない`` () =
  for encoding in
    [ Encodings.ShiftJis
      Encodings.EucJp
      Encodings.Gb18030
      Encodings.Big5
      Encodings.EucKr ] do
    Assert.Equal("\uFFFD\na", decode encoding [| 0xFFuy; 0x0Auy; byte 'a' |])

  for encoding in [ Encodings.EucJp; Encodings.Big5; Encodings.EucKr ] do
    Assert.Equal("\uFFFD\na", decode encoding [| 0x8Fuy; 0x0Auy; byte 'a' |])

[<Fact>]
let ``ISO-2022-JP の不正な後続バイトは次の指示子を消費しない`` () =
  let source =
    [| 0x1Buy; byte '$'; byte 'B'; 0x46uy; 0x1Buy; byte '('; byte 'B'; byte 'a' |]

  Assert.Equal("\uFFFDa", decode Encodings.Iso2022Jp source)

[<Fact>]
let ``GB18030 の不正な四バイト列は ASCII を文字へ組み込まない`` () =
  Assert.Equal("\uFFFD0\uFFFD A", decode Encodings.Gb18030 [| 0x81uy; 0x30uy; 0x81uy; 0x20uy; byte 'A' |])

[<Fact>]
let ``復号先が不足した場合は部分的な成功として返さない`` () =
  let source = [| 0x82uy; 0xA0uy |]
  let destination = Array.zeroCreate<byte> 2

  Assert.Throws<ArgumentException>(fun () ->
    LegacyEncodings.decode Encodings.ShiftJis (ReadOnlySpan source) (Span destination)
    |> ignore)
  |> ignore
