/// ファイルの言語判定と属性フラグの決定。
///
/// 判定は拡張子と固定のファイル名だけを使う。対象リポジトリ由来の設定で判定規則を
/// 変えないのは、リポジトリからツールの挙動を制御させないためである。
/// docs/security.md C-1 を参照。
module Srcnet.Discovery.Classify

open System
open System.Collections.Generic
open Srcnet.Core.Graph
open Srcnet.Core.Paths
open Srcnet.Text

let private byExtension =
  let table = Dictionary<string, Language>(StringComparer.Ordinal)

  let add extension language = table[extension] <- language

  add "c" C
  add "h" CHeader
  add "cc" Cpp
  add "cpp" Cpp
  add "cxx" Cpp
  add "c++" Cpp
  add "hh" CppHeader
  add "hpp" CppHeader
  add "hxx" CppHeader
  add "inc" CppHeader
  add "ipp" CppHeader
  add "m" ObjectiveC
  add "mm" ObjectiveCpp
  add "rs" Rust
  add "py" Python
  add "pyi" Python
  add "js" JavaScript
  add "mjs" JavaScript
  add "cjs" JavaScript
  add "jsx" JavaScript
  add "ts" TypeScript
  add "tsx" Tsx
  add "mts" TypeScript
  add "cts" TypeScript
  add "java" Java
  add "go" Go
  add "cs" CSharp
  add "fs" FSharp
  add "fsi" FSharpSignature
  add "fsx" FSharp
  add "s" Assembly
  add "asm" Assembly
  add "sh" Shell
  add "bash" Shell
  add "zsh" Shell
  add "mk" Makefile
  add "cmake" CMake
  add "gn" GnBuild
  add "gni" GnBuild
  add "yml" Yaml
  add "yaml" Yaml
  add "json" Json
  add "jsonc" Json
  add "toml" Toml
  add "xml" Xml
  add "md" Markdown
  add "markdown" Markdown
  add "txt" PlainText
  table

let private byFileName =
  let table = Dictionary<string, Language>(StringComparer.Ordinal)

  let add name language = table[name] <- language

  add "Makefile" Makefile
  add "makefile" Makefile
  add "GNUmakefile" Makefile
  add "Kbuild" Makefile
  add "CMakeLists.txt" CMake
  add "BUILD.gn" GnBuild
  add "BUILD" GnBuild
  add "Kconfig" Kconfig
  add "OWNERS" Owners
  add "MAINTAINERS" Owners
  add "CODEOWNERS" Owners
  table

/// 拡張子と固定ファイル名から言語を判定する。判定できない場合は `Unknown`。
let language (path: LogicalPath) =
  let name = fileName path

  match byFileName.TryGetValue name with
  | true, known -> known
  | false, _ ->
    // `Kconfig.debug` のような接尾辞付きの構成ファイルを拾う。
    if name.StartsWith("Kconfig", StringComparison.Ordinal) then Kconfig
    else
      match byExtension.TryGetValue(extension path) with
      | true, known -> known
      | false, _ -> Unknown

/// 取り込み元の第三者コードを示すディレクトリ名。
/// Chromium と Linux カーネルで実際に使われている名前を対象にする。
let private vendorDirectories =
  HashSet<string>(
    [ "third_party"; "thirdparty"; "vendor"; "vendored"; "node_modules"; "external"; "externals" ],
    StringComparer.Ordinal
  )

let private testDirectories =
  HashSet<string>([ "test"; "tests"; "testing"; "__tests__"; "spec" ], StringComparer.Ordinal)

/// パスの構造から決まる属性ビット。
///
/// 名前による推定であり確実ではないが、検索順位の減点（docs/query-and-cli.md 3）に使う
/// 用途では十分で、内容を読まずに決定できる利点が大きい。
let pathFlags (path: LogicalPath) =
  let segments = (value path).Split '/'
  let mutable flags = NodeFlags.None

  for index in 0 .. segments.Length - 1 do
    let segment = Unicode.caseFold segments[index]
    let isLastSegment = index = segments.Length - 1

    if not isLastSegment then
      if vendorDirectories.Contains segment then flags <- flags ||| NodeFlags.Vendored
      if testDirectories.Contains segment then flags <- flags ||| NodeFlags.Test

  let name = Unicode.caseFold (fileName path)

  if
    name.StartsWith("test_", StringComparison.Ordinal)
    || name.Contains("_test.", StringComparison.Ordinal)
    || name.Contains(".test.", StringComparison.Ordinal)
    || name.Contains("_unittest.", StringComparison.Ordinal)
  then
    flags <- flags ||| NodeFlags.Test

  if
    name.EndsWith(".g.cs", StringComparison.Ordinal)
    || name.EndsWith(".generated.cs", StringComparison.Ordinal)
    || name.EndsWith(".pb.go", StringComparison.Ordinal)
    || name.EndsWith(".pb.cc", StringComparison.Ordinal)
    || name.EndsWith(".pb.h", StringComparison.Ordinal)
  then
    flags <- flags ||| NodeFlags.Generated

  flags
