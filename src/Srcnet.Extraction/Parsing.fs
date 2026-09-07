/// 言語非依存の構文解析 API。
///
/// 抽出器はこの層より上でのみ tree-sitter の存在を意識しない。構文解析器の差し替えが
/// 必要になった場合も、影響をこのモジュールに閉じ込められる（docs/architecture.md 4）。
///
/// 共有ライブラリが無い環境では「利用不可」を返して失敗にしない。構造グラフ（M1）は
/// 構文解析なしで成立するため、解析器の不在で走査全体を止める理由がない。
module Srcnet.Extraction.Parsing

open System
open System.Runtime.InteropServices
open System.Threading
open Srcnet.Core.Graph

/// 1 ファイルあたりの解析時間の上限（マイクロ秒）。
/// 病的な入力で解析が終わらないことを防ぐ（docs/security.md C-4）。
[<Literal>]
let DefaultTimeoutMicroseconds = 5_000_000UL

/// tree-sitter が扱えるソースの上限。長さは 32 ビットで表現される。
[<Literal>]
let MaxSourceBytes = 2_000_000_000

type ParseFailure =
  /// 構文解析の共有ライブラリが無い、または読み込めない。
  | ParserUnavailable of detail: string
  /// この言語に対応する文法を同梱していない。
  | GrammarUnavailable of language: Language
  | SourceTooLarge of bytes: int
  | Cancelled
  | TimedOut
  /// 解析器が木を返さなかった。構文誤りではなく解析器側の失敗を指す。
  | ParseFailed

module ParseFailure =

  let describe failure =
    match failure with
    | ParserUnavailable detail -> $"構文解析器を利用できません: {detail}"
    | GrammarUnavailable language -> $"{Language.name language} の文法を同梱していません"
    | SourceTooLarge bytes -> $"ソースが解析の上限を超えています ({bytes} バイト)"
    | Cancelled -> "解析が取り消されました"
    | TimedOut -> "解析が時間の上限を超えました"
    | ParseFailed -> "解析器が構文木を返しませんでした"

/// 構文解析器を利用できるか。共有ライブラリの有無を一度だけ確かめる。
let isAvailable =
  lazy
    (try
      Grammars.available().Length > 0
     with
     | :? DllNotFoundException -> false
     | :? BadImageFormatException -> false)

/// 同梱している文法に対応する言語の一覧。安定した順序で返す。
let availableLanguages () =
  if not isAvailable.Value then Array.empty
  else
    Grammars.available ()
    |> Array.map Grammars.GrammarId.name

/// 指定した言語を解析できるか。
let supports (language: Language) =
  match Grammars.forLanguage language with
  | ValueNone -> false
  | ValueSome grammar ->
    match Grammars.tryResolve grammar with
    | ValueSome _ -> true
    | ValueNone -> false

// tree-sitter counts LF, whereas the decoding and T1 contracts also accept CR.
// Replace bare CR only in a private copy, preserving every original byte offset.
let private normalizeLineBreaks (source: byte[]) (length: int) (cancellation: CancellationToken) =
  let mutable parsed = source
  let mutable position = 0

  while position < length && not cancellation.IsCancellationRequested do
    let next = ReadOnlySpan(source, position, length - position).IndexOf '\r'B

    if next < 0 then position <- length
    else
      let index = position + next

      if index + 1 = length || source[index + 1] <> '\n'B then
        if Object.ReferenceEquals(parsed, source) then
          parsed <- ReadOnlySpan(source, 0, length).ToArray()

        parsed[index] <- '\n'B

      position <- index + 1

  if cancellation.IsCancellationRequested then ValueNone else ValueSome parsed

/// UTF-8 のソースを解析して構文木を返す。
///
/// `length` は `source` のうち実際に内容が入っている長さ。バッファを再利用する
/// 呼び出し側が、余りを含めずに解析できるようにするために分けて受け取る。
///
/// 戻り値の木は破棄すること。木は原文を保持しないため、ノードの本文を得るには
/// 呼び出し側が同じ `source` を保持しておく必要がある。
let parseRange
  (language: Language)
  (source: byte[])
  (length: int)
  (timeoutMicroseconds: uint64)
  (cancellation: CancellationToken)
  : Result<SyntaxTree.Tree, ParseFailure> =

  ArgumentNullException.ThrowIfNull source
  if length < 0 || length > source.Length then invalidArg (nameof length) "解析長がバッファ長を超えています"
  elif length > MaxSourceBytes then Error(SourceTooLarge length)
  elif cancellation.IsCancellationRequested then Error Cancelled
  else

  match Grammars.forLanguage language with
  | ValueNone -> Error(GrammarUnavailable language)
  | ValueSome grammarId ->

  let resolved =
    try
      Ok(Grammars.tryResolve grammarId)
    with
    | :? DllNotFoundException as ex -> Error(ParserUnavailable ex.Message)
    | :? BadImageFormatException as ex -> Error(ParserUnavailable ex.Message)

  match resolved with
  | Error failure -> Error failure
  | Ok ValueNone -> Error(GrammarUnavailable language)
  | Ok(ValueSome grammar) ->

  match normalizeLineBreaks source length cancellation with
  | ValueNone -> Error Cancelled
  | ValueSome parsedSource ->

  let parser = Native.ts_parser_new ()

  if parser = 0n then Error(ParserUnavailable "解析器を確保できません")
  else

  try
    // Each allocation is protected before acquiring the next resource.
    let flag = Marshal.AllocHGlobal IntPtr.Size

    try
      Marshal.WriteIntPtr(flag, 0n)
      let pinned = GCHandle.Alloc(parsedSource, GCHandleType.Pinned)

      try
        use registration = cancellation.Register(fun () -> Marshal.WriteIntPtr(flag, 1n))

        if not (Native.toBool(Native.ts_parser_set_language(parser, grammar))) then
          Error(ParserUnavailable "文法の版が解析器と一致しません")
        else
          Native.ts_parser_set_timeout_micros(parser, timeoutMicroseconds)
          Native.ts_parser_set_cancellation_flag(parser, flag)

          let tree =
            Native.ts_parser_parse_string(parser, 0n, pinned.AddrOfPinnedObject(), uint32 length)

          if cancellation.IsCancellationRequested then
            if tree <> 0n then Native.ts_tree_delete tree
            Error Cancelled
          elif tree = 0n then
            if timeoutMicroseconds = 0UL then Error ParseFailed else Error TimedOut
          else
            Ok(new SyntaxTree.Tree(tree, length))
      finally
        pinned.Free()
    finally
      // The registration has been disposed, so no callback can touch this flag.
      try
        Native.ts_parser_set_cancellation_flag(parser, 0n)
      finally
        Marshal.FreeHGlobal flag
  finally
    Native.ts_parser_delete parser

/// UTF-8 のソース全体を解析して構文木を返す。
let parse
  (language: Language)
  (source: byte[])
  (timeoutMicroseconds: uint64)
  (cancellation: CancellationToken)
  : Result<SyntaxTree.Tree, ParseFailure> =
  ArgumentNullException.ThrowIfNull source
  parseRange language source source.Length timeoutMicroseconds cancellation

/// 既定の上限で解析する。
let parseDefault (language: Language) (source: byte[]) (cancellation: CancellationToken) =
  parse language source DefaultTimeoutMicroseconds cancellation
