/// 同梱する文法の固定表。
///
/// 文法の入口関数は**コンパイル時に決まる固定の一覧**として持つ。解析対象リポジトリの
/// 内容や設定から文法を動的に読み込む経路を作らないことが、任意コード実行を排除する
/// 前提になる（docs/security.md C-1、docs/architecture.md 4）。
///
/// どの文法が実際に同梱されているかは `tools/build_native.py` の構築対象で決まる。
/// 入口記号が無い場合は「その言語は利用不可」として扱い、失敗にはしない。
module Srcnet.Extraction.Grammars

open System
open System.Collections.Concurrent
open System.Runtime.InteropServices
open Srcnet.Core.Graph

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_c()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_cpp()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_python()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_rust()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_go()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_java()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_javascript()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_c_sharp()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_typescript()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_tsx()

[<DllImport(Native.Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private tree_sitter_fsharp()


/// 文法の識別子。`native/sources.json` の `language` に対応する。
type GrammarId =
  | C
  | Cpp
  | Python
  | Rust
  | Go
  | Java
  | JavaScript
  | CSharp
  | TypeScript
  | Tsx
  | FSharp

module GrammarId =

  let name id =
    match id with
    | C -> "c"
    | Cpp -> "cpp"
    | Python -> "python"
    | Rust -> "rust"
    | Go -> "go"
    | Java -> "java"
    | JavaScript -> "javascript"
    | CSharp -> "csharp"
    | TypeScript -> "typescript"
    | Tsx -> "tsx"
    | FSharp -> "fsharp"

  let all =
    [| C
       Cpp
       Python
       Rust
       Go
       Java
       JavaScript
       CSharp
       TypeScript
       Tsx
       FSharp |]

/// 言語から文法への対応。ヘッダーは対応する本体の文法で解析する。
/// 対応する文法がない言語は `ValueNone` を返し、構造層（M1）の扱いのままにする。
let forLanguage language =
  match language with
  | Language.C
  | Language.CHeader -> ValueSome C
  | Language.Cpp
  | Language.CppHeader -> ValueSome Cpp
  | Language.Python -> ValueSome Python
  | Language.Rust -> ValueSome Rust
  | Language.Go -> ValueSome Go
  | Language.Java -> ValueSome Java
  | Language.JavaScript -> ValueSome JavaScript
  | Language.CSharp -> ValueSome CSharp
  | Language.TypeScript -> ValueSome TypeScript
  // `.tsx` は JSX 構文を含み、TypeScript とは別の文法で解析する必要がある。
  | Language.Tsx -> ValueSome Tsx
  | Language.FSharp -> ValueSome FSharp
  // `.fsi` は本体と同じ文法で解析する。ionide の fsharp_signature 文法は
  // module 宣言を含む現実的なシグネチャ ファイルを解析できず、通常の文法が
  // 同じ入力を誤りなく解析したため。C/C++ のヘッダーと同じ扱いになる。
  | Language.FSharpSignature -> ValueSome FSharp
  | Language.Unknown
  | Language.PlainText
  | Language.ObjectiveC
  | Language.ObjectiveCpp
  | Language.Assembly
  | Language.Shell
  | Language.Makefile
  | Language.CMake
  | Language.GnBuild
  | Language.Kconfig
  | Language.Yaml
  | Language.Json
  | Language.Toml
  | Language.Xml
  | Language.Markdown
  | Language.Owners -> ValueNone

let private entryPoint id =
  match id with
  | C -> tree_sitter_c ()
  | Cpp -> tree_sitter_cpp ()
  | Python -> tree_sitter_python ()
  | Rust -> tree_sitter_rust ()
  | Go -> tree_sitter_go ()
  | Java -> tree_sitter_java ()
  | JavaScript -> tree_sitter_javascript ()
  | CSharp -> tree_sitter_c_sharp ()
  | TypeScript -> tree_sitter_typescript ()
  | Tsx -> tree_sitter_tsx ()
  | FSharp -> tree_sitter_fsharp ()

// 入口記号の解決結果は変わらないため一度だけ確かめる。
// 共有ライブラリ自体が無い場合と、特定の文法だけが含まれない場合の両方を吸収する。
let private resolved = ConcurrentDictionary<GrammarId, nativeint>()

let private resolve id =
  resolved.GetOrAdd(
    id,
    fun key ->
      try
        entryPoint key
      with
      | :? DllNotFoundException -> 0n
      | :? EntryPointNotFoundException -> 0n
      | :? BadImageFormatException -> 0n
  )

/// 文法へのポインタ。同梱されていなければ `ValueNone`。
let tryResolve id =
  let pointer = resolve id
  if pointer = 0n then ValueNone else ValueSome pointer

/// 実際に同梱されている文法の一覧。安定した順序で返す。
let available () =
  GrammarId.all |> Array.filter (fun id -> (resolve id) <> 0n)
