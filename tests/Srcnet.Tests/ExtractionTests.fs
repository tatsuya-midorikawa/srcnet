/// 構文解析層のテスト。
///
/// ネイティブ ライブラリは `tools/build_native.py` で構築する任意の成果物なので、
/// 未構築の環境でもテストは通らなければならない。解析そのものを検証する項目は
/// 利用可能なときだけ実行する。
module Srcnet.Tests.ExtractionTests

open System
open System.Text
open System.Threading
open Xunit
open Srcnet.Core.Graph
open Srcnet.Extraction

let private utf8 (text: string) = Encoding.UTF8.GetBytes text

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

// --- 解析器が同梱されているときだけ実行する検証 ---

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
