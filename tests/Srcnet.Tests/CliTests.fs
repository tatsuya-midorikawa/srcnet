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
let ``値を取らないフラグへのインライン値を黙って有効化しない`` () =
  for command, flag in
    [ [ "index"; "repo" ], "--follow-symlinks"
      [ "index"; "repo" ], "--no-gitignore"
      [ "index"; "repo" ], "--allow-partial"
      [ "search"; "name" ], "--ignore-case"
      [ "search"; "name" ], "--json"
      [ "verify" ], "--deterministic" ] do
    for value in [ "false"; "true"; "" ] do
      Assert.Equal(
        Error(Args.InvalidValue(flag, value)),
        parse (command @ [ $"{flag}={value}" ])
      )

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

[<Fact>]
let ``数値オプションの検証順と値の消費をコマンド間で保つ`` () =
  let cases =
    [ [ "index"; "repo"; "--max-depth=bad"; "--jobs=bad" ], Args.InvalidValue("--jobs", "bad")
      [ "index"; "repo"; "--max-file-size=bad" ], Args.InvalidValue("--max-file-size", "bad")
      [ "search"; "name"; "--budget=bad"; "--limit=bad" ], Args.InvalidValue("--limit", "bad")
      [ "export"; "html"; "--max-nodes=bad"; "--depth=bad" ], Args.InvalidValue("--depth", "bad")
      [ "search"; "name"; "--depth" ], Args.MissingValue "--depth"
      [ "export"; "html"; "--max-nodes" ], Args.MissingValue "--max-nodes" ]

  for arguments, expected in cases do
    Assert.Equal(Error expected, parse arguments)

  let indexed = indexArguments [ "index"; "4"; "--jobs=bad"; "--jobs"; "4"; "--max-file-size=64KiB" ]
  Assert.Equal("4", indexed.RootPath)
  Assert.Equal(ValueSome 4, indexed.Jobs)
  Assert.Equal(ValueSome 65_536L, indexed.MaxFileSizeBytes)

[<Theory>]
[<InlineData(1, 256, 0, 1)>]
[<InlineData(10000, 1000000, 16, 20000)>]
let ``照会と HTML 出力は有効な境界値をそのまま使う`` limit budget depth maxNodes =
  match parse [ "search"; "name"; $"--limit={limit}"; $"--budget={budget}" ] with
  | Ok(Args.Search arguments) ->
    Assert.Equal(limit, arguments.Limits.Limit)
    Assert.Equal(budget, arguments.Limits.Budget)
  | other -> failwith $"search として解析できませんでした: {other}"

  match parse [ "neighbors"; "name"; $"--depth={depth}" ] with
  | Ok(Args.Neighbors arguments) -> Assert.Equal(depth, arguments.Depth)
  | other -> failwith $"neighbors として解析できませんでした: {other}"

  match parse [ "export"; "html"; $"--depth={depth}"; $"--max-nodes={maxNodes}" ] with
  | Ok(Args.ExportHtml arguments) ->
    Assert.Equal(depth, arguments.Depth)
    Assert.Equal(maxNodes, arguments.MaxNodes)
  | other -> failwith $"export として解析できませんでした: {other}"

[<Fact>]
let ``件数と予算のゼロ指定は無制限ではなく既定値を使う`` () =
  match parse [ "search"; "name"; "--limit=0"; "--budget=0" ] with
  | Ok(Args.Search arguments) ->
    Assert.Equal(Args.DefaultLimit, arguments.Limits.Limit)
    Assert.Equal(Args.DefaultBudget, arguments.Limits.Budget)
  | other -> failwith $"{other}"

  match parse [ "export"; "html"; "--max-nodes=0" ] with
  | Ok(Args.ExportHtml arguments) -> Assert.Equal(Args.DefaultMaxNodes, arguments.MaxNodes)
  | other -> failwith $"{other}"

[<Fact>]
let ``範囲外の上限は黙って丸めず入力誤りとして返す`` () =
  for arguments, option in
    [ [ "search"; "name"; "--limit=10001" ], "--limit"
      [ "search"; "name"; "--budget=255" ], "--budget"
      [ "search"; "name"; "--budget=1000001" ], "--budget"
      [ "neighbors"; "name"; "--depth=17" ], "--depth"
      [ "export"; "html"; "--max-nodes=20001" ], "--max-nodes" ] do
    match parse arguments with
    | Error(Args.UnsupportedValue(name, _, _)) -> Assert.Equal(option, name)
    | other -> failwith $"上限を拒否しませんでした: {other}"

[<Fact>]
let ``空や不正な照会と競合する HTML 起点を拒否する`` () =
  for arguments in
    [ [ "search"; "" ]
      [ "show"; System.String(char 0xD800, 1) ]
      [ "search"; String.replicate (Args.MaxQueryScalars + 1) "a" ]
      [ "neighbors"; "name"; "--edge=" ]
      [ "export"; "html"; "--query=x"; "--node=y" ]
      [ "export"; "html"; "--query=" ]
      [ "context" ] @ List.replicate (Args.MaxContextKeywords + 1) "name" ] do
    Assert.True(parse arguments |> Result.isError, $"不正な照会を受け入れました: {arguments.Length} 引数")

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
    Tier = 0
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
let ``全コマンドは同じ成果物パスを使い明示した出力先を優先する`` () =
  use repository = new Sandbox()
  repository.Write("日本語.c", "int value;\n")
  Assert.Equal(Commands.ExitCode.Success, runIndex repository.Path)

  let output = Path.Combine(repository.Path, Args.DefaultOutputDirectoryName)
  let limits: Args.QueryLimits = { Limit = Args.DefaultLimit; Budget = Args.DefaultBudget }

  for explicitOutput, rootPath in
    [ ValueNone, ValueSome(repository.Path + string Path.DirectorySeparatorChar)
      ValueSome output, ValueSome(repository.Path + "-missing") ] do
    let search: Args.SearchArguments =
      { OutputDirectory = explicitOutput
        RootPath = rootPath
        Text = "日本語.c"
        IgnoreCase = false
        Limits = limits
        Json = true }

    let show: Args.ShowArguments =
      { OutputDirectory = explicitOutput
        RootPath = rootPath
        Node = "日本語.c"
        Limits = limits
        Json = true }

    let neighbors: Args.NeighborsArguments =
      { OutputDirectory = explicitOutput
        RootPath = rootPath
        Node = "日本語.c"
        Edges = Array.empty
        Direction = "both"
        Depth = 1
        Limits = limits
        Json = true }

    let path: Args.PathArguments =
      { OutputDirectory = explicitOutput
        RootPath = rootPath
        From = "日本語.c"
        To = "日本語.c"
        Edges = Array.empty
        Direction = "both"
        Depth = 1
        Limits = limits
        Json = true }

    let context: Args.ContextArguments =
      { OutputDirectory = explicitOutput
        RootPath = rootPath
        Keywords = [| "日本語.c" |]
        Depth = 1
        Limits = limits
        Json = true }

    let export: Args.ExportArguments =
      { OutputDirectory = explicitOutput
        RootPath = rootPath
        File = ValueNone
        Query = ""
        Node = "日本語.c"
        Depth = 1
        MaxNodes = Args.DefaultMaxNodes
        Json = true }

    Assert.Equal(Commands.ExitCode.Success, Commands.stats { OutputDirectory = explicitOutput; RootPath = rootPath; Json = true })

    let verified =
      Commands.verify
        { OutputDirectory = explicitOutput; RootPath = rootPath; Deterministic = false; Json = true }
        CancellationToken.None

    Assert.Equal(Commands.ExitCode.Success, verified.GetAwaiter().GetResult())
    Assert.Equal(Commands.ExitCode.Success, QueryCommands.search search CancellationToken.None)
    Assert.Equal(Commands.ExitCode.Success, QueryCommands.show show CancellationToken.None)
    Assert.Equal(Commands.ExitCode.Success, QueryCommands.neighbors neighbors CancellationToken.None)
    Assert.Equal(Commands.ExitCode.Success, QueryCommands.path path CancellationToken.None)
    Assert.Equal(Commands.ExitCode.Success, QueryCommands.context context CancellationToken.None)
    Assert.Equal(Commands.ExitCode.Success, ExportCommands.exportHtml export CancellationToken.None)
    Assert.True(File.Exists(Path.Combine(output, ExportCommands.DefaultFileName)))

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
