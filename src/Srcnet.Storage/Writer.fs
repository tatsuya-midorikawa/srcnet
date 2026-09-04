/// セグメントの書き出し。
///
/// 書き込み済みセグメントは不変とし、更新は新セグメントと tombstone で表現する。
/// これによりインデックス更新中でも照会が安全に継続できる。docs/storage.md 3, 6 を参照。
module Srcnet.Storage.Writer

open System
open System.Buffers
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.Threading
open Srcnet.Core
open Srcnet.Core.Diagnostics
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Core.Paths

/// 完全生成で作られる最初のセグメント集合の識別子。増分更新ではデルタ セグメントが続く。
[<Literal>]
let InitialSegmentId = "0001"

[<Literal>]
let private WriteBufferBytes = 65536

type FileInput =
  { Path: LogicalPath
    SizeBytes: int64
    Language: Language
    EncodingCode: uint16
    Flags: NodeFlags
    LineCount: int
    /// BLAKE3 の 32 バイト ダイジェスト。
    ContentHash: byte[] }

type IndexInput =
  { Repository: RepositoryId
    /// 論理パスの序数昇順。ルートは含まない。
    Directories: LogicalPath[]
    /// 論理パスの序数昇順。
    Files: FileInput[] }

type SegmentDescriptor =
  { /// 出力ディレクトリからの相対パス。区切りは `/` に固定する。
    Name: string
    ByteLength: int64
    /// BLAKE3 の 16 進小文字表記。
    Checksum: string }

type WriteResult =
  { Segments: SegmentDescriptor[]
    NodeCount: int
    EdgeCount: int
    StringCount: int
    StringBytes: int64 }

/// 1 セグメント分の書き出し器。書きながら BLAKE3 を計算するため、
/// 完全性検証のための再読み込みが不要になる。
[<Sealed>]
type private SegmentWriter(directory: string, name: string) =
  let fullPath = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar))

  do
    match Path.GetDirectoryName fullPath with
    | null -> ()
    | parent -> IO.Directory.CreateDirectory parent |> ignore

  let stream =
    new FileStream(
      fullPath,
      FileStreamOptions(
        Mode = FileMode.Create,
        Access = FileAccess.Write,
        Share = FileShare.None,
        BufferSize = 0,
        Options = FileOptions.SequentialScan
      )
    )

  let hasher = Blake3.Hasher()
  let buffer = ArrayPool<byte>.Shared.Rent WriteBufferBytes
  let mutable position = 0
  let mutable total = 0L

  member private _.FlushBuffer() =
    if position > 0 then
      let span = ReadOnlySpan(buffer, 0, position)
      stream.Write span
      hasher.Update span
      total <- total + int64 position
      position <- 0

  /// `length` バイトの書き込み領域を予約する。`length` はバッファ長以下でなければならない。
  member this.Reserve(length: int) : Span<byte> =
    if length > buffer.Length then
      invalidArg (nameof length) "予約長がバッファ長を超えています"

    if position + length > buffer.Length then this.FlushBuffer()

    let span = Span(buffer, position, length)
    span.Clear()
    position <- position + length
    span

  member this.WriteRaw(source: ReadOnlySpan<byte>) =
    if source.Length <= buffer.Length - position then
      source.CopyTo(Span(buffer, position, source.Length))
      position <- position + source.Length
    else
      this.FlushBuffer()
      stream.Write source
      hasher.Update source
      total <- total + int64 source.Length

  member this.WriteHeader(header: Format.Header) =
    Format.writeHeader (this.Reserve Format.HeaderLength) header

  member this.Complete() =
    this.FlushBuffer()
    stream.Flush()
    let digest = Array.zeroCreate<byte> Blake3.HashLength
    hasher.Finish(Span digest)

    { Name = name
      ByteLength = total
      Checksum = Convert.ToHexStringLower digest }

  interface IDisposable with

    member _.Dispose() =
      ArrayPool<byte>.Shared.Return buffer
      stream.Dispose()

let private segmentName (suffix: string) = $"segments/{InitialSegmentId}.{suffix}"

/// 論理パスからノード名（最後のセグメント）を取り出す。ルートは空文字列。
let private nodeName (path: LogicalPath) = fileName path

/// 成果物を書き出す。
///
/// ノードの密インデックスは「0 = リポジトリ、続いてディレクトリ、続いてファイル」の順で、
/// それぞれ論理パスの序数昇順に並ぶ。この順序が決定性の基礎になる。
let write
  (outputDirectory: string)
  (input: IndexInput)
  (diagnostics: DiagnosticSink)
  (cancellation: CancellationToken)
  : WriteResult =
  let directoryCount = input.Directories.Length
  let fileCount = input.Files.Length
  let nodeCount = 1 + directoryCount + fileCount

  let strings = Strings.StringTable()
  let repositoryNameRef = strings.Intern(RepositoryId.value input.Repository)

  let directoryNameRefs = Array.zeroCreate<int> directoryCount
  let directoryPathRefs = Array.zeroCreate<int> directoryCount

  for index in 0 .. directoryCount - 1 do
    let path = input.Directories[index]
    directoryNameRefs[index] <- strings.Intern(nodeName path)
    directoryPathRefs[index] <- strings.Intern(value path)

  let fileNameRefs = Array.zeroCreate<int> fileCount
  let filePathRefs = Array.zeroCreate<int> fileCount

  for index in 0 .. fileCount - 1 do
    let path = input.Files[index].Path
    fileNameRefs[index] <- strings.Intern(nodeName path)
    filePathRefs[index] <- strings.Intern(value path)

  // --- ノード ID ---
  cancellation.ThrowIfCancellationRequested()
  let ids = Array.zeroCreate<NodeId> nodeCount

  use builder = new NodeIdBuilder()

  ids[0] <- builder.Compute(Repository, input.Repository, root, RepositoryId.value input.Repository, 0u)

  for index in 0 .. directoryCount - 1 do
    ids[1 + index] <- builder.Compute(Directory, input.Repository, input.Directories[index], "", 0u)

  for index in 0 .. fileCount - 1 do
    ids[1 + directoryCount + index] <- builder.Compute(File, input.Repository, input.Files[index].Path, "", 0u)

  // 衝突は握りつぶさず明示的な診断にする。docs/graph-model.md 4.2 を参照。
  let sortedIds = Array.copy ids
  Array.sortInPlace sortedIds

  for index in 1 .. nodeCount - 1 do
    if sortedIds[index] = sortedIds[index - 1] then
      diagnostics.Add(NodeIdCollision, "", $"ノード ID {sortedIds[index]} が重複しています")

  // --- CONTAINS の親子関係 ---
  cancellation.ThrowIfCancellationRequested()
  let indexByPath = Dictionary<string, int>(directoryCount + 1, StringComparer.Ordinal)
  indexByPath[value root] <- 0

  for index in 0 .. directoryCount - 1 do
    indexByPath[value input.Directories[index]] <- 1 + index

  let parents = Array.zeroCreate<int> nodeCount
  parents[0] <- -1

  let resolveParent (path: LogicalPath) =
    match parent path with
    | ValueNone -> 0
    | ValueSome parentPath ->
      match indexByPath.TryGetValue(value parentPath) with
      | true, parentIndex -> parentIndex
      | false, _ ->
        // 走査は親ディレクトリを必ず先に登録するため、ここへは来ない。
        // 到達した場合は成果物を壊さずリポジトリ直下として扱い、診断で明示する。
        diagnostics.Add(PathRejected, value path, "親ディレクトリが見つかりません")
        0

  for index in 0 .. directoryCount - 1 do
    parents[1 + index] <- resolveParent input.Directories[index]

  for index in 0 .. fileCount - 1 do
    parents[1 + directoryCount + index] <- resolveParent input.Files[index].Path

  let edgeCount = nodeCount - 1
  let offsets = Array.zeroCreate<uint64> (nodeCount + 1)

  for index in 1 .. nodeCount - 1 do
    offsets[parents[index] + 1] <- offsets[parents[index] + 1] + 1UL

  for index in 1 .. nodeCount do
    offsets[index] <- offsets[index] + offsets[index - 1]

  let targets = Array.zeroCreate<uint32> edgeCount
  let cursor = Array.zeroCreate<uint64> nodeCount
  Array.blit offsets 0 cursor 0 nodeCount

  // 添字の昇順に詰めるため、各ノードの隣接配列は自然に昇順になる。
  for index in 1 .. nodeCount - 1 do
    let parentIndex = parents[index]
    targets[int cursor[parentIndex]] <- uint32 index
    cursor[parentIndex] <- cursor[parentIndex] + 1UL

  // --- 書き出し ---
  cancellation.ThrowIfCancellationRequested()
  let descriptors = List<SegmentDescriptor>()

  let stringOffsets = strings.Offsets()
  let blobLength = stringOffsets[strings.Count]

  do
    use writer = new SegmentWriter(outputDirectory, segmentName "strings")

    writer.WriteHeader
      { Kind = Format.Strings
        PrimaryCount = uint64 strings.Count
        SecondaryCount = blobLength
        RecordLength = 0u
        PayloadLength = blobLength }

    let encoder = Text.Encoding.UTF8
    let mutable scratch = ArrayPool<byte>.Shared.Rent 4096

    try
      for index in 0 .. strings.Count - 1 do
        let text = strings[index]
        let required = encoder.GetMaxByteCount text.Length

        if required > scratch.Length then
          ArrayPool<byte>.Shared.Return scratch
          scratch <- ArrayPool<byte>.Shared.Rent required

        let written = encoder.GetBytes(text.AsSpan(), Span scratch)
        writer.WriteRaw(ReadOnlySpan(scratch, 0, written))

      descriptors.Add(writer.Complete())
    finally
      ArrayPool<byte>.Shared.Return scratch

  do
    use writer = new SegmentWriter(outputDirectory, segmentName "stroffsets")
    let payloadLength = uint64 (stringOffsets.Length * 8)

    writer.WriteHeader
      { Kind = Format.StringOffsets
        PrimaryCount = uint64 strings.Count
        SecondaryCount = blobLength
        RecordLength = 8u
        PayloadLength = payloadLength }

    for offset in stringOffsets do
      BinaryPrimitives.WriteUInt64LittleEndian(writer.Reserve 8, offset)

    descriptors.Add(writer.Complete())

  do
    use writer = new SegmentWriter(outputDirectory, segmentName "files")
    let payloadLength = uint64 fileCount * uint64 Format.RecordLength

    writer.WriteHeader
      { Kind = Format.Files
        PrimaryCount = uint64 fileCount
        SecondaryCount = 0UL
        RecordLength = uint32 Format.RecordLength
        PayloadLength = payloadLength }

    for index in 0 .. fileCount - 1 do
      let file = input.Files[index]
      let record = writer.Reserve Format.RecordLength
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.FileRecord.PathOffset, 4), uint32 filePathRefs[index])
      BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(Format.FileRecord.LanguageOffset, 2), Language.toCode file.Language)
      BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(Format.FileRecord.EncodingOffset, 2), file.EncodingCode)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.FileRecord.FlagsOffset, 4), uint32 file.Flags)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.FileRecord.LineCountOffset, 4), uint32 file.LineCount)
      BinaryPrimitives.WriteInt64LittleEndian(record.Slice(Format.FileRecord.SizeOffset, 8), file.SizeBytes)

      ReadOnlySpan(file.ContentHash)
        .Slice(0, Format.FileRecord.ContentHashLength)
        .CopyTo(record.Slice(Format.FileRecord.ContentHashOffset, Format.FileRecord.ContentHashLength))

    descriptors.Add(writer.Complete())

  do
    use writer = new SegmentWriter(outputDirectory, segmentName "nodes")
    let payloadLength = uint64 nodeCount * uint64 Format.RecordLength

    writer.WriteHeader
      { Kind = Format.Nodes
        PrimaryCount = uint64 nodeCount
        SecondaryCount = 0UL
        RecordLength = uint32 Format.RecordLength
        PayloadLength = payloadLength }

    let writeNode (id: NodeId) (kind: NodeKind) (language: Language) (flags: NodeFlags) (fileIndex: uint32) (nameRef: int) (qualifiedRef: int) (lineCount: int) =
      let record = writer.Reserve Format.RecordLength
      NodeId.writeTo (record.Slice(Format.NodeRecord.IdOffset, Ids.NodeIdLength)) id
      record[Format.NodeRecord.KindOffset] <- NodeKind.toCode kind
      BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(Format.NodeRecord.LanguageOffset, 2), Language.toCode language)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.FlagsOffset, 4), uint32 flags)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.FileIndexOffset, 4), fileIndex)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.NameOffset, 4), uint32 nameRef)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.QualifiedNameOffset, 4), uint32 qualifiedRef)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.StartLineOffset, 4), (if lineCount > 0 then 1u else 0u))
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.EndLineOffset, 4), uint32 lineCount)

    writeNode ids[0] Repository Unknown NodeFlags.None Format.NodeRecord.NoFile repositoryNameRef repositoryNameRef 0

    for index in 0 .. directoryCount - 1 do
      writeNode
        ids[1 + index]
        Directory
        Unknown
        NodeFlags.None
        Format.NodeRecord.NoFile
        directoryNameRefs[index]
        directoryPathRefs[index]
        0

    for index in 0 .. fileCount - 1 do
      let file = input.Files[index]

      writeNode
        ids[1 + directoryCount + index]
        File
        file.Language
        file.Flags
        (uint32 index)
        fileNameRefs[index]
        filePathRefs[index]
        file.LineCount

    descriptors.Add(writer.Complete())

  do
    use writer = new SegmentWriter(outputDirectory, segmentName $"edges.{EdgeKind.name Contains}")
    let payloadLength = uint64 (nodeCount + 1) * 8UL + uint64 edgeCount * 4UL

    writer.WriteHeader
      { Kind = Format.AdjacencyCsr
        PrimaryCount = uint64 nodeCount
        SecondaryCount = uint64 edgeCount
        RecordLength = 0u
        PayloadLength = payloadLength }

    for offset in offsets do
      BinaryPrimitives.WriteUInt64LittleEndian(writer.Reserve 8, offset)

    for target in targets do
      BinaryPrimitives.WriteUInt32LittleEndian(writer.Reserve 4, target)

    descriptors.Add(writer.Complete())

  do
    // 後方 CSR。「このノードを含むのは誰か」を全走査せずに解けるようにする。
    // CONTAINS では各ノードの親は高々 1 つなので、隣接は 0 個か 1 個になる。
    use writer = new SegmentWriter(outputDirectory, segmentName $"redges.{EdgeKind.name Contains}")
    let payloadLength = uint64 (nodeCount + 1) * 8UL + uint64 edgeCount * 4UL

    writer.WriteHeader
      { Kind = Format.AdjacencyCsr
        PrimaryCount = uint64 nodeCount
        SecondaryCount = uint64 edgeCount
        RecordLength = 0u
        PayloadLength = payloadLength }

    let mutable running = 0UL
    BinaryPrimitives.WriteUInt64LittleEndian(writer.Reserve 8, running)

    for index in 0 .. nodeCount - 1 do
      if parents[index] >= 0 then running <- running + 1UL
      BinaryPrimitives.WriteUInt64LittleEndian(writer.Reserve 8, running)

    for index in 0 .. nodeCount - 1 do
      if parents[index] >= 0 then
        BinaryPrimitives.WriteUInt32LittleEndian(writer.Reserve 4, uint32 parents[index])

    descriptors.Add(writer.Complete())

  do
    use writer = new SegmentWriter(outputDirectory, segmentName "idmap")
    let payloadLength = uint64 nodeCount * uint64 (Ids.NodeIdLength + 4)

    writer.WriteHeader
      { Kind = Format.IdMap
        PrimaryCount = uint64 nodeCount
        SecondaryCount = 0UL
        RecordLength = uint32 (Ids.NodeIdLength + 4)
        PayloadLength = payloadLength }

    let order = Array.init nodeCount id
    Array.sortInPlaceWith (fun (a: int) (b: int) -> compare ids[a] ids[b]) order

    for position in order do
      NodeId.writeTo (writer.Reserve Ids.NodeIdLength) ids[position]

    for position in order do
      BinaryPrimitives.WriteUInt32LittleEndian(writer.Reserve 4, uint32 position)

    descriptors.Add(writer.Complete())

  let ordered = descriptors.ToArray()
  Array.sortInPlaceWith (fun (a: SegmentDescriptor) (b: SegmentDescriptor) -> String.CompareOrdinal(a.Name, b.Name)) ordered

  { Segments = ordered
    NodeCount = nodeCount
    EdgeCount = edgeCount
    StringCount = strings.Count
    StringBytes = strings.TotalBytes }
