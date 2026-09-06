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

/// 大規模ループでキャンセルを確認する間隔。2 の冪にしてビット積で判定する。
///
/// 毎反復で確認すると内側ループの分岐が増えて throughput を損なう。
/// 1 回の確認あたりの処理量が十分小さく、かつ確認自体が無視できる値を選ぶ。
[<Literal>]
let private CancellationCheckStride = 8192

let inline private checkCancellation (cancellation: CancellationToken) (index: int) =
  if index &&& (CancellationCheckStride - 1) = 0 then
    cancellation.ThrowIfCancellationRequested()

/// 抽出したシンボル 1 件。位置は所属ファイル内のバイト位置と行番号。
type SymbolInput =
  { Kind: NodeKind
    Name: string
    QualifiedName: string
    /// 同一ファイル内の親シンボルの添字。ファイル直下は -1。
    Parent: int
    Ordinal: uint32
    StartLine: int
    EndLine: int
    StartByte: int
    EndByte: int
    Flags: NodeFlags }

/// 未解決の参照候補 1 件。解決は M3 で行う。docs/extraction.md 5 を参照。
type ReferenceInput =
  { /// 同一ファイル内の発生元シンボルの添字。ファイル自身が発生元の場合は -1。
    Source: int
    Kind: EdgeKind
    Target: string
    Qualifier: string
    Language: Language
    StartByte: int
    EndByte: int
    Line: int
    Stage: byte
    Confidence: Confidence }

type FileInput =
  { Path: LogicalPath
    SizeBytes: int64
    Language: Language
    EncodingCode: uint16
    Flags: NodeFlags
    LineCount: int
    /// 内容ハッシュ (SHA-256) の 32 バイト ダイジェスト。
    ContentHash: byte[]
    /// バイト位置の昇順。親は必ず子より前に並ぶ。
    Symbols: SymbolInput[]
    /// 発生元の添字 → エッジ種別コード → バイト位置の昇順。
    References: ReferenceInput[] }

type IndexInput =
  { Repository: RepositoryId
    /// 論理パスの序数昇順。ルートは含まない。
    Directories: LogicalPath[]
    /// 論理パスの序数昇順。
    Files: FileInput[] }

type SegmentDescriptor =
  { /// セグメント ディレクトリ内の名前。公開時に `segments/<generation>/` を前置きする。
    Name: string
    ByteLength: int64
    /// チェックサム (SHA-256) の 16 進小文字表記。
    Checksum: string }

/// 種別ごとの件数。名前は成果物とマニフェストで共有する。
type KindCount = { Kind: string; Count: int }

type WriteResult =
  { Segments: SegmentDescriptor[]
    NodeCount: int
    EdgeCount: int
    StringCount: int
    StringBytes: int64
    SymbolCount: int
    ConfigSymbolCount: int
    ReferenceCount: int
    /// ノード種別ごとの件数。種別名の序数昇順。
    NodeKinds: KindCount[]
    /// エッジ種別ごとの件数。種別名の序数昇順。
    EdgeKinds: KindCount[]
    /// 同一のノード ID が複数のノードへ割り当てられた件数。0 でなければ公開してはならない。
    IdCollisions: int }

/// 1 セグメント分の書き出し器。書きながらチェックサムを計算するため、
/// 完全性検証のための再読み込みが不要になる。
[<Sealed>]
type private SegmentWriter(directory: string, name: string) =
  let fullPath = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar))

  do
    match Path.GetDirectoryName fullPath with
    | null -> ()
    | parent -> IO.Directory.CreateDirectory parent |> ignore

  // 既存の項目がリンクだと `FileMode.Create` はリンク先を上書きしてしまう。
  // 先に項目自体を取り除き、排他的に新規作成することでリンクを追跡しない。
  do File.Delete fullPath

  let stream =
    new FileStream(
      fullPath,
      FileStreamOptions(
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        BufferSize = 0,
        Options = FileOptions.SequentialScan
      )
    )

  let hasher = new Hashing.Hasher()
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
    let digest = Array.zeroCreate<byte> Hashing.HashLength
    hasher.Finish(Span digest)

    { Name = name
      ByteLength = total
      Checksum = Convert.ToHexStringLower digest }

  interface IDisposable with

    member _.Dispose() =
      ArrayPool<byte>.Shared.Return buffer
      (hasher :> IDisposable).Dispose()
      stream.Dispose()

let private segmentName (suffix: string) = $"{InitialSegmentId}.{suffix}"

/// 論理パスからノード名（最後のセグメント）を取り出す。ルートは空文字列。
let private nodeName (path: LogicalPath) = fileName path

/// M2 が生成するエッジ種別。docs/storage.md 2 の `<id>.edges.<kind>` に対応する。
let private producedEdgeKinds = [| Contains; Declares; Defines; GuardedBy |]

/// 疎な辺集合から CSR を作る。
///
/// 隣接配列は昇順に整列し、同じ始点から同じ終点への重複辺は 1 本にまとめる。
/// `edges` は破壊的に並べ替える。
let private buildCsr (nodeCount: int) (edges: struct (int * int)[]) (cancellation: CancellationToken) =
  Array.sortInPlaceWith
    (fun (struct (leftSource, leftTarget)) (struct (rightSource, rightTarget)) ->
      let bySource = compare leftSource rightSource
      if bySource <> 0 then bySource else compare leftTarget rightTarget)
    edges

  let offsets = Array.zeroCreate<uint64> (nodeCount + 1)
  let targets = ResizeArray<uint32> edges.Length
  let mutable previousSource = -1
  let mutable previousTarget = -1

  for index in 0 .. edges.Length - 1 do
    checkCancellation cancellation index
    let struct (source, target) = edges[index]

    if source <> previousSource || target <> previousTarget then
      offsets[source + 1] <- offsets[source + 1] + 1UL
      targets.Add(uint32 target)
      previousSource <- source
      previousTarget <- target

  for index in 1 .. nodeCount do
    offsets[index] <- offsets[index] + offsets[index - 1]

  struct (offsets, targets.ToArray())

/// CSR を 1 セグメントとして書き出す。
let private writeCsr
  (segmentDirectory: string)
  (name: string)
  (nodeCount: int)
  (offsets: uint64[])
  (targets: uint32[])
  (cancellation: CancellationToken)
  =
  use writer = new SegmentWriter(segmentDirectory, name)
  let payloadLength = uint64 (nodeCount + 1) * 8UL + uint64 targets.Length * 4UL

  writer.WriteHeader
    { Kind = Format.AdjacencyCsr
      PrimaryCount = uint64 nodeCount
      SecondaryCount = uint64 targets.Length
      RecordLength = 0u
      PayloadLength = payloadLength }

  let mutable cursor = 0

  for offset in offsets do
    checkCancellation cancellation cursor
    cursor <- cursor + 1
    BinaryPrimitives.WriteUInt64LittleEndian(writer.Reserve 8, offset)

  for target in targets do
    checkCancellation cancellation cursor
    cursor <- cursor + 1
    BinaryPrimitives.WriteUInt32LittleEndian(writer.Reserve 4, target)

  writer.Complete()

/// セグメントを書き出す。
///
/// `segmentDirectory` は staging 内のセグメント ディレクトリで、返す名前はその中での
/// 相対名である。世代を含む最終的な名前は公開時に決まる。
///
/// ノードの密インデックスは
/// 「0 = リポジトリ → ディレクトリ → ファイル → シンボル → 構成シンボル」の順で、
/// ディレクトリとファイルは論理パスの序数昇順、シンボルはファイル順・ファイル内の
/// バイト位置順、構成シンボルは名前の序数昇順に並ぶ。この順序が決定性の基礎になる。
let write
  (segmentDirectory: string)
  (input: IndexInput)
  (diagnostics: DiagnosticSink)
  (cancellation: CancellationToken)
  : WriteResult =
  let directoryCount = input.Directories.Length
  let fileCount = input.Files.Length
  let structuralCount = 1 + directoryCount + fileCount

  // --- シンボルの密インデックスを決める ---
  cancellation.ThrowIfCancellationRequested()
  let symbolStart = Array.zeroCreate<int> (fileCount + 1)
  let mutable symbolTotal = 0

  for index in 0 .. fileCount - 1 do
    checkCancellation cancellation index
    symbolStart[index] <- symbolTotal
    symbolTotal <- symbolTotal + input.Files[index].Symbols.Length

  symbolStart[fileCount] <- symbolTotal
  let symbolBase = structuralCount

  // --- 構成シンボルを集める ---
  // `GUARDED_BY` の相手はファイルに属さない大域のノードである。ファイルごとに作ると
  // 同じ `CONFIG_X` がファイル数だけ複製される。名前で重複排除して 1 つにまとめる。
  let configNames = SortedSet<string>(StringComparer.Ordinal)

  for fileIndex in 0 .. fileCount - 1 do
    checkCancellation cancellation fileIndex

    for reference in input.Files[fileIndex].References do
      if reference.Kind = GuardedBy && reference.Target.Length > 0 then
        configNames.Add reference.Target |> ignore

  let configArray = Array.zeroCreate<string> configNames.Count
  configNames.CopyTo configArray
  let configBase = symbolBase + symbolTotal
  let nodeCount = configBase + configArray.Length

  let configIndexOf = Dictionary<string, int>(configArray.Length, StringComparer.Ordinal)

  for index in 0 .. configArray.Length - 1 do
    configIndexOf[configArray[index]] <- configBase + index

  // --- 文字列表 ---
  cancellation.ThrowIfCancellationRequested()
  let strings = Strings.StringTable()
  let repositoryNameRef = strings.Intern(RepositoryId.value input.Repository)

  let directoryNameRefs = Array.zeroCreate<int> directoryCount
  let directoryPathRefs = Array.zeroCreate<int> directoryCount

  for index in 0 .. directoryCount - 1 do
    checkCancellation cancellation index
    let path = input.Directories[index]
    directoryNameRefs[index] <- strings.Intern(nodeName path)
    directoryPathRefs[index] <- strings.Intern(value path)

  let fileNameRefs = Array.zeroCreate<int> fileCount
  let filePathRefs = Array.zeroCreate<int> fileCount

  for index in 0 .. fileCount - 1 do
    checkCancellation cancellation index
    let path = input.Files[index].Path
    fileNameRefs[index] <- strings.Intern(nodeName path)
    filePathRefs[index] <- strings.Intern(value path)

  let symbolNameRefs = Array.zeroCreate<int> symbolTotal
  let symbolQualifiedRefs = Array.zeroCreate<int> symbolTotal

  for fileIndex in 0 .. fileCount - 1 do
    let file = input.Files[fileIndex]
    let start = symbolStart[fileIndex]

    for index in 0 .. file.Symbols.Length - 1 do
      checkCancellation cancellation (start + index)
      let symbol = file.Symbols[index]
      symbolNameRefs[start + index] <- strings.Intern symbol.Name
      symbolQualifiedRefs[start + index] <- strings.Intern symbol.QualifiedName

  let configNameRefs = Array.zeroCreate<int> configArray.Length

  for index in 0 .. configArray.Length - 1 do
    checkCancellation cancellation index
    configNameRefs[index] <- strings.Intern configArray[index]

  let mutable referenceTotal = 0

  for index in 0 .. fileCount - 1 do
    referenceTotal <- referenceTotal + input.Files[index].References.Length

  let referenceTargetRefs = Array.zeroCreate<int> referenceTotal
  let referenceQualifierRefs = Array.zeroCreate<int> referenceTotal
  let mutable internCursor = 0

  for fileIndex in 0 .. fileCount - 1 do
    for reference in input.Files[fileIndex].References do
      checkCancellation cancellation internCursor
      referenceTargetRefs[internCursor] <- strings.Intern reference.Target
      referenceQualifierRefs[internCursor] <- strings.Intern reference.Qualifier
      internCursor <- internCursor + 1

  // --- ノード ID ---
  cancellation.ThrowIfCancellationRequested()
  let ids = Array.zeroCreate<NodeId> nodeCount

  use builder = new NodeIdBuilder()

  ids[0] <- builder.Compute(Repository, input.Repository, root, RepositoryId.value input.Repository, 0u)

  for index in 0 .. directoryCount - 1 do
    checkCancellation cancellation index
    ids[1 + index] <- builder.Compute(Directory, input.Repository, input.Directories[index], "", 0u)

  for index in 0 .. fileCount - 1 do
    checkCancellation cancellation index
    ids[1 + directoryCount + index] <- builder.Compute(File, input.Repository, input.Files[index].Path, "", 0u)

  for fileIndex in 0 .. fileCount - 1 do
    let file = input.Files[fileIndex]
    let start = symbolStart[fileIndex]

    for index in 0 .. file.Symbols.Length - 1 do
      checkCancellation cancellation (start + index)
      let symbol = file.Symbols[index]

      ids[symbolBase + start + index] <-
        builder.Compute(symbol.Kind, input.Repository, file.Path, symbol.QualifiedName, symbol.Ordinal)

  for index in 0 .. configArray.Length - 1 do
    checkCancellation cancellation index
    // 構成シンボルはファイルに属さない。ID の材料からもパスを外す。
    ids[configBase + index] <- builder.Compute(ConfigSymbol, input.Repository, root, configArray[index], 0u)

  // 衝突は握りつぶさず、公開を止める失敗として扱う。docs/graph-model.md 4.2 を参照。
  let sortedIds = Array.copy ids
  Array.sortInPlace sortedIds
  let mutable idCollisions = 0

  for index in 1 .. nodeCount - 1 do
    checkCancellation cancellation index

    if sortedIds[index] = sortedIds[index - 1] then
      idCollisions <- idCollisions + 1
      diagnostics.Add(NodeIdCollision, "", $"ノード ID {sortedIds[index]} が重複しています")

  // --- エッジ ---
  cancellation.ThrowIfCancellationRequested()
  let indexByPath = Dictionary<string, int>(directoryCount + 1, StringComparer.Ordinal)
  indexByPath[value root] <- 0

  for index in 0 .. directoryCount - 1 do
    indexByPath[value input.Directories[index]] <- 1 + index

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

  let contains = ResizeArray<struct (int * int)> nodeCount
  let declares = ResizeArray<struct (int * int)>()
  let defines = ResizeArray<struct (int * int)>()
  let guardedBy = ResizeArray<struct (int * int)>()

  for index in 0 .. directoryCount - 1 do
    checkCancellation cancellation index
    contains.Add(struct (resolveParent input.Directories[index], 1 + index))

  for index in 0 .. fileCount - 1 do
    checkCancellation cancellation index
    contains.Add(struct (resolveParent input.Files[index].Path, 1 + directoryCount + index))

  for fileIndex in 0 .. fileCount - 1 do
    let file = input.Files[fileIndex]
    let fileNode = 1 + directoryCount + fileIndex
    let start = symbolStart[fileIndex]

    for index in 0 .. file.Symbols.Length - 1 do
      checkCancellation cancellation (start + index)
      let symbol = file.Symbols[index]
      let node = symbolBase + start + index

      // 親が範囲外の値でも成果物を壊さない。ファイル直下として扱う。
      let parentNode =
        if symbol.Parent >= 0 && symbol.Parent < file.Symbols.Length then
          symbolBase + start + symbol.Parent
        else fileNode

      contains.Add(struct (parentNode, node))

      if symbol.Flags.HasFlag NodeFlags.DeclarationOnly then declares.Add(struct (fileNode, node))
      if symbol.Flags.HasFlag NodeFlags.Definition then defines.Add(struct (fileNode, node))

    for reference in file.References do
      if reference.Kind = GuardedBy then
        match configIndexOf.TryGetValue reference.Target with
        | true, configNode ->
          let sourceNode =
            if reference.Source >= 0 && reference.Source < file.Symbols.Length then
              symbolBase + start + reference.Source
            else fileNode

          guardedBy.Add(struct (sourceNode, configNode))
        | false, _ -> ()

  for index in 0 .. configArray.Length - 1 do
    checkCancellation cancellation index
    contains.Add(struct (0, configBase + index))

  let edgeArrays =
    [| Contains, contains.ToArray()
       Declares, declares.ToArray()
       Defines, defines.ToArray()
       GuardedBy, guardedBy.ToArray() |]

  let csrByKind = Dictionary<EdgeKind, struct (uint64[] * uint32[])>()
  let reverseByKind = Dictionary<EdgeKind, struct (uint64[] * uint32[])>()
  let mutable edgeTotal = 0

  for kind, edges in edgeArrays do
    cancellation.ThrowIfCancellationRequested()
    let reversed = edges |> Array.map (fun (struct (source, target)) -> struct (target, source))
    let struct (offsets, targets) = buildCsr nodeCount edges cancellation
    let struct (reverseOffsets, reverseTargets) = buildCsr nodeCount reversed cancellation
    csrByKind[kind] <- struct (offsets, targets)
    reverseByKind[kind] <- struct (reverseOffsets, reverseTargets)
    edgeTotal <- edgeTotal + targets.Length

  // --- 書き出し ---
  cancellation.ThrowIfCancellationRequested()
  let descriptors = List<SegmentDescriptor>()
  let stringOffsets = strings.Offsets()
  let blobLength = stringOffsets[strings.Count]

  do
    use writer = new SegmentWriter(segmentDirectory, segmentName "strings")

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
        checkCancellation cancellation index
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
    use writer = new SegmentWriter(segmentDirectory, segmentName "stroffsets")
    let payloadLength = uint64 (stringOffsets.Length * 8)

    writer.WriteHeader
      { Kind = Format.StringOffsets
        PrimaryCount = uint64 strings.Count
        SecondaryCount = blobLength
        RecordLength = 8u
        PayloadLength = payloadLength }

    let mutable offsetIndex = 0

    for offset in stringOffsets do
      checkCancellation cancellation offsetIndex
      offsetIndex <- offsetIndex + 1
      BinaryPrimitives.WriteUInt64LittleEndian(writer.Reserve 8, offset)

    descriptors.Add(writer.Complete())

  do
    use writer = new SegmentWriter(segmentDirectory, segmentName "files")
    let payloadLength = uint64 fileCount * uint64 Format.RecordLength

    writer.WriteHeader
      { Kind = Format.Files
        PrimaryCount = uint64 fileCount
        SecondaryCount = 0UL
        RecordLength = uint32 Format.RecordLength
        PayloadLength = payloadLength }

    for index in 0 .. fileCount - 1 do
      checkCancellation cancellation index
      let file = input.Files[index]
      let record = writer.Reserve Format.RecordLength
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.FileRecord.PathOffset, 4), uint32 filePathRefs[index])

      BinaryPrimitives.WriteUInt16LittleEndian(
        record.Slice(Format.FileRecord.LanguageOffset, 2),
        Language.toCode file.Language
      )

      BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(Format.FileRecord.EncodingOffset, 2), file.EncodingCode)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.FileRecord.FlagsOffset, 4), uint32 file.Flags)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.FileRecord.LineCountOffset, 4), uint32 file.LineCount)
      BinaryPrimitives.WriteInt64LittleEndian(record.Slice(Format.FileRecord.SizeOffset, 8), file.SizeBytes)

      ReadOnlySpan(file.ContentHash)
        .Slice(0, Format.FileRecord.ContentHashLength)
        .CopyTo(record.Slice(Format.FileRecord.ContentHashOffset, Format.FileRecord.ContentHashLength))

    descriptors.Add(writer.Complete())

  let nodeKindCounts = Dictionary<NodeKind, int>()

  do
    use writer = new SegmentWriter(segmentDirectory, segmentName "nodes")
    let payloadLength = uint64 nodeCount * uint64 Format.RecordLength

    writer.WriteHeader
      { Kind = Format.Nodes
        PrimaryCount = uint64 nodeCount
        SecondaryCount = 0UL
        RecordLength = uint32 Format.RecordLength
        PayloadLength = payloadLength }

    let writeNode
      (id: NodeId)
      (kind: NodeKind)
      (language: Language)
      (flags: NodeFlags)
      (fileIndex: uint32)
      (nameRef: int)
      (qualifiedRef: int)
      (startLine: int)
      (endLine: int)
      (ordinal: uint32)
      (startByte: int64)
      (endByte: int64)
      =
      match nodeKindCounts.TryGetValue kind with
      | true, existing -> nodeKindCounts[kind] <- existing + 1
      | false, _ -> nodeKindCounts[kind] <- 1

      let record = writer.Reserve Format.RecordLength
      NodeId.writeTo (record.Slice(Format.NodeRecord.IdOffset, Ids.NodeIdLength)) id
      record[Format.NodeRecord.KindOffset] <- NodeKind.toCode kind

      BinaryPrimitives.WriteUInt16LittleEndian(
        record.Slice(Format.NodeRecord.LanguageOffset, 2),
        Language.toCode language
      )

      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.FlagsOffset, 4), uint32 flags)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.FileIndexOffset, 4), fileIndex)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.NameOffset, 4), uint32 nameRef)

      BinaryPrimitives.WriteUInt32LittleEndian(
        record.Slice(Format.NodeRecord.QualifiedNameOffset, 4),
        uint32 qualifiedRef
      )

      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.StartLineOffset, 4), uint32 startLine)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.EndLineOffset, 4), uint32 endLine)
      BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.NodeRecord.OrdinalOffset, 4), ordinal)
      BinaryPrimitives.WriteInt64LittleEndian(record.Slice(Format.NodeRecord.StartByteOffset, 8), startByte)
      BinaryPrimitives.WriteInt64LittleEndian(record.Slice(Format.NodeRecord.EndByteOffset, 8), endByte)

    writeNode
      ids[0]
      Repository
      Unknown
      NodeFlags.None
      Format.NodeRecord.NoFile
      repositoryNameRef
      repositoryNameRef
      0
      0
      0u
      0L
      0L

    for index in 0 .. directoryCount - 1 do
      checkCancellation cancellation index

      writeNode
        ids[1 + index]
        Directory
        Unknown
        NodeFlags.None
        Format.NodeRecord.NoFile
        directoryNameRefs[index]
        directoryPathRefs[index]
        0
        0
        0u
        0L
        0L

    for index in 0 .. fileCount - 1 do
      checkCancellation cancellation index
      let file = input.Files[index]

      writeNode
        ids[1 + directoryCount + index]
        File
        file.Language
        file.Flags
        (uint32 index)
        fileNameRefs[index]
        filePathRefs[index]
        (if file.LineCount > 0 then 1 else 0)
        file.LineCount
        0u
        0L
        file.SizeBytes

    for fileIndex in 0 .. fileCount - 1 do
      let file = input.Files[fileIndex]
      let start = symbolStart[fileIndex]

      for index in 0 .. file.Symbols.Length - 1 do
        checkCancellation cancellation (start + index)
        let symbol = file.Symbols[index]

        writeNode
          ids[symbolBase + start + index]
          symbol.Kind
          file.Language
          symbol.Flags
          (uint32 fileIndex)
          symbolNameRefs[start + index]
          symbolQualifiedRefs[start + index]
          symbol.StartLine
          symbol.EndLine
          symbol.Ordinal
          (int64 symbol.StartByte)
          (int64 symbol.EndByte)

    for index in 0 .. configArray.Length - 1 do
      checkCancellation cancellation index

      writeNode
        ids[configBase + index]
        ConfigSymbol
        Unknown
        NodeFlags.None
        Format.NodeRecord.NoFile
        configNameRefs[index]
        configNameRefs[index]
        0
        0
        0u
        0L
        0L

    descriptors.Add(writer.Complete())

  for kind in producedEdgeKinds do
    let struct (offsets, targets) = csrByKind[kind]

    descriptors.Add(
      writeCsr segmentDirectory (segmentName $"edges.{EdgeKind.name kind}") nodeCount offsets targets cancellation
    )

    // 後方 CSR。「このノードを指すのは誰か」を全走査せずに解けるようにする。
    let struct (reverseOffsets, reverseTargets) = reverseByKind[kind]

    descriptors.Add(
      writeCsr
        segmentDirectory
        (segmentName $"redges.{EdgeKind.name kind}")
        nodeCount
        reverseOffsets
        reverseTargets
        cancellation
    )

  do
    use writer = new SegmentWriter(segmentDirectory, segmentName "refs")
    let payloadLength = uint64 referenceTotal * uint64 Format.RecordLength

    writer.WriteHeader
      { Kind = Format.References
        PrimaryCount = uint64 referenceTotal
        SecondaryCount = 0UL
        RecordLength = uint32 Format.RecordLength
        PayloadLength = payloadLength }

    let mutable cursor = 0

    for fileIndex in 0 .. fileCount - 1 do
      let file = input.Files[fileIndex]
      let fileNode = 1 + directoryCount + fileIndex
      let start = symbolStart[fileIndex]

      for reference in file.References do
        checkCancellation cancellation cursor

        let sourceNode =
          if reference.Source >= 0 && reference.Source < file.Symbols.Length then
            symbolBase + start + reference.Source
          else fileNode

        let record = writer.Reserve Format.RecordLength
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.ReferenceRecord.SourceOffset, 4), uint32 sourceNode)

        BinaryPrimitives.WriteUInt32LittleEndian(
          record.Slice(Format.ReferenceRecord.TargetOffset, 4),
          uint32 referenceTargetRefs[cursor]
        )

        BinaryPrimitives.WriteUInt32LittleEndian(
          record.Slice(Format.ReferenceRecord.QualifierOffset, 4),
          uint32 referenceQualifierRefs[cursor]
        )

        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(Format.ReferenceRecord.LineOffset, 4), uint32 reference.Line)

        BinaryPrimitives.WriteInt64LittleEndian(
          record.Slice(Format.ReferenceRecord.StartByteOffset, 8),
          int64 reference.StartByte
        )

        BinaryPrimitives.WriteInt64LittleEndian(
          record.Slice(Format.ReferenceRecord.EndByteOffset, 8),
          int64 reference.EndByte
        )

        BinaryPrimitives.WriteUInt16LittleEndian(
          record.Slice(Format.ReferenceRecord.LanguageOffset, 2),
          Language.toCode reference.Language
        )

        record[Format.ReferenceRecord.EdgeKindOffset] <- EdgeKind.toCode reference.Kind
        record[Format.ReferenceRecord.StageOffset] <- reference.Stage
        record[Format.ReferenceRecord.ConfidenceOffset] <- Confidence.toCode reference.Confidence
        cursor <- cursor + 1

    descriptors.Add(writer.Complete())

  do
    use writer = new SegmentWriter(segmentDirectory, segmentName "idmap")
    let payloadLength = uint64 nodeCount * uint64 (Ids.NodeIdLength + 4)

    writer.WriteHeader
      { Kind = Format.IdMap
        PrimaryCount = uint64 nodeCount
        SecondaryCount = 0UL
        RecordLength = uint32 (Ids.NodeIdLength + 4)
        PayloadLength = payloadLength }

    let order = Array.init nodeCount id
    Array.sortInPlaceWith (fun (a: int) (b: int) -> compare ids[a] ids[b]) order

    let mutable idMapIndex = 0

    for position in order do
      checkCancellation cancellation idMapIndex
      idMapIndex <- idMapIndex + 1
      NodeId.writeTo (writer.Reserve Ids.NodeIdLength) ids[position]

    for position in order do
      checkCancellation cancellation idMapIndex
      idMapIndex <- idMapIndex + 1
      BinaryPrimitives.WriteUInt32LittleEndian(writer.Reserve 4, uint32 position)

    descriptors.Add(writer.Complete())

  let ordered = descriptors.ToArray()
  Array.sortInPlaceWith (fun (a: SegmentDescriptor) (b: SegmentDescriptor) -> String.CompareOrdinal(a.Name, b.Name)) ordered

  let nodeKinds =
    nodeKindCounts
    |> Seq.map (fun entry -> { Kind = NodeKind.name entry.Key; Count = entry.Value })
    |> Seq.sortWith (fun left right -> String.CompareOrdinal(left.Kind, right.Kind))
    |> Seq.toArray

  let edgeKinds =
    producedEdgeKinds
    |> Array.map (fun kind ->
      let struct (_, targets) = csrByKind[kind]

      { Kind = EdgeKind.name kind
        Count = targets.Length })
    |> Array.sortWith (fun left right -> String.CompareOrdinal(left.Kind, right.Kind))

  { Segments = ordered
    NodeCount = nodeCount
    EdgeCount = edgeTotal
    StringCount = strings.Count
    StringBytes = strings.TotalBytes
    SymbolCount = symbolTotal
    ConfigSymbolCount = configArray.Length
    ReferenceCount = referenceTotal
    NodeKinds = nodeKinds
    EdgeKinds = edgeKinds
    IdCollisions = idCollisions }
