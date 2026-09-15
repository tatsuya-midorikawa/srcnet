/// コマンド ライン引数の解析。
///
/// 外部パッケージを使わず手書きするのは、製品コードへの NuGet 追加を原則禁止としている
/// ためである。解析は前方から 1 回走査するだけで、入力依存のバックトラッキングを持たない。
module Srcnet.Cli.Args

open System
open Srcnet.Core.Ids
open Srcnet.Storage

/// 生成物の既定の出力先。解析ルートからの相対。
[<Literal>]
let DefaultOutputDirectoryName = ".srcnet"

/// 抽出段階の既定値。docs/query-and-cli.md 2.1 の `--tier` に対応する。
[<Literal>]
let DefaultTier = 2

/// 照会が返すノード数の既定上限。
[<Literal>]
let DefaultLimit = 50

/// 返すノード数の絶対上限。利用者が指定しても、これを超える出力は作らない。
[<Literal>]
let MaxLimit = 10_000

/// 出力トークン予算の既定値。AI エージェントの 1 回の読み取りに収まる大きさにする。
[<Literal>]
let DefaultBudget = 8_000

/// 診断を含む JSON 封筒にも予算が必要になる。これ未満は入力誤りとして拒否する。
[<Literal>]
let MinBudget = 256

[<Literal>]
let MaxBudget = 1_000_000

/// 近傍探索の既定の深さ。
[<Literal>]
let DefaultDepth = 1

/// 深さの絶対上限。無制限の探索を許さない。
[<Literal>]
let MaxQueryDepth = 16

/// HTML へ載せるノード数の既定値。
[<Literal>]
let DefaultMaxNodes = 800

/// HTML へ載せるノード数の絶対上限。
/// 全体エクスポートは提供しない。上限を超える入力は集約または打ち切りにする。
[<Literal>]
let MaxExportNodes = 20_000

[<Literal>]
let MaxContextKeywords = 32

[<Literal>]
let MaxQueryScalars = 4096

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

/// 照会コマンドに共通する出力の制限。
///
/// 出力量は必ず有界にする。AI エージェントのコスト削減が目的である以上、予算制御は
/// 中核機能であり任意機能ではない（docs/query-and-cli.md 4）。
type QueryLimits =
  { /// 返すノード数の上限。
    Limit: int
    /// 出力トークンの予算。0 は無制限ではなく既定値を使うことを表す。
    Budget: int }

type SearchArguments =
  { OutputDirectory: string voption
    RootPath: string voption
    Text: string
    /// 大文字小文字を畳んで比較する。
    IgnoreCase: bool
    Limits: QueryLimits
    Json: bool }

type ShowArguments =
  { OutputDirectory: string voption
    RootPath: string voption
    /// ノード ID（16 進 32 桁）または名前。名前が一意でない場合は候補を返す。
    Node: string
    Limits: QueryLimits
    Json: bool }

type NeighborsArguments =
  { OutputDirectory: string voption
    RootPath: string voption
    Node: string
    /// エッジ種別。空なら成果物が持つすべての種別。
    Edges: string[]
    Direction: string
    Depth: int
    Limits: QueryLimits
    Json: bool }

type PathArguments =
  { OutputDirectory: string voption
    RootPath: string voption
    From: string
    To: string
    Edges: string[]
    Direction: string
    Depth: int
    Limits: QueryLimits
    Json: bool }

type ContextArguments =
  { OutputDirectory: string voption
    RootPath: string voption
    Keywords: string[]
    Depth: int
    Limits: QueryLimits
    Json: bool }

type ExportArguments =
  { OutputDirectory: string voption
    RootPath: string voption
    /// 出力先のファイル。指定がなければ `<out>/graph.html`。
    File: string voption
    /// 起点を検索で決める。空なら使わない。
    Query: string
    /// 起点をノード ID または完全一致の名前で決める。空なら使わない。
    Node: string
    Depth: int
    /// 表示するノード数の上限。ブラウザーをフリーズさせないための安全弁である。
    MaxNodes: int
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
  | Search of SearchArguments
  | Show of ShowArguments
  | Neighbors of NeighborsArguments
  | Path of PathArguments
  | Context of ContextArguments
  | ExportHtml of ExportArguments
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

let private strictUtf8 = Text.UTF8Encoding(false, true)

let internal validateQueryText (name: string) (text: string) =
  if isNull(box text) then ValueSome(InvalidValue(name, "null"))
  elif text.Length = 0 then ValueSome(InvalidValue(name, text))
  elif text.Length > MaxQueryScalars * 2 then
    ValueSome(UnsupportedValue(name, "", $"文字列は {MaxQueryScalars} Unicode scalar 以下にしてください"))
  else
    try
      strictUtf8.GetByteCount text |> ignore
      let mutable count = 0
      let mutable runes = text.EnumerateRunes()

      while count <= MaxQueryScalars && runes.MoveNext() do
        count <- count + 1

      if count > MaxQueryScalars then
        ValueSome(UnsupportedValue(name, "", $"文字列は {MaxQueryScalars} Unicode scalar 以下にしてください"))
      else ValueNone
    with :? Text.EncoderFallbackException ->
      ValueSome(InvalidValue(name, "不正な Unicode 文字列"))

let internal validatePath (name: string) (text: string) =
  if String.IsNullOrWhiteSpace text || Srcnet.Text.Unicode.hasUnpairedSurrogate text then
    ValueSome(InvalidValue(name, text))
  else
    try
      IO.Path.GetFullPath text |> ignore
      ValueNone
    with
    | :? ArgumentException
    | :? NotSupportedException
    | :? IO.PathTooLongException -> ValueSome(InvalidValue(name, text))

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
  let mutable positionalOnly = false

  while index < arguments.Length do
    let argument = arguments[index]

    if not positionalOnly && argument = "--" then
      positionalOnly <- true
      index <- index + 1
    elif not positionalOnly && (argument.StartsWith("--", StringComparison.Ordinal) || argument = "-h") then
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
          && arguments[index + 1] <> "-h"

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

        match option.Value with
        | ValueSome text when option.ValueTokenIndex < 0 && error.IsNone ->
          error <- ValueSome(InvalidValue(name, text))
        | _ -> ()

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

  member this.Path(name: string) =
    let value = this.Value name
    match value with
    | ValueSome text when error.IsNone -> error <- validatePath name text
    | _ -> ()
    value

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
  let help = reader.Flag "--help"
  let shortHelp = reader.Flag "-h"

  if
    (help || shortHelp)
    && Array.contains command [| "index"; "search"; "show"; "neighbors"; "path"; "context"; "export"; "stats"; "verify" |]
  then
    match reader.Error with
    | ValueSome error -> Error error
    | ValueNone -> Ok Help
  else

  let optionalValue name = reader.Value name
  let optionalPath name = reader.Path name

  let optionalParsed parseValue name =
    match reader.Value name with
    | ValueNone -> ValueNone
    | ValueSome text ->
      match parseValue text with
      | ValueSome value -> ValueSome(Ok value)
      | ValueNone -> ValueSome(Error(InvalidValue(name, text)))

  let optionalInt name = optionalParsed tryParseInt name
  let optionalSize name = optionalParsed tryParseSize name

  let boundedInt name lower upper zeroIsDefault =
    match optionalInt name with
    | ValueSome(Ok value) when value > upper || (value < lower && not (zeroIsDefault && value = 0)) ->
      ValueSome(
        Error(
          UnsupportedValue(
            name,
            value.ToString Globalization.CultureInfo.InvariantCulture,
            $"{lower} 以上 {upper} 以下で指定してください"
          )
        )
      )
    | value -> value

  let errorOf (value: Result<'T, ParseError> voption) =
    match value with
    | ValueSome(Error error) -> Some error
    | ValueSome(Ok _)
    | ValueNone -> None

  let number (value: Result<int, ParseError> voption) (fallback: int) zeroIsDefault =
    match value with
    | ValueSome(Ok 0) when zeroIsDefault -> fallback
    | ValueSome(Ok parsed) -> parsed
    | ValueSome(Error _)
    | ValueNone -> fallback

  match command with
  | "index" ->
    let output = optionalPath "--out"
    let repository = optionalValue "--repo"
    let jobs = boundedInt "--jobs" 1 Srcnet.Discovery.Walk.WalkOptions.MaxJobs true
    let maxFileSize = optionalSize "--max-file-size"
    let maxDepth = boundedInt "--max-depth" 0 Srcnet.Core.Paths.MaxDepth false
    let assumeEncoding = optionalValue "--assume-encoding"
    let encodingError =
      match assumeEncoding with
      | ValueSome text when Srcnet.Text.Encodings.tryParse text |> ValueOption.isNone ->
        Some(InvalidValue("--assume-encoding", text))
      | _ -> None
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

    match List.tryPick id [ errorOf jobs; errorOf maxDepth; errorOf maxFileSize; errorOf tier; encodingError ] with
    | Some error -> Error error
    | None ->
      if reader.Error |> ValueOption.isSome then Error(ValueOption.get reader.Error)
      elif positional.Count = 0 then Error(MissingArgument "<path>")
      else
        match validatePath "<path>" positional[0].Text with
        | ValueSome error -> Error error
        | ValueNone ->
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
  | "search"
  | "show"
  | "neighbors"
  | "path"
  | "context" ->
    let output = optionalPath "--out"
    let root = optionalPath "--root"
    let limit = boundedInt "--limit" 1 MaxLimit true
    let budget = boundedInt "--budget" MinBudget MaxBudget true
    let traversal = command = "neighbors" || command = "path"
    let depth =
      if traversal || command = "context" then boundedInt "--depth" 0 MaxQueryDepth false
      else ValueNone
    let direction = if traversal then optionalValue "--direction" else ValueNone
    let edges = if traversal then optionalValue "--edge" else ValueNone
    let ignoreCase = command = "search" && reader.Flag "--ignore-case"

    let directionText =
      match direction with
      | ValueSome text -> text
      | ValueNone -> "both"

    let edgeKinds =
      match edges with
      | ValueNone -> Array.empty
      | ValueSome text ->
        text.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

    match List.tryPick id [ errorOf limit; errorOf budget; errorOf depth ] with
    | Some error -> Error error
    | None ->

    let limits =
      { Limit = number limit DefaultLimit true
        Budget = number budget DefaultBudget true }

    let resolvedDepth = number depth DefaultDepth false

    let parsedDirection = Query.Direction.tryParse directionText

    if parsedDirection.IsNone then
      Error(InvalidValue("--direction", directionText))
    elif edges.IsSome && edgeKinds.Length = 0 then
      Error(InvalidValue("--edge", ""))
    else

    let textError =
      positional
      |> Seq.tryPick (fun item -> validateQueryText "<query>" item.Text |> ValueOption.toOption)

    match textError with
    | Some error -> Error error
    | None ->

    match command with
    | "search" ->
      if positional.Count = 0 then Error(MissingArgument "<text>")
      else
        finish reader positional 1 (fun () ->
          Search
            { OutputDirectory = output
              RootPath = root
              Text = positional[0].Text
              IgnoreCase = ignoreCase
              Limits = limits
              Json = json })
    | "show" ->
      if positional.Count = 0 then Error(MissingArgument "<node>")
      else
        finish reader positional 1 (fun () ->
          Show
            { OutputDirectory = output
              RootPath = root
              Node = positional[0].Text
              Limits = limits
              Json = json })
    | "neighbors" ->
      if positional.Count = 0 then Error(MissingArgument "<node>")
      else
        finish reader positional 1 (fun () ->
          Neighbors
            { OutputDirectory = output
              RootPath = root
              Node = positional[0].Text
              Edges = edgeKinds
              Direction = directionText
              Depth = resolvedDepth
              Limits = limits
              Json = json })
    | "path" ->
      if positional.Count < 2 then Error(MissingArgument "<from> <to>")
      else
        finish reader positional 2 (fun () ->
          Path
            { OutputDirectory = output
              RootPath = root
              From = positional[0].Text
              To = positional[1].Text
              Edges = edgeKinds
              Direction = directionText
              Depth = resolvedDepth
              Limits = limits
              Json = json })
    | _ ->
      if positional.Count = 0 then Error(MissingArgument "<keywords...>")
      elif positional.Count > MaxContextKeywords then
        Error(UnsupportedValue("<keywords...>", "", $"キーワードは {MaxContextKeywords} 個までです"))
      else
        // `context` は語をいくつでも取る。位置引数の上限を実際の個数に合わせる。
        finish reader positional positional.Count (fun () ->
          Context
            { OutputDirectory = output
              RootPath = root
              Keywords = positional |> Seq.map (fun item -> item.Text) |> Seq.toArray
              Depth = resolvedDepth
              Limits = limits
              Json = json })
  | "export" ->
    let output = optionalPath "--out"
    let root = optionalPath "--root"
    let file = optionalPath "--file"
    let queryText = optionalValue "--query"
    let node = optionalValue "--node"
    let depth = boundedInt "--depth" 0 MaxQueryDepth false
    let maxNodes = boundedInt "--max-nodes" 1 MaxExportNodes true

    match List.tryPick id [ errorOf depth; errorOf maxNodes ] with
    | Some error -> Error error
    | None ->

    if positional.Count = 0 then Error(MissingArgument "<format>")
    elif positional[0].Text <> "html" then
      Error(UnsupportedValue("export", positional[0].Text, "対応しているのは html だけです"))
    elif queryText.IsSome && node.IsSome then
      Error(UnsupportedValue("--query", "", "--node と同時には指定できません"))
    else

      let seedError =
        [ "--query", queryText; "--node", node ]
        |> List.tryPick (fun (name, value) ->
          match value with
          | ValueSome text -> validateQueryText name text |> ValueOption.toOption
          | ValueNone -> None)

      match seedError with
      | Some error -> Error error
      | None ->

      finish reader positional 1 (fun () ->
        ExportHtml
          { OutputDirectory = output
            RootPath = root
            File = file
            Query =
              match queryText with
              | ValueSome text -> text
              | ValueNone -> ""
            Node =
              match node with
              | ValueSome text -> text
              | ValueNone -> ""
            Depth = number depth DefaultDepth false
            MaxNodes = number maxNodes DefaultMaxNodes true
            Json = json })
  | "stats" ->
    let output = optionalPath "--out"

    match if positional.Count > 0 then validatePath "<path>" positional[0].Text else ValueNone with
    | ValueSome error -> Error error
    | ValueNone ->
    finish reader positional 1 (fun () ->
      Stats
        { OutputDirectory = output
          RootPath = (if positional.Count > 0 then ValueSome positional[0].Text else ValueNone)
          Json = json })
  | "verify" ->
    let output = optionalPath "--out"
    let deterministic = reader.Flag "--deterministic"

    match reader.Error with
    | ValueSome error -> Error error
    | ValueNone ->
    if deterministic && positional.Count = 0 then Error(MissingArgument "<path>")
    else
    match if positional.Count > 0 then validatePath "<path>" positional[0].Text else ValueNone with
    | ValueSome error -> Error error
    | ValueNone ->
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
      "  srcnet index <path> [オプション]      インデックスを生成する"
      "  srcnet search <text> [オプション]     名前・修飾名・パスを検索する"
      "  srcnet show <node> [オプション]       ノードの属性と定義位置を表示する"
      "  srcnet neighbors <node> [オプション]  近傍を辿る"
      "  srcnet path <from> <to> [オプション]  2 ノード間の最短経路を求める"
      "  srcnet context <keywords...>         予算内に収めた文脈をまとめる"
      "  srcnet export html [オプション]       対話的な HTML グラフを出力する"
      "  srcnet stats [<path>] [オプション]    生成物の統計を表示する"
      "  srcnet verify [<path>] [オプション]   生成物の整合性を検証する"
      "  srcnet --version                   版を表示する"
      "  srcnet --help                      この説明を表示する"
      ""
      "index のオプション:"
      "  --out <dir>            出力先 (既定 <path>/.srcnet)"
      "  --repo <id>            リポジトリ ID (既定 <path> のディレクトリ名)"
      "  --jobs <n>             並列度 (1..256、0 は CPU 数)。結果には影響しない"
      "  --max-file-size <size> 1 ファイルの処理上限 (例 64MiB)"
      "  --max-depth <n>        走査する階層の深さ上限 (0..256)"
      "  --tier <0|1|2>         抽出段階 (0 走査のみ / 1 行指向 / 2 構文。既定 2)"
      "  --assume-encoding <名>  符号化が曖昧なファイルに適用する符号化 (例 EUC-JP)"
      "  --no-gitignore         .gitignore / .srcnetignore を無視する"
      "  --follow-symlinks      シンボリック リンクを追跡する (ルート外は拒否)"
      "  --allow-partial        不完全な走査結果での上書きを許可する"
      ""
      "verify のオプション:"
      "  --deterministic        二度生成して成果物がバイト一致するか検証する"
      ""
      "照会 (search / show / neighbors / path / context) のオプション:"
      "  --root <path>          解析ルート (生成物の位置を決めるために使う)"
      "  --limit <n>            返すノード数の上限 (既定 50、上限 10000)"
      "  --budget <tokens>      JSON 封筒を含む出力予算 (256..1000000、既定 8000)"
      "  --depth <n>            neighbors / path / context の深さ (0..16、既定 1)"
      "  --edge <kind,...>      neighbors / path のエッジ種別 (既定はすべて)"
      "  --direction in|out|both neighbors / path の向き (既定 both)"
      "  --ignore-case          search の大文字小文字を畳む"
      ""
      "export html のオプション:"
      "  --file <path>          出力先 (既定 <out>/graph.html)"
      "  --query <text>         起点を検索で決める"
      "  --node <id|name>       起点をノード ID または完全一致の名前で決める"
      "  --depth <n>            起点からの深さ (0..16、既定 1)"
      "  --max-nodes <n>        表示するノード数の上限 (既定 800、上限 20000)"
      "  --query と --node は同時に指定できません"
      ""
      "共通:"
      "  --out <dir>            生成物の位置"
      "  --json                 機械可読出力"
      "  --help, -h             サブコマンドからも説明を表示する"
      "  --                     以降をオプションではなく位置引数として扱う"
      ""
      "終了コード: 0 成功 / 1 該当なし / 2 入力誤り / 3 生成物なし・非互換 / 4 診断あり / 5 中断 / 70 内部エラー" ]
