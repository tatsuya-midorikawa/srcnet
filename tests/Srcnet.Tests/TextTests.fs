/// Unicode 正規化、表示幅、書記素境界、無害化、語分割のテスト。
/// docs/testing.md T-4（CJK）と T-6（ANSI エスケープ無害化）に対応する。
module Srcnet.Tests.TextTests

open Xunit
open Srcnet.Text

[<Fact>]
let ``ASCII は正規化で新しい文字列を作らない`` () =
  let input = "src/main/parser.c"
  Assert.Same(input, Unicode.normalize input)

[<Fact>]
let ``NFD の濁点付き仮名が NFC へ合成される`` () =
  // "が" を「か」+ 結合濁点で表したもの。macOS のファイルシステムはこの形を返し得る。
  let decomposed = "\u304B\u3099"
  let composed = "\u304C"
  Assert.Equal(composed, Unicode.normalize decomposed)
  Assert.NotEqual<string>(decomposed, composed)

[<Fact>]
let ``正規化は冪等である`` () =
  let inputs = [| "\u304B\u3099"; "がぎぐげご"; "abc"; "한국어"; "\uFF21\uFF22" |]

  for input in inputs do
    let once = Unicode.normalize input
    Assert.Equal(once, Unicode.normalize once)

[<Fact>]
let ``東アジア文字は 2 桁として数える`` () =
  Assert.Equal(2, Unicode.displayWidth "あ")
  Assert.Equal(2, Unicode.displayWidth "漢")
  Assert.Equal(2, Unicode.displayWidth "한")
  Assert.Equal(2, Unicode.displayWidth "\uFF21")
  Assert.Equal(1, Unicode.displayWidth "A")
  Assert.Equal(9, Unicode.displayWidth "日本語abc")

[<Fact>]
let ``結合文字は幅 0 として数える`` () =
  Assert.Equal(2, Unicode.displayWidth "\u304B\u3099")
  Assert.Equal(1, Unicode.graphemeCount "\u304B\u3099")

[<Fact>]
let ``切り詰めは書記素クラスタ境界で行う`` () =
  // 2 桁の文字が 3 つ。幅 5 では 2 文字までしか入らない。
  Assert.Equal("日本", Unicode.truncateToWidth 5 "日本語")
  Assert.Equal("日本語", Unicode.truncateToWidth 6 "日本語")
  Assert.Equal("", Unicode.truncateToWidth 1 "日本語")

[<Fact>]
let ``異体字セレクタ付きの文字を途中で切らない`` () =
  let text = "\U0001F469\u200D\U0001F4BB"
  Assert.Equal(1, Unicode.graphemeCount text)
  // クラスタ全体が入らない幅では 1 文字も出さない。
  Assert.Equal("", Unicode.truncateToWidth 1 text)

[<Fact>]
let ``ケース フォールドは文化圏に依存しない`` () =
  Assert.Equal("i", Unicode.caseFold "I")
  Assert.Equal("straße", Unicode.caseFold "STRAßE")

[<Fact>]
let ``ANSI エスケープが端末出力で無害化される`` () =
  let hostile = "normal\u001b[31mred\u001b[0m"
  let sanitized = Sanitize.forTerminal hostile
  // 文化圏依存の部分文字列検索は制御文字を無視するため、序数比較で判定する
  Assert.False(sanitized.Contains '\u001b')
  Assert.Equal(hostile.Length, sanitized.Length)

[<Fact>]
let ``双方向テキスト制御が無害化される`` () =
  let trojan = "safe\u202Eevil\u202C"
  let sanitized = Sanitize.forTerminal trojan
  Assert.False(sanitized.Contains '\u202E')
  Assert.False(sanitized.Contains '\u202C')

[<Fact>]
let ``アラビア文字マークも両方の端末出力経路で無害化する`` () =
  Assert.Equal("safe\uFFFDevil", Sanitize.forTerminal "safe\u061Cevil")
  Assert.Equal("safe\uFFFDevil\n", Sanitize.forTerminalMultiline "safe\u061Cevil\n")

[<Fact>]
let ``安全な文字列は同じインスタンスを返す`` () =
  let input = "src/parser.c"
  Assert.Same(input, Sanitize.forTerminal input)

[<Fact>]
let ``複数行版は改行とタブを保持する`` () =
  let input = "line1\n\tline2"
  Assert.Same(input, Sanitize.forTerminalMultiline input)
  Assert.False((Sanitize.forTerminalMultiline "a\u001bb").Contains '\u001b')

[<Fact>]
let ``識別子を語へ分割する`` () =
  Assert.Equal<string[]>([| "parse"; "http"; "header" |], Words.split "parse_http_header")
  Assert.Equal<string[]>([| "parse"; "http"; "header" |], Words.split "parseHttpHeader")
  Assert.Equal<string[]>([| "http"; "server" |], Words.split "HTTPServer")
  Assert.Equal<string[]>([| "utf"; "8"; "decoder" |], Words.split "utf8Decoder")
  Assert.Equal<string[]>([| "foo"; "bar" |], Words.split "foo-bar")

[<Fact>]
let ``CJK 識別子はラテン部分と分けて語になる`` () =
  Assert.Equal<string[]>([| "解析"; "parser" |], Words.split "解析Parser")
  Assert.Equal<string[]>([| "文字列"; "buffer" |], Words.split "文字列_buffer")

[<Fact>]
let ``n-gram は文字種で長さを変える`` () =
  // ラテンは 3 文字、CJK は 2 文字。
  Assert.Contains("par", Words.ngrams "parser")
  Assert.Contains("日本", Words.ngrams "日本語")
  Assert.Contains("本語", Words.ngrams "日本語")
  Assert.DoesNotContain("日本語", Words.ngrams "日本語")

[<Fact>]
let ``n-gram は重複を除いた昇順である`` () =
  let grams = Words.ngrams "aaaa"
  Assert.Equal<string[]>([| "aaa" |], grams)

[<Fact>]
let ``短すぎる入力も索引できる`` () =
  Assert.Equal<string[]>([| "ab" |], Words.ngrams "ab")
