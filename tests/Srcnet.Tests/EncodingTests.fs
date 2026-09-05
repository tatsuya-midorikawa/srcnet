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
    [| 0x1Buy; 0x24uy; 0x42uy; 0x46uy; 0x7Cuy; 0x4Buy; 0x5Cuy; 0x1Buy; 0x28uy; 0x42uy |]

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

// --- 打ち切られた prefix 末尾の扱い（backlogs/009） ---

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
      let bytes = Array.append (Text.Encoding.ASCII.GetBytes "abc") sequence[0 .. keep - 1]
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
  let detection = detect (Text.Encoding.UTF8.GetBytes "日本語")
  Assert.False detection.Ambiguous
  Assert.Empty detection.Candidates
