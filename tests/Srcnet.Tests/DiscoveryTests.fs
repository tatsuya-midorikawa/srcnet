/// 無視パターンの照合と言語判定のテスト。
module Srcnet.Tests.DiscoveryTests

open Xunit
open Srcnet.Core.Graph
open Srcnet.Core.Paths
open Srcnet.Discovery

/// 列挙値へのメンバー呼び出しは防御的コピーを生むため、ビット演算で判定する。
let private hasFlag (flag: NodeFlags) (flags: NodeFlags) = (flags &&& flag) = flag

let private ok result =
  match result with
  | Ok value -> value
  | Error error -> failwith $"想定外の失敗: {error}"

let private ruleSet depth lines = Ignore.parse depth lines

let private decide (ruleSets: Ignore.RuleSet[]) (path: string) (isDirectory: bool) =
  Ignore.decide ruleSets (Ignore.segmentsOf (ok (tryCreate path))) isDirectory

[<Fact>]
let ``拡張子パターンは任意の階層で一致する`` () =
  let rules = [| ruleSet 0 [ "*.o" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "main.o" false)
  Assert.Equal(Ignore.Ignored, decide rules "kernel/sched/core.o" false)
  Assert.Equal(Ignore.NotMatched, decide rules "kernel/sched/core.c" false)

[<Fact>]
let ``先頭の / はディレクトリに固定する`` () =
  let rules = [| ruleSet 0 [ "/build" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "build" true)
  Assert.Equal(Ignore.NotMatched, decide rules "kernel/build" true)

[<Fact>]
let ``末尾の / はディレクトリのみに一致する`` () =
  let rules = [| ruleSet 0 [ "build/" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "build" true)
  Assert.Equal(Ignore.NotMatched, decide rules "build" false)

[<Fact>]
let ``中間の / を含むパターンは相対位置に固定される`` () =
  let rules = [| ruleSet 0 [ "doc/build" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "doc/build" true)
  Assert.Equal(Ignore.NotMatched, decide rules "a/doc/build" true)

[<Fact>]
let ``二重アスタリスクは 0 個以上の階層に一致する`` () =
  let rules = [| ruleSet 0 [ "doc/**/build" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "doc/build" true)
  Assert.Equal(Ignore.Ignored, decide rules "doc/a/build" true)
  Assert.Equal(Ignore.Ignored, decide rules "doc/a/b/c/build" true)
  Assert.Equal(Ignore.NotMatched, decide rules "src/a/build" true)

[<Fact>]
let ``否定パターンは後勝ちで再包含する`` () =
  let rules = [| ruleSet 0 [ "*.log"; "!important.log" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "debug.log" false)
  Assert.Equal(Ignore.Reincluded, decide rules "important.log" false)

[<Fact>]
let ``同一ファイル内では後の規則が優先する`` () =
  let rules = [| ruleSet 0 [ "!important.log"; "*.log" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "important.log" false)

[<Fact>]
let ``深い位置の無視ファイルが浅い位置より優先する`` () =
  let shallow = ruleSet 0 [ "*.log" ]
  let deep = ruleSet 1 [ "!*.log" ]
  Assert.Equal(Ignore.Reincluded, decide [| shallow; deep |] "sub/a.log" false)
  Assert.Equal(Ignore.Ignored, decide [| shallow; deep |] "a.log" false)

[<Fact>]
let ``コメントと空行は無視される`` () =
  let rules = [| ruleSet 0 [ "# comment"; ""; "   "; "*.o" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "a.o" false)
  Assert.Equal(Ignore.NotMatched, decide rules "comment" false)

[<Fact>]
let ``ルート外を参照するパターンは無視される`` () =
  let rules = [| ruleSet 0 [ "../outside"; ".." ] |]
  Assert.True(Ignore.isEmpty rules[0])

[<Fact>]
let ``疑問符は 1 文字に一致する`` () =
  let rules = [| ruleSet 0 [ "a?.c" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "ab.c" false)
  Assert.Equal(Ignore.NotMatched, decide rules "abc.c" false)

[<Fact>]
let ``文字クラスと範囲を扱える`` () =
  let rules = [| ruleSet 0 [ "a[0-9].c"; "b[!x].c" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "a5.c" false)
  Assert.Equal(Ignore.NotMatched, decide rules "az.c" false)
  Assert.Equal(Ignore.Ignored, decide rules "by.c" false)
  Assert.Equal(Ignore.NotMatched, decide rules "bx.c" false)

[<Fact>]
let ``アスタリスクはセグメント境界を越えない`` () =
  let rules = [| ruleSet 0 [ "doc/*.md" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "doc/a.md" false)
  Assert.Equal(Ignore.NotMatched, decide rules "doc/sub/a.md" false)

[<Fact>]
let ``病的なパターンでも線形時間で終わる`` () =
  // バックトラッキングする実装では指数時間になる形。動的計画法なので終了する。
  let pattern = String.replicate 24 "*a" |> fun body -> $"{body}b"
  let rules = [| ruleSet 0 [ pattern ] |]
  let subject = String.replicate 200 "a"
  Assert.Equal(Ignore.NotMatched, decide rules subject false)

[<Fact>]
let ``CJK のファイル名でも照合できる`` () =
  let rules = [| ruleSet 0 [ "設計/*.md" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "設計/概要.md" false)
  Assert.Equal(Ignore.NotMatched, decide rules "実装/概要.md" false)

// --- 言語判定 ---

[<Fact>]
let ``拡張子から言語を判定する`` () =
  let language path = Classify.language (ok (tryCreate path))
  Assert.Equal(C, language "kernel/sched/core.c")
  Assert.Equal(CHeader, language "include/linux/sched.h")
  Assert.Equal(Cpp, language "base/task.cc")
  Assert.Equal(CppHeader, language "base/task.hpp")
  Assert.Equal(Rust, language "src/main.rs")
  Assert.Equal(FSharp, language "src/Program.fs")
  Assert.Equal(Unknown, language "assets/logo.png")

[<Fact>]
let ``固定のファイル名から言語を判定する`` () =
  let language path = Classify.language (ok (tryCreate path))
  Assert.Equal(Makefile, language "Makefile")
  Assert.Equal(CMake, language "src/CMakeLists.txt")
  Assert.Equal(GnBuild, language "base/BUILD.gn")
  Assert.Equal(Kconfig, language "drivers/Kconfig")
  Assert.Equal(Kconfig, language "drivers/Kconfig.debug")
  Assert.Equal(Owners, language "base/OWNERS")
  Assert.Equal(Owners, language "MAINTAINERS")

[<Fact>]
let ``パスから第三者コードとテストを推定する`` () =
  let flags path = Classify.pathFlags (ok (tryCreate path))
  Assert.True(hasFlag NodeFlags.Vendored (flags "third_party/zlib/zlib.c"))
  Assert.True(hasFlag NodeFlags.Vendored (flags "node_modules/pkg/index.js"))
  Assert.True(hasFlag NodeFlags.Test (flags "src/tests/parser.c"))
  Assert.True(hasFlag NodeFlags.Test (flags "src/parser_test.cc"))
  Assert.True(hasFlag NodeFlags.Test (flags "src/test_parser.py"))
  Assert.Equal(NodeFlags.None, flags "src/parser.c")

[<Fact>]
let ``ディレクトリ名の判定は末尾セグメントを対象にしない`` () =
  // `vendor` という名前のファイルは第三者コードではない。
  Assert.Equal(NodeFlags.None, Classify.pathFlags (ok (tryCreate "src/vendor")))

[<Fact>]
let ``規則数の上限を超えた無視ファイルは切り詰められる`` () =
  // 規則数は照合コストに線形に効き、対象リポジトリが自由に決められる値である。
  let lines = List.init (Ignore.MaxRulesPerFile + 500) (fun index -> $"pattern{index}/*.o")
  let rules = ruleSet 0 lines
  Assert.True rules.IsTruncated
  let withinLimit = ruleSet 0 [ "*.o" ]
  Assert.False withinLimit.IsTruncated
