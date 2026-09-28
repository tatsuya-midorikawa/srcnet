/// 端末への出力。
///
/// `stdout` には結果のみを出し、進捗・警告・診断は `stderr` へ出す。
/// 対象リポジトリ由来の文字列は必ず無害化してから書く。docs/security.md C-5 を参照。
module Srcnet.Cli.Terminal

open System
open System.IO
open System.Text
open Srcnet.Text

/// 出力の改行はプラットフォームによらず LF に固定する。決定性のため。
/// docs/platform-and-i18n.md 5 を参照。
[<Literal>]
let Newline = "\n"

let private makeWriter (stream: Stream) =
  // BOM を付けない UTF-8。Windows の既定コード ページのままでは CJK が文字化けする。
  let writer = new StreamWriter(stream, UTF8Encoding false, 8192, AutoFlush = false)
  writer.NewLine <- Newline
  writer

let private standardOutput = makeWriter(Console.OpenStandardOutput())
let private standardError = makeWriter(Console.OpenStandardError())

/// 端末以外（パイプ、リダイレクト）へは制御シーケンスを一切出力しない。
let isInteractive = not Console.IsOutputRedirected && not Console.IsErrorRedirected

/// 端末の桁数。取得できない場合の既定値は 80。
let width =
  try
    if Console.IsOutputRedirected then
      80
    else
      max 20 Console.WindowWidth
  with :? IO.IOException ->
    80

let out (text: string) = standardOutput.Write text

let outLine (text: string) = standardOutput.WriteLine text

let errLine (text: string) = standardError.WriteLine text

let progressLine (text: string) =
  standardError.WriteLine text
  standardError.Flush()

/// リポジトリ由来の文字列を含む行を `stderr` へ出す。
let diagnosticLine (text: string) =
  standardError.WriteLine(Sanitize.forTerminal text)

/// リポジトリ由来の文字列を含む行を `stdout` へ出す。
let resultLine (text: string) =
  standardOutput.WriteLine(Sanitize.forTerminal text)

/// 端末の桁数に収まるよう、東アジア文字幅を考慮して切り詰める。
let fitToWidth (text: string) =
  let sanitized = Sanitize.forTerminal text

  if Unicode.displayWidth sanitized <= width then
    sanitized
  else
    Unicode.truncateToWidth (width - 1) sanitized + "…"

let flush () =
  standardOutput.Flush()
  standardError.Flush()
