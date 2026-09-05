/// 生成物の整合性検証。
///
/// 形式版、チェックサム、参照の閉包を検査する。破損を検出した場合は部分的に読み進めず、
/// 明示的な失敗として扱う。docs/requirements.md FR-6、docs/security.md C-6 を参照。
module Srcnet.Storage.Verify

open System
open System.Buffers
open System.Buffers.Binary
open System.IO
open System.Threading
open Srcnet.Core
open Srcnet.Core.Graph

type Issue =
  | ManifestUnreadable of detail: string
  | SegmentMissing of name: string
  | SegmentUnreadable of name: string * detail: string
  | LengthMismatch of name: string * expected: int64 * actual: int64
  | ChecksumMismatch of name: string * expected: string * actual: string
  | CountMismatch of name: string * expected: int64 * actual: int64
  | ReferenceOutOfRange of name: string * detail: string
  | OrderViolation of name: string * detail: string
  | UnknownNodeKind of name: string * code: byte
  /// セグメント名から成果物内のパスへ変換できなかった。
  | SegmentPathRejected of name: string * detail: string

module Issue =

  let describe issue =
    match issue with
    | ManifestUnreadable detail -> detail
    | SegmentMissing name -> $"セグメントがありません: {name}"
    | SegmentUnreadable(name, detail) -> $"{name}: {detail}"
    | LengthMismatch(name, expected, actual) -> $"{name}: 長さが {expected} ではなく {actual} です"
    | ChecksumMismatch(name, expected, actual) -> $"{name}: チェックサムが {expected} ではなく {actual} です"
    | CountMismatch(name, expected, actual) -> $"{name}: 件数が {expected} ではなく {actual} です"
    | ReferenceOutOfRange(name, detail) -> $"{name}: 参照が範囲外です ({detail})"
    | OrderViolation(name, detail) -> $"{name}: 整列の不変条件が破れています ({detail})"
    | UnknownNodeKind(name, code) -> $"{name}: 未知のノード種別コード {code} です"
    | SegmentPathRejected(_, detail) -> detail

type Report =
  { Issues: Issue[]
    SegmentsChecked: int
    BytesChecked: int64 }

  member this.IsValid = this.Issues.Length = 0

[<Literal>]
let private HashBufferBytes = 262144

let private hashFile (path: string) (cancellation: CancellationToken) =
  use hasher = new Hashing.Hasher()
  let buffer = ArrayPool<byte>.Shared.Rent HashBufferBytes

  try
    use stream =
      new FileStream(
        path,
        FileStreamOptions(
          Mode = FileMode.Open,
          Access = FileAccess.Read,
          Share = FileShare.Read,
          BufferSize = 0,
          Options = FileOptions.SequentialScan
        )
      )

    let mutable reading = true

    while reading do
      cancellation.ThrowIfCancellationRequested()
      let read = stream.Read(Span(buffer, 0, buffer.Length))
      if read = 0 then reading <- false else hasher.Update(ReadOnlySpan(buffer, 0, read))

    let digest = Array.zeroCreate<byte> Hashing.HashLength
    hasher.Finish(Span digest)
    Convert.ToHexStringLower digest
  finally
    ArrayPool<byte>.Shared.Return buffer

let private findSegment (manifest: Manifest.Manifest) (suffix: string) =
  manifest.Segments
  |> Array.tryFind (fun segment -> segment.Name.EndsWith("." + suffix, StringComparison.Ordinal))

/// 文字列オフセット表が単調非減少で、末尾が blob 長に一致することを確かめる。
let private checkStringOffsets (name: string) (segment: Reader.MappedSegment) (issues: ResizeArray<Issue>) =
  let payload = segment.Payload
  let count = int segment.Header.PrimaryCount
  let expectedLength = (count + 1) * 8

  if payload.Length <> expectedLength then
    issues.Add(LengthMismatch(name, int64 expectedLength, int64 payload.Length))
  else
    let mutable previous = 0UL
    let mutable index = 0

    while index <= count do
      let offset = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(index * 8, 8))

      if offset < previous then
        issues.Add(OrderViolation(name, $"オフセット {index} が減少しています"))
        index <- count
      elif index = count && offset <> segment.Header.SecondaryCount then
        issues.Add(CountMismatch(name, int64 segment.Header.SecondaryCount, int64 offset))

      previous <- offset
      index <- index + 1

let private checkNodes
  (name: string)
  (segment: Reader.MappedSegment)
  (stringCount: int)
  (fileCount: int)
  (issues: ResizeArray<Issue>)
  =
  let payload = segment.Payload
  let count = int segment.Header.PrimaryCount
  let expectedLength = count * Format.RecordLength

  if payload.Length <> expectedLength then
    issues.Add(LengthMismatch(name, int64 expectedLength, int64 payload.Length))
  else
    let mutable index = 0
    let mutable reported = false

    while index < count do
      let record = payload.Slice(index * Format.RecordLength, Format.RecordLength)
      let kindCode = record[Format.NodeRecord.KindOffset]

      if not reported then
        match NodeKind.ofCode kindCode with
        | ValueNone ->
          issues.Add(UnknownNodeKind(name, kindCode))
          reported <- true
        | ValueSome _ -> ()

        let nameRef = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.NameOffset, 4))
        let qualifiedRef = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.QualifiedNameOffset, 4))
        let fileIndex = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.FileIndexOffset, 4))

        if nameRef >= uint32 stringCount || qualifiedRef >= uint32 stringCount then
          issues.Add(ReferenceOutOfRange(name, $"ノード {index} の文字列参照が {stringCount} 件を超えています"))
          reported <- true
        elif fileIndex <> Format.NodeRecord.NoFile && fileIndex >= uint32 fileCount then
          issues.Add(ReferenceOutOfRange(name, $"ノード {index} のファイル参照が {fileCount} 件を超えています"))
          reported <- true

      index <- index + 1

let private checkFiles (name: string) (segment: Reader.MappedSegment) (stringCount: int) (issues: ResizeArray<Issue>) =
  let payload = segment.Payload
  let count = int segment.Header.PrimaryCount
  let expectedLength = count * Format.RecordLength

  if payload.Length <> expectedLength then
    issues.Add(LengthMismatch(name, int64 expectedLength, int64 payload.Length))
  else
    let mutable index = 0
    let mutable reported = false

    while index < count && not reported do
      let record = payload.Slice(index * Format.RecordLength, Format.RecordLength)
      let pathRef = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.FileRecord.PathOffset, 4))

      if pathRef >= uint32 stringCount then
        issues.Add(ReferenceOutOfRange(name, $"ファイル {index} のパス参照が {stringCount} 件を超えています"))
        reported <- true

      index <- index + 1

let private checkCsr (name: string) (segment: Reader.MappedSegment) (nodeCount: int) (issues: ResizeArray<Issue>) =
  let payload = segment.Payload
  let count = int segment.Header.PrimaryCount
  let edgeCount = int64 segment.Header.SecondaryCount

  if count <> nodeCount then
    issues.Add(CountMismatch(name, int64 nodeCount, int64 count))
  else

  let expectedLength = int64 (count + 1) * 8L + edgeCount * 4L

  if int64 payload.Length <> expectedLength then
    issues.Add(LengthMismatch(name, expectedLength, int64 payload.Length))
  else
    let targetsOffset = (count + 1) * 8
    let mutable previous = 0UL
    let mutable index = 0
    let mutable reported = false

    while index <= count && not reported do
      let offset = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(index * 8, 8))

      if offset < previous then
        issues.Add(OrderViolation(name, $"オフセット {index} が減少しています"))
        reported <- true
      elif offset > uint64 edgeCount then
        issues.Add(ReferenceOutOfRange(name, $"オフセット {index} が隣接要素数を超えています"))
        reported <- true

      previous <- offset
      index <- index + 1

    if not reported && previous <> uint64 edgeCount then
      issues.Add(CountMismatch(name, edgeCount, int64 previous))

    let mutable target = 0L

    while target < edgeCount && not reported do
      let value = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(targetsOffset + int target * 4, 4))

      if value >= uint32 nodeCount then
        issues.Add(ReferenceOutOfRange(name, $"隣接要素 {target} がノード数 {nodeCount} を超えています"))
        reported <- true

      target <- target + 1L

let private checkIdMap (name: string) (segment: Reader.MappedSegment) (nodeCount: int) (issues: ResizeArray<Issue>) =
  let payload = segment.Payload
  let count = int segment.Header.PrimaryCount
  let expectedLength = count * (Ids.NodeIdLength + 4)

  if count <> nodeCount then
    issues.Add(CountMismatch(name, int64 nodeCount, int64 count))
  elif payload.Length <> expectedLength then
    issues.Add(LengthMismatch(name, int64 expectedLength, int64 payload.Length))
  else
    let indicesOffset = count * Ids.NodeIdLength
    let mutable index = 0
    let mutable reported = false

    while index < count && not reported do
      if index > 0 then
        let previous = payload.Slice((index - 1) * Ids.NodeIdLength, Ids.NodeIdLength)
        let current = payload.Slice(index * Ids.NodeIdLength, Ids.NodeIdLength)

        // ID はビッグ エンディアンで格納するため、バイト列の辞書順が数値順と一致する。
        if previous.SequenceCompareTo current >= 0 then
          issues.Add(OrderViolation(name, $"ID {index} が昇順になっていません"))
          reported <- true

      let dense = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(indicesOffset + index * 4, 4))

      if dense >= uint32 nodeCount then
        issues.Add(ReferenceOutOfRange(name, $"密インデックス {dense} がノード数 {nodeCount} を超えています"))
        reported <- true

      index <- index + 1

let private inspect (outputDirectory: string) (manifest: Manifest.Manifest) (cancellation: CancellationToken) =
  let issues = ResizeArray<Issue>()
  let mutable bytesChecked = 0L
  let mutable checkedSegments = 0

  for descriptor in manifest.Segments do
    cancellation.ThrowIfCancellationRequested()

    // 名前からパスへの変換は必ず検証を通す。成果物外のファイルを読み、
    // 「問題ありません」と報告してしまうのを防ぐ。docs/security.md C-6 を参照。
    match Artifact.tryResolveSegment outputDirectory descriptor.Name with
    | Error error -> issues.Add(SegmentPathRejected(descriptor.Name, Artifact.PathError.describe error))
    | Ok path ->

    // 権限や、検証中に退役した世代による読み取り失敗は、破損した成果物と同じく
    // 想定内の失敗である。内部エラーで終わらせず問題として記録し、
    // `readStable` にマニフェストを読み直させる。
    try
      if not (File.Exists path) then issues.Add(SegmentMissing descriptor.Name)
      else
        let actualLength = FileInfo(path).Length

        if actualLength <> descriptor.ByteLength then
          issues.Add(LengthMismatch(descriptor.Name, descriptor.ByteLength, actualLength))
        else
          let actual = hashFile path cancellation

          if not (String.Equals(actual, descriptor.Checksum, StringComparison.Ordinal)) then
            issues.Add(ChecksumMismatch(descriptor.Name, descriptor.Checksum, actual))

          bytesChecked <- bytesChecked + actualLength
          checkedSegments <- checkedSegments + 1
    with
    | :? FileNotFoundException -> issues.Add(SegmentMissing descriptor.Name)
    | :? DirectoryNotFoundException -> issues.Add(SegmentMissing descriptor.Name)
    | :? IOException as ex -> issues.Add(SegmentUnreadable(descriptor.Name, ex.Message))
    | :? UnauthorizedAccessException -> issues.Add(SegmentUnreadable(descriptor.Name, "読み取り権限がありません"))

  let openSegment (suffix: string) (check: string -> Reader.MappedSegment -> unit) =
    match findSegment manifest suffix with
    | None -> issues.Add(SegmentMissing suffix)
    | Some descriptor ->
      match Artifact.tryResolveSegment outputDirectory descriptor.Name with
      | Error error -> issues.Add(SegmentPathRejected(descriptor.Name, Artifact.PathError.describe error))
      | Ok path ->
        match Reader.MappedSegment.Open path with
        | Error error -> issues.Add(SegmentUnreadable(descriptor.Name, Reader.OpenError.describe error))
        | Ok segment ->
          use segment = segment
          check descriptor.Name segment

  // チェックサムが壊れている状態で構造を読むと、誤った診断が連鎖する。
  if issues.Count = 0 then
    openSegment "stroffsets" (fun name segment -> checkStringOffsets name segment issues)
    openSegment "nodes" (fun name segment -> checkNodes name segment manifest.Counts.Strings manifest.Counts.Files issues)
    openSegment "files" (fun name segment -> checkFiles name segment manifest.Counts.Strings issues)
    openSegment $"edges.{EdgeKind.name Contains}" (fun name segment -> checkCsr name segment manifest.Counts.Nodes issues)
    openSegment $"redges.{EdgeKind.name Contains}" (fun name segment -> checkCsr name segment manifest.Counts.Nodes issues)
    openSegment "idmap" (fun name segment -> checkIdMap name segment manifest.Counts.Nodes issues)

  { Issues = issues.ToArray()
    SegmentsChecked = checkedSegments
    BytesChecked = bytesChecked }

/// 出力ディレクトリの成果物を検証する。
///
/// 検証中に再索引が完了すると、退役した世代を参照したまま欠損を報告してしまう。
/// `readStable` で観測の一貫性を確かめ、切替と競合した場合は測り直す。
let run (outputDirectory: string) (cancellation: CancellationToken) : Result<Report, Manifest.ManifestError> =
  Manifest.readStable outputDirectory (fun manifest -> inspect outputDirectory manifest cancellation)
