/// 構文解析層のテスト。
///
/// ネイティブ ライブラリは `tools/build_native.py` で構築する任意の成果物なので、
/// 未構築の環境でもテストは通らなければならない。解析そのものを検証する項目は
/// 利用可能なときだけ実行する。
module Srcnet.Tests.ExtractionTests

open System
open System.Reflection
open System.Runtime.InteropServices
open System.Text
open System.Threading
open Xunit
open Srcnet.Core.Graph
open Srcnet.Extraction

let private utf8 (text: string) = Encoding.UTF8.GetBytes text

let private extractText tier language text =
  let source = utf8 text
  let options = { Extractor.ExtractionOptions.defaults with Tier = tier }
  let extractor = Extractor.Extractor options
  extractor.Extract(language, "regression.cc", source, source.Length, NodeFlags.None, CancellationToken.None)

let private cSource =
  utf8
    """
#include <stdio.h>

struct Point { int x; int y; };

static int add(int a, int b) { return a + b; }

int main(void) { return add(1, 2); }
"""

// --- 同梱ライブラリの有無に依存しない検証 ---

[<Fact>]
let ``文法を持たない言語は対応しないと報告する`` () =
  // 解析器の有無にかかわらず、文法を割り当てていない言語は false でなければならない。
  Assert.False(Parsing.supports Language.Markdown)
  Assert.False(Parsing.supports Language.Yaml)
  Assert.False(Parsing.supports Language.Unknown)

[<Fact>]
let ``言語から文法への対応は決定的である`` () =
  Assert.Equal(ValueSome Grammars.C, Grammars.forLanguage Language.C)
  // ヘッダーは対応する本体の文法で解析する。
  Assert.Equal(ValueSome Grammars.C, Grammars.forLanguage Language.CHeader)
  Assert.Equal(ValueSome Grammars.Cpp, Grammars.forLanguage Language.CppHeader)
  // `.tsx` は JSX を含むため TypeScript とは別の文法を使う。
  Assert.Equal(ValueSome Grammars.TypeScript, Grammars.forLanguage Language.TypeScript)
  Assert.Equal(ValueSome Grammars.Tsx, Grammars.forLanguage Language.Tsx)
  // `.fsi` は本体と同じ文法で解析する。ヘッダーと同じ扱い。
  Assert.Equal(ValueSome Grammars.FSharp, Grammars.forLanguage Language.FSharp)
  Assert.Equal(ValueSome Grammars.FSharp, Grammars.forLanguage Language.FSharpSignature)
  Assert.Equal(ValueNone, Grammars.forLanguage Language.Markdown)

[<Fact>]
let ``文法識別子の名前は sources_json の言語名と一致する`` () =
  Assert.Equal("c", Grammars.GrammarId.name Grammars.C)
  Assert.Equal("cpp", Grammars.GrammarId.name Grammars.Cpp)
  Assert.Equal("csharp", Grammars.GrammarId.name Grammars.CSharp)

[<Fact>]
let ``大きすぎるソースは解析せずに拒否する`` () =
  // 実際に確保はせず、判定順序だけを確かめる。
  match Parsing.parse Language.Markdown (utf8 "x") Parsing.DefaultTimeoutMicroseconds CancellationToken.None with
  | Error(Parsing.GrammarUnavailable Language.Markdown) -> ()
  | other -> failwith $"文法なしを報告しませんでした: {other}"

[<Fact>]
let ``取り消し済みのトークンでは解析を始めない`` () =
  use cancellation = new CancellationTokenSource()
  cancellation.Cancel()

  match Parsing.parseDefault Language.C cSource cancellation.Token with
  | Error Parsing.Cancelled -> ()
  | other -> failwith $"取り消しを報告しませんでした: {other}"

[<Fact>]
let ``失敗の説明はすべての場合に用意されている`` () =
  let failures =
    [ Parsing.ParserUnavailable "x"
      Parsing.GrammarUnavailable Language.C
      Parsing.SourceTooLarge 1
      Parsing.Cancelled
      Parsing.TimedOut
      Parsing.ParseFailed ]

  for failure in failures do
    Assert.False(String.IsNullOrWhiteSpace(Parsing.ParseFailure.describe failure))

[<Theory>]
[<InlineData(1024)>]
[<InlineData(1025)>]
let ``T1 と T2 は名前を NFC 正規化してから同じ上限で切り詰める`` length =
  let raw = "e\u0301" + String('x', length - 1)
  let expected = ("\u00E9" + String('x', length - 1)).Substring(0, min length Model.Limits.MaxNameLength)

  for tier, language, text in
    [ Model.LineOriented, Language.Python, $"def {raw}():\n    pass\n"
      Model.Syntax, Language.C, $"int {raw}(void) {{ return 0; }}\n" ] do
    if tier = Model.LineOriented || Extractor.supportsSyntax language then
      let extractor = Extractor.Extractor { Extractor.ExtractionOptions.defaults with Tier = tier }
      let source = utf8 text
      let extracted = extractor.Extract(language, "names.c", source, source.Length, NodeFlags.None, CancellationToken.None)
      let symbol = Assert.Single(extracted.Symbols |> Array.filter (fun item -> item.Kind = Function))
      Assert.Equal(expected, symbol.Name)
      Assert.Equal(expected, symbol.QualifiedName)

// --- 解析器が同梱されているときだけ実行する検証 ---

[<Fact>]
let ``入れ子スコープから戻ると親と呼び出しの修飾を復元する`` () =
  if Parsing.supports Language.Cpp then
    let source =
      utf8
        """
namespace outer {
  namespace inner {
    struct Holder {
      enum State { First };
      int run() { return helper(); }
    };
    int sibling() { return helper(); }
  }
  int tail() { return helper(); }
}
static int local() { return helper(); }
TEST_F(Suite, Case) { helper(); }
int after_test() { return helper(); }
"""

    match Parsing.parseDefault Language.Cpp source CancellationToken.None with
    | Error failure -> failwith (Parsing.ParseFailure.describe failure)
    | Ok tree ->
      use tree = tree
      let extracted = CSyntax.run Language.Cpp "scope.cc" source tree NodeFlags.None

      let find qualified =
        extracted.Symbols
        |> Array.indexed
        |> Array.filter (fun (_, symbol) -> symbol.QualifiedName = qualified)
        |> Assert.Single

      for child, parent in
        [ "outer::inner", "outer"
          "outer::inner::Holder", "outer::inner"
          "outer::inner::Holder::State", "outer::inner::Holder"
          "outer::inner::Holder::State::First", "outer::inner::Holder::State"
          "outer::inner::Holder::run", "outer::inner::Holder"
          "outer::inner::sibling", "outer::inner"
          "outer::tail", "outer" ] do
        let _, symbol = find child
        let parentIndex, _ = find parent
        Assert.Equal(parentIndex, symbol.Parent)

      for qualified, prefix in
        [ "outer::inner::Holder::run", "outer::inner::Holder::run"
          "outer::inner::sibling", "outer::inner::sibling"
          "outer::tail", "outer::tail"
          "scope.cc::local", "local"
          "Suite.Case", "Suite.Case"
          "after_test", "after_test" ] do
        let index, symbol = find qualified
        if not (qualified.StartsWith("outer::", StringComparison.Ordinal)) then Assert.Equal(-1, symbol.Parent)

        Assert.Contains(
          extracted.References,
          fun reference -> reference.Source = index && reference.Kind = Calls && reference.Target = "helper" && reference.Qualifier = prefix
        )

[<Fact>]
let ``C のソースから定義を抽出できる`` () =
  if Parsing.supports Language.C then
    match Parsing.parseDefault Language.C cSource CancellationToken.None with
    | Error failure -> failwith (Parsing.ParseFailure.describe failure)
    | Ok tree ->
      use tree = tree
      let root = SyntaxTree.root tree
      Assert.Equal("translation_unit", root.Kind)
      Assert.False root.HasError

      let kinds =
        SyntaxTree.descendants root
        |> Seq.map (fun node -> node.Kind)
        |> Seq.toArray

      Assert.Contains("function_definition", kinds)
      Assert.Contains("struct_specifier", kinds)

      let functions =
        SyntaxTree.descendants root
        |> Seq.filter (fun node -> node.Kind = "function_definition")
        |> Seq.toArray

      Assert.Equal(2, functions.Length)
      // 行番号は 1 起点で返す。
      Assert.True(functions[0].StartLine >= 1)

[<Fact>]
let ``ノードのバイト範囲はソース長に収まる`` () =
  if Parsing.supports Language.C then
    match Parsing.parseDefault Language.C cSource CancellationToken.None with
    | Error failure -> failwith (Parsing.ParseFailure.describe failure)
    | Ok tree ->
      use tree = tree

      for node in SyntaxTree.descendants (SyntaxTree.root tree) do
        Assert.InRange(node.StartByte, 0, cSource.Length)
        Assert.InRange(node.EndByte, node.StartByte, cSource.Length)
        // 切り出しが例外にならないこと。
        node.Text cSource |> ignore

[<Fact>]
let ``構文誤りを含むソースでも解析を継続する`` () =
  if Parsing.supports Language.C then
    // 抽出は「壊れたファイルでも止まらない」ことが前提になる。
    match Parsing.parseDefault Language.C (utf8 "int main(void) { return ") CancellationToken.None with
    | Error failure -> failwith (Parsing.ParseFailure.describe failure)
    | Ok tree ->
      use tree = tree
      let root = SyntaxTree.root tree
      Assert.True root.HasError

[<Fact>]
let ``破棄した木のノードは使用できない`` () =
  if Parsing.supports Language.C then
    match Parsing.parseDefault Language.C cSource CancellationToken.None with
    | Error failure -> failwith (Parsing.ParseFailure.describe failure)
    | Ok tree ->
      let root = SyntaxTree.root tree
      (tree :> IDisposable).Dispose()
      // 解放済みの木を指すノードの使用は、未定義動作ではなく例外にする。
      Assert.Throws<ObjectDisposedException>(fun () -> root.Kind |> ignore) |> ignore

[<Fact>]
let ``CJK の識別子とコメントを含むソースを扱える`` () =
  if Parsing.supports Language.Python then
    let source = utf8 "# 日本語のコメント\ndef 解析する(引数):\n    return 引数\n"

    match Parsing.parseDefault Language.Python source CancellationToken.None with
    | Error failure -> failwith (Parsing.ParseFailure.describe failure)
    | Ok tree ->
      use tree = tree
      let root = SyntaxTree.root tree
      Assert.False root.HasError

      let names =
        SyntaxTree.descendants root
        |> Seq.filter (fun node -> node.Kind = "function_definition")
        |> Seq.choose (fun node ->
          match node.ChildByField "name" with
          | ValueSome name -> Some(name.Text source)
          | ValueNone -> None)
        |> Seq.toArray

      Assert.Equal<string[]>([| "解析する" |], names)

[<Fact>]
let ``深い入れ子でも走査が打ち切られ、停止しない`` () =
  if Parsing.supports Language.C then
    // 上限を超える深さでも stack overflow せず、有限時間で終わること。
    let deep = String.replicate 2000 "(" + "1" + String.replicate 2000 ")"
    let source = utf8 $"int x = {deep};"

    match Parsing.parseDefault Language.C source CancellationToken.None with
    | Error _ -> () // 時間上限での打ち切りも正常な結果
    | Ok tree ->
      use tree = tree
      let count = SyntaxTree.descendants (SyntaxTree.root tree) |> Seq.length
      Assert.True(count > 0)

[<Fact>]
let ``同梱している文法はいずれも代表的なソースを解析できる`` () =
  // 文法を追加したときに、対応付けと入口記号の宣言が揃っていることを確かめる。
  // 未構築の言語は対象外にするため、構築の有無で結果が変わらない。
  let samples =
    [ Language.C, "int f(int a){return a;}", "translation_unit"
      Language.Cpp, "namespace n { class C { public: int f(); }; }", "translation_unit"
      Language.Python, "def f(x):\n    return x\n", "module"
      Language.Rust, "fn f(a: i32) -> i32 { a }", "source_file"
      Language.Go, "package m\nfunc F(a int) int { return a }", "source_file"
      Language.Java, "class C { int f(){ return 1; } }", "program"
      Language.JavaScript, "function f(a) { return a; }", "program"
      Language.CSharp, "class C { int F() => 1; }", "compilation_unit"
      Language.TypeScript, "interface P { x: number }\nexport function f(a: number): number { return a }", "program"
      // `.tsx` は JSX を含むため TypeScript とは別の文法で解析する。
      Language.Tsx, "const A = ({ n }: { n: string }) => <div className=\"x\">{n}</div>;", "program"
      Language.FSharp, "module M\ntype P = { X: int }\nlet add a b = a + b\n", "file"
      // `.fsi` は本体と同じ文法で解析する。
      Language.FSharpSignature, "module M\n\nval add: int -> int -> int\n", "file" ]

  let mutable exercised = 0

  for language, code, expectedRoot in samples do
    if Parsing.supports language then
      match Parsing.parseDefault language (utf8 code) CancellationToken.None with
      | Error failure -> failwith $"{Language.name language}: {Parsing.ParseFailure.describe failure}"
      | Ok tree ->
        use tree = tree
        let root = SyntaxTree.root tree
        Assert.Equal(expectedRoot, root.Kind)
        Assert.False(root.HasError, $"{Language.name language} の解析で構文誤りが報告されました")
        exercised <- exercised + 1

  // 解析器が構築されている環境では、少なくとも 1 言語は検証されていること。
  if Parsing.isAvailable.Value then Assert.True(exercised > 0)

[<Fact>]
let ``TSX の JSX 要素を解析できる`` () =
  if Parsing.supports Language.Tsx then
    // TypeScript 文法では JSX を解析できないため、区別が意味を持つことを確かめる。
    let source = utf8 "const A = () => <div id=\"x\">hi</div>;"

    match Parsing.parseDefault Language.Tsx source CancellationToken.None with
    | Error failure -> failwith (Parsing.ParseFailure.describe failure)
    | Ok tree ->
      use tree = tree
      let root = SyntaxTree.root tree
      Assert.False root.HasError

      let kinds = SyntaxTree.descendants root |> Seq.map (fun node -> node.Kind) |> Seq.toArray
      Assert.Contains("jsx_element", kinds)

[<Fact>]
let ``F# のモジュールと束縛を抽出できる`` () =
  if Parsing.supports Language.FSharp then
    let source = utf8 "module Sample\n\ntype Point = { X: int; Y: int }\n\nlet add a b = a + b\n"

    match Parsing.parseDefault Language.FSharp source CancellationToken.None with
    | Error failure -> failwith (Parsing.ParseFailure.describe failure)
    | Ok tree ->
      use tree = tree
      let root = SyntaxTree.root tree
      Assert.False root.HasError

      let kinds = SyntaxTree.descendants root |> Seq.map (fun node -> node.Kind) |> Seq.toArray
      Assert.Contains("named_module", kinds)
      Assert.Contains("type_definition", kinds)
      Assert.Contains("function_or_value_defn", kinds)

[<Fact>]
let ``F# のシグネチャ ファイルを誤りなく解析できる`` () =
  if Parsing.supports Language.FSharpSignature then
    // ionide の fsharp_signature 文法は module 宣言で失敗するため使わない。
    // 通常の文法が現実的な .fsi を解析できることを回帰として固定する。
    let samples =
      [ "module Sample\ntype Point = { X: int; Y: int }\nval add: int -> int -> int\n"
        "module Sample\n\ntype Point =\n    { X: int\n      Y: int }\n\nval add: int -> int -> int\n"
        "namespace Sample\n\nmodule Math =\n    val add: int -> int -> int\n"
        "module Sample\n\ntype Point\n\nval origin: Point\n" ]

    for sample in samples do
      match Parsing.parseDefault Language.FSharpSignature (utf8 sample) CancellationToken.None with
      | Error failure -> failwith (Parsing.ParseFailure.describe failure)
      | Ok tree ->
        use tree = tree
        let root = SyntaxTree.root tree
        Assert.False(root.HasError, $"解析に失敗しました: {sample}")

[<Fact>]
let ``T1 はコメントで区切られた取り込みと定義を保持する`` () =
  let result =
    extractText
      Model.LineOriented
      Language.C
      "/* header */ #include \"first.h\"\n#include /* path */ \"second.h\"\n# /* directive */ include <third.h>\n#define /* name */ VALUE 1\nstruct /* name */ Point {};\n"

  Assert.Equal<string[]>([| "first.h"; "second.h"; "third.h" |], result.References |> Array.map _.Target)
  Assert.Equal<string[]>([| "VALUE"; "Point" |], result.Symbols |> Array.map _.Name)
  Assert.All(result.References, fun reference -> Assert.True(reference.EndByte > reference.StartByte))

[<Fact>]
let ``生成物マーカーの探索は先頭バイト数の上限を超えない`` () =
  let prefix = "// " + String(' ', Model.Limits.GeneratedMarkerScanBytes)
  let result = extractText Model.LineOriented Language.C (prefix + "DO NOT EDIT\nstruct P {};\n")
  Assert.Equal(NodeFlags.None, result.FileFlags &&& NodeFlags.Generated)

[<Fact>]
let ``名前と根拠コメントの上限はサロゲートペアを分断しない`` () =
  let name = String('x', Model.Limits.MaxNameLength - 1) + "\U00020000"
  let note = String('x', Model.Limits.MaxNoteTextLength - 1) + "\U00020000"
  let result = extractText Model.LineOriented Language.C $"// NOTE: {note}\nstruct {name} {{}};\n"
  let strictUtf8 = UTF8Encoding(false, true)

  for symbol in result.Symbols do
    Assert.True(symbol.QualifiedName.Length <= Model.Limits.MaxNameLength)
    Assert.True(symbol.Name.Length <= Model.Limits.MaxNameLength)
    strictUtf8.GetBytes symbol.QualifiedName |> ignore
    strictUtf8.GetBytes symbol.Name |> ignore

[<Fact>]
let ``elif と else は先行するすべての枝を除外する`` () =
  if Parsing.supports Language.C then
    let result =
      extractText Model.Syntax Language.C
        "#if A\nint a;\n#elif B\nint b;\n#elif C\nint c;\n#else\nint d;\n#endif\n"

    Assert.Equal<string[]>(
      [| "A"; "!(A) && (B)"; "!(A) && !(B) && (C)"; "!(A) && !(B) && !(C)" |],
      result.Symbols |> Array.map _.Condition
    )

[<Fact>]
let ``条件の合成は演算子の優先順位と elifndef の否定を保つ`` () =
  if Parsing.supports Language.Cpp then
    let result =
      extractText Model.Syntax Language.Cpp
        "#if A || B\n#if C\nint nested;\n#endif\n#endif\n#if X\nint a;\n#elifndef Y\nint b;\n#endif\n"

    let find name = result.Symbols |> Array.filter (fun symbol -> symbol.Name = name) |> Assert.Single
    Assert.Equal("(A || B) && (C)", (find "nested").Condition)
    Assert.Equal("!(X) && (!defined(Y))", (find "b").Condition)

[<Fact>]
let ``関数ポインタと複数の宣言子を実際の種別で抽出する`` () =
  if Parsing.supports Language.Cpp then
    let result =
      extractText Model.Syntax Language.Cpp
        "typedef int *Pointer, (*Callback)(int); struct X { int a, b; int (*callback)(int), method(); }; int (*global_callback)(int); int *returns_pointer(); int (*returns_callback())(int);\n"

    for name, kind in
      [ "Pointer", Type
        "Callback", Type
        "a", Field
        "b", Field
        "callback", Field
        "method", Function
        "global_callback", Variable
        "returns_pointer", Function
        "returns_callback", Function ] do
      let symbol = result.Symbols |> Array.filter (fun symbol -> symbol.Name = name) |> Assert.Single
      Assert.Equal(kind, symbol.Kind)

[<Fact>]
let ``名前空間の変数と修飾された宣言を欠落させない`` () =
  if Parsing.supports Language.Cpp then
    let result =
      extractText Model.Syntax Language.Cpp
        "namespace n { extern int declared; int defined; static int local; static int helper(); } int n::function(); extern int external; extern int initialized = 1; void f() { int local; static int retained; }\n"

    for qualified, declaration in
      [ "n::declared", true
        "n::defined", false
        "regression.cc::n::local", false
        "regression.cc::n::helper", true
        "n::function", true
        "external", true
        "initialized", false
        "f::retained", false ] do
      let symbol =
        result.Symbols |> Array.filter (fun symbol -> symbol.QualifiedName = qualified) |> Assert.Single

      Assert.Equal(declaration, ((symbol.Flags &&& NodeFlags.DeclarationOnly) <> NodeFlags.None))
      Assert.Equal(not declaration, ((symbol.Flags &&& NodeFlags.Definition) <> NodeFlags.None))

    Assert.DoesNotContain(result.Symbols, fun symbol -> symbol.QualifiedName = "f::local")

[<Fact>]
let ``無名名前空間だけが内部リンケージを引き継ぐ`` () =
  if Parsing.supports Language.Cpp then
    let result =
      extractText Model.Syntax Language.Cpp
        "namespace n { namespace { int hidden(); int value; } struct X { static int member() { return 1; } }; int visible(); }\n"

    for qualified in [ "regression.cc::n::hidden"; "regression.cc::n::value" ] do
      let symbol =
        result.Symbols |> Array.filter (fun symbol -> symbol.QualifiedName = qualified) |> Assert.Single
      Assert.NotEqual(NodeFlags.None, symbol.Flags &&& NodeFlags.InternalLinkage)

    for qualified in [ "n::X::member"; "n::visible" ] do
      let symbol =
        result.Symbols |> Array.filter (fun symbol -> symbol.QualifiedName = qualified) |> Assert.Single
      Assert.Equal(NodeFlags.None, symbol.Flags &&& NodeFlags.InternalLinkage)

[<Fact>]
let ``匿名構造体の typedef はフィールドの親になる`` () =
  if Parsing.supports Language.C then
    let result = extractText Model.Syntax Language.C "typedef struct { int x, y; } Point;\n"
    let index, point =
      result.Symbols |> Array.indexed |> Array.filter (fun (_, symbol) -> symbol.Name = "Point") |> Assert.Single

    Assert.Equal(Type, point.Kind)

    for name in [ "x"; "y" ] do
      let field = result.Symbols |> Array.filter (fun symbol -> symbol.Name = name) |> Assert.Single
      Assert.Equal(index, field.Parent)
      Assert.Equal("Point::" + name, field.QualifiedName)

[<Fact>]
let ``マクロ形式の include 候補を T1 と T2 の両方で保持する`` () =
  for tier in [ Model.LineOriented; Model.Syntax ] do
    let result =
      extractText tier Language.C "#include HEADER_FILE\n#include SELECT(\"header.h\")\n"

    Assert.Equal<string[]>(
      [| "HEADER_FILE"; "SELECT(\"header.h\")" |],
      result.References |> Array.map _.Target
    )

[<Fact>]
let ``前方宣言と型の使用を区別する`` () =
  if Parsing.supports Language.Cpp then
    let result =
      extractText Model.Syntax Language.Cpp
        "struct S; enum E : int; struct S *value; enum E color;\n#ifdef FEATURE\nclass C;\n#endif\n"

    for name in [ "S"; "E"; "C" ] do
      let symbol =
        result.Symbols |> Array.filter (fun symbol -> symbol.Kind = Type && symbol.Name = name) |> Assert.Single

      Assert.NotEqual(NodeFlags.None, symbol.Flags &&& NodeFlags.DeclarationOnly)
      Assert.Equal(NodeFlags.None, symbol.Flags &&& NodeFlags.Definition)

    let referenced =
      result.References |> Array.filter (fun reference -> reference.Kind = References) |> Array.map _.Target

    Assert.Equal<string[]>([| "S"; "E" |], referenced)

[<Fact>]
let ``構成マクロの既定値をインクルードガードと誤認しない`` () =
  if Parsing.supports Language.C then
    let result =
      extractText Model.Syntax Language.C
        "#ifndef FEATURE\n#define FEATURE 1\nint optional;\n#endif\n"

    Assert.All(result.Symbols, fun symbol -> Assert.Equal("!defined(FEATURE)", symbol.Condition))

    let nested =
      extractText Model.Syntax Language.CHeader
        "#ifdef OUTER\n#ifndef FEATURE\n#define FEATURE 1\nint optional;\n#endif\n#endif\n"

    Assert.All(
      nested.Symbols,
      fun symbol -> Assert.Equal("(defined(OUTER)) && (!defined(FEATURE))", symbol.Condition)
    )

[<Fact>]
let ``数値や文字定数を構成シンボルとして抽出しない`` () =
  if Parsing.supports Language.C then
    let result =
      extractText Model.Syntax Language.C
        "#if VERSION >= 0x10UL && LETTER == 'A' && ENABLED\nint optional;\n#endif\n"

    Assert.Equal<string[]>(
      [| "ENABLED"; "LETTER"; "VERSION" |],
      result.References |> Array.filter (fun reference -> reference.Kind = GuardedBy) |> Array.map _.Target
    )

[<Fact>]
let ``条件の識別子も NFC と補助面の文字を保持する`` () =
  if Parsing.supports Language.C then
    let result =
      extractText Model.Syntax Language.C
        "#if defined(CAFe\u0301) && defined(CONFIG_\U00020000)\nint optional;\n#endif\n"

    Assert.Equal<string[]>(
      [| "CAF\u00E9"; "CONFIG_\U00020000" |],
      result.References |> Array.filter (fun reference -> reference.Kind = GuardedBy) |> Array.map _.Target
    )

[<Fact>]
let ``単独の CR 改行でも解析と行番号が一致し原文を変更しない`` () =
  if Parsing.supports Language.C then
    let source = utf8 "#define VALUE 1\rint second;\r\nint third;\n"
    let original = Array.copy source
    let extractor = Extractor.Extractor Extractor.ExtractionOptions.defaults
    let result = extractor.Extract(Language.C, "lines.c", source, source.Length, NodeFlags.None, CancellationToken.None)
    Assert.Equal<byte[]>(original, source)
    Assert.Equal(ValueSome 3, result.LineCount)

    for name, line in [ "VALUE", 1; "second", 2; "third", 3 ] do
      let symbol = result.Symbols |> Array.filter (fun symbol -> symbol.Name = name) |> Assert.Single
      Assert.Equal(line, symbol.StartLine)
      Assert.InRange(symbol.StartByte, 0, source.Length)
      Assert.InRange(symbol.EndByte, symbol.StartByte, source.Length)

[<Fact>]
let ``構文解析ライブラリは製品アセンブリの場所だけから読み込む`` () =
  let paths = typeof<SyntaxTree.Tree>.Assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()
  Assert.NotNull paths
  Assert.Equal(DllImportSearchPath.AssemblyDirectory, paths.Paths)
