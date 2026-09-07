/// 対象リポジトリ由来の文字列を端末へ出す前の無害化。
///
/// ファイル名・識別子・コメントはそのまま端末と AI エージェントへ流れる。
/// 無害化を怠るとリポジトリ側から端末表示を偽装できる。docs/security.md C-5 を参照。
module Srcnet.Text.Sanitize

open System

/// 除去した文字の位置に置く文字。1 桁で表示され、除去があったことが目に見える。
[<Literal>]
let private Replacement = '\uFFFD'

/// 端末表示を偽装し得る文字か。
///
/// - C0 制御文字（ESC を含む）と DEL: ANSI エスケープ シーケンスの起点になる
/// - C1 制御文字: 一部の端末で 8 ビット エスケープとして解釈される
/// - 双方向テキスト制御: 表示順を入れ替えてコードの意味を偽装できる
let private isUnsafe (c: char) =
  c < ' '
  || (c >= '\u007F' && c <= '\u009F')
  || c = '\u061C'
  || c = '\u200E'
  || c = '\u200F'
  || (c >= '\u202A' && c <= '\u202E')
  || (c >= '\u2066' && c <= '\u2069')

let private needsSanitizing (text: string) =
  let mutable found = false
  let mutable i = 0

  while not found && i < text.Length do
    if isUnsafe text[i] then
      found <- true

    i <- i + 1

  found

/// 単一行として端末へ出力できる文字列に変換する。
/// 安全な入力に対しては入力インスタンスをそのまま返し、割り当てを避ける。
let forTerminal (text: string) =
  if not(needsSanitizing text) then
    text
  else
    String.Create(
      text.Length,
      text,
      fun destination source ->
        for i in 0 .. source.Length - 1 do
          destination[i] <- if isUnsafe source[i] then Replacement else source[i]
    )

/// 複数行の出力で、改行とタブだけは保持したい場合に使う。
let forTerminalMultiline (text: string) =
  let unsafeExceptWhitespace (c: char) =
    c <> '\n' && c <> '\r' && c <> '\t' && isUnsafe c

  let mutable found = false
  let mutable i = 0

  while not found && i < text.Length do
    if unsafeExceptWhitespace text[i] then
      found <- true

    i <- i + 1

  if not found then
    text
  else
    String.Create(
      text.Length,
      text,
      fun destination source ->
        for i in 0 .. source.Length - 1 do
          destination[i] <-
            if unsafeExceptWhitespace source[i] then
              Replacement
            else
              source[i]
    )
