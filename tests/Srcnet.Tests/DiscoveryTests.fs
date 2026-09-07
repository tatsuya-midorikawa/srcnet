/// 無視パターンの照合と言語判定のテスト。
module Srcnet.Tests.DiscoveryTests

open System
open Xunit
open Srcnet.Core.Diagnostics
open Srcnet.Core.Graph
open Srcnet.Core.Paths
open Srcnet.Discovery
open Srcnet.Extraction
open Srcnet.Text

/// 列挙値へのメンバー呼び出しは防御的コピーを生むため、ビット演算で判定する。
let private hasFlag (flag: NodeFlags) (flags: NodeFlags) = (flags &&& flag) = flag

let private ok result =
  match result with
  | Ok value -> value
  | Error error -> failwith $"想定外の失敗: {error}"

let private ruleSet depth lines = Ignore.parse depth lines

let private decide (ruleSets: Ignore.RuleSet[]) (path: string) (isDirectory: bool) =
  Ignore.decide ruleSets (Ignore.segmentsOf(ok(tryCreate path))) isDirectory

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
let ``末尾の二重アスタリスクは親自身を除外しない`` () =
  let rules = [| ruleSet 0 [ "build/**"; "!build/keep.c" ] |]
  Assert.Equal(Ignore.NotMatched, decide rules "build" true)
  Assert.Equal(Ignore.Ignored, decide rules "build/drop.c" false)
  Assert.Equal(Ignore.Ignored, decide rules "build/sub/drop.c" false)
  Assert.Equal(Ignore.Reincluded, decide rules "build/keep.c" false)

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

[<Fact>]
let ``無視パターンも論理パスと同じ NFC で照合する`` () =
  let rules = [| ruleSet 0 [ "\u304B\u3099/*.o" ] |]
  Assert.Equal(Ignore.Ignored, decide rules "が/main.o" false)

// --- 言語判定 ---

[<Fact>]
let ``拡張子から言語を判定する`` () =
  let language path = Classify.language(ok(tryCreate path))
  Assert.Equal(C, language "kernel/sched/core.c")
  Assert.Equal(CHeader, language "include/linux/sched.h")
  Assert.Equal(Cpp, language "base/task.cc")
  Assert.Equal(CppHeader, language "base/task.hpp")
  Assert.Equal(Rust, language "src/main.rs")
  Assert.Equal(FSharp, language "src/Program.fs")
  Assert.Equal(Unknown, language "assets/logo.png")

[<Fact>]
let ``固定のファイル名から言語を判定する`` () =
  let language path = Classify.language(ok(tryCreate path))
  Assert.Equal(Makefile, language "Makefile")
  Assert.Equal(CMake, language "src/CMakeLists.txt")
  Assert.Equal(GnBuild, language "base/BUILD.gn")
  Assert.Equal(Kconfig, language "drivers/Kconfig")
  Assert.Equal(Kconfig, language "drivers/Kconfig.debug")
  Assert.Equal(Owners, language "base/OWNERS")
  Assert.Equal(Owners, language "MAINTAINERS")

[<Fact>]
let ``Kconfig の接頭辞だけが一致するソースを誤分類しない`` () =
  Assert.Equal(FSharp, Classify.language(ok(tryCreate "KconfigReader.fs")))
  Assert.Equal(PlainText, Classify.language(ok(tryCreate "Kconfiguration.txt")))

[<Fact>]
let ``大文字の C と H の拡張子は C++ として分類する`` () =
  Assert.Equal(Cpp, Classify.language(ok(tryCreate "src/example.C")))
  Assert.Equal(CppHeader, Classify.language(ok(tryCreate "src/example.H")))
  Assert.Equal(C, Classify.language(ok(tryCreate "src/example.c")))
  Assert.Equal(CHeader, Classify.language(ok(tryCreate "src/example.h")))

[<Fact>]
let ``パスから第三者コードとテストを推定する`` () =
  let flags path = Classify.pathFlags(ok(tryCreate path))
  Assert.True(hasFlag NodeFlags.Vendored (flags "third_party/zlib/zlib.c"))
  Assert.True(hasFlag NodeFlags.Vendored (flags "node_modules/pkg/index.js"))
  Assert.True(hasFlag NodeFlags.Test (flags "src/tests/parser.c"))
  Assert.True(hasFlag NodeFlags.Test (flags "src/parser_test.cc"))
  Assert.True(hasFlag NodeFlags.Test (flags "src/test_parser.py"))
  Assert.Equal(NodeFlags.None, flags "src/parser.c")

[<Fact>]
let ``ディレクトリ名の判定は末尾セグメントを対象にしない`` () =
  // `vendor` という名前のファイルは第三者コードではない。
  Assert.Equal(NodeFlags.None, Classify.pathFlags(ok(tryCreate "src/vendor")))

[<Fact>]
let ``規則数の上限を超えた無視ファイルは切り詰められる`` () =
  // 規則数は照合コストに線形に効き、対象リポジトリが自由に決められる値である。
  let lines =
    List.init (Ignore.MaxRulesPerFile + 500) (fun index -> $"pattern{index}/*.o")

  let rules = ruleSet 0 lines
  Assert.Equal(ValueSome Ignore.RuleCountExceeded, rules.Truncation)
  let withinLimit = ruleSet 0 [ "*.o" ]
  Assert.Equal(ValueNone, withinLimit.Truncation)

[<Fact>]
let ``tsx と fsi は文法が異なるため別の言語として分類する`` () =
  let language path = Classify.language(ok(tryCreate path))
  Assert.Equal(TypeScript, language "src/app.ts")
  Assert.Equal(TypeScript, language "src/app.mts")
  // JSX を含むため TypeScript とは別の文法で解析する。
  Assert.Equal(Tsx, language "src/App.tsx")
  Assert.Equal(FSharp, language "src/Program.fs")
  Assert.Equal(FSharp, language "build.fsx")
  // シグネチャ ファイルは本体と区別して記録する。
  Assert.Equal(FSharpSignature, language "src/Program.fsi")

// --- 無視ファイル読取の上限とキャンセル（backlogs/completed/004） ---

let private readRules (content: string) =
  use stream = new IO.MemoryStream(Text.Encoding.UTF8.GetBytes content)
  Ignore.read 0 stream Threading.CancellationToken.None

[<Fact>]
let ``無視ファイルは改行の種別によらず同じ規則になる`` () =
  let expected = decide [| readRules "*.o\nbuild/\n" |] "main.o" false
  Assert.Equal(Ignore.Ignored, expected)
  Assert.Equal(Ignore.Ignored, decide [| readRules "*.o\r\nbuild/\r\n" |] "main.o" false)
  Assert.Equal(Ignore.Ignored, decide [| readRules "*.o\rbuild/\r" |] "main.o" false)
  // 改行で終わらない最終行も規則として扱う。
  Assert.Equal(Ignore.Ignored, decide [| readRules "*.o" |] "main.o" false)

[<Fact>]
let ``UTF-8 BOM 付きの無視ファイルも先頭の規則を適用する`` () =
  for rules in [ readRules "\uFEFF*.o\n"; ruleSet 0 [ "\uFEFF*.o" ] ] do
    Assert.Equal(Ignore.Ignored, decide [| rules |] "main.o" false)

[<Fact>]
let ``無視ファイルの読取はバイト数の上限で打ち切る`` () =
  // 規則数の上限だけでは、コメントばかりの巨大ファイルを止められない。
  let padding = String.replicate 64 "#"
  let line = padding + "\n"
  let repeats = (Ignore.MaxFileBytes / line.Length) + 64
  let rules = readRules(String.replicate repeats line)
  Assert.Equal(ValueSome Ignore.ByteLimitExceeded, rules.Truncation)

[<Fact>]
let ``無視ファイルがバイト数上限ちょうどなら打ち切りとしない`` () =
  let content = String.replicate (Ignore.MaxFileBytes / 2) "#\n"
  Assert.Equal(ValueNone, (readRules content).Truncation)
  Assert.Equal(ValueSome Ignore.ByteLimitExceeded, (readRules(content + "#")).Truncation)

[<Fact>]
let ``規則数上限の後に空行やコメントがあっても打ち切りとしない`` () =
  let lines =
    [ yield! List.init Ignore.MaxRulesPerFile (fun index -> $"file{index}.o")
      yield ""
      yield "# comment" ]

  for rules in [ ruleSet 0 lines; readRules(String.concat "\n" lines) ] do
    Assert.Equal(ValueNone, rules.Truncation)
    Assert.Equal(Ignore.Ignored, decide [| rules |] $"file{Ignore.MaxRulesPerFile - 1}.o" false)

[<Fact>]
let ``無視ファイルの読取は一行の長さの上限で打ち切る`` () =
  let rules = readRules(String.replicate (Ignore.MaxLineBytes + 16) "a" + "\n*.o\n")
  Assert.Equal(ValueSome Ignore.LineLengthExceeded, rules.Truncation)
  // 打ち切った以降の規則は適用しない。読めた範囲だけを使う。
  Assert.Equal(Ignore.NotMatched, decide [| rules |] "main.o" false)

[<Fact>]
let ``無視ファイルの読取は規則数の上限で打ち切る`` () =
  let content =
    String.concat "" [ for index in 0 .. Ignore.MaxRulesPerFile + 16 -> $"pattern{index}/*.o\n" ]

  Assert.Equal(ValueSome Ignore.RuleCountExceeded, (readRules content).Truncation)

[<Fact>]
let ``取り消し済みのトークンでは無視ファイルを読まない`` () =
  use stream = new IO.MemoryStream(Text.Encoding.UTF8.GetBytes "*.o\n")
  use cancellation = new Threading.CancellationTokenSource()
  cancellation.Cancel()

  Assert.ThrowsAny<OperationCanceledException>(fun () -> Ignore.read 0 stream cancellation.Token |> ignore)
  |> ignore

[<Fact>]
let ``BOM だけのファイルは本文が空なので行数も 0 になる`` () =
  let path = IO.Path.GetTempFileName()

  try
    IO.File.WriteAllBytes(path, [| 0xEFuy; 0xBBuy; 0xBFuy |])

    for retained in [ 0L; 1024L ] do
      use reader = new Content.ContentReader(1024L, retained)
      let summary = ok(reader.Read(path, 3L, Threading.CancellationToken.None))
      Assert.Equal(0, summary.LineCount)
      Assert.Equal(0, summary.DecodedLength)
  finally
    IO.File.Delete path

[<Fact>]
let ``判定不能のファイルも診断フラグ付きの置換本文を抽出へ渡す`` () =
  let path = IO.Path.GetTempFileName()

  try
    let bytes = [| byte 'a'; 0xFFuy; 0x0Auy; byte 'b' |]
    IO.File.WriteAllBytes(path, bytes)
    use reader = new Content.ContentReader(1024L, 1024L)

    let summary =
      ok(reader.Read(path, int64 bytes.Length, Threading.CancellationToken.None))

    Assert.True summary.Decoded
    Assert.True(hasFlag NodeFlags.UndeterminedEncoding summary.Flags)
    Assert.Equal(Encodings.Undetermined, summary.Encoding)
    Assert.Equal(2, summary.LineCount)
    Assert.Equal("a\uFFFD\nb", Text.Encoding.UTF8.GetString(reader.Decoded, 0, summary.DecodedLength))
  finally
    IO.File.Delete path

[<Fact>]
let ``取り消し済みなら空ファイルやサイズ超過も成功として返さない`` () =
  use reader = new Content.ContentReader 10L
  use cancellation = new Threading.CancellationTokenSource()
  cancellation.Cancel()

  for length in [ 0L; 11L ] do
    Assert.ThrowsAny<OperationCanceledException>(fun () -> reader.Read("unused", length, cancellation.Token) |> ignore)
    |> ignore

[<Fact>]
let ``末尾の二重アスタリスクで無視したディレクトリ内を再包含できる`` () =
  task {
    let workspace = IO.Directory.CreateTempSubdirectory "srcnet-discovery-"

    try
      let build =
        IO.Directory.CreateDirectory(IO.Path.Combine(workspace.FullName, "build"))

      IO.File.WriteAllText(IO.Path.Combine(workspace.FullName, ".gitignore"), "build/**\n!build/keep.c\n")
      IO.File.WriteAllText(IO.Path.Combine(build.FullName, "keep.c"), "int keep;\n")
      IO.File.WriteAllText(IO.Path.Combine(build.FullName, "drop.c"), "int drop;\n")
      let diagnostics = DiagnosticSink()

      let options =
        { Walk.WalkOptions.defaults with
            Jobs = 1
            Extraction =
              { Extractor.ExtractionOptions.defaults with
                  Tier = Model.Structure } }

      let! result = Walk.run workspace.FullName options diagnostics Threading.CancellationToken.None
      Assert.True result.Complete
      Assert.Equal<string[]>([| ".gitignore"; "build/keep.c" |], result.Files |> Array.map(fun file -> value file.Path))
    finally
      workspace.Delete true
  }

[<Fact>]
let ``抽出しない理由を未対応の符号化と混同しない`` () =
  task {
    let workspace = IO.Directory.CreateTempSubdirectory "srcnet-discovery-"

    try
      let source = "int value;\n"
      IO.File.WriteAllText(IO.Path.Combine(workspace.FullName, "a.c"), source)

      for tier, limit, skipped in
        [ Model.Structure, 1024L, ValueNone
          Model.LineOriented, 4L, ValueSome(Model.TooLargeToExtract(int64 source.Length)) ] do
        let diagnostics = DiagnosticSink()

        let options =
          { Walk.WalkOptions.defaults with
              Jobs = 1
              Extraction =
                { Extractor.ExtractionOptions.defaults with
                    Tier = tier
                    MaxExtractionBytes = limit } }

        let! result = Walk.run workspace.FullName options diagnostics Threading.CancellationToken.None
        let file = Assert.Single result.Files
        Assert.Equal(skipped, file.Extraction.Skipped)
    finally
      workspace.Delete true
  }

[<Fact>]
let ``同じ実体への別経路のリンクは循環ではなく並列度にも依存しない`` () =
  task {
    let workspace = IO.Directory.CreateTempSubdirectory "srcnet-discovery-"

    try
      let real = IO.Directory.CreateDirectory(IO.Path.Combine(workspace.FullName, "real"))
      let first = IO.Directory.CreateDirectory(IO.Path.Combine(workspace.FullName, "a"))
      let second = IO.Directory.CreateDirectory(IO.Path.Combine(workspace.FullName, "b"))
      IO.File.WriteAllText(IO.Path.Combine(real.FullName, "value.c"), "int value;\n")

      let created =
        try
          IO.Directory.CreateSymbolicLink(IO.Path.Combine(first.FullName, "alias"), real.FullName)
          |> ignore

          IO.Directory.CreateSymbolicLink(IO.Path.Combine(second.FullName, "alias"), real.FullName)
          |> ignore

          true
        with
        | :? IO.IOException -> false
        | :? UnauthorizedAccessException -> false

      if created then
        for jobs in [ 1; 4 ] do
          let diagnostics = DiagnosticSink()

          let options =
            { Walk.WalkOptions.defaults with
                Jobs = jobs
                FollowSymbolicLinks = true
                Extraction =
                  { Extractor.ExtractionOptions.defaults with
                      Tier = Model.Structure } }

          let! result = Walk.run workspace.FullName options diagnostics Threading.CancellationToken.None
          Assert.True result.Complete

          Assert.Equal<string[]>(
            [| "a/alias/value.c"; "b/alias/value.c"; "real/value.c" |],
            result.Files |> Array.map(fun file -> value file.Path)
          )
    finally
      workspace.Delete true
  }

[<Fact>]
let ``祖先へのリンクは再走査する前に循環として拒否する`` () =
  task {
    let workspace = IO.Directory.CreateTempSubdirectory "srcnet-discovery-"

    try
      let real = IO.Directory.CreateDirectory(IO.Path.Combine(workspace.FullName, "real"))
      IO.File.WriteAllText(IO.Path.Combine(real.FullName, "value.c"), "int value;\n")

      let created =
        try
          IO.Directory.CreateSymbolicLink(IO.Path.Combine(real.FullName, "parent"), workspace.FullName)
          |> ignore

          true
        with
        | :? IO.IOException -> false
        | :? UnauthorizedAccessException -> false

      if created then
        let diagnostics = DiagnosticSink()

        let options =
          { Walk.WalkOptions.defaults with
              Jobs = 1
              FollowSymbolicLinks = true
              Extraction =
                { Extractor.ExtractionOptions.defaults with
                    Tier = Model.Structure } }

        let! result = Walk.run workspace.FullName options diagnostics Threading.CancellationToken.None
        Assert.Equal<string[]>([| "real/value.c" |], result.Files |> Array.map(fun file -> value file.Path))

        Assert.Contains(
          diagnostics.Samples(),
          fun sample -> sample.Kind = DirectoryCycle && sample.Path = "real/parent"
        )
    finally
      workspace.Delete true
  }

[<Fact>]
let ``空のディレクトリも項目数の上限で打ち切る`` () =
  task {
    let workspace = IO.Directory.CreateTempSubdirectory "srcnet-discovery-"

    try
      for index in 0..4 do
        IO.Directory.CreateDirectory(IO.Path.Combine(workspace.FullName, $"dir{index}"))
        |> ignore

      let diagnostics = DiagnosticSink()

      let options =
        { Walk.WalkOptions.defaults with
            Jobs = 1
            MaxEntries = 2 }

      let! result = Walk.run workspace.FullName options diagnostics Threading.CancellationToken.None
      Assert.Equal(2, result.Directories.Length)
      Assert.False result.Complete
      Assert.Contains(diagnostics.Counts(), (fun (struct (kind, _)) -> kind = EntryLimitExceeded))
    finally
      workspace.Delete true
  }

[<Fact>]
let ``ファイルシステムのルート直下をルート外と誤認しない`` () =
  task {
    let rootPath = IO.Path.GetPathRoot(IO.Directory.GetCurrentDirectory())
    let diagnostics = DiagnosticSink()

    let options =
      { Walk.WalkOptions.defaults with
          Jobs = 1
          MaxDepth = 0
          MaxEntries = 0
          RespectIgnoreFiles = false
          Extraction =
            { Extractor.ExtractionOptions.defaults with
                Tier = Model.Structure } }

    let! _ = Walk.run rootPath options diagnostics Threading.CancellationToken.None
    Assert.DoesNotContain(diagnostics.Counts(), (fun (struct (kind, _)) -> kind = SymbolicLinkEscapesRoot))

    Assert.Contains(
      diagnostics.Counts(),
      fun (struct (kind, _)) -> kind = DepthLimitExceeded || kind = EntryLimitExceeded
    )
  }

[<Fact>]
let ``論理パスとして表せない名前を落とした走査は不完全とする`` () =
  task {
    if not(OperatingSystem.IsWindows()) then
      let workspace = IO.Directory.CreateTempSubdirectory "srcnet-discovery-"

      try
        IO.File.WriteAllText(IO.Path.Combine(workspace.FullName, "invalid\\name.c"), "int value;\n")
        let diagnostics = DiagnosticSink()
        let! result = Walk.run workspace.FullName Walk.WalkOptions.defaults diagnostics Threading.CancellationToken.None
        Assert.False result.Complete
        Assert.Contains(diagnostics.Counts(), (fun (struct (kind, _)) -> kind = PathRejected))
      finally
        workspace.Delete true
  }

[<Fact>]
let ``T2 のタイムアウトは T1 の結果と完全性を保ってファイル別に診断する`` () =
  task {
    if Parsing.supports Language.C then
      let workspace = IO.Directory.CreateTempSubdirectory "srcnet-discovery-"

      try
        let source = "struct Retained {};\n" + String.replicate 4096 "int repeated(void) { return 0; }\n"
        IO.File.WriteAllText(IO.Path.Combine(workspace.FullName, "slow.c"), source)
        let diagnostics = DiagnosticSink()

        let options =
          { Walk.WalkOptions.defaults with
              Jobs = 1
              Extraction =
                { Extractor.ExtractionOptions.defaults with
                    Tier = Model.Syntax
                    TimeoutMicroseconds = 1UL } }

        let! result = Walk.run workspace.FullName options diagnostics Threading.CancellationToken.None
        let file = Assert.Single result.Files
        Assert.True result.Complete
        Assert.Equal(Model.LineOriented, file.Extraction.Tier)
        Assert.Equal(ValueSome Model.ParseTimedOut, file.Extraction.Skipped)
        Assert.Contains(file.Extraction.Symbols, fun symbol -> symbol.Name = "Retained")

        Assert.Contains(
          diagnostics.Samples(),
          fun sample ->
            sample.Kind = ExtractionDegraded
            && sample.Path = "slow.c"
            && sample.Detail = Model.SkipReason.describe Model.ParseTimedOut
            && DiagnosticKind.severity sample.Kind = Severity.Info
        )
      finally
        workspace.Delete true
  }
