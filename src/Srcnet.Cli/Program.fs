/// エントリー ポイント。
///
/// 長時間処理はすべて取り消し可能で、中断しても生成物を破損させない。
/// docs/requirements.md NFR-13 を参照。
module Srcnet.Cli.Program

open System
open System.Text
open System.Threading
open System.Threading.Tasks

let private configureConsole () =
  // Windows の既定コード ページのままでは CJK が文字化けする。
  // 出力先がリダイレクトされている場合は設定できないことがあるため、失敗しても続行する。
  try
    Console.OutputEncoding <- UTF8Encoding false
  with
  | :? IO.IOException -> ()
  | :? PlatformNotSupportedException -> ()

let private run (arguments: string[]) (cancellation: CancellationToken) : Task<int> =
  task {
    match Args.parse arguments with
    | Error Args.NoCommand ->
      Terminal.outLine Args.usage
      return Commands.ExitCode.UserError
    | Error error ->
      return QueryCommands.writeArgumentError arguments Commands.ExitCode.UserError (Args.ParseError.describe error)
    | Ok Args.Help ->
      Terminal.outLine Args.usage
      return Commands.ExitCode.Success
    | Ok Args.Version ->
      Terminal.outLine $"srcnet {Srcnet.Storage.Manifest.toolVersion}"

      // 構文解析器は任意の構成要素なので、解析できる言語を版と一緒に示す。
      // 構築の有無を確かめる手段がないと、抽出結果が変わった原因を切り分けられない。
      match Srcnet.Extraction.Parsing.availableLanguages () with
      | [||] -> Terminal.outLine "構文解析器: 未構築 (python3 tools/build_native.py で構築します)"
      | languages ->
        let joined = String.Join(", ", languages)
        Terminal.outLine $"構文解析器: {joined}"

      return Commands.ExitCode.Success
    | Ok(Args.Index arguments) -> return! Commands.index arguments cancellation
    | Ok(Args.Stats arguments) -> return Commands.stats arguments
    | Ok(Args.Verify arguments) -> return! Commands.verify arguments cancellation
    | Ok(Args.Search arguments) -> return QueryCommands.search arguments cancellation
    | Ok(Args.Show arguments) -> return QueryCommands.show arguments cancellation
    | Ok(Args.Neighbors arguments) -> return QueryCommands.neighbors arguments cancellation
    | Ok(Args.Path arguments) -> return QueryCommands.path arguments cancellation
    | Ok(Args.Context arguments) -> return QueryCommands.context arguments cancellation
    | Ok(Args.ExportHtml arguments) -> return ExportCommands.exportHtml arguments cancellation
  }

[<EntryPoint>]
let main arguments =
  configureConsole ()
  use cancellation = new CancellationTokenSource()

  let handler =
    ConsoleCancelEventHandler(fun _ event ->
      // 既定の即時終了を止め、後始末を経て中断コードで終わらせる。
      event.Cancel <- true
      cancellation.Cancel())

  Console.CancelKeyPress.AddHandler handler

  try
    try
      (run arguments cancellation.Token).GetAwaiter().GetResult()
    with
    | :? OperationCanceledException ->
      QueryCommands.writeArgumentError arguments Commands.ExitCode.Interrupted "中断しました"
    | ex ->
      // 想定外の障害。文脈を失わないよう型と本文を残す。
      QueryCommands.writeArgumentError arguments Commands.ExitCode.InternalError
        $"内部エラー: {ex.GetType().Name}: {ex.Message}"
  finally
    Console.CancelKeyPress.RemoveHandler handler
    Terminal.flush ()
