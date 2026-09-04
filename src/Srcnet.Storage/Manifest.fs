/// マニフェストの読み書き。
///
/// マニフェストは形式版、生成条件、セグメント一覧、チェックサムを持つ。
/// 原子的な差し替えによって、読み手は常に一貫したセグメント集合を見る。
/// 決定性のため、タイムスタンプと絶対パスは一切含めない。docs/storage.md 3 を参照。
module Srcnet.Storage.Manifest

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text.Json

/// マニフェストの構造の版。セグメント形式の版とは独立に管理する。
[<Literal>]
let ManifestVersion = 1u

[<Literal>]
let FileName = "manifest.json"

[<Literal>]
let SegmentDirectory = "segments"

/// 生成中の成果物を置く一時ディレクトリ名。
///
/// セグメントを最終位置へ直接書くと、途中で失敗したときに既存の成果物が
/// 壊れたまま残り、マニフェストと整合しなくなる。いったんここへ全部書き、
/// 成功したときだけ差し替える。docs/storage.md 3 の原子性の要件に対応する。
[<Literal>]
let StagingDirectory = ".staging"

/// 実行時に決まる版番号。ビルドが同じなら同じ値になるため、決定性を損なわない。
let toolVersion =
  match Assembly.GetExecutingAssembly().GetName().Version with
  | null -> "0.0.0"
  | version -> $"{version.Major}.{version.Minor}.{version.Build}"

type Counts =
  { Nodes: int
    Edges: int
    Strings: int
    StringBytes: int64
    Directories: int
    Files: int }

type IndexOptions =
  { FollowSymbolicLinks: bool
    RespectIgnoreFiles: bool
    MaxDepth: int
    MaxFileSizeBytes: int64 }

type DiagnosticCount = { Kind: string; Count: int }

type Manifest =
  { ManifestVersion: uint32
    FormatVersion: uint32
    ToolVersion: string
    RepositoryId: string
    /// 上限や失敗による打ち切りがなく、ツリー全体を走査できたか。
    Complete: bool
    Options: IndexOptions
    Counts: Counts
    /// 名前の序数昇順。
    Segments: Writer.SegmentDescriptor[]
    /// 種別名の序数昇順。
    Diagnostics: DiagnosticCount[] }

type ManifestError =
  | NotFound of path: string
  | Malformed of detail: string
  | UnsupportedManifestVersion of found: uint32
  | UnsupportedFormatVersion of found: uint32

module ManifestError =

  let describe error =
    match error with
    | NotFound path -> $"生成物が見つかりません: {path}"
    | Malformed detail -> $"マニフェストを解釈できません: {detail}"
    | UnsupportedManifestVersion found -> $"マニフェスト版 {found} は未対応です (対応版 {ManifestVersion})"
    | UnsupportedFormatVersion found -> $"セグメント形式版 {found} は未対応です (対応版 {Format.FormatVersion})"

let private writerOptions =
  // 改行を明示しないと OS 既定の改行が使われ、成果物が OS 依存になる。
  JsonWriterOptions(Indented = true, IndentCharacter = ' ', IndentSize = 2, NewLine = "\n")

let private writeSegments (writer: Utf8JsonWriter) (segments: Writer.SegmentDescriptor[]) =
  writer.WriteStartArray "segments"

  for segment in segments do
    writer.WriteStartObject()
    writer.WriteString("name", segment.Name)
    writer.WriteNumber("byteLength", segment.ByteLength)
    writer.WriteString("blake3", segment.Checksum)
    writer.WriteEndObject()

  writer.WriteEndArray()

let private serialize (manifest: Manifest) =
  use buffer = new MemoryStream()
  use writer = new Utf8JsonWriter(buffer, writerOptions)

  writer.WriteStartObject()
  writer.WriteNumber("manifestVersion", manifest.ManifestVersion)
  writer.WriteNumber("formatVersion", manifest.FormatVersion)
  writer.WriteString("tool", "srcnet")
  writer.WriteString("toolVersion", manifest.ToolVersion)
  writer.WriteString("repositoryId", manifest.RepositoryId)
  writer.WriteBoolean("complete", manifest.Complete)

  writer.WriteStartObject "options"
  writer.WriteBoolean("followSymbolicLinks", manifest.Options.FollowSymbolicLinks)
  writer.WriteBoolean("respectIgnoreFiles", manifest.Options.RespectIgnoreFiles)
  writer.WriteNumber("maxDepth", manifest.Options.MaxDepth)
  writer.WriteNumber("maxFileSizeBytes", manifest.Options.MaxFileSizeBytes)
  writer.WriteEndObject()

  writer.WriteStartObject "counts"
  writer.WriteNumber("nodes", manifest.Counts.Nodes)
  writer.WriteNumber("edges", manifest.Counts.Edges)
  writer.WriteNumber("strings", manifest.Counts.Strings)
  writer.WriteNumber("stringBytes", manifest.Counts.StringBytes)
  writer.WriteNumber("directories", manifest.Counts.Directories)
  writer.WriteNumber("files", manifest.Counts.Files)
  writer.WriteEndObject()

  writeSegments writer manifest.Segments

  writer.WriteStartArray "diagnostics"

  for diagnostic in manifest.Diagnostics do
    writer.WriteStartObject()
    writer.WriteString("kind", diagnostic.Kind)
    writer.WriteNumber("count", diagnostic.Count)
    writer.WriteEndObject()

  writer.WriteEndArray()
  writer.WriteEndObject()
  writer.Flush()
  buffer.ToArray()

/// マニフェストを原子的に差し替える。
///
/// 一時ファイルへ書いてから置換することで、読み手が半端な内容を見ることがない。
/// Windows は開いているファイルの削除・改名に制約があるため、置換 API を使う。
/// docs/platform-and-i18n.md 5 を参照。
let write (outputDirectory: string) (manifest: Manifest) =
  Directory.CreateDirectory outputDirectory |> ignore
  let destination = Path.Combine(outputDirectory, FileName)
  let temporary = destination + ".tmp"
  let payload = serialize manifest
  File.WriteAllBytes(temporary, payload)
  File.Move(temporary, destination, true)

/// 生成中の成果物を置く場所。
let stagingPath (outputDirectory: string) =
  Path.Combine(outputDirectory, StagingDirectory)

/// 後片付け用の削除。失敗しても成果物の整合性に影響させないため、握り潰してよい。
let private deleteQuietly (directory: string) =
  if Directory.Exists directory then
    try
      Directory.Delete(directory, true)
    with
    | :? IOException -> ()
    | :? UnauthorizedAccessException -> ()

/// 生成に失敗した場合に、書きかけの成果物を捨てる。
let discardStaging (outputDirectory: string) = deleteQuietly (stagingPath outputDirectory)

/// 完成した成果物を公開する。
///
/// 手順は「旧セグメントの退避 → 新セグメントの設置 → マニフェストの原子的置換 → 後片付け」。
/// 読み手はマニフェストを通してのみセグメント集合を見るため、置換が完了するまで
/// 一貫した旧世代を、完了後は一貫した新世代を見る。
/// 後片付けを置換より後に置くのは、掃除の失敗で成果物を壊さないためである。
let publish (outputDirectory: string) (manifest: Manifest) =
  let staging = stagingPath outputDirectory
  let stagedSegments = Path.Combine(staging, SegmentDirectory)
  let liveSegments = Path.Combine(outputDirectory, SegmentDirectory)
  let retired = Path.Combine(outputDirectory, StagingDirectory + ".retired")

  if Directory.Exists stagedSegments then
    // 退避先の残骸を先に片付ける。ここで失敗しても既存の成果物は無傷のままである。
    deleteQuietly retired
    if Directory.Exists retired then raise (IOException $"退避先を片付けられません: {retired}")

    let retiredLive = Directory.Exists liveSegments
    if retiredLive then Directory.Move(liveSegments, retired)

    try
      Directory.Move(stagedSegments, liveSegments)
    with _ ->
      // 新世代の設置に失敗したら旧世代を戻し、成果物を消失させない。
      if retiredLive && not (Directory.Exists liveSegments) then Directory.Move(retired, liveSegments)
      reraise ()

  write outputDirectory manifest
  deleteQuietly retired
  discardStaging outputDirectory

let private requireProperty (element: JsonElement) (name: string) =
  match element.TryGetProperty name with
  | true, value -> Ok value
  | false, _ -> Error(Malformed $"必須の項目 `{name}` がありません")

let private readSegments (element: JsonElement) =
  let segments = List<Writer.SegmentDescriptor>()
  let mutable failure = ValueNone

  for item in element.EnumerateArray() do
    match requireProperty item "name", requireProperty item "byteLength", requireProperty item "blake3" with
    | Ok name, Ok byteLength, Ok checksum ->
      match name.GetString(), checksum.GetString() with
      | null, _
      | _, null -> failure <- ValueSome(Malformed "セグメント項目の文字列が null です")
      | nameValue, checksumValue ->
        segments.Add
          { Name = nameValue
            ByteLength = byteLength.GetInt64()
            Checksum = checksumValue }
    | _ -> failure <- ValueSome(Malformed "セグメント項目の形式が不正です")

  match failure with
  | ValueSome error -> Error error
  | ValueNone -> Ok(segments.ToArray())

let read (outputDirectory: string) : Result<Manifest, ManifestError> =
  let path = Path.Combine(outputDirectory, FileName)

  if not (File.Exists path) then Error(NotFound path)
  else

  try
    use document = JsonDocument.Parse(File.ReadAllBytes path)
    let rootElement = document.RootElement

    let manifestVersion =
      match rootElement.TryGetProperty "manifestVersion" with
      | true, value -> value.GetUInt32()
      | false, _ -> 0u

    if manifestVersion <> ManifestVersion then Error(UnsupportedManifestVersion manifestVersion)
    else

    let formatVersion =
      match rootElement.TryGetProperty "formatVersion" with
      | true, value -> value.GetUInt32()
      | false, _ -> 0u

    if formatVersion <> Format.FormatVersion then Error(UnsupportedFormatVersion formatVersion)
    else

    match requireProperty rootElement "segments" with
    | Error error -> Error error
    | Ok segmentsElement ->

    match readSegments segmentsElement with
    | Error error -> Error error
    | Ok segments ->

    let readString name fallback =
      match rootElement.TryGetProperty(name: string) with
      | true, value ->
        match value.GetString() with
        | null -> fallback
        | text -> text
      | false, _ -> fallback

    let readObject name = rootElement.TryGetProperty(name: string)

    let counts =
      match readObject "counts" with
      | true, element ->
        let number (name: string) =
          match element.TryGetProperty name with
          | true, value -> value.GetInt64()
          | false, _ -> 0L

        { Nodes = int (number "nodes")
          Edges = int (number "edges")
          Strings = int (number "strings")
          StringBytes = number "stringBytes"
          Directories = int (number "directories")
          Files = int (number "files") }
      | false, _ ->
        { Nodes = 0
          Edges = 0
          Strings = 0
          StringBytes = 0L
          Directories = 0
          Files = 0 }

    let options =
      match readObject "options" with
      | true, element ->
        let flag (name: string) =
          match element.TryGetProperty name with
          | true, value -> value.GetBoolean()
          | false, _ -> false

        let number (name: string) =
          match element.TryGetProperty name with
          | true, value -> value.GetInt64()
          | false, _ -> 0L

        { FollowSymbolicLinks = flag "followSymbolicLinks"
          RespectIgnoreFiles = flag "respectIgnoreFiles"
          MaxDepth = int (number "maxDepth")
          MaxFileSizeBytes = number "maxFileSizeBytes" }
      | false, _ ->
        { FollowSymbolicLinks = false
          RespectIgnoreFiles = true
          MaxDepth = 0
          MaxFileSizeBytes = 0L }

    let diagnostics =
      match readObject "diagnostics" with
      | true, element ->
        [| for item in element.EnumerateArray() do
             let kind =
               match item.TryGetProperty "kind" with
               | true, value ->
                 match value.GetString() with
                 | null -> ""
                 | text -> text
               | false, _ -> ""

             let count =
               match item.TryGetProperty "count" with
               | true, value -> value.GetInt32()
               | false, _ -> 0

             { Kind = kind; Count = count } |]
      | false, _ -> Array.empty

    Ok
      { ManifestVersion = manifestVersion
        FormatVersion = formatVersion
        ToolVersion = readString "toolVersion" "0.0.0"
        RepositoryId = readString "repositoryId" ""
        Complete =
          (match rootElement.TryGetProperty "complete" with
           | true, value -> value.GetBoolean()
           | false, _ -> false)
        Options = options
        Counts = counts
        Segments = segments
        Diagnostics = diagnostics }
  with
  | :? JsonException as ex -> Error(Malformed ex.Message)
  | :? IOException as ex -> Error(Malformed ex.Message)
