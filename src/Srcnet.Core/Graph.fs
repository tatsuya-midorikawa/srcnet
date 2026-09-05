/// グラフの領域モデル。ノード種別、エッジ種別、確度、言語。
///
/// 各種別には成果物へ書き出す安定した数値を割り当てる。列挙の並び順に依存しないため、
/// 種別を追加しても既存の成果物の意味は変わらない。docs/graph-model.md 1, 2, 3 を参照。
module Srcnet.Core.Graph

open System

/// ノード種別。docs/graph-model.md 1 に対応する。
type NodeKind =
  | Repository
  | Directory
  | File
  | Module
  | Type
  | Function
  | Field
  | Variable
  | Constant
  | Macro
  | BuildTarget
  | ConfigSymbol
  | Owner
  | Note
  | Community

module NodeKind =

  /// 成果物とノード ID に埋め込む安定コード。既存の値を変更してはならない。
  let toCode kind =
    match kind with
    | Repository -> 1uy
    | Directory -> 2uy
    | File -> 3uy
    | Module -> 4uy
    | Type -> 5uy
    | Function -> 6uy
    | Field -> 7uy
    | Variable -> 8uy
    | Constant -> 9uy
    | Macro -> 10uy
    | BuildTarget -> 11uy
    | ConfigSymbol -> 12uy
    | Owner -> 13uy
    | Note -> 14uy
    | Community -> 15uy

  let ofCode code =
    match code with
    | 1uy -> ValueSome Repository
    | 2uy -> ValueSome Directory
    | 3uy -> ValueSome File
    | 4uy -> ValueSome Module
    | 5uy -> ValueSome Type
    | 6uy -> ValueSome Function
    | 7uy -> ValueSome Field
    | 8uy -> ValueSome Variable
    | 9uy -> ValueSome Constant
    | 10uy -> ValueSome Macro
    | 11uy -> ValueSome BuildTarget
    | 12uy -> ValueSome ConfigSymbol
    | 13uy -> ValueSome Owner
    | 14uy -> ValueSome Note
    | 15uy -> ValueSome Community
    | _ -> ValueNone

  let name kind =
    match kind with
    | Repository -> "Repository"
    | Directory -> "Directory"
    | File -> "File"
    | Module -> "Module"
    | Type -> "Type"
    | Function -> "Function"
    | Field -> "Field"
    | Variable -> "Variable"
    | Constant -> "Constant"
    | Macro -> "Macro"
    | BuildTarget -> "BuildTarget"
    | ConfigSymbol -> "ConfigSymbol"
    | Owner -> "Owner"
    | Note -> "Note"
    | Community -> "Community"

/// エッジ種別。docs/graph-model.md 2 に対応する。
type EdgeKind =
  | Contains
  | Includes
  | Imports
  | Declares
  | Defines
  | Calls
  | References
  | Inherits
  | Implements
  | Overrides
  | TypedAs
  | Tests
  | BuiltFrom
  | BuildDepends
  | GuardedBy
  | OwnedBy
  | CoChanged
  | Explains
  | MemberOf

module EdgeKind =

  let toCode kind =
    match kind with
    | Contains -> 1uy
    | Includes -> 2uy
    | Imports -> 3uy
    | Declares -> 4uy
    | Defines -> 5uy
    | Calls -> 6uy
    | References -> 7uy
    | Inherits -> 8uy
    | Implements -> 9uy
    | Overrides -> 10uy
    | TypedAs -> 11uy
    | Tests -> 12uy
    | BuiltFrom -> 13uy
    | BuildDepends -> 14uy
    | GuardedBy -> 15uy
    | OwnedBy -> 16uy
    | CoChanged -> 17uy
    | Explains -> 18uy
    | MemberOf -> 19uy

  /// セグメント ファイル名に使う識別子。docs/storage.md 2 の `<id>.edges.<kind>` に対応する。
  let name kind =
    match kind with
    | Contains -> "CONTAINS"
    | Includes -> "INCLUDES"
    | Imports -> "IMPORTS"
    | Declares -> "DECLARES"
    | Defines -> "DEFINES"
    | Calls -> "CALLS"
    | References -> "REFERENCES"
    | Inherits -> "INHERITS"
    | Implements -> "IMPLEMENTS"
    | Overrides -> "OVERRIDES"
    | TypedAs -> "TYPED_AS"
    | Tests -> "TESTS"
    | BuiltFrom -> "BUILT_FROM"
    | BuildDepends -> "BUILD_DEPENDS"
    | GuardedBy -> "GUARDED_BY"
    | OwnedBy -> "OWNED_BY"
    | CoChanged -> "CO_CHANGED"
    | Explains -> "EXPLAINS"
    | MemberOf -> "MEMBER_OF"

/// 解決の確度。推測を確定として出力しないための表明である。docs/graph-model.md 3 を参照。
type Confidence =
  /// ソースに明示的に書かれている。
  | Extracted
  /// 一意な候補へ決定的規則で解決した。
  | Resolved
  /// 複数候補があり、規則で一意化できない。
  | Ambiguous

module Confidence =

  let toCode confidence =
    match confidence with
    | Extracted -> 1uy
    | Resolved -> 2uy
    | Ambiguous -> 3uy

  let name confidence =
    match confidence with
    | Extracted -> "EXTRACTED"
    | Resolved -> "RESOLVED"
    | Ambiguous -> "AMBIGUOUS"

/// 言語。抽出器の実装有無とは独立に、分類の語彙として先に固定する。
type Language =
  | Unknown
  | PlainText
  | C
  | CHeader
  | Cpp
  | CppHeader
  | ObjectiveC
  | ObjectiveCpp
  | Rust
  | Python
  | JavaScript
  | TypeScript
  | Java
  | Go
  | CSharp
  | FSharp
  | Assembly
  | Shell
  | Makefile
  | CMake
  | GnBuild
  | Kconfig
  | Yaml
  | Json
  | Toml
  | Xml
  | Markdown
  | Owners
  /// JSX 構文を含む TypeScript。`.tsx` は文法が別なので言語としても区別する。
  | Tsx
  /// F# のシグネチャ ファイル（`.fsi`）。C/C++ のヘッダーと同じ理由で本体と区別する。
  | FSharpSignature

module Language =

  let toCode language =
    match language with
    | Unknown -> 0us
    | PlainText -> 1us
    | C -> 2us
    | CHeader -> 3us
    | Cpp -> 4us
    | CppHeader -> 5us
    | ObjectiveC -> 6us
    | ObjectiveCpp -> 7us
    | Rust -> 8us
    | Python -> 9us
    | JavaScript -> 10us
    | TypeScript -> 11us
    | Java -> 12us
    | Go -> 13us
    | CSharp -> 14us
    | FSharp -> 15us
    | Assembly -> 16us
    | Shell -> 17us
    | Makefile -> 18us
    | CMake -> 19us
    | GnBuild -> 20us
    | Kconfig -> 21us
    | Yaml -> 22us
    | Json -> 23us
    | Toml -> 24us
    | Xml -> 25us
    | Markdown -> 26us
    | Owners -> 27us
    | Tsx -> 28us
    | FSharpSignature -> 29us

  let name language =
    match language with
    | Unknown -> "unknown"
    | PlainText -> "text"
    | C -> "c"
    | CHeader -> "c-header"
    | Cpp -> "cpp"
    | CppHeader -> "cpp-header"
    | ObjectiveC -> "objective-c"
    | ObjectiveCpp -> "objective-cpp"
    | Rust -> "rust"
    | Python -> "python"
    | JavaScript -> "javascript"
    | TypeScript -> "typescript"
    | Java -> "java"
    | Go -> "go"
    | CSharp -> "csharp"
    | FSharp -> "fsharp"
    | Assembly -> "assembly"
    | Shell -> "shell"
    | Makefile -> "makefile"
    | CMake -> "cmake"
    | GnBuild -> "gn"
    | Kconfig -> "kconfig"
    | Yaml -> "yaml"
    | Json -> "json"
    | Toml -> "toml"
    | Xml -> "xml"
    | Markdown -> "markdown"
    | Owners -> "owners"
    | Tsx -> "tsx"
    | FSharpSignature -> "fsharp-signature"

/// ノードの属性ビット。docs/graph-model.md 5 の `flags` に対応する。
[<Flags>]
type NodeFlags =
  | None = 0u
  /// 定義の実体（宣言のみではない）。
  | Definition = 1u
  /// 宣言のみ。
  | DeclarationOnly = 2u
  /// テスト コード。
  | Test = 4u
  /// 生成コード。
  | Generated = 8u
  /// 取り込み元の第三者コード（`third_party`、`vendor` など）。
  | Vendored = 16u
  /// 条件付きコンパイル下にある。
  | Conditional = 32u
  /// テキストとして解釈できないファイル。
  | Binary = 64u
  /// 符号化を判定できず、置換文字で復号した。
  | UndeterminedEncoding = 128u
  /// 処理上限を超えたため内容を読んでいない。
  | Skipped = 256u
  /// シンボリック リンク。
  | SymbolicLink = 512u
  /// 複数のレガシー符号化に構造上適合し、単一の符号化へ確定できなかった。
  /// 記録した符号化は候補の先頭にすぎず、抽出時に確定値として扱ってはならない。
  | AmbiguousEncoding = 1024u
