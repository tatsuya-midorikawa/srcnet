/// レガシー符号化の復号（docs/decisions.md ADR-9、backlog 022）。
///
/// 検証する不変条件は 2 つある。
/// 1. 符号化が一意に決まるファイルは、識別子とコメントが欠落なく復号される（T-4）
/// 2. 符号化が曖昧なファイルでは、候補の先頭を確定値として扱わない（ADR-5）
module Srcnet.Tests.DecodingTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open Srcnet.Core.Graph
open Srcnet.Discovery
open Srcnet.Extraction
open Srcnet.Text

/// 復号したうえで抽出する。走査と同じ経路（判定 → 復号 → 抽出）を通す。
let private extract (assume: Encodings.DetectedEncoding voption) (path: string) =
  let extractor = Extractor.Extractor Extractor.ExtractionOptions.defaults

  use reader =
    new Content.ContentReader(
      Walk.WalkOptions.DefaultMaxFileSizeBytes,
      Extractor.ExtractionOptions.defaults.MaxExtractionBytes,
      assume
    )

  let info = FileInfo path

  match reader.Read(path, info.Length, CancellationToken.None) with
  | Error error -> failwith (Content.ReadError.describe error)
  | Ok summary ->
    let decoded =
      if summary.Decoded && summary.DecodedLength > 0 then
        Encoding.UTF8.GetString(reader.Decoded, 0, summary.DecodedLength)
      else ""

    let extracted =
      if summary.Decoded && summary.DecodedLength > 0 then
        extractor.Extract(
          Language.C,
          Path.GetFileName path,
          reader.Decoded,
          summary.DecodedLength,
          NodeFlags.None,
          CancellationToken.None
        )
      else Model.ExtractedFile.empty Model.Structure

    struct (summary, decoded, extracted)

let private cjkFile (name: string) = Path.Combine(Corpus.corpusPath "cjk", name)

[<Fact>]
let ``一意に判定できるレガシー符号化は欠落なく復号される`` () =
  // ISO-2022-JP は 7 ビットのエスケープ列で構造上一意に決まる。
  let struct (summary, decoded, extracted) = extract ValueNone (cjkFile "iso2022jp.c")

  Assert.Equal(Encodings.Iso2022Jp, summary.Encoding)
  Assert.Empty summary.EncodingCandidates
  Assert.True summary.Decoded

  // 文字化けと欠落がないこと（docs/testing.md T-4）。
  Assert.DoesNotContain("\uFFFD", decoded)
  Assert.Contains("日本語のコメント", decoded)
  Assert.Contains("iso2022_value", decoded)

  if Corpus.hasSyntaxParser() then
    let names = extracted.Symbols |> Array.map (fun symbol -> symbol.Name)
    Assert.Contains("iso2022_value", names)

[<Fact>]
let ``符号化が曖昧なファイルは候補の先頭を確定値として扱わない`` () =
  // EUC-JP のファイルは Shift_JIS としても構造上妥当になる。判定順の先頭で復号すると
  // 文字化けするため、確定できないファイルは復号しない。
  let struct (summary, decoded, extracted) = extract ValueNone (cjkFile "euc_jp.c")

  Assert.True(summary.EncodingCandidates.Length > 1)
  Assert.Contains(Encodings.EucJp, summary.EncodingCandidates)
  Assert.False summary.Decoded
  Assert.Equal("", decoded)
  Assert.Empty extracted.Symbols

[<Fact>]
let ``利用者が符号化を明示すると曖昧なファイルも復号される`` () =
  // 明示された符号化は利用者が持ち込んだ事実であり、こちらの推測ではない。
  let struct (summary, decoded, extracted) = extract (ValueSome Encodings.EucJp) (cjkFile "euc_jp.c")

  Assert.True summary.Decoded
  Assert.DoesNotContain("\uFFFD", decoded)
  Assert.Contains("日本語のコメント", decoded)
  Assert.Contains("値", decoded)

  if Corpus.hasSyntaxParser() then
    let names = extracted.Symbols |> Array.map (fun symbol -> symbol.Name)
    Assert.Contains("値", names)

[<Fact>]
let ``候補に含まれない符号化の指定は適用しない`` () =
  // 明示された符号化が構造規則を満たさないなら、それも誤りである。黙って使わない。
  let struct (summary, _, _) = extract (ValueSome Encodings.Utf16Le) (cjkFile "euc_jp.c")
  Assert.False summary.Decoded

[<Fact>]
let ``主要なレガシー符号化を明示すれば識別子とコメントが復号される`` () =
  let cases =
    [ "shift_jis.c", Encodings.ShiftJis, "日本語のコメント", "値"
      "euc_jp.c", Encodings.EucJp, "日本語のコメント", "値"
      "gb18030.c", Encodings.Gb18030, "注释", "值"
      "big5.c", Encodings.Big5, "註釋", "值"
      "euc_kr.c", Encodings.EucKr, "주석", "값" ]

  for name, encoding, comment, identifier in cases do
    let struct (summary, decoded, extracted) = extract (ValueSome encoding) (cjkFile name)

    Assert.True(summary.Decoded, $"{name} を {Encodings.name encoding} として復号できませんでした")
    Assert.DoesNotContain("\uFFFD", decoded)
    Assert.Contains(comment, decoded)
    Assert.Contains(identifier, decoded)

    if Corpus.hasSyntaxParser() then
      let names = extracted.Symbols |> Array.map (fun symbol -> symbol.Name)
      Assert.Contains(identifier, names)

[<Fact>]
let ``UTF-16 のファイルは復号後に行数が確定する`` () =
  for name in [ "utf16le.c"; "utf16be.c" ] do
    let struct (summary, decoded, _) = extract ValueNone (cjkFile name)

    Assert.True summary.Decoded
    Assert.DoesNotContain("\uFFFD", decoded)
    // 3 行のファイル。バイト単位の計数では 0 になる。
    Assert.Equal(3, summary.LineCount)

[<Fact>]
let ``変換表は同梱されている`` () =
  // 埋め込み資源が publish から落ちると、復号が静かに未対応へ縮退する。
  for encoding in
    [ Encodings.ShiftJis
      Encodings.EucJp
      Encodings.Iso2022Jp
      Encodings.Gb18030
      Encodings.Big5
      Encodings.EucKr ] do
    Assert.True(LegacyEncodings.isSupported encoding, $"{Encodings.name encoding} の変換表がありません")

[<Fact>]
let ``不正なバイト列は置換して継続する`` () =
  // 復号の失敗で走査を止めない。
  let source = [| 0x81uy; 0xFFuy; byte 'a'; 0x82uy |]
  let destination = Array.zeroCreate<byte> (LegacyEncodings.maxUtf8Bytes source.Length)
  let written = LegacyEncodings.decode Encodings.ShiftJis (ReadOnlySpan source) (Span destination)

  Assert.True(written > 0)
  let text = Encoding.UTF8.GetString(destination, 0, written)
  Assert.Contains("a", text)
  Assert.Contains("\uFFFD", text)
