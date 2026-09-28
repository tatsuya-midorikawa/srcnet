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
open System.Text
open System.Text.Json
open System.Threading
open Srcnet.Core

/// マニフェストの構造の版。セグメント形式の版とは独立に管理する。
///
/// 2: セグメント名へ世代を含め、チェックサムのアルゴリズムを SHA-256 にした。
/// 3: シンボルと参照候補を載せ、抽出段階・文法の版・種別ごとの件数を記録した（M2）。
[<Literal>]
let ManifestVersion = 3u

[<Literal>]
let FileName = "manifest.json"

/// マニフェストを差し替えるときの一時ファイルの拡張子。
[<Literal>]
let TemporarySuffix = ".tmp"

/// セグメントのチェックサムを載せる項目名。アルゴリズムを名前で明示する。
[<Literal>]
let AlgorithmField = Hashing.AlgorithmName

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
  {
    Nodes: int
    Edges: int
    Strings: int
    StringBytes: int64
    Directories: int
    Files: int
    /// 抽出したシンボル ノードの数。ファイルに属さないノードは含まない。
    Symbols: int
    /// 未解決の参照候補の数。
    ReferenceCandidates: int
    /// ノード種別ごとの件数。種別名の序数昇順。
    NodeKinds: Writer.KindCount[]
    /// エッジ種別ごとの件数。種別名の序数昇順。
    EdgeKinds: Writer.KindCount[]
  }

/// 同梱している文法 1 つ分。実行ファイルへコンパイル時に埋め込んだ値を記録する。
/// 対象リポジトリからも構成ファイルからも読み込まない（docs/security.md C-1）。
type GrammarRecord =
  {
    Language: string
    Version: string
    /// 取得元アーカイブの SHA-256（16 進小文字）。
    Sha256: string
  }

type IndexOptions =
  {
    FollowSymbolicLinks: bool
    RespectIgnoreFiles: bool
    MaxDepth: int
    MaxFileSizeBytes: int64
    /// 実際に適用した抽出段階（0..2）。
    Tier: int
    /// 要求段階。旧成果物には存在しないため、省略と明示値を区別する。
    RequestedTier: int voption
    /// 構文解析器を利用できたか。段階が下がった理由を成果物から説明できるようにする。
    ParserAvailable: bool
    /// 同梱している文法の一覧。言語名の序数昇順。
    Grammars: GrammarRecord[]
    /// 符号化が曖昧なファイルへ適用した符号化。指定がなければ空文字列。
    /// 利用者が持ち込んだ事実であり、成果物の内容を決める条件の一部になる。
    AssumedEncoding: string
  }

type DiagnosticCount = { Kind: string; Count: int }

type ExtractionCount =
  { Language: string
    Tier: int
    Reason: string
    Count: int
    SyntaxSupported: bool
    GrammarAvailable: bool
    Examples: string[] }

type Manifest =
  {
    ManifestVersion: uint32
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
    Diagnostics: DiagnosticCount[]
    Extraction: ExtractionCount[] voption
  }

type ManifestError =
  | NotFound of path: string
  | Malformed of detail: string
  | UnsupportedManifestVersion of found: uint32
  | UnsupportedFormatVersion of found: uint32
  /// 読取中に公開が繰り返され、一貫した世代を観測できなかった。
  | Busy of path: string

let segmentFileNames (directory: string) (manifest: Manifest) : ISet<string> =
  let names = HashSet<string>(StringComparer.Ordinal)

  for segment in manifest.Segments do
    names.Add(Path.GetFullPath(Path.Combine(directory, segment.Name.Replace('/', Path.DirectorySeparatorChar))))
    |> ignore

  names

module ManifestError =

  let describe error =
    match error with
    | NotFound path -> $"生成物が見つかりません: {path}"
    | Malformed detail -> $"マニフェストを解釈できません: {detail}"
    | UnsupportedManifestVersion found -> $"マニフェスト版 {found} は未対応です (対応版 {ManifestVersion})"
    | UnsupportedFormatVersion found -> $"セグメント形式版 {found} は未対応です (対応版 {Format.FormatVersion})"
    | Busy path -> $"成果物が更新され続けているため、一貫した世代を読めませんでした: {path}"

let private writerOptions =
  // 改行を明示しないと OS 既定の改行が使われ、成果物が OS 依存になる。
  JsonWriterOptions(Indented = true, IndentCharacter = ' ', IndentSize = 2, NewLine = "\n")

let private writeSegments (writer: Utf8JsonWriter) (segments: Writer.SegmentDescriptor[]) =
  writer.WriteStartArray "segments"

  for segment in segments do
    writer.WriteStartObject()
    writer.WriteString("name", segment.Name)
    writer.WriteNumber("byteLength", segment.ByteLength)
    writer.WriteString(AlgorithmField, segment.Checksum)
    writer.WriteEndObject()

  writer.WriteEndArray()

let serialize (manifest: Manifest) =
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
  writer.WriteNumber("tier", manifest.Options.Tier)

  match manifest.Options.RequestedTier with
  | ValueSome tier -> writer.WriteNumber("requestedTier", tier)
  | ValueNone -> ()

  writer.WriteBoolean("parserAvailable", manifest.Options.ParserAvailable)
  writer.WriteString("assumedEncoding", manifest.Options.AssumedEncoding)
  writer.WriteStartArray "grammars"

  for grammar in manifest.Options.Grammars do
    writer.WriteStartObject()
    writer.WriteString("language", grammar.Language)
    writer.WriteString("version", grammar.Version)
    writer.WriteString("sha256", grammar.Sha256)
    writer.WriteEndObject()

  writer.WriteEndArray()
  writer.WriteEndObject()

  writer.WriteStartObject "counts"
  writer.WriteNumber("nodes", manifest.Counts.Nodes)
  writer.WriteNumber("edges", manifest.Counts.Edges)
  writer.WriteNumber("strings", manifest.Counts.Strings)
  writer.WriteNumber("stringBytes", manifest.Counts.StringBytes)
  writer.WriteNumber("directories", manifest.Counts.Directories)
  writer.WriteNumber("files", manifest.Counts.Files)
  writer.WriteNumber("symbols", manifest.Counts.Symbols)
  writer.WriteNumber("referenceCandidates", manifest.Counts.ReferenceCandidates)
  writer.WriteStartArray "nodeKinds"

  for entry in manifest.Counts.NodeKinds do
    writer.WriteStartObject()
    writer.WriteString("kind", entry.Kind)
    writer.WriteNumber("count", entry.Count)
    writer.WriteEndObject()

  writer.WriteEndArray()
  writer.WriteStartArray "edgeKinds"

  for entry in manifest.Counts.EdgeKinds do
    writer.WriteStartObject()
    writer.WriteString("kind", entry.Kind)
    writer.WriteNumber("count", entry.Count)
    writer.WriteEndObject()

  writer.WriteEndArray()
  writer.WriteEndObject()

  writeSegments writer manifest.Segments

  writer.WriteStartArray "diagnostics"

  for diagnostic in manifest.Diagnostics do
    writer.WriteStartObject()
    writer.WriteString("kind", diagnostic.Kind)
    writer.WriteNumber("count", diagnostic.Count)
    writer.WriteEndObject()

  writer.WriteEndArray()

  match manifest.Extraction with
  | ValueNone -> ()
  | ValueSome coverage ->
    writer.WriteStartArray "extraction"

    for entry in coverage do
      writer.WriteStartObject()
      writer.WriteString("language", entry.Language)
      writer.WriteNumber("tier", entry.Tier)
      writer.WriteString("reason", entry.Reason)
      writer.WriteNumber("count", entry.Count)
      writer.WriteBoolean("syntaxSupported", entry.SyntaxSupported)
      writer.WriteBoolean("grammarAvailable", entry.GrammarAvailable)
      writer.WriteStartArray "examples"

      for path in entry.Examples do
        writer.WriteStringValue path

      writer.WriteEndArray()
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
///
/// 一時ファイルは既存の項目を追跡せず排他的に作る。`FileMode.Create` は既存のリンクを
/// 辿ってリンク先を上書きしてしまうため、ここでは使えない。
let write (outputDirectory: string) (manifest: Manifest) : Result<unit, Artifact.PathError> =
  let destination = Path.Combine(outputDirectory, FileName)
  let temporary = destination + TemporarySuffix

  match Artifact.ensureNotLink destination with
  | Error error -> Error error
  | Ok() ->

    match Artifact.ensureNotLink temporary with
    | Error error -> Error error
    | Ok() ->

      try
        Directory.CreateDirectory outputDirectory |> ignore
        let payload = serialize manifest
        File.Delete temporary

        do
          use stream =
            new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)

          stream.Write(ReadOnlySpan payload)
          stream.Flush()

        Artifact.replaceFile temporary destination
        Ok()
      with
      | :? IOException as ex -> Error(Artifact.Unavailable(destination, ex.Message))
      | :? UnauthorizedAccessException -> Error(Artifact.Unavailable(destination, "書き込む権限がありません"))

/// 生成中の成果物を置く場所。
let stagingPath (outputDirectory: string) =
  Path.Combine(outputDirectory, StagingDirectory)

/// 生成中のセグメントを置く場所。
let stagedSegmentsPath (outputDirectory: string) =
  Path.Combine(stagingPath outputDirectory, Artifact.SegmentDirectory)

/// Hold this lease from staging preparation through publication and cleanup.
/// Keep the empty lock file: unlinking it would let another writer lock a new inode.
let acquireWriter (outputDirectory: string) : Result<IDisposable, Artifact.PathError> =
  let path = Path.Combine(outputDirectory, ".writer.lock")

  match Artifact.ensureNotLink path with
  | Error error -> Error error
  | Ok() ->
    try
      let stream =
        new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

      if stream.CanSeek then
        Ok(stream :> IDisposable)
      else
        stream.Dispose()
        Error(Artifact.Unavailable(path, "書き込みロックが通常ファイルではありません"))
    with
    | :? IOException as ex -> Error(Artifact.Unavailable(path, $"別の索引生成が実行中か、書き込みロックを取得できません: {ex.Message}"))
    | :? UnauthorizedAccessException -> Error(Artifact.Unavailable(path, "書き込む権限がありません"))

/// リンク自身だけを取り除く。掃除でリンク先の管理外ファイルを消してはならない。
///
/// Unix のシンボリック リンクは `unlink` で消えるが、Windows のディレクトリ
/// reparse point は `DeleteFile` では消せず `RemoveDirectory` が要る。両方を試す。
/// 消し損ねたリンクをそのまま使うと、次の書き込みがリンク先へ届いてしまう。
let private deleteLink (path: string) =
  try
    File.Delete path
  with
  | :? IOException
  | :? UnauthorizedAccessException ->
    // 再帰は指定しない。reparse point 自体を外すだけで、リンク先は辿らせない。
    Directory.Delete(path, false)

/// 後片付け用の削除。失敗しても成果物の整合性に影響させないため、握り潰してよい。
let private deleteQuietly (path: string) =
  try
    if Artifact.isLink path then
      deleteLink path
    elif Directory.Exists path then
      Directory.Delete(path, true)
    elif File.Exists path then
      File.Delete path
  with
  | :? IOException -> ()
  | :? UnauthorizedAccessException -> ()

/// 生成に失敗した場合に、書きかけの成果物を捨てる。
let discardStaging (outputDirectory: string) =
  deleteQuietly(stagingPath outputDirectory)

/// 生成中の領域を用意し、セグメントの書き出し先を返す。
///
/// 片付けは best-effort なので、リンクを消し損ねたまま書くとリンク先へ書いてしまう。
/// 片付けたうえで、staging とその中のセグメント ディレクトリがリンクでないことを必ず確かめる。
let prepareStaging (outputDirectory: string) : Result<string, Artifact.PathError> =
  discardStaging outputDirectory

  match Artifact.ensureNotLink(stagingPath outputDirectory) with
  | Error error -> Error error
  | Ok() ->
    let segments = stagedSegmentsPath outputDirectory

    match Artifact.ensureNotLink segments with
    | Error error -> Error error
    | Ok() -> Ok segments

/// 世代識別子。セグメントの名前、長さ、チェックサムだけから決まる。
///
/// 世代ごとに不変のパスへ置くことで、公開の切替点をマニフェストの置換だけにできる。
/// 識別子を内容から導くのは、同じ入力から二度生成した成果物がバイト単位で一致する
/// という決定性の要件を保つためである。時刻や連番では一致しなくなる。
let generationOf (segments: Writer.SegmentDescriptor[]) =
  let ordered = Array.copy segments

  Array.sortInPlaceWith
    (fun (left: Writer.SegmentDescriptor) (right: Writer.SegmentDescriptor) ->
      String.CompareOrdinal(left.Name, right.Name))
    ordered

  let builder = StringBuilder()

  for segment in ordered do
    builder
      .Append(segment.Name)
      .Append('\u0000')
      .Append(segment.ByteLength.ToString Globalization.CultureInfo.InvariantCulture)
      .Append('\u0000')
      .Append(segment.Checksum)
      .Append('\n')
    |> ignore

  let digest = Hashing.hash(ReadOnlySpan(Encoding.UTF8.GetBytes(builder.ToString())))
  Convert.ToHexStringLower(ReadOnlySpan(digest, 0, Artifact.GenerationLength / 2))

/// 世代を含む最終的なセグメント名を与える。
let qualify (generation: string) (segments: Writer.SegmentDescriptor[]) =
  segments
  |> Array.map(fun segment ->
    { segment with
        Name = $"{Artifact.SegmentDirectory}/{generation}/{segment.Name}" })

/// マニフェストが参照している世代。セグメントを持たない成果物では `ValueNone`。
let generationIn (manifest: Manifest) =
  if manifest.Segments.Length = 0 then
    ValueNone
  else
    let parts = manifest.Segments[0].Name.Split '/'
    if parts.Length = 3 then ValueSome parts[1] else ValueNone

// --- 読み取り ---------------------------------------------------------------

/// 世代の切替と競合したときに読み直す回数の上限。
/// 公開は有限時間で終わるため、上限に達するのは異常な更新頻度のときだけである。
[<Literal>]
let private StableReadAttempts = 8

// 成果物は破損しているか、悪意をもって改変されている可能性がある外部入力である。
// 値の種別、必須性、範囲、型固有の不変条件をすべて確かめてから領域型へ変換する。
// 想定内の破損は例外ではなくドメイン エラーにする。docs/security.md C-6 を参照。

let private requireProperty (element: JsonElement) (name: string) =
  match element.TryGetProperty name with
  | true, value -> Ok value
  | false, _ -> Error(Malformed $"必須の項目 `{name}` がありません")

let private requireKind (element: JsonElement) (name: string) (kind: JsonValueKind) =
  match requireProperty element name with
  | Error error -> Error error
  | Ok value ->
    if value.ValueKind = kind then
      Ok value
    else
      Error(Malformed $"項目 `{name}` の種別が {value.ValueKind} です ({kind} が必要です)")

let private requireObject (element: JsonElement) (name: string) =
  requireKind element name JsonValueKind.Object

let private requireArray (element: JsonElement) (name: string) =
  requireKind element name JsonValueKind.Array

let private requireString (element: JsonElement) (name: string) =
  match requireKind element name JsonValueKind.String with
  | Error error -> Error error
  | Ok value ->
    match value.GetString() with
    | null -> Error(Malformed $"項目 `{name}` が null です")
    | text -> Ok text

let private requireBoolean (element: JsonElement) (name: string) =
  match requireProperty element name with
  | Error error -> Error error
  | Ok value ->
    match value.ValueKind with
    | JsonValueKind.True -> Ok true
    | JsonValueKind.False -> Ok false
    | kind -> Error(Malformed $"項目 `{name}` の種別が {kind} です (真偽値が必要です)")

/// 整数を範囲付きで読む。`GetInt64` は小数や範囲外で例外を投げるため、
/// `TryGetInt64` で受けてからドメイン エラーへ変換する。
let private requireInt64 (element: JsonElement) (name: string) (lower: int64) (upper: int64) =
  match requireKind element name JsonValueKind.Number with
  | Error error -> Error error
  | Ok value ->
    match value.TryGetInt64() with
    | true, number when number >= lower && number <= upper -> Ok number
    | true, number -> Error(Malformed $"項目 `{name}` の値 {number} が範囲 [{lower}, {upper}] の外です")
    | false, _ -> Error(Malformed $"項目 `{name}` が整数ではありません")

/// 32 ビットへ収まることを確かめてから縮める。暗黙の切り捨てを起こさない。
let private requireInt32 (element: JsonElement) (name: string) =
  match requireInt64 element name 0L (int64 Int32.MaxValue) with
  | Error error -> Error error
  | Ok number -> Ok(Checked.int number)

let private requireUInt32 (element: JsonElement) (name: string) =
  match requireInt64 element name 0L (int64 UInt32.MaxValue) with
  | Error error -> Error error
  | Ok number -> Ok(Checked.uint32 number)

let private isHexLower (c: char) =
  (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

let private readSegments (element: JsonElement) =
  let segments = List<Writer.SegmentDescriptor>()
  let mutable failure = ValueNone

  for item in element.EnumerateArray() do
    if failure.IsNone then
      if item.ValueKind <> JsonValueKind.Object then
        failure <- ValueSome(Malformed "セグメント項目がオブジェクトではありません")
      else
        let result =
          match
            requireString item "name",
            requireInt64 item "byteLength" 0L Int64.MaxValue,
            requireString item AlgorithmField
          with
          | Ok name, Ok byteLength, Ok checksum ->
            // 名前は成果物ディレクトリからの相対パスとしてのみ意味を持つ。
            // ここで形を狭く固定し、パス変換の前に成果物外への参照を断つ。
            match Artifact.validateSegmentName name with
            | Error detail -> Error(Malformed $"セグメント名が不正です ({detail}): {name}")
            | Ok() ->
              if
                checksum.Length <> Hashing.HashLength * 2
                || not(String.forall isHexLower checksum)
              then
                Error(Malformed $"{name}: チェックサムが 16 進小文字 {Hashing.HashLength * 2} 桁ではありません")
              else
                // `Writer` には `Name` を持つレコードが複数あるため、型を明示して選ぶ。
                let descriptor: Writer.SegmentDescriptor =
                  { Name = name
                    ByteLength = byteLength
                    Checksum = checksum }

                Ok descriptor
          | Error error, _, _
          | _, Error error, _
          | _, _, Error error -> Error error

        match result with
        | Ok descriptor -> segments.Add descriptor
        | Error error -> failure <- ValueSome error

  match failure with
  | ValueSome error -> Error error
  | ValueNone ->
    let names = HashSet<string>(StringComparer.Ordinal)

    for segment in segments do
      if not(names.Add segment.Name) then
        failure <- ValueSome(Malformed "Duplicate segment name")

    for segment in segments do
      let marker = segment.Name.LastIndexOf(".part-", StringComparison.Ordinal)

      if marker >= 0 then
        let root = segment.Name.Substring(0, marker)
        let suffix = segment.Name.Substring(marker + 6)

        match Int32.TryParse(suffix, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
        | true, index when suffix.Length = 6 && index > 0 && index < Reader.MaxParts ->
          if
            not(names.Contains root)
            || not(names.Contains(Reader.partPath root (index - 1)))
          then
            failure <- ValueSome(Malformed "Missing segment root or preceding part")
        | _ -> failure <- ValueSome(Malformed "Invalid segment part name")

    match failure with
    | ValueSome error -> Error error
    | ValueNone -> Ok(segments.ToArray())

/// 種別ごとの件数を読む。並びは書き出し側で名前の序数昇順に固定されている。
let private readKindCounts (element: JsonElement) (name: string) =
  match requireArray element name with
  | Error error -> Error error
  | Ok array ->
    let entries = List<Writer.KindCount>()
    let mutable failure = ValueNone

    for item in array.EnumerateArray() do
      if failure.IsNone then
        if item.ValueKind <> JsonValueKind.Object then
          failure <- ValueSome(Malformed $"{name} の項目がオブジェクトではありません")
        else
          match requireString item "kind", requireInt32 item "count" with
          | Ok kind, Ok count -> entries.Add { Kind = kind; Count = count }
          | Error error, _
          | _, Error error -> failure <- ValueSome error

    match failure with
    | ValueSome error -> Error error
    | ValueNone -> Ok(entries.ToArray())

let private nodeKindNames =
  HashSet<string>(
    [| for code in 0..255 do
         match Graph.NodeKind.ofCode(byte code) with
         | ValueSome kind -> yield Graph.NodeKind.name kind
         | ValueNone -> () |],
    StringComparer.Ordinal
  )

let private edgeKindNames =
  HashSet<string>(Graph.EdgeKind.all |> Array.map Graph.EdgeKind.name, StringComparer.Ordinal)

let private validateKindCounts name total (known: HashSet<string>) (entries: Writer.KindCount[]) =
  let seen = HashSet<string>(StringComparer.Ordinal)
  let mutable sum = 0L
  let mutable failure = ValueNone

  for entry in entries do
    if not(known.Contains entry.Kind) || not(seen.Add entry.Kind) then
      failure <- ValueSome(Malformed $"{name} に未知または重複した種別があります: {entry.Kind}")

    sum <- sum + int64 entry.Count

  match failure with
  | ValueSome error -> Error error
  | ValueNone when sum <> int64 total -> Error(Malformed $"{name} の合計 {sum} が件数 {total} と一致しません")
  | ValueNone -> Ok()

let private validateEdgeSegments (segments: Writer.SegmentDescriptor[]) (kinds: Writer.KindCount[]) =
  let expected = HashSet<string>(StringComparer.Ordinal)
  let found = HashSet<string>(StringComparer.Ordinal)
  let mutable failure = ValueNone

  for kind in kinds do
    expected.Add(".edges." + kind.Kind) |> ignore
    expected.Add(".redges." + kind.Kind) |> ignore

  for segment in segments do
    let name = segment.Name.Split('/')[2]

    for marker in [| ".edges."; ".redges." |] do
      let index = name.IndexOf(marker, StringComparison.Ordinal)

      if index >= 0 && not(name.Contains(".part-", StringComparison.Ordinal)) then
        let role = name.Substring index

        if not(expected.Contains role) || not(found.Add role) then
          failure <- ValueSome(Malformed $"未宣言または重複した CSR セグメントです: {segment.Name}")

  match failure with
  | ValueSome error -> Error error
  | ValueNone when not(found.SetEquals expected) -> Error(Malformed "エッジ種別の前方・後方 CSR セグメントが揃っていません")
  | ValueNone -> Ok()

let private readCounts (element: JsonElement) =
  match
    requireInt32 element "nodes",
    requireInt32 element "edges",
    requireInt32 element "strings",
    requireInt64 element "stringBytes" 0L Int64.MaxValue,
    requireInt32 element "directories",
    requireInt32 element "files"
  with
  | Ok nodes, Ok edges, Ok strings, Ok stringBytes, Ok directories, Ok files ->
    match
      requireInt32 element "symbols",
      requireInt32 element "referenceCandidates",
      readKindCounts element "nodeKinds",
      readKindCounts element "edgeKinds"
    with
    | Ok symbols, Ok referenceCandidates, Ok nodeKinds, Ok edgeKinds ->
      match validateKindCounts "nodeKinds" nodes nodeKindNames nodeKinds with
      | Error error -> Error error
      | Ok() ->
        match validateKindCounts "edgeKinds" edges edgeKindNames edgeKinds with
        | Error error -> Error error
        | Ok() ->
          Ok
            { Nodes = nodes
              Edges = edges
              Strings = strings
              StringBytes = stringBytes
              Directories = directories
              Files = files
              Symbols = symbols
              ReferenceCandidates = referenceCandidates
              NodeKinds = nodeKinds
              EdgeKinds = edgeKinds }
    | Error error, _, _, _
    | _, Error error, _, _
    | _, _, Error error, _
    | _, _, _, Error error -> Error error
  | Error error, _, _, _, _, _
  | _, Error error, _, _, _, _
  | _, _, Error error, _, _, _
  | _, _, _, Error error, _, _
  | _, _, _, _, Error error, _
  | _, _, _, _, _, Error error -> Error error

let private readGrammars (element: JsonElement) =
  match requireArray element "grammars" with
  | Error error -> Error error
  | Ok array ->
    let entries = List<GrammarRecord>()
    let mutable failure = ValueNone

    for item in array.EnumerateArray() do
      if failure.IsNone then
        if item.ValueKind <> JsonValueKind.Object then
          failure <- ValueSome(Malformed "文法の項目がオブジェクトではありません")
        else
          match requireString item "language", requireString item "version", requireString item "sha256" with
          | Ok language, Ok version, Ok sha256 ->
            entries.Add
              { Language = language
                Version = version
                Sha256 = sha256 }
          | Error error, _, _
          | _, Error error, _
          | _, _, Error error -> failure <- ValueSome error

    match failure with
    | ValueSome error -> Error error
    | ValueNone -> Ok(entries.ToArray())

let private readOptions (element: JsonElement) =
  match
    requireBoolean element "followSymbolicLinks",
    requireBoolean element "respectIgnoreFiles",
    requireInt64 element "maxDepth" 0L (int64 Paths.MaxDepth),
    requireInt64 element "maxFileSizeBytes" 0L Int64.MaxValue
  with
  | Ok followSymbolicLinks, Ok respectIgnoreFiles, Ok maxDepth, Ok maxFileSizeBytes ->
    match requireInt64 element "tier" 0L 2L, requireBoolean element "parserAvailable", readGrammars element with
    | Ok tier, Ok parserAvailable, Ok grammars ->
      let requested =
        match element.TryGetProperty "requestedTier" with
        | false, _ -> Ok ValueNone
        | true, _ -> requireInt64 element "requestedTier" tier 2L |> Result.map(int >> ValueSome)

      match requested with
      | Error error -> Error error
      | Ok requestedTier ->
        match requireString element "assumedEncoding" with
        | Error error -> Error error
        | Ok assumedEncoding when
          assumedEncoding <> ""
          && (Srcnet.Text.Encodings.tryParse assumedEncoding |> ValueOption.isNone)
          ->
          Error(Malformed "assumedEncoding が対応する符号化名ではありません")
        | Ok assumedEncoding ->
          Ok
            { FollowSymbolicLinks = followSymbolicLinks
              RespectIgnoreFiles = respectIgnoreFiles
              MaxDepth = int maxDepth
              MaxFileSizeBytes = maxFileSizeBytes
              Tier = int tier
              RequestedTier = requestedTier
              ParserAvailable = parserAvailable
              Grammars = grammars
              AssumedEncoding = assumedEncoding }
    | Error error, _, _
    | _, Error error, _
    | _, _, Error error -> Error error
  | Error error, _, _, _
  | _, Error error, _, _
  | _, _, Error error, _
  | _, _, _, Error error -> Error error

let private readDiagnostics (element: JsonElement) =
  let diagnostics = List<DiagnosticCount>()
  let mutable failure = ValueNone

  for item in element.EnumerateArray() do
    if failure.IsNone then
      if item.ValueKind <> JsonValueKind.Object then
        failure <- ValueSome(Malformed "診断項目がオブジェクトではありません")
      else
        match requireString item "kind", requireInt32 item "count" with
        | Ok kind, Ok count -> diagnostics.Add { Kind = kind; Count = count }
        | Error error, _
        | _, Error error -> failure <- ValueSome error

  match failure with
  | ValueSome error -> Error error
  | ValueNone -> Ok(diagnostics.ToArray())

let private readExtraction (root: JsonElement) fileCount =
  match root.TryGetProperty "extraction" with
  | false, _ -> Ok ValueNone
  | true, value when value.ValueKind <> JsonValueKind.Array || value.GetArrayLength() > 512 ->
    Error(Malformed "extraction must be an array of at most 512 entries")
  | true, values ->
    let rows = ResizeArray<ExtractionCount>()
    let keys = HashSet<struct (string * int * string)>()
    let mutable total = 0L
    let mutable failure = ValueNone

    for item in values.EnumerateArray() do
      if failure.IsNone then
        if item.ValueKind <> JsonValueKind.Object then
          failure <- ValueSome(Malformed "invalid extraction entry")
        else
          match
            requireString item "language",
            requireInt64 item "tier" 0L 2L,
            requireString item "reason",
            requireInt32 item "count",
            requireBoolean item "syntaxSupported",
            requireBoolean item "grammarAvailable",
            requireArray item "examples"
          with
          | Ok language, Ok tier, Ok reason, Ok count, Ok supported, Ok grammar, Ok examples ->
            let paths = ResizeArray<string>()

            if
              language.Length > 64
              || reason.Length > 64
              || count = 0
              || examples.GetArrayLength() > 2
              || not(keys.Add(struct (language, int tier, reason)))
            then
              failure <- ValueSome(Malformed "invalid extraction counts or examples")
            else
              for path in examples.EnumerateArray() do
                if path.ValueKind <> JsonValueKind.String then
                  failure <- ValueSome(Malformed "invalid extraction example")
                else
                  match path.GetString() with
                  | null -> failure <- ValueSome(Malformed "null extraction example")
                  | text when text.Length > 512 ->
                    failure <- ValueSome(Malformed "extraction example exceeds 512 characters")
                  | text -> paths.Add text

              total <- total + int64 count

              rows.Add
                { Language = language
                  Tier = int tier
                  Reason = reason
                  Count = count
                  SyntaxSupported = supported
                  GrammarAvailable = grammar
                  Examples = paths.ToArray() }
          | Error error, _, _, _, _, _, _
          | _, Error error, _, _, _, _, _
          | _, _, Error error, _, _, _, _
          | _, _, _, Error error, _, _, _
          | _, _, _, _, Error error, _, _
          | _, _, _, _, _, Error error, _
          | _, _, _, _, _, _, Error error -> failure <- ValueSome error

    match failure with
    | ValueSome error -> Error error
    | ValueNone when total <> int64 fileCount -> Error(Malformed "extraction counts do not match files")
    | ValueNone -> Ok(ValueSome(rows.ToArray()))

let private parse (payload: byte[]) : Result<Manifest, ManifestError> =
  try
    use document = JsonDocument.Parse(ReadOnlyMemory payload)
    let rootElement = document.RootElement

    if rootElement.ValueKind <> JsonValueKind.Object then
      Error(Malformed "最上位がオブジェクトではありません")
    else

      // 版の判定は他の項目より先に行う。非互換な成果物を、破損として報告しないためである。
      match requireUInt32 rootElement "manifestVersion" with
      | Error _ -> Error(UnsupportedManifestVersion 0u)
      | Ok manifestVersion ->

        if manifestVersion <> ManifestVersion then
          Error(UnsupportedManifestVersion manifestVersion)
        else

          match requireUInt32 rootElement "formatVersion" with
          | Error _ -> Error(UnsupportedFormatVersion 0u)
          | Ok formatVersion ->

            if formatVersion <> Format.FormatVersion then
              Error(UnsupportedFormatVersion formatVersion)
            else

              match requireArray rootElement "segments" with
              | Error error -> Error error
              | Ok segmentsElement ->

                match readSegments segmentsElement with
                | Error error -> Error error
                | Ok segments ->

                  match requireObject rootElement "counts" with
                  | Error error -> Error error
                  | Ok countsElement ->

                    match readCounts countsElement with
                    | Error error -> Error error
                    | Ok counts ->

                      match validateEdgeSegments segments counts.EdgeKinds with
                      | Error error -> Error error
                      | Ok() ->

                        match requireObject rootElement "options" with
                        | Error error -> Error error
                        | Ok optionsElement ->

                          match readOptions optionsElement with
                          | Error error -> Error error
                          | Ok options ->

                            match requireArray rootElement "diagnostics" with
                            | Error error -> Error error
                            | Ok diagnosticsElement ->

                              match readDiagnostics diagnosticsElement with
                              | Error error -> Error error
                              | Ok diagnostics ->

                                match readExtraction rootElement counts.Files with
                                | Error error -> Error error
                                | Ok extraction ->

                                  match
                                    requireString rootElement "toolVersion",
                                    requireString rootElement "repositoryId",
                                    requireBoolean rootElement "complete"
                                  with
                                  | Error error, _, _
                                  | _, Error error, _
                                  | _, _, Error error -> Error error
                                  | Ok toolVersion, Ok repositoryId, Ok complete ->
                                    // 構造ノードとシンボルの関係は書き出し側の不変条件である。破れていれば破損とみなす。
                                    //
                                    // `CONTAINS` はルート以外のすべてのノードへ 1 本ずつ張るため、エッジ総数は
                                    // 少なくともノード数 - 1 になる。構成シンボルはファイルに属さないため、
                                    // ノード数はディレクトリ・ファイル・シンボルの合計を下回らない。
                                    let structural =
                                      1L + int64 counts.Directories + int64 counts.Files + int64 counts.Symbols

                                    if counts.Nodes > 0 && counts.Edges < counts.Nodes - 1 then
                                      Error(Malformed $"エッジ数 {counts.Edges} がノード数 {counts.Nodes} と整合しません")
                                    elif int64 counts.Nodes < structural then
                                      Error(Malformed $"ノード数 {counts.Nodes} がディレクトリ・ファイル・シンボルの合計と整合しません")
                                    else
                                      Ok
                                        { ManifestVersion = manifestVersion
                                          FormatVersion = formatVersion
                                          ToolVersion = toolVersion
                                          RepositoryId = repositoryId
                                          Complete = complete
                                          Options = options
                                          Counts = counts
                                          Segments = segments
                                          Diagnostics = diagnostics
                                          Extraction = extraction }
  with
  | :? JsonException as ex -> Error(Malformed ex.Message)
  | :? OverflowException -> Error(Malformed "数値が扱える範囲を超えています")

/// マニフェストとして受け入れる最大バイト数。
/// 外部入力である以上、読み込み量を先に有界にする。
[<Literal>]
let private MaxManifestBytes = 67_108_864L

let internal readPayload (path: string) =
  try
    if Artifact.isLink path then
      Error(Malformed "マニフェストへのリンクは読み込めません")
    elif not(File.Exists path) then
      Error(NotFound path)
    elif FileInfo(path).Length = 0L then
      // FIFO もサイズ 0 になる。開いてから Length を調べると取り消し不能な待機になる。
      Error(Malformed "マニフェストが空か、通常ファイルではありません")
    else
      // 削除の共有を許す。Windows では共有を許さない読み手が居ると、公開側の
      // 原子的な置換に必要な共有を許可する。
      use stream =
        new FileStream(
          path,
          FileStreamOptions(
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = (FileShare.ReadWrite ||| FileShare.Delete),
            Options = FileOptions.SequentialScan
          )
        )

      let length = stream.Length

      if length > MaxManifestBytes then
        Error(Malformed $"マニフェストが上限 {MaxManifestBytes} バイトを超えています")
      else
        let payload = Array.zeroCreate<byte>(int length)
        stream.ReadExactly(Span payload)
        Ok payload
  with
  | :? FileNotFoundException -> Error(NotFound path)
  | :? DirectoryNotFoundException -> Error(NotFound path)
  | :? EndOfStreamException -> Error(Malformed "マニフェストを読み切れませんでした")
  | :? IOException as ex -> Error(Malformed ex.Message)
  | :? UnauthorizedAccessException -> Error(Malformed "読み取り権限がありません")

let read (outputDirectory: string) : Result<Manifest, ManifestError> =
  match readPayload(Path.Combine(outputDirectory, FileName)) with
  | Error error -> Error error
  | Ok payload -> parse payload

/// 世代の切替と競合せずに成果物を読む。
///
/// マニフェストを読んでからセグメントを開くまでの間に公開が起きると、退役した世代を
/// 参照して欠損を報告してしまう。読み取りの前後でマニフェストのバイト列を比べ、
/// 変化していれば新しいマニフェストでやり直す。世代は 1 つぶんの猶予をもって
/// 退役するため、この確認と併せて「欠損なし・世代混在なし」が保証される。
/// docs/storage.md 3 を参照。
let readStable (outputDirectory: string) (body: Manifest -> 'T) : Result<'T, ManifestError> =
  let path = Path.Combine(outputDirectory, FileName)
  let mutable attempt = 0
  let mutable outcome = ValueNone

  while outcome.IsNone && attempt < StableReadAttempts do
    attempt <- attempt + 1

    match readPayload path with
    | Error error -> outcome <- ValueSome(Error error)
    | Ok before ->
      match parse before with
      | Error error -> outcome <- ValueSome(Error error)
      | Ok manifest ->
        let value = body manifest

        match readPayload path with
        | Ok after when after.AsSpan().SequenceEqual(ReadOnlySpan before) -> outcome <- ValueSome(Ok value)
        | Error error -> outcome <- ValueSome(Error error)
        // 読取中に公開が起きた。観測が一貫していないため、新しいマニフェストで測り直す。
        | Ok _ -> ()

  match outcome with
  | ValueSome result -> result
  | ValueNone -> Error(Busy path)


let private isGenerationInstalled (outputDirectory: string) (manifest: Manifest) (cancellation: CancellationToken) =
  manifest.Segments
  |> Array.forall(fun segment ->
    cancellation.ThrowIfCancellationRequested()

    match Artifact.tryResolveSegment outputDirectory segment.Name with
    | Error _ -> false
    | Ok path ->
      let info = FileInfo path

      info.Exists
      && info.Length = segment.ByteLength
      && String.Equals(Reader.checksum path cancellation, segment.Checksum, StringComparison.Ordinal))

/// 保持対象以外の世代を片付ける。切替が終わってから呼ぶ。
///
/// Windows では読み手が開いている世代を削除できないことがある。失敗しても
/// 公開済みの成果物は正しいままなので、次回以降の実行へ持ち越す。
let private retireStaleGenerations (outputDirectory: string) (retained: string[]) =
  let segmentsRoot = Path.Combine(outputDirectory, Artifact.SegmentDirectory)

  if Directory.Exists segmentsRoot then
    try
      for entry in Directory.EnumerateFileSystemEntries segmentsRoot do
        match Path.GetFileName entry with
        | null -> ()
        | name ->
          if Artifact.isGeneration name && not(Array.contains name retained) then
            deleteQuietly entry
    with
    | :? IOException -> ()
    | :? UnauthorizedAccessException -> ()

/// 公開済みマニフェストが参照している世代。退役の猶予を決めるために使う。
let private publishedGeneration (outputDirectory: string) =
  match read outputDirectory with
  | Ok manifest -> generationIn manifest
  | Error _ -> ValueNone

/// 完成した成果物を公開する。
///
/// セグメントは世代ごとに不変のパスへ置き、`manifest.json` の原子的置換だけを
/// 公開の切替点にする。旧世代は置換が終わるまで残るため、公開の途中でも
/// 読み手は旧世代か新世代のどちらか一方を完全に見る。
/// docs/storage.md 3 の原子性の要件に対応する。
let publishWithCancellation
  (outputDirectory: string)
  (generation: string)
  (manifest: Manifest)
  (cancellation: CancellationToken)
  : Result<unit, Artifact.PathError> =
  let staged = stagedSegmentsPath outputDirectory
  let segmentsRoot = Path.Combine(outputDirectory, Artifact.SegmentDirectory)
  let target = Path.Combine(segmentsRoot, generation)

  let install () =
    if not(Directory.Exists staged) then
      // 書き出した領域が消えている。実体のないセグメントを指すマニフェストを
      // 正常終了で残してはならない。同じ世代が既に揃っている場合だけ公開を続ける。
      if isGenerationInstalled outputDirectory manifest cancellation then
        Ok()
      else
        Error(Artifact.Unavailable(staged, "書き出したセグメントが見つかりません"))
    else

      match Artifact.ensureNotLink segmentsRoot with
      | Error error -> Error error
      | Ok() ->

        Directory.CreateDirectory segmentsRoot |> ignore

        // 作成の直前に置き換えられる競合を検出するため、作成後にもう一度確かめる。
        match Artifact.ensureNotLink segmentsRoot with
        | Error error -> Error error
        | Ok() ->

          match Artifact.ensureNotLink target with
          | Error error -> Error error
          | Ok() ->

            if not(Directory.Exists target) then
              Directory.Move(staged, target)
              Ok()
            elif isGenerationInstalled outputDirectory manifest cancellation then
              // 同じ世代が既にあるのは、同じ入力を再び索引した場合である。内容は世代識別子から
              // 一意に決まるため、揃っていればそのまま使う。読み手の参照も切らさない。
              Ok()
            else
              // 読み手が mmap している可能性があるため、不変パスの破損を上書きで修復しない。
              Error(Artifact.Unavailable(target, "既存の世代が欠損または破損しています。別の出力先へ再索引してください"))

  try
    cancellation.ThrowIfCancellationRequested()
    // 切替の前に、いま参照されている世代を控える。切替の直後に消してしまうと、
    // 旧マニフェストを読み終えた直後の読み手が参照先を失う。
    let previous = publishedGeneration outputDirectory

    match install() with
    | Error error -> Error error
    | Ok() ->

      cancellation.ThrowIfCancellationRequested()

      match write outputDirectory manifest with
      | Error error -> Error error
      | Ok() ->
        // 掃除は切替の後に置く。掃除の失敗で公開済みの成果物を壊さないためである。
        //
        // 直前の世代を残すのは、読み手がマニフェストを読んでからセグメントを開くまでの
        // 猶予を作るためである。猶予だけでは連続した公開に追い越され得るため、
        // 読み手側は `readStable` で観測の一貫性を確かめる。
        let retained =
          match previous with
          | ValueSome name when name <> generation -> [| generation; name |]
          | ValueSome _
          | ValueNone -> [| generation |]

        retireStaleGenerations outputDirectory retained
        discardStaging outputDirectory
        Ok()
  with
  | :? IOException as ex -> Error(Artifact.Unavailable(outputDirectory, ex.Message))
  | :? UnauthorizedAccessException -> Error(Artifact.Unavailable(outputDirectory, "書き込む権限がありません"))

let publish (outputDirectory: string) (generation: string) (manifest: Manifest) =
  publishWithCancellation outputDirectory generation manifest CancellationToken.None
