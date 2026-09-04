/// 論理パスと物理パスの型による分離。
///
/// 論理パスはリポジトリ ルートからの相対、区切りは `/`、Unicode NFC、大文字小文字は原文のまま。
/// 物理パスは OS 固有の表現で、ファイルシステムへのアクセスにのみ使う。
/// 両者を混同するとノード ID が OS 依存になり、決定性要件 NFR-11 が破れる。
/// docs/platform-and-i18n.md 1 を参照。
module Srcnet.Core.Paths

open System
open Srcnet.Text

type PathError =
  /// 絶対パス、またはドライブ文字・UNC を含む。
  | NotRelative
  /// `..` によるルート外への参照。
  | ParentTraversal
  /// 空のセグメント（`a//b` など）。
  | EmptySegment
  /// 名前自体が区切り文字を含む。POSIX ではファイル名に `\` を使えるが、
  /// 論理パスでは区切りと区別できないため受け付けない。
  | SeparatorInName
  /// 対になっていないサロゲートを含む。NTFS はこうした名前を許すが、
  /// Unicode スカラー値の列として解釈できないため正規化も索引もできない。
  | InvalidUnicode
  /// 制御文字を含む。端末出力の偽装とファイルシステム操作の双方で危険。
  | ControlCharacter
  /// 深さの上限を超えた。
  | TooDeep

module PathError =

  let describe error =
    match error with
    | NotRelative -> "パスがリポジトリ相対ではありません"
    | ParentTraversal -> "パスが `..` でルート外を参照しています"
    | EmptySegment -> "パスに空のセグメントが含まれています"
    | SeparatorInName -> "名前にパス区切り文字が含まれています"
    | InvalidUnicode -> "名前に対になっていないサロゲートが含まれています"
    | ControlCharacter -> "パスに制御文字が含まれています"
    | TooDeep -> "パスの階層が深さ上限を超えています"

/// 走査とパス構築で許容する最大の階層の深さ。
/// 上限を設けるのは無限走査と資源枯渇を防ぐためである。docs/security.md C-4 を参照。
[<Literal>]
let MaxDepth = 256

/// リポジトリ相対の正規化済みパス。
/// 生成経路を `tryCreate` と `append` に限定することで、未正規化の値が混入しない。
[<Struct; StructuralEquality; StructuralComparison>]
type LogicalPath =
  private
  | Path of string

  /// `/` 区切りの NFC 正規化済み文字列。ルートは空文字列。
  member this.Value =
    let (Path value) = this
    value

  override this.ToString() = this.Value

/// 解析ルートそのものを指す論理パス。
let root = Path ""

let value (path: LogicalPath) = path.Value

let isRoot (path: LogicalPath) = path.Value.Length = 0

/// 対になっていないサロゲートを含むか。
///
/// `String.Normalize` はこうした文字列に対して `ArgumentException` を投げる。
/// 正規化の前に弾かなければ、不正な名前のファイルが 1 つあるだけで走査全体が
/// 例外で停止する。NTFS 上では実際に起こり得る入力である。
let private hasUnpairedSurrogate (text: string) =
  let mutable found = false
  let mutable i = 0

  while not found && i < text.Length do
    let c = text[i]

    if Char.IsHighSurrogate c then
      if i + 1 >= text.Length || not (Char.IsLowSurrogate text[i + 1]) then found <- true else i <- i + 1
    elif Char.IsLowSurrogate c then found <- true

    i <- i + 1

  found

let private hasControlCharacter (text: string) =
  let mutable found = false
  let mutable i = 0

  while not found && i < text.Length do
    let c = text[i]
    if c < ' ' || c = '\u007F' then found <- true
    i <- i + 1

  found

/// 単一セグメントとして妥当かを検査する。区切り文字は含められない。
let private validateSegment (segment: string) =
  if segment.Length = 0 then Error EmptySegment
  elif segment = "." || segment = ".." then Error ParentTraversal
  elif segment.IndexOf('/') >= 0 || segment.IndexOf('\\') >= 0 then Error SeparatorInName
  elif hasControlCharacter segment then Error ControlCharacter
  elif hasUnpairedSurrogate segment then Error InvalidUnicode
  else Ok()

let private countSegments (path: string) =
  if path.Length = 0 then 0
  else
    let mutable count = 1
    let mutable i = 0

    while i < path.Length do
      if path[i] = '/' then count <- count + 1
      i <- i + 1

    count

/// 生のリポジトリ相対パスを正規化して論理パスにする。
/// 区切りを `/` に統一し、NFC 正規化し、ルート外への参照を拒否する。
let tryCreate (raw: string) : Result<LogicalPath, PathError> =
  if raw.Length = 0 then Ok root
  elif raw[0] = '/' || raw[0] = '\\' then Error NotRelative
  elif raw.Length >= 2 && raw[1] = ':' then Error NotRelative
  else

  let unified = if raw.IndexOf('\\') >= 0 then raw.Replace('\\', '/') else raw

  if hasUnpairedSurrogate unified then Error InvalidUnicode
  else

  let normalized = Unicode.normalize unified
  let segments = normalized.Split '/'

  if segments.Length > MaxDepth then Error TooDeep
  else

  let mutable error = ValueNone
  let mutable index = 0

  while error.IsNone && index < segments.Length do
    match validateSegment segments[index] with
    | Error e -> error <- ValueSome e
    | Ok() -> index <- index + 1

  match error with
  | ValueSome e -> Error e
  | ValueNone -> Ok(Path normalized)

/// 論理パスへ 1 セグメントを追加する。セグメント側も NFC 正規化する。
let append (parent: LogicalPath) (segment: string) : Result<LogicalPath, PathError> =
  if hasUnpairedSurrogate segment then Error InvalidUnicode
  else

  let normalized = Unicode.normalize segment

  match validateSegment normalized with
  | Error e -> Error e
  | Ok() ->
    let parentValue = parent.Value

    if countSegments parentValue >= MaxDepth then Error TooDeep
    elif parentValue.Length = 0 then Ok(Path normalized)
    else Ok(Path(String.Concat(parentValue, "/", normalized)))

/// 最後のセグメント。ルートに対しては空文字列を返す。
let fileName (path: LogicalPath) =
  let value = path.Value
  let index = value.LastIndexOf '/'
  if index < 0 then value else value.Substring(index + 1)

/// 親の論理パス。ルートには親がない。
let parent (path: LogicalPath) =
  let value = path.Value

  if value.Length = 0 then ValueNone
  else
    let index = value.LastIndexOf '/'
    if index < 0 then ValueSome root else ValueSome(Path(value.Substring(0, index)))

let depth (path: LogicalPath) = countSegments path.Value

/// 最後の `.` 以降をケース フォールドして返す。先頭が `.` だけのファイルは拡張子なしとする。
let extension (path: LogicalPath) =
  let name = fileName path
  let index = name.LastIndexOf '.'

  if index <= 0 || index = name.Length - 1 then ""
  else Unicode.caseFold (name.Substring(index + 1))

/// 物理パスへ変換する。
///
/// Windows では .NET が必要に応じて拡張長パス表記を内部で付与するため、
/// ここでは区切り文字の変換のみを行う。docs/platform-and-i18n.md 1.4 を参照。
let toPhysical (physicalRoot: string) (path: LogicalPath) =
  let value = path.Value

  if value.Length = 0 then physicalRoot
  else
    let native =
      if IO.Path.DirectorySeparatorChar = '/' then value
      else value.Replace('/', IO.Path.DirectorySeparatorChar)

    IO.Path.Combine(physicalRoot, native)

/// 決定的な整列順。序数比較であり、文化圏設定に依存しない。
/// F# 既定の `compare` を隠さないよう別名にしている。
let comparePath (left: LogicalPath) (right: LogicalPath) =
  String.CompareOrdinal(left.Value, right.Value)
