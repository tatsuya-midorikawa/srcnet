/// コマンド ライン引数の解析のテスト。
module Srcnet.Tests.CliTests

open Xunit
open Srcnet.Cli
open Srcnet.Storage

let private parse (arguments: string list) = Args.parse (List.toArray arguments)

let private indexArguments (arguments: string list) =
  match parse arguments with
  | Ok(Args.Index parsed) -> parsed
  | other -> failwith $"index として解析できませんでした: {other}"

[<Fact>]
let ``インライン値と同じ位置引数を取り除かない`` () =
  // `--repo=repo` の値は位置引数として現れていない。文字列一致で取り除くと、
  // 解析ルートの `repo` まで消えて「引数が必要です」と誤報する。
  let parsed = indexArguments [ "index"; "repo"; "--repo=repo" ]
  Assert.Equal("repo", parsed.RootPath)
  Assert.Equal(ValueSome "repo", parsed.RepositoryId)

[<Fact>]
let ``インライン値と同じ位置引数が出力先でも保たれる`` () =
  let parsed = indexArguments [ "index"; "--out=out"; "out" ]
  Assert.Equal("out", parsed.RootPath)
  Assert.Equal(ValueSome "out", parsed.OutputDirectory)

[<Fact>]
let ``空白区切りの値だけを位置引数から取り除く`` () =
  let parsed = indexArguments [ "index"; "--repo"; "repo"; "root" ]
  Assert.Equal("root", parsed.RootPath)
  Assert.Equal(ValueSome "repo", parsed.RepositoryId)

[<Fact>]
let ``同じ値の位置引数が複数あっても消えるのは値のトークンだけ`` () =
  // `4` が 2 つある。取り除くのは `--jobs` が消費したトークンだけでなければならない。
  let parsed = indexArguments [ "index"; "--jobs"; "4"; "4" ]
  Assert.Equal("4", parsed.RootPath)
  Assert.Equal(ValueSome 4, parsed.Jobs)

[<Fact>]
let ``インライン値を持つオプションの後ろの位置引数を保つ`` () =
  let parsed = indexArguments [ "index"; "--jobs=4"; "4" ]
  Assert.Equal("4", parsed.RootPath)
  Assert.Equal(ValueSome 4, parsed.Jobs)

[<Fact>]
let ``フラグの次の位置引数は消費しない`` () =
  let parsed = indexArguments [ "index"; "--follow-symlinks"; "repo" ]
  Assert.Equal("repo", parsed.RootPath)
  Assert.True parsed.FollowSymbolicLinks

[<Fact>]
let ``未知のオプションは誤りとして報告する`` () =
  Assert.Equal(Error(Args.UnknownOption "--bogus"), parse [ "index"; "repo"; "--bogus" ])

[<Fact>]
let ``余分な位置引数は誤りとして報告する`` () =
  Assert.Equal(Error(Args.UnexpectedArgument "extra"), parse [ "index"; "repo"; "extra" ])

[<Fact>]
let ``解析ルートの指定がなければ誤りとして報告する`` () =
  Assert.Equal(Error(Args.MissingArgument "<path>"), parse [ "index" ])

[<Fact>]
let ``値を取るオプションに値がなければ誤りとして報告する`` () =
  Assert.Equal(Error(Args.MissingValue "--out"), parse [ "stats"; "--out" ])

[<Fact>]
let ``不正な数値は誤りとして報告する`` () =
  Assert.Equal(Error(Args.InvalidValue("--jobs", "many")), parse [ "index"; "repo"; "--jobs=many" ])

[<Fact>]
let ``stats と verify も位置引数を保つ`` () =
  match parse [ "stats"; "out"; "--out=out" ] with
  | Ok(Args.Stats parsed) ->
    Assert.Equal(ValueSome "out", parsed.RootPath)
    Assert.Equal(ValueSome "out", parsed.OutputDirectory)
  | other -> failwith $"stats として解析できませんでした: {other}"

  match parse [ "verify"; "out"; "--out=out"; "--deterministic" ] with
  | Ok(Args.Verify parsed) ->
    Assert.Equal(ValueSome "out", parsed.RootPath)
    Assert.Equal(ValueSome "out", parsed.OutputDirectory)
    Assert.True parsed.Deterministic
  | other -> failwith $"verify として解析できませんでした: {other}"

// --- コマンドの通し検証 ---

open System
open System.IO
open System.Threading

/// テストごとに使い捨てる一時ディレクトリ。
type private Sandbox() =
  let path =
    Path.Combine(Path.GetTempPath(), "srcnet-cli-" + Guid.NewGuid().ToString "N")

  do Directory.CreateDirectory path |> ignore

  member _.Path = path

  member _.Write(relative: string, content: string) =
    let full = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar))

    match Path.GetDirectoryName full with
    | null -> ()
    | parent -> Directory.CreateDirectory parent |> ignore

    File.WriteAllText(full, content, Text.UTF8Encoding false)

  interface IDisposable with

    member _.Dispose() =
      if Directory.Exists path then
        try
          Directory.Delete(path, true)
        with :? IOException ->
          ()

let private indexOptions (root: string) : Args.IndexArguments =
  { RootPath = root
    OutputDirectory = ValueNone
    RepositoryId = ValueNone
    Jobs = ValueSome 1
    MaxFileSizeBytes = ValueNone
    MaxDepth = ValueNone
    Tier = Args.DefaultTier
    AssumeEncoding = ValueNone
    RespectIgnoreFiles = true
    FollowSymbolicLinks = false
    AllowPartial = false
    Json = false }

let private runIndex (root: string) =
  (Commands.index (indexOptions root) CancellationToken.None).Result

let private runVerify (root: string) (deterministic: bool) =
  let arguments: Args.VerifyArguments =
    { OutputDirectory = ValueNone
      RootPath = ValueSome root
      Deterministic = deterministic
      Json = false }

  (Commands.verify arguments CancellationToken.None).Result

[<Fact>]
let ``deterministic verify は manifest.json だけの差も検出する`` () =
  // セグメントの記述子だけを比べると、マニフェストにしか現れない非決定性を見逃す。
  use repository = new Sandbox()
  repository.Write("a.c", "int a;\n")
  repository.Write("設計/概要.md", "# 設計\n")
  Assert.Equal(Commands.ExitCode.Success, runIndex repository.Path)
  Assert.Equal(Commands.ExitCode.Success, runVerify repository.Path true)

  let manifestPath =
    Path.Combine(repository.Path, Args.DefaultOutputDirectoryName, Manifest.FileName)

  let original = File.ReadAllBytes manifestPath

  // 空白 1 文字の違いも「バイト単位で一致」と報告してはならない。
  File.WriteAllBytes(manifestPath, Array.append original " "B)
  Assert.Equal(Commands.ExitCode.CompletedWithDiagnostics, runVerify repository.Path true)

  File.WriteAllBytes(manifestPath, original)
  Assert.Equal(Commands.ExitCode.Success, runVerify repository.Path true)

[<Fact>]
let ``deterministic verify は成果物の追加ファイルを検出する`` () =
  use repository = new Sandbox()
  repository.Write("a.c", "int a;\n")
  Assert.Equal(Commands.ExitCode.Success, runIndex repository.Path)

  File.WriteAllText(
    Path.Combine(repository.Path, Args.DefaultOutputDirectoryName, "stray.bin"),
    "leftover"
  )

  Assert.Equal(Commands.ExitCode.CompletedWithDiagnostics, runVerify repository.Path true)

[<Fact>]
let ``リンクである出力先へは索引せず、利用者向けの終了コードを返す`` () =
  // 既定の出力先は解析対象リポジトリの中にある。リポジトリ作成者がここへ
  // リンクを置くと、索引するだけでリンク先の任意ファイルを壊せてしまう。
  use repository = new Sandbox()
  use outside = new Sandbox()
  repository.Write("a.c", "int a;\n")
  outside.Write("segments/sentinel.txt", "触れてはならない\n")

  let link = Path.Combine(repository.Path, Args.DefaultOutputDirectoryName)

  let created =
    try
      Directory.CreateSymbolicLink(link, outside.Path) |> ignore
      true
    with
    | :? IOException -> false
    | :? UnauthorizedAccessException -> false
    | :? PlatformNotSupportedException -> false

  if created then
    Assert.Equal(Commands.ExitCode.UserError, runIndex repository.Path)
    // リンク先は一切変更されない。
    Assert.True(File.Exists(Path.Combine(outside.Path, "segments", "sentinel.txt")))
    Assert.False(File.Exists(Path.Combine(outside.Path, Manifest.FileName)))

[<Fact>]
let ``deterministic verify は猶予として残した旧世代を差として報告しない`` () =
  // 旧世代は読み手への猶予として意図的に残す。現行の内容ではないため、
  // 「再生成した成果物にない」と報告してはならない。
  use repository = new Sandbox()
  repository.Write("a.c", "int a;\n")
  Assert.Equal(Commands.ExitCode.Success, runIndex repository.Path)

  repository.Write("b.c", "int b;\n")
  Assert.Equal(Commands.ExitCode.Success, runIndex repository.Path)

  let segments =
    Path.Combine(repository.Path, Args.DefaultOutputDirectoryName, Artifact.SegmentDirectory)

  Assert.Equal(2, Directory.EnumerateDirectories segments |> Seq.length)
  Assert.Equal(Commands.ExitCode.Success, runVerify repository.Path true)
