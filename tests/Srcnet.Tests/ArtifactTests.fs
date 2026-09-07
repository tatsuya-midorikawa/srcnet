/// 成果物パスの信頼境界のテスト。
///
/// docs/testing.md T-6（セキュリティ）に対応する。名前からパスへの変換を
/// 検証しないと、索引で管理外を壊し、読み取りで管理外を読める。
module Srcnet.Tests.ArtifactTests

open System
open System.IO
open Xunit
open Srcnet.Storage

/// テストごとに使い捨てる一時ディレクトリ。
type private Scratch() =
  let path =
    Path.Combine(Path.GetTempPath(), "srcnet-artifact-" + Guid.NewGuid().ToString "N")

  do Directory.CreateDirectory path |> ignore

  member _.Path = path

  interface IDisposable with

    member _.Dispose() =
      if Directory.Exists path then
        try
          Directory.Delete(path, true)
        with :? IOException ->
          ()

/// リンクを作れない環境（権限のない Windows など）ではテストを飛ばす。
let private trySymbolicLink (link: string) (target: string) (isDirectory: bool) =
  try
    if isDirectory then Directory.CreateSymbolicLink(link, target) |> ignore
    else File.CreateSymbolicLink(link, target) |> ignore

    true
  with
  | :? IOException -> false
  | :? UnauthorizedAccessException -> false
  | :? PlatformNotSupportedException -> false

let private generation = String('a', Artifact.GenerationLength)

let private validName = $"segments/{generation}/0001.files"

[<Fact>]
let ``想定した形のセグメント名だけを受け入れる`` () =
  Assert.Equal(Ok(), Artifact.validateSegmentName validName)
  Assert.Equal(Ok(), Artifact.validateSegmentName $"segments/{generation}/0001.edges.CONTAINS")

[<Fact>]
let ``成果物の外を指すセグメント名を拒否する`` () =
  // 絶対パス、親参照、UNC、区切りの混在、代替データ ストリームのいずれも、
  // 結合前の段階で拒否しなければ成果物外のファイルを開ける。
  let rejected =
    [ ""
      "/etc/passwd"
      "C:\\Windows\\win.ini"
      "\\\\server\\share\\segment"
      "../outside.files"
      "segments/../../escape.files"
      $"segments/{generation}/../escape"
      $"segments/{generation}/..\\escape"
      $"segments/{generation}/0001.files:stream"
      $"segments/{generation}/"
      $"segments/{generation}/."
      $"segments/{generation}/.."
      "segments/0001.files"
      $"SEGMENTS/{generation}/0001.files"
      $"segments/{generation.Substring(1)}/0001.files"
      $"segments/{generation.ToUpperInvariant()}/0001.files" ]

  for name in rejected do
    match Artifact.validateSegmentName name with
    | Ok() -> failwith $"受け入れてはならないセグメント名を受け入れました: {name}"
    | Error _ -> ()

[<Fact>]
let ``検証済みのセグメント名は成果物ルート配下へ解決する`` () =
  use scratch = new Scratch()

  match Artifact.tryResolveSegment scratch.Path validName with
  | Error error -> failwith (Artifact.PathError.describe error)
  | Ok resolved ->
    Assert.StartsWith(scratch.Path, resolved)
    Assert.EndsWith("0001.files", resolved)

[<Fact>]
let ``経路の途中がリンクならセグメントを解決しない`` () =
  // 名前が正しくても、`segments` がリンクなら成果物の外を読める。
  use scratch = new Scratch()
  use outside = new Scratch()

  if trySymbolicLink (Path.Combine(scratch.Path, Artifact.SegmentDirectory)) outside.Path true then
    match Artifact.tryResolveSegment scratch.Path validName with
    | Ok path -> failwith $"リンク経由のセグメントを解決してしまいました: {path}"
    | Error(Artifact.LinkRejected _) -> ()
    | Error error -> failwith $"想定と異なる拒否理由です: {Artifact.PathError.describe error}"

[<Fact>]
let ``リンクである出力ルートを用意しない`` () =
  use scratch = new Scratch()
  use outside = new Scratch()
  let link = Path.Combine(scratch.Path, ".srcnet")

  if trySymbolicLink link outside.Path true then
    match Artifact.prepareRoot link with
    | Ok path -> failwith $"リンクを出力ルートとして受け入れてしまいました: {path}"
    | Error(Artifact.LinkRejected _) -> ()
    | Error error -> failwith $"想定と異なる拒否理由です: {Artifact.PathError.describe error}"

[<Fact>]
let ``存在しない出力ルートは作成して受け入れる`` () =
  use scratch = new Scratch()
  let target = Path.Combine(scratch.Path, ".srcnet")

  match Artifact.prepareRoot target with
  | Error error -> failwith (Artifact.PathError.describe error)
  | Ok path ->
    Assert.True(Directory.Exists path)
    // 同じ場所を二度用意しても失敗しない。
    Assert.True(Artifact.prepareRoot target |> Result.isOk)

[<Fact>]
let ``ファイルを出力ルートとして受け入れない`` () =
  use scratch = new Scratch()
  let target = Path.Combine(scratch.Path, ".srcnet")
  File.WriteAllText(target, "not a directory")

  match Artifact.prepareRoot target with
  | Error(Artifact.NotDirectory _) -> ()
  | other -> failwith $"ファイルを出力ルートとして扱いました: {other}"

[<Fact>]
let ``存在しないパスをリンクとみなさない`` () =
  // `FileInfo.Attributes` は存在しない項目に対して全ビットが立った値を返す。
  // これを除かないと、これから作るパスがすべてリンク扱いになる。
  use scratch = new Scratch()
  Assert.False(Artifact.isLink (Path.Combine(scratch.Path, "missing")))
  Assert.False(Artifact.isLink scratch.Path)

[<Fact>]
let ``リンクをリンクとして検出する`` () =
  use scratch = new Scratch()
  use outside = new Scratch()
  let directoryLink = Path.Combine(scratch.Path, "dir-link")
  let fileTarget = Path.Combine(outside.Path, "target.txt")
  File.WriteAllText(fileTarget, "x")
  let fileLink = Path.Combine(scratch.Path, "file-link")

  if trySymbolicLink directoryLink outside.Path true then
    Assert.True(Artifact.isLink directoryLink)

  if trySymbolicLink fileLink fileTarget false then
    Assert.True(Artifact.isLink fileLink)

[<Fact>]
let ``only one writer can own an artifact and the lease is reusable`` () =
  use scratch = new Scratch()
  let acquire () =
    match Manifest.acquireWriter scratch.Path with
    | Ok lease -> lease
    | Error error -> failwith (Artifact.PathError.describe error)

  do
    use first = acquire()
    Assert.True(Manifest.acquireWriter scratch.Path |> Result.isError)

  use next = acquire()
  Assert.True(File.Exists(Path.Combine(scratch.Path, ".writer.lock")))

[<Fact>]
let ``linked manifests are rejected instead of reading outside the artifact`` () =
  use scratch = new Scratch()
  use outside = new Scratch()
  let target = Path.Combine(outside.Path, "manifest.json")
  File.WriteAllText(target, """{"manifestVersion":999}""")
  let link = Path.Combine(scratch.Path, Manifest.FileName)

  if trySymbolicLink link target false then
    match Manifest.read scratch.Path with
    | Error(Manifest.Malformed _) -> ()
    | other -> failwith $"Manifest link was followed: {other}"
