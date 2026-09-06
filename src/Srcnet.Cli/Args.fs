/// コマンド ライン引数の解析。
///
/// 外部パッケージを使わず手書きするのは、製品コードへの NuGet 追加を原則禁止としている
/// ためである。解析は前方から 1 回走査するだけで、入力依存のバックトラッキングを持たない。
module Srcnet.Cli.Args

open System
open Srcnet.Core.Ids

/// 生成物の既定の出力先。解析ルートからの相対。
[<Literal>]
let DefaultOutputDirectoryName = ".srcnet"

/// 抽出段階の既定値。docs/query-and-cli.md 2.1 の `--tier` に対応する。
[<Literal>]
let DefaultTier = 2

type IndexArguments =
  { RootPath: string
    OutputDirectory: string voption
    RepositoryId: string voption
    Jobs: int voption
    MaxFileSizeBytes: int64 voption
    MaxDepth: int voption
    /// 抽出段階（0..2）。既定は 2。docs/query-and-cli.md 2.1 を参照。
    Tier: int
    /// 符号化が曖昧なファイルに適用する符号化の名前。指定がなければ空。
    AssumeEncoding: string voption
    RespectIgnoreFiles: bool
    FollowSymbolicLinks: bool
    AllowPartial: bool
    Json: bool }

type StatsArguments =
  { OutputDirectory: string voption
    RootPath: string voption
    Json: bool }

type VerifyArguments =
  { OutputDirectory: string voption
    RootPath: string voption
    /// 同一入力から二度生成し、バイト単位の一致を確認する。
    Deterministic: bool
    Json: bool }

type Command =
  | Index of IndexArguments
  | Stats of StatsArguments
  | Verify of VerifyArguments
  | Help
  | Version

type ParseError =
  | NoCommand
  | UnknownCommand of name: string
  | UnknownOption of name: string
  | MissingValue of option: string
  | InvalidValue of option: string * value: string
  | UnexpectedArgument of value: string
  | MissingArgument of name: string
  /// 構文としては正しいが、この版では実装していない値。
  | UnsupportedValue of option: string * value: string * detail: string

module ParseError =

  let describe error =
    match error with
    | NoCommand -> "コマンドが指定されていません"
    | UnknownCommand name -> $"未知のコマンドです: {name}"
    | UnknownOption name -> $"未知のオプションです: {name}"
    | MissingValue option -> $"オプション {option} に値が必要です"
    | InvalidValue(option, value) -> $"オプション {option} の値が不正です: {value}"
    | UnexpectedArgument value -> $"余分な引数です: {value}"
    | MissingArgument name -> $"引数 {name} が必要です"
    | UnsupportedValue(option, value, detail) -> $"オプション {option} の値 {value} は未対応です: {detail}"

/// `4096`、`64KiB`、`1GB` のようなサイズ表記を解析する。
/// 単位は 2 進接頭辞（KiB/MiB/GiB）と 10 進接頭辞（KB/MB/GB）の両方を受け付ける。
let tryParseSize (text: string) =
  let trimmed = text.Trim()

  if trimmed.Length = 0 then ValueNone
  else

  let struct (digits, multiplier) =
    let upper = trimmed.ToUpperInvariant()

    if upper.EndsWith("KIB", StringComparison.Ordinal) then struct (trimmed.Substring(0, trimmed.Length - 3), 1024L)
    elif upper.EndsWith("MIB", StringComparison.Ordinal) then struct (trimmed.Substring(0, trimmed.Length - 3), 1024L * 1024L)
    elif upper.EndsWith("GIB", StringComparison.Ordinal) then
      struct (trimmed.Substring(0, trimmed.Length - 3), 1024L * 1024L * 1024L)
    elif upper.EndsWith("KB", StringComparison.Ordinal) then struct (trimmed.Substring(0, trimmed.Length - 2), 1000L)
    elif upper.EndsWith("MB", StringComparison.Ordinal) then struct (trimmed.Substring(0, trimmed.Length - 2), 1000_000L)
    elif upper.EndsWith("GB", StringComparison.Ordinal) then struct (trimmed.Substring(0, trimmed.Length - 2), 1000_000_000L)
    elif upper.EndsWith("B", StringComparison.Ordinal) then struct (trimmed.Substring(0, trimmed.Length - 1), 1L)
    else struct (trimmed, 1L)

  match Int64.TryParse(digits.Trim(), Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
  | true, value when value >= 0L && value <= Int64.MaxValue / multiplier -> ValueSome(value * multiplier)
  | _ -> ValueNone

let private tryParseInt (text: string) =
  match Int32.TryParse(text, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
  | true, value -> ValueSome value
  | false, _ -> ValueNone

/// 位置引数の候補。`TokenIndex` は元のトークン位置である。
/// オプション値として消費された引数を、文字列一致ではなく位置で取り除くために使う。
[<Struct>]
type private Positional = { TokenIndex: int; Text: string }

/// 走査したオプション 1 つ分。
///
/// `ValueTokenIndex` は空白区切り形式で値になり得る次トークンの位置で、
/// `--option=value` のインライン形式では -1 になる。この区別がないと、
/// インライン値と同じ文字列を持つ正当な位置引数まで取り除いてしまう。
[<Struct>]
type private ParsedOption =
  { Name: string
    Value: string voption
    ValueTokenIndex: int }

/// 位置引数とオプションを 1 回走査で振り分ける。
/// `--option value` と `--option=value` の両方を受け付ける。
let private split (arguments: string[]) =
  let positional = ResizeArray<Positional>()
  let options = ResizeArray<ParsedOption>()
  let mutable index = 0

  while index < arguments.Length do
    let argument = arguments[index]

    if argument.StartsWith("--", StringComparison.Ordinal) then
      let separator = argument.IndexOf '='

      if separator > 0 then
        options.Add
          { Name = argument.Substring(0, separator)
            Value = ValueSome(argument.Substring(separator + 1))
            ValueTokenIndex = -1 }
      else
        // 値を取るかどうかは呼び出し側で決めるため、ここでは次の引数を暫定的に添える。
        let takesNext =
          index + 1 < arguments.Length
          && not (arguments[index + 1].StartsWith("--", StringComparison.Ordinal))

        options.Add
          { Name = argument
            Value = (if takesNext then ValueSome arguments[index + 1] else ValueNone)
            ValueTokenIndex = (if takesNext then index + 1 else -1) }

      index <- index + 1
    else
      positional.Add { TokenIndex = index; Text = argument }
      index <- index + 1

  struct (positional, options)

/// オプションの走査結果を保持する補助。値を消費した引数は位置引数から取り除く。
type private OptionReader(options: ResizeArray<ParsedOption>, positional: ResizeArray<Positional>) =
  let consumed = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
  let mutable error = ValueNone

  member _.Error = error

  member _.Flag(name: string) =
    let mutable found = false

    for option in options do
      if option.Name = name then
        consumed.Add name |> ignore
        found <- true

    found

  member _.Value(name: string) =
    let mutable result = ValueNone

    for option in options do
      if option.Name = name then
        consumed.Add name |> ignore

        match option.Value with
        | ValueNone -> if error.IsNone then error <- ValueSome(MissingValue name)
        | ValueSome text ->
          result <- ValueSome text

          // `--jobs 4` の形式でのみ、値のトークンが位置引数としても数えられている。
          // 取り除く対象はそのトークン自身であり、同じ文字列を持つ別の引数ではない。
          if option.ValueTokenIndex >= 0 then
            let mutable cursor = 0

            while cursor < positional.Count do
              if positional[cursor].TokenIndex = option.ValueTokenIndex then positional.RemoveAt cursor
              else cursor <- cursor + 1

    result

  member _.Unknown() =
    let mutable unknown = ValueNone

    for option in options do
      if not (consumed.Contains option.Name) && unknown.IsNone then unknown <- ValueSome option.Name

    unknown

let private finish
  (reader: OptionReader)
  (positional: ResizeArray<Positional>)
  (expected: int)
  (build: unit -> Command)
  =
  match reader.Error with
  | ValueSome error -> Error error
  | ValueNone ->
    match reader.Unknown() with
    | ValueSome option -> Error(UnknownOption option)
    | ValueNone ->
      if positional.Count > expected then Error(UnexpectedArgument positional[expected].Text)
      else Ok(build ())

let parse (arguments: string[]) : Result<Command, ParseError> =
  if arguments.Length = 0 then Error NoCommand
  else

  match arguments[0] with
  | "--help"
  | "-h"
  | "help" -> Ok Help
  | "--version" -> Ok Version
  | command ->

  let rest = arguments[1..]
  let struct (positional, options) = split rest

  let reader = OptionReader(options, positional)
  let json = reader.Flag "--json"

  let optionalValue name = reader.Value name

  let optionalInt name =
    match reader.Value name with
    | ValueNone -> ValueNone
    | ValueSome text ->
      match tryParseInt text with
      | ValueSome value -> ValueSome(Ok value)
      | ValueNone -> ValueSome(Error(InvalidValue(name, text)))

  let optionalSize name =
    match reader.Value name with
    | ValueNone -> ValueNone
    | ValueSome text ->
      match tryParseSize text with
      | ValueSome value -> ValueSome(Ok value)
      | ValueNone -> ValueSome(Error(InvalidValue(name, text)))

  match command with
  | "index" ->
    let output = optionalValue "--out"
    let repository = optionalValue "--repo"
    let jobs = optionalInt "--jobs"
    let maxFileSize = optionalSize "--max-file-size"
    let maxDepth = optionalInt "--max-depth"
    let assumeEncoding = optionalValue "--assume-encoding"
    let noGitignore = reader.Flag "--no-gitignore"
    let followSymlinks = reader.Flag "--follow-symlinks"
    let allowPartial = reader.Flag "--allow-partial"

    // `--tier` は段階の番号をそのまま取る。3（ビルド構成に基づく解決）は未実装であり、
    // 内部エラーではなく利用者エラーとして拒否する（backlog 021）。
    let tier =
      match reader.Value "--tier" with
      | ValueNone -> ValueNone
      | ValueSome text ->
        match tryParseInt text with
        | ValueSome 3 ->
          ValueSome(
            Error(
              UnsupportedValue(
                "--tier",
                text,
                "T3 はビルド構成に基づく解決であり、この版では実装していません"
              )
            )
          )
        | ValueSome value when value >= 0 && value <= 2 -> ValueSome(Ok value)
        | ValueSome _
        | ValueNone -> ValueSome(Error(InvalidValue("--tier", text)))

    let errorOf (value: Result<'T, ParseError> voption) =
      match value with
      | ValueSome(Error error) -> Some error
      | ValueSome(Ok _)
      | ValueNone -> None

    match List.tryPick id [ errorOf jobs; errorOf maxDepth; errorOf maxFileSize; errorOf tier ] with
    | Some error -> Error error
    | None ->
      if positional.Count = 0 then Error(MissingArgument "<path>")
      else
        let unwrap (value: Result<'T, ParseError> voption) =
          match value with
          | ValueSome(Ok parsed) -> ValueSome parsed
          | ValueSome(Error _)
          | ValueNone -> ValueNone

        finish reader positional 1 (fun () ->
          Index
            { RootPath = positional[0].Text
              OutputDirectory = output
              RepositoryId = repository
              Jobs = unwrap jobs
              MaxFileSizeBytes = unwrap maxFileSize
              MaxDepth = unwrap maxDepth
              Tier =
                match unwrap tier with
                | ValueSome value -> value
                | ValueNone -> DefaultTier
              AssumeEncoding = assumeEncoding
              RespectIgnoreFiles = not noGitignore
              FollowSymbolicLinks = followSymlinks
              AllowPartial = allowPartial
              Json = json })
  | "stats" ->
    let output = optionalValue "--out"

    finish reader positional 1 (fun () ->
      Stats
        { OutputDirectory = output
          RootPath = (if positional.Count > 0 then ValueSome positional[0].Text else ValueNone)
          Json = json })
  | "verify" ->
    let output = optionalValue "--out"
    let deterministic = reader.Flag "--deterministic"

    finish reader positional 1 (fun () ->
      Verify
        { OutputDirectory = output
          RootPath = (if positional.Count > 0 then ValueSome positional[0].Text else ValueNone)
          Deterministic = deterministic
          Json = json })
  | other -> Error(UnknownCommand other)

/// リポジトリ ID を決める。指定がなければ解析ルートのディレクトリ名を使う。
/// 絶対パスは含めない。成果物が実行環境に依存しなくなるため。
let repositoryIdFor (explicit: string voption) (rootFullPath: string) =
  let candidate =
    match explicit with
    | ValueSome value -> value
    | ValueNone ->
      match IO.Path.GetFileName(IO.Path.TrimEndingDirectorySeparator rootFullPath) with
      | null -> "repository"
      | "" -> "repository"
      | name -> name

  RepositoryId.tryCreate candidate

let usage =
  String.concat
    Terminal.Newline
    [ "srcnet — ソースコードから AI を使わずにナレッジ グラフを生成する"
      ""
      "使い方:"
      "  srcnet index <path> [オプション]   インデックスを生成する"
      "  srcnet stats [<path>] [オプション]  生成物の統計を表示する"
      "  srcnet verify [<path>] [オプション] 生成物の整合性を検証する"
      "  srcnet --version                   版を表示する"
      "  srcnet --help                      この説明を表示する"
      ""
      "index のオプション:"
      "  --out <dir>            出力先 (既定 <path>/.srcnet)"
      "  --repo <id>            リポジトリ ID (既定 <path> のディレクトリ名)"
      "  --jobs <n>             並列度。結果には影響しない"
      "  --max-file-size <size> 1 ファイルの処理上限 (例 64MiB)"
      "  --max-depth <n>        走査する階層の深さ上限"
      "  --tier <0|1|2>         抽出段階 (0 走査のみ / 1 行指向 / 2 構文。既定 2)"
      "  --assume-encoding <名>  符号化が曖昧なファイルに適用する符号化 (例 EUC-JP)"
      "  --no-gitignore         .gitignore / .srcnetignore を無視する"
      "  --follow-symlinks      シンボリック リンクを追跡する (ルート外は拒否)"
      "  --allow-partial        不完全な走査結果での上書きを許可する"
      ""
      "verify のオプション:"
      "  --deterministic        二度生成して成果物がバイト一致するか検証する"
      ""
      "共通:"
      "  --out <dir>            生成物の位置"
      "  --json                 機械可読出力"
      ""
      "終了コード: 0 成功 / 1 該当なし / 2 入力誤り / 3 生成物なし・非互換 / 4 診断あり / 5 中断 / 70 内部エラー" ]
