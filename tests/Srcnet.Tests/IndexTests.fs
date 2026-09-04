/// 走査から生成物までの通しテスト。
///
/// docs/testing.md の T-1（決定性）、T-4（CJK）、T-5（クラッシュ・フリーズ耐性）、
/// T-6（セキュリティ）に対応する。
module Srcnet.Tests.IndexTests

open System
open System.IO
open System.Threading
open Xunit
open Srcnet.Core
open Srcnet.Core.Diagnostics
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Core.Paths
open Srcnet.Discovery
open Srcnet.Storage
open Srcnet.Text

/// テストごとに使い捨てる一時ディレクトリ。
type private Workspace() =
  let path =
    Path.Combine(Path.GetTempPath(), "srcnet-tests-" + Guid.NewGuid().ToString "N")

  do Directory.CreateDirectory path |> ignore

  member _.Path = path

  member _.Directory(relative: string) =
    let full = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar))
    Directory.CreateDirectory full |> ignore
    full

  member this.Write(relative: string, content: string) =
    let full = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar))

    match Path.GetDirectoryName full with
    | null -> ()
    | parent -> Directory.CreateDirectory parent |> ignore

    File.WriteAllText(full, content, Text.UTF8Encoding false)
    ignore this

  member this.WriteBytes(relative: string, content: byte[]) =
    let full = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar))

    match Path.GetDirectoryName full with
    | null -> ()
    | parent -> Directory.CreateDirectory parent |> ignore

    File.WriteAllBytes(full, content)
    ignore this

  interface IDisposable with

    member _.Dispose() =
      if Directory.Exists path then
        try
          Directory.Delete(path, true)
        with :? IOException ->
          ()

/// 代表的な入力を含む合成コーパスを作る。
/// CJK、レガシー符号化、無視設定の階層、バイナリ、深い階層、長い行を含める。
let private buildCorpus (workspace: Workspace) =
  workspace.Write("README.md", "# sample\n")
  workspace.Write("src/main.c", "#include <stdio.h>\nint main(void) { return 0; }\n")
  workspace.Write("src/util.h", "#pragma once\n")
  workspace.Write("src/parser_test.cc", "TEST(Parser, Works) {}\n")
  workspace.Write("設計/概要.md", "# 設計\n日本語の内容\n")
  workspace.Write("설계/개요.md", "# 설계\n")
  workspace.Write("third_party/zlib/zlib.c", "int inflate(void) { return 0; }\n")
  workspace.Write(".gitignore", "build/\n*.log\n")
  workspace.Write("build/generated.c", "int generated;\n")
  workspace.Write("debug.log", "noise\n")
  workspace.Write("logs/.gitignore", "!keep.log\n")
  workspace.Write("logs/keep.log", "kept\n")
  workspace.Write("logs/drop.log", "dropped\n")
  // Shift_JIS の「日本語」。UTF-8 としては不正なバイト列。
  workspace.WriteBytes("legacy/sjis.txt", [| 0x93uy; 0xFAuy; 0x96uy; 0x7Cuy; 0x8Cuy; 0xEAuy; 0x0Auy |])
  workspace.WriteBytes("assets/blob.bin", [| 0x00uy; 0x01uy; 0x02uy; 0x00uy |])
  workspace.Write("deep/a/b/c/d/e/f/leaf.c", "int leaf;\n")
  workspace.Write("pathological/longline.c", String('x', 200_000) + "\n")
  // CRLF と CR のみの改行を混在させる。行数は改行種別によらず一致しなければならない。
  workspace.WriteBytes("newlines/crlf.txt", Text.Encoding.UTF8.GetBytes "a\r\nb\r\nc")
  workspace.WriteBytes("newlines/cr.txt", Text.Encoding.UTF8.GetBytes "a\rb\rc")
  workspace.WriteBytes("newlines/lf.txt", Text.Encoding.UTF8.GetBytes "a\nb\nc")

let private walk (workspace: Workspace) (options: Walk.WalkOptions) =
  let diagnostics = DiagnosticSink()
  let result = (Walk.run workspace.Path options diagnostics CancellationToken.None).Result
  struct (result, diagnostics)

/// 列挙値へのメンバー呼び出しは防御的コピーを生むため、ビット演算で判定する。
let private hasFlag (flag: NodeFlags) (flags: NodeFlags) = (flags &&& flag) = flag

let private paths (result: Walk.WalkResult) =
  result.Files |> Array.map (fun file -> value file.Path) |> Set.ofArray

let private fileNamed (result: Walk.WalkResult) (path: string) =
  result.Files |> Array.tryFind (fun file -> value file.Path = path)

[<Fact>]
let ``無視設定が階層的に適用される`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  let struct (result, _) = walk workspace Walk.WalkOptions.defaults
  let found = paths result

  Assert.Contains("src/main.c", found)
  Assert.Contains("logs/keep.log", found)
  Assert.DoesNotContain("build/generated.c", found)
  Assert.DoesNotContain("debug.log", found)
  Assert.DoesNotContain("logs/drop.log", found)

[<Fact>]
let ``--no-gitignore は無視設定を適用しない`` () =
  use workspace = new Workspace()
  buildCorpus workspace

  let struct (result, _) =
    walk workspace { Walk.WalkOptions.defaults with RespectIgnoreFiles = false }

  let found = paths result
  Assert.Contains("build/generated.c", found)
  Assert.Contains("debug.log", found)

[<Fact>]
let ``CJK のパスと内容を欠落なく扱う`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  let struct (result, _) = walk workspace Walk.WalkOptions.defaults
  let found = paths result

  Assert.Contains("設計/概要.md", found)
  Assert.Contains("설계/개요.md", found)

  match fileNamed result "設計/概要.md" with
  | Some file ->
    Assert.Equal(Markdown, file.Language)
    Assert.Equal(Encodings.Utf8, file.Encoding)
    Assert.Equal(2, file.LineCount)
  | None -> failwith "CJK パスのファイルが見つかりません"

[<Fact>]
let ``レガシー符号化のファイルを UTF-8 と誤判定しない`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  let struct (result, _) = walk workspace Walk.WalkOptions.defaults

  match fileNamed result "legacy/sjis.txt" with
  | Some file ->
    Assert.NotEqual(Encodings.Utf8, file.Encoding)
    Assert.NotEqual(Encodings.Undetermined, file.Encoding)
  | None -> failwith "レガシー符号化のファイルが見つかりません"

[<Fact>]
let ``バイナリを検出してフラグを立てる`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  let struct (result, _) = walk workspace Walk.WalkOptions.defaults

  match fileNamed result "assets/blob.bin" with
  | Some file ->
    Assert.Equal(Encodings.Binary, file.Encoding)
    Assert.True(hasFlag NodeFlags.Binary file.Flags)
  | None -> failwith "バイナリ ファイルが見つかりません"

[<Fact>]
let ``行数は改行種別によらず一致する`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  let struct (result, _) = walk workspace Walk.WalkOptions.defaults

  let lineCount path =
    match fileNamed result path with
    | Some file -> file.LineCount
    | None -> failwith $"{path} が見つかりません"

  Assert.Equal(3, lineCount "newlines/lf.txt")
  Assert.Equal(3, lineCount "newlines/crlf.txt")
  Assert.Equal(3, lineCount "newlines/cr.txt")

[<Fact>]
let ``大きすぎるファイルは診断付きでスキップし、走査は継続する`` () =
  use workspace = new Workspace()
  buildCorpus workspace

  let struct (result, diagnostics) =
    walk workspace { Walk.WalkOptions.defaults with MaxFileSizeBytes = 1024L }

  Assert.Contains("pathological/longline.c", paths result)

  match fileNamed result "pathological/longline.c" with
  | Some file -> Assert.True(hasFlag NodeFlags.Skipped file.Flags)
  | None -> failwith "巨大ファイルのノードがありません"

  Assert.Contains(diagnostics.Counts(), fun (struct (kind, _)) -> kind = FileTooLarge)

[<Fact>]
let ``深さ上限を超えても失敗せず診断する`` () =
  use workspace = new Workspace()
  buildCorpus workspace

  let struct (result, diagnostics) =
    walk workspace { Walk.WalkOptions.defaults with MaxDepth = 2 }

  Assert.Contains("src/main.c", paths result)
  Assert.DoesNotContain("deep/a/b/c/d/e/f/leaf.c", paths result)
  Assert.Contains(diagnostics.Counts(), fun (struct (kind, _)) -> kind = DepthLimitExceeded)

[<Fact>]
let ``シンボリック リンクは既定で追跡しない`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  let target = workspace.Directory "src"
  let link = Path.Combine(workspace.Path, "link-to-src")

  let created =
    try
      Directory.CreateSymbolicLink(link, target) |> ignore
      true
    with
    | :? IOException -> false
    | :? UnauthorizedAccessException -> false

  if created then
    let struct (result, diagnostics) = walk workspace Walk.WalkOptions.defaults
    Assert.DoesNotContain("link-to-src/main.c", paths result)
    Assert.Contains(diagnostics.Counts(), fun (struct (kind, _)) -> kind = SymbolicLinkSkipped)

[<Fact>]
let ``ルート外を指すリンクは追跡を許可しても拒否される`` () =
  use workspace = new Workspace()
  use outside = new Workspace()
  buildCorpus workspace
  outside.Write("secret.txt", "秘密\n")
  let link = Path.Combine(workspace.Path, "escape")

  let created =
    try
      Directory.CreateSymbolicLink(link, outside.Path) |> ignore
      true
    with
    | :? IOException -> false
    | :? UnauthorizedAccessException -> false

  if created then
    let struct (result, diagnostics) =
      walk workspace { Walk.WalkOptions.defaults with FollowSymbolicLinks = true }

    Assert.DoesNotContain("escape/secret.txt", paths result)
    Assert.Contains(diagnostics.Counts(), fun (struct (kind, _)) -> kind = SymbolicLinkEscapesRoot)

[<Fact>]
let ``ルート内のリンクを経由してもルート外へ脱出できない`` () =
  // `ResolveLinkTarget` は最終要素の鎖しか辿らず、経路の途中の要素は解決しない。
  // 経路をルート内のリンクで中継すると、文字列としてはルート内に見えてしまう。
  use workspace = new Workspace()
  use outside = new Workspace()
  buildCorpus workspace
  outside.Directory "inner" |> ignore
  outside.Write("inner/leaked.txt", "秘密\n")

  let hop = Path.Combine(workspace.Path, "hop")
  let door = Path.Combine(workspace.Path, "door")

  let created =
    try
      Directory.CreateSymbolicLink(hop, outside.Path) |> ignore
      Directory.CreateSymbolicLink(door, Path.Combine(hop, "inner")) |> ignore
      true
    with
    | :? IOException -> false
    | :? UnauthorizedAccessException -> false

  if created then
    let struct (result, diagnostics) =
      walk workspace { Walk.WalkOptions.defaults with FollowSymbolicLinks = true }

    let found = paths result
    Assert.DoesNotContain("door/leaked.txt", found)
    Assert.DoesNotContain("hop/inner/leaked.txt", found)
    Assert.Contains(diagnostics.Counts(), fun (struct (kind, _)) -> kind = SymbolicLinkEscapesRoot)

[<Fact>]
let ``ルート内で完結するリンクは追跡できる`` () =
  // 脱出を防ぐ実装が、正当なリンクまで拒否していないことを確かめる。
  use workspace = new Workspace()
  workspace.Write("real/a.c", "int a;\n")
  let alias = Path.Combine(workspace.Path, "alias")

  let created =
    try
      Directory.CreateSymbolicLink(alias, Path.Combine(workspace.Path, "real")) |> ignore
      true
    with
    | :? IOException -> false
    | :? UnauthorizedAccessException -> false

  if created then
    let struct (result, _) =
      walk workspace { Walk.WalkOptions.defaults with FollowSymbolicLinks = true }

    let found = paths result
    Assert.Contains("real/a.c", found)
    Assert.Contains("alias/a.c", found)

[<Fact>]
let ``サイズ 0 のファイルは開かずに空として扱う`` () =
  // FIFO やキャラクタ デバイスは通常ファイルと managed API で区別できず、
  // 開くと無期限に blocking する。サイズ 0 を一律に開かないことで停止を防ぐ。
  use workspace = new Workspace()
  workspace.Write("empty.c", "")
  workspace.Write("filled.c", "int a;\n")
  let struct (result, _) = walk workspace Walk.WalkOptions.defaults

  match fileNamed result "empty.c" with
  | Some file ->
    Assert.Equal(0L, file.SizeBytes)
    Assert.Equal(0, file.LineCount)
    Assert.Equal<byte[]>(Blake3.hash ReadOnlySpan.Empty, file.Hash)
  | None -> failwith "空ファイルのノードがありません"

[<Fact>]
let ``読み取り中に上限を超えたファイルは診断して打ち切る`` () =
  use workspace = new Workspace()
  workspace.Write("big.c", String('x', 200_000))

  let struct (result, diagnostics) =
    walk workspace { Walk.WalkOptions.defaults with MaxFileSizeBytes = 1024L }

  Assert.Contains(diagnostics.Counts(), fun (struct (kind, _)) -> kind = FileTooLarge)

  match fileNamed result "big.c" with
  | Some file -> Assert.True(hasFlag NodeFlags.Skipped file.Flags)
  | None -> failwith "上限超過ファイルのノードがありません"

[<Fact>]
let ``並列度を変えても走査結果は同じ`` () =
  use workspace = new Workspace()
  buildCorpus workspace

  let struct (single, _) = walk workspace { Walk.WalkOptions.defaults with Jobs = 1 }
  let struct (many, _) = walk workspace { Walk.WalkOptions.defaults with Jobs = 16 }

  Assert.Equal<string[]>(
    single.Files |> Array.map (fun file -> value file.Path),
    many.Files |> Array.map (fun file -> value file.Path)
  )

  Assert.Equal<string[]>(single.Directories |> Array.map value, many.Directories |> Array.map value)

[<Fact>]
let ``取り消しは走査を停止させ、ハングしない`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  use cancellation = new CancellationTokenSource()
  // 取り消し済みのトークンで開始する。時間に依存せず決定的に検証できる。
  cancellation.Cancel()
  let diagnostics = DiagnosticSink()

  let run () =
    (Walk.run workspace.Path Walk.WalkOptions.defaults diagnostics cancellation.Token)
      .GetAwaiter()
      .GetResult()
    |> ignore

  Assert.ThrowsAny<OperationCanceledException> run |> ignore

// --- 生成物 ---
let private indexInto (workspace: Workspace) (output: string) (jobs: int) =
  let diagnostics = DiagnosticSink()

  let options =
    { Walk.WalkOptions.defaults with
        Jobs = jobs
        ExcludedPaths = [| ".srcnet" |] }

  let result = (Walk.run workspace.Path options diagnostics CancellationToken.None).Result

  let repository =
    match RepositoryId.tryCreate "sample" with
    | Ok value -> value
    | Error error -> failwith (RepositoryId.describe error)

  let input: Writer.IndexInput =
    { Repository = repository
      Directories = result.Directories
      Files =
        result.Files
        |> Array.map (fun file ->
          { Writer.Path = file.Path
            Writer.SizeBytes = file.SizeBytes
            Writer.Language = file.Language
            Writer.EncodingCode = Encodings.toCode file.Encoding
            Writer.Flags = file.Flags
            Writer.LineCount = file.LineCount
            Writer.ContentHash = file.Hash }) }

  let written = Writer.write output input diagnostics CancellationToken.None

  let manifest: Manifest.Manifest =
    { ManifestVersion = Manifest.ManifestVersion
      FormatVersion = Format.FormatVersion
      ToolVersion = Manifest.toolVersion
      RepositoryId = RepositoryId.value repository
      Complete = result.Complete
      Options =
        { FollowSymbolicLinks = options.FollowSymbolicLinks
          RespectIgnoreFiles = options.RespectIgnoreFiles
          MaxDepth = options.MaxDepth
          MaxFileSizeBytes = options.MaxFileSizeBytes }
      Counts =
        { Nodes = written.NodeCount
          Edges = written.EdgeCount
          Strings = written.StringCount
          StringBytes = written.StringBytes
          Directories = result.Directories.Length
          Files = result.Files.Length }
      Segments = written.Segments
      Diagnostics = Array.empty }

  Manifest.write output manifest
  manifest

let private allFileBytes (directory: string) =
  Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
  |> Seq.map (fun path -> Path.GetRelativePath(directory, path).Replace('\\', '/'), File.ReadAllBytes path)
  |> Seq.sortBy fst
  |> Seq.toArray

[<Fact>]
let ``同じ入力から二度生成した成果物はバイト単位で一致する`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  use first = new Workspace()
  use second = new Workspace()

  indexInto workspace first.Path 1 |> ignore
  indexInto workspace second.Path 16 |> ignore

  let left = allFileBytes first.Path
  let right = allFileBytes second.Path

  Assert.Equal(left.Length, right.Length)

  for index in 0 .. left.Length - 1 do
    let leftName, leftBytes = left[index]
    let rightName, rightBytes = right[index]
    Assert.Equal(leftName, rightName)
    Assert.Equal<byte[]>(leftBytes, rightBytes)

[<Fact>]
let ``生成した成果物は検証を通る`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  use output = new Workspace()
  indexInto workspace output.Path 4 |> ignore

  match Verify.run output.Path CancellationToken.None with
  | Error error -> failwith (Manifest.ManifestError.describe error)
  | Ok report ->
    Assert.Empty(report.Issues |> Array.map Verify.Issue.describe)
    Assert.True report.IsValid
    Assert.Equal(7, report.SegmentsChecked)

[<Fact>]
let ``セグメントを改竄すると検証が失敗する`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  use output = new Workspace()
  let manifest = indexInto workspace output.Path 4

  let target =
    Path.Combine(
      output.Path,
      (manifest.Segments |> Array.find (fun segment -> segment.Name.EndsWith ".nodes")).Name.Replace('/', Path.DirectorySeparatorChar)
    )

  let bytes = File.ReadAllBytes target
  bytes[bytes.Length - 1] <- bytes[bytes.Length - 1] ^^^ 0xFFuy
  File.WriteAllBytes(target, bytes)

  match Verify.run output.Path CancellationToken.None with
  | Error error -> failwith (Manifest.ManifestError.describe error)
  | Ok report ->
    Assert.False report.IsValid
    Assert.Contains(report.Issues, fun issue -> (Verify.Issue.describe issue).Contains "チェックサム")

[<Fact>]
let ``形式版が非互換なマニフェストは拒否される`` () =
  use output = new Workspace()

  File.WriteAllText(
    Path.Combine(output.Path, Manifest.FileName),
    """{"manifestVersion":1,"formatVersion":999,"segments":[]}"""
  )

  match Manifest.read output.Path with
  | Error(Manifest.UnsupportedFormatVersion found) -> Assert.Equal(999u, found)
  | other -> failwith $"非互換な形式版を拒否できませんでした: {other}"

[<Fact>]
let ``生成物がない場合は明示的に失敗する`` () =
  use output = new Workspace()

  match Manifest.read output.Path with
  | Error(Manifest.NotFound _) -> ()
  | other -> failwith $"生成物の不在を検出できませんでした: {other}"

[<Fact>]
let ``統計は言語と符号化の内訳を返す`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  use output = new Workspace()
  let manifest = indexInto workspace output.Path 4

  match Stats.readFileStatistics output.Path manifest with
  | Error error -> failwith (Reader.OpenError.describe error)
  | Ok statistics ->
    Assert.Equal(manifest.Counts.Files, statistics.Languages |> Array.sumBy (fun entry -> entry.Files))
    Assert.Contains(statistics.Languages, fun entry -> entry.Language = C)
    Assert.Contains(statistics.Languages, fun entry -> entry.Language = Markdown)
    Assert.Contains(statistics.Encodings, fun entry -> entry.Encoding = Encodings.Binary)

[<Fact>]
let ``ノード数は リポジトリ + ディレクトリ + ファイル に一致する`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  use output = new Workspace()
  let manifest = indexInto workspace output.Path 4

  Assert.Equal(1 + manifest.Counts.Directories + manifest.Counts.Files, manifest.Counts.Nodes)
  Assert.Equal(manifest.Counts.Nodes - 1, manifest.Counts.Edges)

[<Fact>]
let ``空のリポジトリでも成果物を生成できる`` () =
  use workspace = new Workspace()
  use output = new Workspace()
  let manifest = indexInto workspace output.Path 2

  Assert.Equal(0, manifest.Counts.Files)
  Assert.Equal(1, manifest.Counts.Nodes)
  Assert.Equal(0, manifest.Counts.Edges)

  match Verify.run output.Path CancellationToken.None with
  | Error error -> failwith (Manifest.ManifestError.describe error)
  | Ok report -> Assert.True report.IsValid
