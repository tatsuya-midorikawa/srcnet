/// 成果物ディレクトリの信頼境界。
///
/// 既定の出力先 `.srcnet` は解析対象リポジトリの中にあるため、リポジトリ作成者が
/// あらかじめリンクを置ける。字句的な `Path.GetFullPath` だけで書き込み先を決めると、
/// 索引するだけでリンク先の任意ファイルを上書き・削除できてしまう。
/// 逆に読み取り側では、改変された `manifest.json` のセグメント名で成果物外の
/// ファイルを開けてしまう。どちらも「名前からパスへの変換」を検証しないことが原因なので、
/// 変換をこのモジュールへ集約する。docs/security.md C-3, C-6 を参照。
module Srcnet.Storage.Artifact

open System
open System.IO

/// セグメントを収める成果物内のディレクトリ名。
[<Literal>]
let SegmentDirectory = "segments"

/// 世代識別子の長さ。128 ビットのダイジェストを 16 進小文字で表現する。
[<Literal>]
let GenerationLength = 32

/// セグメント名の長さの上限。想定する形は `segments/<世代>/<短い名前>` に収まる。
[<Literal>]
let MaxSegmentNameLength = 128

type PathError =
  /// シンボリック リンク、junction、その他の reparse point を経由していた。
  | LinkRejected of path: string
  | NotDirectory of path: string
  | InvalidSegmentName of name: string * detail: string
  | EscapesRoot of name: string
  | Unavailable of path: string * message: string

module PathError =

  let describe error =
    match error with
    | LinkRejected path -> $"リンクを経由する成果物のパスは扱えません: {path}"
    | NotDirectory path -> $"出力先がディレクトリではありません: {path}"
    | InvalidSegmentName(name, detail) -> $"セグメント名が不正です ({detail}): {name}"
    | EscapesRoot name -> $"セグメント名が成果物ディレクトリの外を指しています: {name}"
    | Unavailable(path, message) -> $"{path}: {message}"

/// パスの比較規則。Windows は大文字小文字を区別しないため、包含判定でも同じ規則に揃える。
let private pathComparison =
  if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
  else StringComparison.Ordinal

/// シンボリック リンク、junction、その他の reparse point か。
///
/// `LinkTarget` は macOS のシンボリック リンクと Windows の symlink / junction を捉える。
/// `ReparsePoint` 属性はそれ以外の再解析点（mount point など）も捉えるため、両方を見る。
/// 存在しないパスはリンクではない。
let isLink (path: string) =
  let info = FileInfo path

  if not (isNull info.LinkTarget) then true
  else
    try
      // 存在しない項目に対して `Attributes` は全ビットが立った値を返す。
      // 先に除かなければ、これから作るパスをすべてリンクと誤判定する。
      let attributes = info.Attributes

      attributes <> enum<FileAttributes> -1
      && attributes &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint
    with
    | :? FileNotFoundException -> false
    | :? DirectoryNotFoundException -> false
    | :? IOException -> false
    | :? UnauthorizedAccessException -> false

/// 管理対象のパスがリンクでないことを確かめる。存在しなければ成功とする。
let ensureNotLink (path: string) : Result<unit, PathError> =
  if isLink path then Error(LinkRejected path) else Ok()

/// 書き込み先の成果物ルートを用意する。
///
/// 生成、移動、再帰削除のいずれもここを起点にするため、ルート自身がリンクであれば
/// 以降のすべての操作が管理外へ届く。作成の前後で確かめるのは、作成の直前に
/// 置き換えられる競合を検出するためである。
let prepareRoot (directory: string) : Result<string, PathError> =
  let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath directory)

  if isLink full then Error(LinkRejected full)
  elif File.Exists full then Error(NotDirectory full)
  else
    try
      Directory.CreateDirectory full |> ignore
      if isLink full then Error(LinkRejected full) else Ok full
    with
    | :? IOException as ex -> Error(Unavailable(full, ex.Message))
    | :? UnauthorizedAccessException -> Error(Unavailable(full, "作成する権限がありません"))

/// 読み取り側の成果物ルート。利用者が指定した位置そのものは受け入れ、配下だけを検証する。
let resolveRoot (directory: string) =
  Path.TrimEndingDirectorySeparator(Path.GetFullPath directory)

/// Publish a completed sibling temporary file without deleting an open destination.
/// The caller validates paths and owns the temporary file until this succeeds.
let replaceFile (temporary: string) (destination: string) =
  if File.Exists destination then
    File.Replace(temporary, destination, null)
  else
    try
      File.Move(temporary, destination)
    with :? IOException when File.Exists destination ->
      // Another exporter may have completed the first publication concurrently.
      File.Replace(temporary, destination, null)

let private isHexLower (c: char) = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

/// セグメント ファイル名に許可する文字。区切り、ドライブ名、代替データ ストリーム、
/// 制御文字、空白のいずれも含められない。
let private isNameChar (c: char) =
  (c >= 'a' && c <= 'z')
  || (c >= 'A' && c <= 'Z')
  || (c >= '0' && c <= '9')
  || c = '.'
  || c = '-'
  || c = '_'

/// 世代識別子として妥当か。
let isGeneration (text: string) =
  text.Length = GenerationLength && String.forall isHexLower text

/// セグメント名を検証する。
///
/// 名前は成果物ディレクトリからの相対パスとしてのみ意味を持つ。許可する形を
/// `segments/<generation>/<name>` に限ることで、絶対パス、親参照、UNC、
/// 混在した区切り、代替データ ストリームをまとめて排除する。
let validateSegmentName (name: string) : Result<unit, string> =
  if String.IsNullOrEmpty name then Error "空です"
  elif name.Length > MaxSegmentNameLength then Error "長すぎます"
  else
    let parts = name.Split '/'

    if parts.Length <> 3 then Error "`segments/<generation>/<name>` の形ではありません"
    elif parts[0] <> SegmentDirectory then Error $"先頭が `{SegmentDirectory}` ではありません"
    elif not (isGeneration parts[1]) then Error $"世代識別子が {GenerationLength} 桁の 16 進小文字ではありません"
    elif parts[2].Length = 0 then Error "ファイル名が空です"
    elif parts[2] = "." || parts[2] = ".." then Error "ファイル名が相対参照です"
    elif not (String.forall isNameChar parts[2]) then Error "ファイル名に使えない文字があります"
    else Ok()

/// 検証済みのセグメント名を物理パスへ変換する。
///
/// `verify`、`stats`、将来の reader はすべてこの関数を通す。結合後の実体が
/// 成果物ルート配下にあること、経路上の各要素がリンクでないことをここで保証する。
let tryResolveSegment (root: string) (name: string) : Result<string, PathError> =
  match validateSegmentName name with
  | Error detail -> Error(InvalidSegmentName(name, detail))
  | Ok() ->

  let rootFull = resolveRoot root
  let parts = name.Split '/'
  let mutable current = rootFull
  let mutable failure = ValueNone

  for part in parts do
    current <- Path.Combine(current, part)

    // 名前が正しくても、経路の途中がリンクなら成果物の外へ出られる。
    if failure.IsNone && isLink current then failure <- ValueSome(LinkRejected current)

  match failure with
  | ValueSome error -> Error error
  | ValueNone ->
    let resolved = Path.GetFullPath current
    let prefix = rootFull + string Path.DirectorySeparatorChar

    if resolved.StartsWith(prefix, pathComparison) then Ok resolved else Error(EscapesRoot name)
