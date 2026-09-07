/// 論理パスとノード ID のテスト。
/// NFC 正規化が ID に影響しないことは決定性要件 NFR-11 の中核である。
module Srcnet.Tests.CoreTests

open System
open Xunit
open Srcnet.Core
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Core.Paths

let private ok result =
  match result with
  | Ok value -> value
  | Error error -> failwith $"想定外の失敗: {error}"

let private repository = ok(RepositoryId.tryCreate "linux")

// --- 論理パス ---

[<Fact>]
let ``区切りは / に統一される`` () =
  Assert.Equal("kernel/sched/core.c", value(ok(tryCreate "kernel\\sched\\core.c")))

[<Fact>]
let ``絶対パスは拒否される`` () =
  Assert.Equal(Error NotRelative, tryCreate "/etc/passwd")
  Assert.Equal(Error NotRelative, tryCreate "\\\\server\\share")
  Assert.Equal(Error NotRelative, tryCreate "C:/Windows")

[<Fact>]
let ``ルート外への参照は拒否される`` () =
  Assert.Equal(Error ParentTraversal, tryCreate "../secrets")
  Assert.Equal(Error ParentTraversal, tryCreate "kernel/../../etc")
  Assert.Equal(Error ParentTraversal, tryCreate "./kernel")

[<Fact>]
let ``空セグメントは拒否される`` () =
  Assert.Equal(Error EmptySegment, tryCreate "kernel//core.c")

[<Fact>]
let ``制御文字を含むパスは拒否される`` () =
  Assert.Equal(Error ControlCharacter, tryCreate "kernel/\u001b[31mcore.c")
  Assert.Equal(Error ControlCharacter, tryCreate "kernel/core\u0000.c")

[<Fact>]
let ``論理パスは NFC 正規化される`` () =
  let decomposed = ok(tryCreate "ソース/\u304B\u3099ぞう.c")
  let composed = ok(tryCreate "ソース/がぞう.c")
  Assert.Equal(composed, decomposed)

[<Fact>]
let ``大文字小文字は保持され、区別される`` () =
  let upper = ok(tryCreate "README.md")
  let lower = ok(tryCreate "readme.md")
  Assert.NotEqual(upper, lower)
  Assert.Equal("README.md", value upper)

[<Fact>]
let ``親とファイル名を取り出せる`` () =
  let path = ok(tryCreate "kernel/sched/core.c")
  Assert.Equal("core.c", fileName path)
  Assert.Equal(ValueSome(ok(tryCreate "kernel/sched")), parent path)
  Assert.Equal("c", extension path)
  Assert.Equal(3, depth path)

[<Fact>]
let ``ルートには親がない`` () =
  Assert.Equal(ValueNone, parent root)
  Assert.True(isRoot root)
  Assert.Equal(0, depth root)

[<Fact>]
let ``拡張子はケース フォールドされる`` () =
  Assert.Equal("cpp", extension(ok(tryCreate "a/B.CPP")))
  Assert.Equal("", extension(ok(tryCreate "a/Makefile")))
  Assert.Equal("", extension(ok(tryCreate "a/.gitignore")))

[<Fact>]
let ``append はセグメントを検証する`` () =
  let baseDir = ok(tryCreate "kernel")
  Assert.Equal(Ok(ok(tryCreate "kernel/core.c")), append baseDir "core.c")
  Assert.Equal(Error ParentTraversal, append baseDir "..")
  Assert.Equal(Error EmptySegment, append baseDir "")

[<Fact>]
let ``ルートへの append もドライブ相対パスを拒否する`` () =
  for segment in [ "C:source.c"; "d:" ] do
    Assert.Equal(Error NotRelative, tryCreate segment)
    Assert.Equal(Error NotRelative, append root segment)

[<Fact>]
let ``名前に区切り文字を含むファイルは区別できる誤りとして拒否される`` () =
  // POSIX では `a\b` は 1 つのファイル名になり得るが、論理パスでは区切りと区別できない。
  let baseDir = ok(tryCreate "kernel")
  Assert.Equal(Error SeparatorInName, append baseDir "a/b")
  Assert.Equal(Error SeparatorInName, append baseDir "a\\b")

[<Fact>]
let ``深さ上限を超える append は拒否される`` () =
  let mutable path = root

  for index in 1..MaxDepth do
    path <- ok(append path $"d{index}")

  Assert.Equal(Error TooDeep, append path "overflow")

// --- ノード ID ---

[<Fact>]
let ``同じ材料から同じ ID が得られる`` () =
  use builder = new NodeIdBuilder()
  let path = ok(tryCreate "kernel/sched/core.c")
  let first = builder.Compute(File, repository, path, "", 0u)
  let second = builder.Compute(File, repository, path, "", 0u)
  Assert.Equal(first, second)

[<Fact>]
let ``NFC と NFD のパスから同じ ID が得られる`` () =
  use builder = new NodeIdBuilder()
  let decomposed = ok(tryCreate "ソース/\u304B\u3099ぞう.c")
  let composed = ok(tryCreate "ソース/がぞう.c")

  Assert.Equal(
    builder.Compute(File, repository, decomposed, "", 0u),
    builder.Compute(File, repository, composed, "", 0u)
  )

[<Fact>]
let ``種別が違えば ID も違う`` () =
  use builder = new NodeIdBuilder()
  let path = ok(tryCreate "kernel")
  Assert.NotEqual(builder.Compute(Directory, repository, path, "", 0u), builder.Compute(File, repository, path, "", 0u))

[<Fact>]
let ``序数が違えば ID も違う`` () =
  use builder = new NodeIdBuilder()
  let path = ok(tryCreate "a.c")

  Assert.NotEqual(
    builder.Compute(Function, repository, path, "f", 0u),
    builder.Compute(Function, repository, path, "f", 1u)
  )

[<Fact>]
let ``長さ前置きにより材料の境界が曖昧にならない`` () =
  use builder = new NodeIdBuilder()
  let repositoryA = ok(RepositoryId.tryCreate "ab")
  let repositoryB = ok(RepositoryId.tryCreate "a")
  // 長さを前置きしなければ ("ab", "c") と ("a", "bc") が同じ材料になる。
  let first = builder.Compute(Function, repositoryA, root, "c", 0u)
  let second = builder.Compute(Function, repositoryB, root, "bc", 0u)
  Assert.NotEqual(first, second)

[<Fact>]
let ``ID は 32 桁の 16 進で表され、往復する`` () =
  use builder = new NodeIdBuilder()
  let id = builder.Compute(File, repository, ok(tryCreate "a.c"), "", 0u)
  let text = id.ToString()
  Assert.Equal(32, text.Length)
  Assert.Equal(ValueSome id, NodeId.tryParse text)

[<Fact>]
let ``不正な 16 進表記は解析されない`` () =
  Assert.Equal(ValueNone, NodeId.tryParse "short")
  Assert.Equal(ValueNone, NodeId.tryParse(String('z', 32)))

[<Fact>]
let ``ID のバイト列の辞書順は数値順に一致する`` () =
  use builder = new NodeIdBuilder()

  let ids =
    [| for index in 1..64 -> builder.Compute(File, repository, ok(tryCreate $"file{index}.c"), "", 0u) |]

  let sorted = Array.sort ids

  for index in 1 .. sorted.Length - 1 do
    let left = Array.zeroCreate<byte> NodeIdLength
    let right = Array.zeroCreate<byte> NodeIdLength
    NodeId.writeTo (Span left) sorted[index - 1]
    NodeId.writeTo (Span right) sorted[index]
    Assert.True(ReadOnlySpan(left).SequenceCompareTo(ReadOnlySpan right) < 0)

// --- リポジトリ ID ---

[<Fact>]
let ``リポジトリ ID はパス区切りを含められない`` () =
  Assert.Equal(Error RepositoryId.ContainsSeparator, RepositoryId.tryCreate "a/b")
  Assert.Equal(Error RepositoryId.ContainsSeparator, RepositoryId.tryCreate "a\\b")

[<Fact>]
let ``リポジトリ ID は空にできない`` () =
  Assert.Equal(Error RepositoryId.Empty, RepositoryId.tryCreate "")

[<Fact>]
let ``リポジトリ ID は制御文字を含められない`` () =
  Assert.Equal(Error RepositoryId.ContainsControlCharacter, RepositoryId.tryCreate "a\u001bb")

[<Fact>]
let ``リポジトリ ID は NFC 正規化される`` () =
  Assert.Equal(RepositoryId.tryCreate "がぞう", RepositoryId.tryCreate "\u304B\u3099ぞう")

[<Fact>]
let ``リポジトリ ID の孤立サロゲートは例外ではなく検証エラーになる`` () =
  for surrogate in [ char 0xD800; char 0xDC00 ] do
    let invalid = String([| 'a'; surrogate; 'b' |])
    Assert.Equal(Error RepositoryId.InvalidUnicode, RepositoryId.tryCreate invalid)

  Assert.True(Result.isOk(RepositoryId.tryCreate "\U00020BB7"))

// --- 種別コード ---

[<Fact>]
let ``ノード種別コードは往復する`` () =
  let all =
    [ Repository
      Directory
      File
      Module
      Type
      Function
      Field
      Variable
      Constant
      Macro
      BuildTarget
      ConfigSymbol
      Owner
      Note
      Community ]

  for kind in all do
    Assert.Equal(ValueSome kind, NodeKind.ofCode(NodeKind.toCode kind))

[<Fact>]
let ``エッジ種別コードは一意である`` () =
  let all =
    [ Contains
      Includes
      Imports
      Declares
      Defines
      Calls
      References
      Inherits
      Implements
      Overrides
      TypedAs
      Tests
      BuiltFrom
      BuildDepends
      GuardedBy
      OwnedBy
      CoChanged
      Explains
      MemberOf ]

  let codes = all |> List.map EdgeKind.toCode
  Assert.Equal(codes.Length, List.distinct codes |> List.length)

// --- 診断 ---

[<Fact>]
let ``診断は件数を打ち切らずに集計する`` () =
  let sink = Diagnostics.DiagnosticSink 4

  for index in 1..100 do
    sink.Add(Diagnostics.FileTooLarge, $"file{index:D4}.bin", "大きすぎます")

  Assert.Equal(100, sink.Total)
  Assert.Equal(4, sink.Samples().Length)

[<Fact>]
let ``保持される診断の標本は到着順に依存しない`` () =
  let build (order: int[]) =
    let sink = Diagnostics.DiagnosticSink 3

    for index in order do
      sink.Add(Diagnostics.FileTooLarge, $"file{index:D4}.bin", "大きすぎます")

    sink.Samples() |> Array.map(fun sample -> sample.Path)

  let ascending = build [| 1..20 |]
  let descending = build [| for index in 20..-1..1 -> index |]
  Assert.Equal<string[]>(ascending, descending)

[<Fact>]
let ``致命的な診断だけがエラーとして数えられる`` () =
  let sink = Diagnostics.DiagnosticSink()
  sink.Add(Diagnostics.SymbolicLinkSkipped, "a", "")
  sink.Add(Diagnostics.NodeIdCollision, "", "")
  Assert.Equal(2, sink.Total)
  Assert.Equal(1, sink.ErrorCount)

[<Fact>]
let ``対になっていないサロゲートを含む名前は拒否される`` () =
  // NTFS はこうした名前を許すが、`String.Normalize` は例外を投げる。
  // 正規化の前に弾かなければ、不正な名前 1 つで走査全体が停止する。
  //
  // F# のソース リテラルは孤立サロゲートを U+FFFD へ置き換えてしまうため、
  // 実行時に組み立てなければこの経路を検証できない。
  let loneHigh = String([| 'a'; char 0xD800; 'b' |])
  let loneLow = String([| 'a'; char 0xDC00; 'b' |])
  Assert.Equal(Error InvalidUnicode, append root loneHigh)
  Assert.Equal(Error InvalidUnicode, append root loneLow)
  Assert.Equal(Error InvalidUnicode, tryCreate("dir/" + loneHigh))
  // 対になっているサロゲート（絵文字）は通常どおり受け付ける。
  Assert.True(Result.isOk(append root "\U0001F600.txt"))

[<Fact>]
let ``孤立サロゲートを含む名前でも正規化が例外を投げない`` () =
  // 走査は名前を検証してから正規化する。順序が逆だと走査全体が例外で止まる。
  let loneHigh = String([| 'a'; char 0xD800; 'b' |])
  Assert.Equal(Error InvalidUnicode, append root loneHigh)

[<Fact>]
let ``保持される診断は種別ごとに最小の N 件で、重複があっても順序に依存しない`` () =
  let build (order: (string * string) list) =
    let sink = Diagnostics.DiagnosticSink 2

    for path, detail in order do
      sink.Add(Diagnostics.FileTooLarge, path, detail)

    sink.Samples() |> Array.map(fun sample -> sample.Path)

  let ascending = build [ "A", "x"; "B", "x"; "B", "x"; "C", "x" ]
  let descending = build [ "B", "x"; "C", "x"; "B", "x"; "A", "x" ]
  Assert.Equal<string[]>([| "A"; "B" |], ascending)
  Assert.Equal<string[]>(ascending, descending)

[<Fact>]
let ``種別が違う診断は互いの標本枠を奪わない`` () =
  let sink = Diagnostics.DiagnosticSink 1
  sink.Add(Diagnostics.FileTooLarge, "a", "")
  sink.Add(Diagnostics.FileTooLarge, "b", "")
  sink.Add(Diagnostics.PermissionDenied, "c", "")
  Assert.Equal(3, sink.Total)
  Assert.Equal(2, sink.Samples().Length)
