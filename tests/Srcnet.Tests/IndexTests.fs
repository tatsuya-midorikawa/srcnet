/// 走査から生成物までの通しテスト。
///
/// docs/testing.md の T-1（決定性）、T-4（CJK）、T-5（クラッシュ・フリーズ耐性）、
/// T-6（セキュリティ）に対応する。
module Srcnet.Tests.IndexTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open Xunit
open Srcnet.Core
open Srcnet.Core.Diagnostics
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Core.Paths
open Srcnet.Discovery
open Srcnet.Extraction
open Srcnet.Storage
open Srcnet.Text

/// 抽出結果を書き出し用の入力へ写す。製品側の `Commands` と同じ対応を使う。
let private toSymbolInput (symbol: Model.ExtractedSymbol) : Writer.SymbolInput =
  { Kind = symbol.Kind
    Name = symbol.Name
    QualifiedName = symbol.QualifiedName
    Parent = symbol.Parent
    Ordinal = symbol.Ordinal
    StartLine = symbol.StartLine
    EndLine = symbol.EndLine
    StartByte = symbol.StartByte
    EndByte = symbol.EndByte
    Flags = symbol.Flags }

let private toReferenceInput (reference: Model.ExtractedReference) : Writer.ReferenceInput =
  { Source = reference.Source
    Kind = reference.Kind
    Target = reference.Target
    Qualifier = reference.Qualifier
    Language = reference.Language
    StartByte = reference.StartByte
    EndByte = reference.EndByte
    Line = reference.Line
    Stage = reference.Stage
    Confidence = reference.Confidence }

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
    Assert.Equal<byte[]>(Hashing.hash ReadOnlySpan.Empty, file.Hash)
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
          ({ Path = file.Path
             SizeBytes = file.SizeBytes
             Language = file.Language
             EncodingCode = Encodings.toCode file.Encoding
             Flags = file.Flags
             LineCount = file.LineCount
             ContentHash = file.Hash
             Symbols = file.Extraction.Symbols |> Array.map toSymbolInput
             References = file.Extraction.References |> Array.map toReferenceInput }
          : Writer.FileInput)) }

  let written =
    Writer.write (Manifest.stagedSegmentsPath output) input diagnostics CancellationToken.None

  let generation = Manifest.generationOf written.Segments

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
          MaxFileSizeBytes = options.MaxFileSizeBytes
          Tier = int (Model.Tier.toCode result.AppliedTier)
          ParserAvailable = result.ParserAvailable
          Grammars = Array.empty }
      Counts =
        { Nodes = written.NodeCount
          Edges = written.EdgeCount
          Strings = written.StringCount
          StringBytes = written.StringBytes
          Directories = result.Directories.Length
          Files = result.Files.Length
          Symbols = written.SymbolCount
          ReferenceCandidates = written.ReferenceCount
          NodeKinds = written.NodeKinds
          EdgeKinds = written.EdgeKinds }
      Segments = Manifest.qualify generation written.Segments
      Diagnostics = Array.empty }

  match Manifest.publish output generation manifest with
  | Error error -> failwith (Artifact.PathError.describe error)
  | Ok() -> manifest

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
  let manifest = indexInto workspace output.Path 4

  match Verify.run output.Path CancellationToken.None with
  | Error error -> failwith (Manifest.ManifestError.describe error)
  | Ok report ->
    Assert.Empty(report.Issues |> Array.map Verify.Issue.describe)
    Assert.True report.IsValid

    // 文字列・オフセット・ファイル・ノード・参照候補・ID 表に加えて、
    // エッジ種別ごとの前方・後方 CSR を書き出す。
    let expectedSegments = 6 + 2 * manifest.Counts.EdgeKinds.Length
    Assert.Equal(expectedSegments, report.SegmentsChecked)

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
    $$"""{"manifestVersion":{{Manifest.ManifestVersion}},"formatVersion":999,"segments":[]}"""
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
let ``ノード数は 構造ノードとシンボルの合計以上になる`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  use output = new Workspace()
  let manifest = indexInto workspace output.Path 4

  // 構成シンボルはファイルに属さないため、ノード数は構造ノードとシンボルの合計を上回り得る。
  let structural =
    1 + manifest.Counts.Directories + manifest.Counts.Files + manifest.Counts.Symbols

  Assert.True(manifest.Counts.Nodes >= structural)

  // `CONTAINS` はルート以外のすべてのノードへ 1 本ずつ張る。
  let contains =
    manifest.Counts.EdgeKinds
    |> Array.tryFind (fun entry -> entry.Kind = EdgeKind.name Contains)

  match contains with
  | Some entry -> Assert.Equal(manifest.Counts.Nodes - 1, entry.Count)
  | None -> failwith "CONTAINS の件数が記録されていません"

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

// --- 無視ファイルの信頼境界と完全性（backlogs/completed/003, 004, 005） ---

let private trySymbolicLink (link: string) (target: string) =
  try
    File.CreateSymbolicLink(link, target) |> ignore
    true
  with
  | :? IOException -> false
  | :? UnauthorizedAccessException -> false
  | :? PlatformNotSupportedException -> false

[<Fact>]
let ``ルート外を指す無視ファイルの規則は適用しない`` () =
  // ignore ファイルを通常エントリより先に開くと、リンクを追跡しない設定でも
  // 解析ルート外のファイルを読み、その内容で索引対象を操作されてしまう。
  use workspace = new Workspace()
  use outside = new Workspace()
  outside.Write("rules.txt", "secret.txt\n")
  workspace.Write("secret.txt", "秘密\n")
  workspace.Write("a.c", "int a;\n")

  if trySymbolicLink (Path.Combine(workspace.Path, ".gitignore")) (Path.Combine(outside.Path, "rules.txt")) then
    let struct (result, diagnostics) = walk workspace Walk.WalkOptions.defaults

    // 外部の規則は適用されない。
    Assert.Contains("secret.txt", paths result)
    Assert.Contains(diagnostics.Counts(), fun (struct (kind, _)) -> kind = IgnoreFileUnreadable)
    // 適用できなかった規則がある以上、索引対象を確定できない。
    Assert.False result.Complete

[<Fact>]
let ``規則を読み切れなかった無視ファイルは走査を不完全にする`` () =
  // 上限を超えた規則は適用されない。除外すべきものを取り込んだかを
  // 成果物から判別できないため、`complete` を真にしてはならない。
  use workspace = new Workspace()

  let rules =
    String.concat "" [ for index in 0 .. Ignore.MaxRulesPerFile -> $"nomatch{index}/\n" ]

  workspace.Write(".gitignore", rules + "secret.txt\n")
  workspace.Write("secret.txt", "秘密\n")
  workspace.Write("a.c", "int a;\n")

  let struct (result, diagnostics) = walk workspace Walk.WalkOptions.defaults
  Assert.Contains(diagnostics.Counts(), fun (struct (kind, _)) -> kind = IgnoreFileUnreadable)
  Assert.False result.Complete

[<Fact>]
let ``読める無視ファイルだけなら走査は完全になる`` () =
  // 不完全と判定する条件が広すぎて、正常な走査まで不完全にしていないことを確かめる。
  use workspace = new Workspace()
  workspace.Write(".gitignore", "*.log\n")
  workspace.Write("a.c", "int a;\n")
  workspace.Write("debug.log", "noise\n")

  let struct (result, _) = walk workspace Walk.WalkOptions.defaults
  Assert.True result.Complete
  Assert.DoesNotContain("debug.log", paths result)

[<Fact>]
let ``空の無視ファイルは開かずに規則なしとして扱う`` () =
  // FIFO やキャラクタ デバイスは `stat` 上のサイズが 0 で通常ファイルと区別できず、
  // 開くと無期限に blocking する。サイズ 0 を開かないことで停止を構造的に防ぐ。
  use workspace = new Workspace()
  workspace.Write(".gitignore", "")
  workspace.Write("a.c", "int a;\n")

  let struct (result, _) = walk workspace Walk.WalkOptions.defaults
  Assert.True result.Complete
  Assert.Contains("a.c", paths result)

[<Fact>]
let ``曖昧な符号化は診断として記録し、候補を保持する`` () =
  // 0x81 0x81 は複数のレガシー符号化の文法に適合する。ADR-5 に従い曖昧さを隠さない。
  use workspace = new Workspace()
  workspace.WriteBytes("ambiguous.txt", [| 0x81uy; 0x81uy; 0x0Auy |])

  let struct (result, diagnostics) = walk workspace Walk.WalkOptions.defaults
  Assert.Contains(diagnostics.Counts(), fun (struct (kind, _)) -> kind = AmbiguousEncoding)

  match fileNamed result "ambiguous.txt" with
  | Some file ->
    Assert.True(file.EncodingCandidates.Length > 1)
    Assert.True(hasFlag NodeFlags.AmbiguousEncoding file.Flags)
  | None -> failwith "曖昧な符号化のファイルが見つかりません"

// --- 世代単位の公開（backlogs/completed/002） ---

let private generationsIn (output: string) =
  let root = Path.Combine(output, Artifact.SegmentDirectory)

  if Directory.Exists root then
    Directory.EnumerateDirectories root |> Seq.map Path.GetFileName |> Seq.toArray
  else Array.empty

[<Fact>]
let ``セグメントは世代ごとに固有のパスへ置かれる`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  use output = new Workspace()
  let manifest = indexInto workspace output.Path 4

  Assert.All(
    manifest.Segments,
    fun segment -> Assert.Equal(Ok(), Artifact.validateSegmentName segment.Name)
  )

  Assert.Single(generationsIn output.Path) |> ignore

[<Fact>]
let ``同じ入力の再索引は同じ世代を再利用する`` () =
  // 世代識別子は内容から導く。時刻や連番を使うと、同じ入力から二度生成した
  // 成果物がバイト単位で一致しなくなる。
  use workspace = new Workspace()
  buildCorpus workspace
  use output = new Workspace()
  let first = indexInto workspace output.Path 1
  let second = indexInto workspace output.Path 8

  Assert.Equal<string[]>(
    first.Segments |> Array.map (fun segment -> segment.Name),
    second.Segments |> Array.map (fun segment -> segment.Name)
  )

  Assert.Single(generationsIn output.Path) |> ignore

[<Fact>]
let ``内容が変わると新しい世代へ切り替わり、旧世代は 1 つだけ残る`` () =
  // 旧世代を切替の直後に消すと、旧マニフェストを読み終えた読み手が参照先を失う。
  // 1 世代ぶんの猶予を残しつつ、際限なく溜めない。
  use workspace = new Workspace()
  workspace.Write("a.c", "int a;\n")
  use output = new Workspace()
  let first = indexInto workspace output.Path 1

  workspace.Write("b.c", "int b;\n")
  let second = indexInto workspace output.Path 1

  Assert.NotEqual<string>(first.Segments[0].Name, second.Segments[0].Name)
  Assert.Equal(2, (generationsIn output.Path).Length)

  workspace.Write("c.c", "int c;\n")
  indexInto workspace output.Path 1 |> ignore
  Assert.Equal(2, (generationsIn output.Path).Length)

[<Fact>]
let ``公開後のマニフェストとセグメントは常に同じ世代を指す`` () =
  use workspace = new Workspace()
  buildCorpus workspace
  use output = new Workspace()
  indexInto workspace output.Path 4 |> ignore

  match Manifest.read output.Path with
  | Error error -> failwith (Manifest.ManifestError.describe error)
  | Ok manifest ->
    let generations =
      manifest.Segments |> Array.map (fun segment -> segment.Name.Split('/')[1]) |> Array.distinct

    Assert.Single generations |> ignore

    for segment in manifest.Segments do
      match Artifact.tryResolveSegment output.Path segment.Name with
      | Error error -> failwith (Artifact.PathError.describe error)
      | Ok path ->
        Assert.True(File.Exists path, $"{segment.Name} がありません")
        Assert.Equal(segment.ByteLength, FileInfo(path).Length)

// --- マニフェストの検証（backlogs/completed/006, 007） ---

let private tamperManifest (output: string) (edit: string -> string) =
  let path = Path.Combine(output, Manifest.FileName)
  File.WriteAllText(path, edit (File.ReadAllText path))

let private indexedWorkspace (workspace: Workspace) (output: Workspace) =
  workspace.Write("a.c", "int a;\n")
  indexInto workspace output.Path 1

[<Fact>]
let ``成果物の外を指すセグメント名は形式エラーとして拒否される`` () =
  use workspace = new Workspace()
  use output = new Workspace()
  let manifest = indexedWorkspace workspace output
  let original = manifest.Segments[0].Name

  for escape in [ "../outside.files"; "/etc/passwd"; "segments/../../escape.files" ] do
    tamperManifest output.Path (fun text -> text.Replace(original, escape))

    match Manifest.read output.Path with
    | Error(Manifest.Malformed _) -> ()
    | other -> failwith $"成果物外を指す名前を拒否できませんでした ({escape}): {other}"

    tamperManifest output.Path (fun text -> text.Replace(escape, original))

[<Fact>]
let ``型や範囲が不正なマニフェストは内部エラーにならない`` () =
  // 破損した成果物は外部入力である。未処理例外ではなくドメイン エラーで終わらせる。
  use workspace = new Workspace()
  use output = new Workspace()
  indexedWorkspace workspace output |> ignore
  let original = File.ReadAllText(Path.Combine(output.Path, Manifest.FileName))

  let replaceSegmentsWithObject (text: string) =
    Regex.Replace(text, @"""segments"": \[[\s\S]*?\n  \]", @"""segments"": {}")

  let cases =
    [ "segments が配列でない", replaceSegmentsWithObject
      "件数が負", (fun (text: string) -> text.Replace(@"""files"": 1", @"""files"": -1"))
      "件数が Int32 を超える", (fun text -> text.Replace(@"""files"": 1", @"""files"": 2147483648"))
      "件数が文字列", (fun text -> text.Replace(@"""files"": 1", @"""files"": ""1"""))
      "complete が文字列", (fun text -> text.Replace(@"""complete"": true", @"""complete"": ""yes"""))
      "byteLength が負", (fun text -> Regex.Replace(text, @"""byteLength"": \d+", @"""byteLength"": -1"))
      "チェックサムが 16 進でない",
      (fun text -> Regex.Replace(text, @"""sha256"": ""[0-9a-f]+""", @"""sha256"": ""zz""")) ]

  for name, edit in cases do
    File.WriteAllText(Path.Combine(output.Path, Manifest.FileName), edit original)

    match Manifest.read output.Path with
    | Error(Manifest.Malformed _) -> ()
    | other -> failwith $"{name}: 形式エラーとして拒否できませんでした: {other}"

  File.WriteAllText(Path.Combine(output.Path, Manifest.FileName), original)
  Assert.True(Manifest.read output.Path |> Result.isOk)

// --- 書き出し中の取り消し（backlogs/completed/012） ---

[<Fact>]
let ``書き出し中の取り消しは成果物を残さない`` () =
  use workspace = new Workspace()

  for index in 0..2000 do
    workspace.Write($"src/f{index:D4}.c", $"int f{index};\n")

  use output = new Workspace()
  let diagnostics = DiagnosticSink()
  let result = (Walk.run workspace.Path Walk.WalkOptions.defaults diagnostics CancellationToken.None).Result

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
          ({ Path = file.Path
             SizeBytes = file.SizeBytes
             Language = file.Language
             EncodingCode = Encodings.toCode file.Encoding
             Flags = file.Flags
             LineCount = file.LineCount
             ContentHash = file.Hash
             Symbols = file.Extraction.Symbols |> Array.map toSymbolInput
             References = file.Extraction.References |> Array.map toReferenceInput }
          : Writer.FileInput)) }

  use cancellation = new CancellationTokenSource()
  // 取り消し済みのトークンで開始する。時間に依存せず決定的に検証できる。
  cancellation.Cancel()

  let write () =
    Writer.write (Manifest.stagedSegmentsPath output.Path) input diagnostics cancellation.Token
    |> ignore

  Assert.ThrowsAny<OperationCanceledException> write |> ignore
  Assert.False(File.Exists(Path.Combine(output.Path, Manifest.FileName)))

// --- 読み取り失敗と公開の失敗を、想定内の失敗として扱う ---

[<Fact>]
let ``読めないセグメントは内部エラーではなく問題として報告する`` () =
  // 権限や、検証中に退役した世代による読み取り失敗は破損と同じく想定内である。
  // 例外が漏れると CLI が終了コード 70 で終わり、破損と実装不具合を区別できない。
  use workspace = new Workspace()
  use output = new Workspace()
  let manifest = indexedWorkspace workspace output

  let target =
    match Artifact.tryResolveSegment output.Path manifest.Segments[0].Name with
    | Ok path -> path
    | Error error -> failwith (Artifact.PathError.describe error)

  let denied =
    try
      // 読み取りだけを外す。書き込み権限は残し、後片付けを妨げない。
      File.SetUnixFileMode(target, UnixFileMode.UserWrite)
      true
    with
    | :? PlatformNotSupportedException -> false
    | :? UnauthorizedAccessException -> false

  if denied then
    try
      match Verify.run output.Path CancellationToken.None with
      | Error error -> failwith (Manifest.ManifestError.describe error)
      | Ok report ->
        Assert.False report.IsValid
        Assert.NotEmpty report.Issues
    finally
      File.SetUnixFileMode(target, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

[<Fact>]
let ``書き出した領域が消えていたらマニフェストを公開しない`` () =
  // 実体のないセグメントを指すマニフェストを正常終了で残してはならない。
  use workspace = new Workspace()
  use output = new Workspace()
  workspace.Write("a.c", "int a;\n")

  let diagnostics = DiagnosticSink()
  let walked = (Walk.run workspace.Path Walk.WalkOptions.defaults diagnostics CancellationToken.None).Result

  let repository =
    match RepositoryId.tryCreate "sample" with
    | Ok value -> value
    | Error error -> failwith (RepositoryId.describe error)

  let input: Writer.IndexInput =
    { Repository = repository
      Directories = walked.Directories
      Files =
        walked.Files
        |> Array.map (fun file ->
          ({ Path = file.Path
             SizeBytes = file.SizeBytes
             Language = file.Language
             EncodingCode = Encodings.toCode file.Encoding
             Flags = file.Flags
             LineCount = file.LineCount
             ContentHash = file.Hash
             Symbols = file.Extraction.Symbols |> Array.map toSymbolInput
             References = file.Extraction.References |> Array.map toReferenceInput }
          : Writer.FileInput)) }

  let written =
    Writer.write (Manifest.stagedSegmentsPath output.Path) input diagnostics CancellationToken.None

  let generation = Manifest.generationOf written.Segments

  let manifest: Manifest.Manifest =
    { ManifestVersion = Manifest.ManifestVersion
      FormatVersion = Format.FormatVersion
      ToolVersion = Manifest.toolVersion
      RepositoryId = RepositoryId.value repository
      Complete = walked.Complete
      Options =
        { FollowSymbolicLinks = false
          RespectIgnoreFiles = true
          MaxDepth = Walk.WalkOptions.defaults.MaxDepth
          MaxFileSizeBytes = Walk.WalkOptions.defaults.MaxFileSizeBytes
          Tier = int (Model.Tier.toCode walked.AppliedTier)
          ParserAvailable = walked.ParserAvailable
          Grammars = Array.empty }
      Counts =
        { Nodes = written.NodeCount
          Edges = written.EdgeCount
          Strings = written.StringCount
          StringBytes = written.StringBytes
          Directories = walked.Directories.Length
          Files = walked.Files.Length
          Symbols = written.SymbolCount
          ReferenceCandidates = written.ReferenceCount
          NodeKinds = written.NodeKinds
          EdgeKinds = written.EdgeKinds }
      Segments = Manifest.qualify generation written.Segments
      Diagnostics = Array.empty }

  // 並行する別の生成が staging を消した状況を再現する。
  Manifest.discardStaging output.Path

  match Manifest.publish output.Path generation manifest with
  | Ok() -> failwith "セグメントが無いのに公開してしまいました"
  | Error _ -> Assert.False(File.Exists(Path.Combine(output.Path, Manifest.FileName)))

[<Fact>]
let ``リンクである staging へは書き出さない`` () =
  // 片付けは best-effort である。リンクを消し損ねたまま書くとリンク先へ書いてしまう。
  use output = new Workspace()
  use outside = new Workspace()
  let staging = Manifest.stagingPath output.Path

  let created =
    try
      Directory.CreateSymbolicLink(staging, outside.Path) |> ignore
      true
    with
    | :? IOException -> false
    | :? UnauthorizedAccessException -> false
    | :? PlatformNotSupportedException -> false

  if created then
    // 片付けで消せた場合は用意に成功してよい。残っている場合は必ず拒否する。
    match Manifest.prepareStaging output.Path with
    | Ok _ -> Assert.False(Artifact.isLink staging)
    | Error(Artifact.LinkRejected _) -> ()
    | Error error -> failwith $"想定と異なる拒否理由です: {Artifact.PathError.describe error}"
