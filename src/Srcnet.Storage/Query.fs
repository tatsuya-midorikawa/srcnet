/// 成果物に対する照会。
///
/// セグメントの詳細を隠し、「ノード属性を読む」「文字列を復元する」「CSR を辿る」だけを
/// 見せる。AI エージェントは CLI 経由でこの層の結果を受け取る（docs/query-and-cli.md 1, 7）。
///
/// 成果物全体を managed object へ展開しない。必要なセグメントを memory-mapped で開き、
/// 触れたページだけを読む。返す `ReadOnlySpan` の寿命は `GraphView` の生存期間に一致する。
module Srcnet.Storage.Query

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.Text
open System.Threading
open Srcnet.Core
open Srcnet.Core.Graph
open Srcnet.Core.Ids

/// 照会の失敗。想定内の失敗は例外ではなくドメイン エラーで返す。
type QueryError =
  | ArtifactUnreadable of error: Manifest.ManifestError
  | SegmentUnavailable of name: string * error: Reader.OpenError
  | SegmentMissing of name: string
  | SegmentCorrupt of name: string * detail: string
  /// 読み取り中に再索引が完了し、一貫した世代を観測できなかった。
  | GenerationChanged
  | InvalidArgument of name: string * detail: string
  | ViewDisposed
  | SearchIndexRequired

/// Artifact-boundary failures from the low-level accessors. CLI/export catch only this type.
exception QueryException of QueryError

let private corrupt name detail =
  raise(QueryException(SegmentCorrupt(name, detail)))

let private invalid name detail =
  raise(QueryException(InvalidArgument(name, detail)))

module QueryError =

  let describe error =
    match error with
    | ArtifactUnreadable error -> Manifest.ManifestError.describe error
    | SegmentUnavailable(name, error) -> $"{name}: {Reader.OpenError.describe error}"
    | SegmentMissing name -> $"セグメントがありません: {name}"
    | SegmentCorrupt(name, detail) -> $"{name}: {detail}"
    | GenerationChanged -> "読み取り中に成果物が更新されました。もう一度実行してください"
    | InvalidArgument(name, detail) -> $"{name}: {detail}"
    | ViewDisposed -> "GraphView has been disposed"
    | SearchIndexRequired ->
      "検索索引がありません。Reindex or explicitly build and publish a lexical lookup; no graph scan was performed."

/// 1 ノード分の属性。文字列は参照のままで、復元は必要な分だけ行う。
[<Struct>]
type NodeView =
  {
    Index: int
    Id: NodeId
    Kind: NodeKind
    Language: Language
    Flags: NodeFlags
    /// 所属ファイルの密インデックス。ファイルに属さないノードは `Format.NodeRecord.NoFile`。
    FileIndex: uint32
    NameRef: int
    QualifiedNameRef: int
    StartLine: int
    EndLine: int
    Ordinal: uint32
    StartByte: int64
    EndByte: int64
  }

/// 探索の向き。
type Direction =
  | Outgoing
  | Incoming
  | Both

module Direction =

  let tryParse (text: string) =
    match text with
    | "out" -> ValueSome Outgoing
    | "in" -> ValueSome Incoming
    | "both" -> ValueSome Both
    | _ -> ValueNone

  let name direction =
    match direction with
    | Outgoing -> "out"
    | Incoming -> "in"
    | Both -> "both"

/// 世代の切替と競合したときに読み直す回数の上限。
[<Literal>]
let private StableOpenAttempts = 4

/// 1 つのエッジ種別に対する前方・後方 CSR。
[<Sealed>]
type private EdgeSegments(forward: Reader.MappedSegment, backward: Reader.MappedSegment) =
  member _.Forward = forward
  member _.Backward = backward

  interface IDisposable with

    member _.Dispose() =
      (forward :> IDisposable).Dispose()
      (backward :> IDisposable).Dispose()

/// 成果物への読み取り専用のビュー。
///
/// 破棄すると、このビューが返したすべての `ReadOnlySpan` は無効になる。
/// 呼び出し側は span を保持せず、必要な値を取り出してから破棄すること。
/// Dispose must not run concurrently with an accessor or while a returned span is in use.
[<Sealed>]
type GraphView
  private
  (
    manifest: Manifest.Manifest,
    nodes: Reader.MappedSegment,
    files: Reader.MappedSegment,
    strings: Reader.MappedSegment,
    stringOffsets: Reader.MappedSegment,
    idMap: Reader.MappedSegment,
    edges: Dictionary<EdgeKind, EdgeSegments>,
    lookup: Reader.MappedSegment voption
  ) =
  let mutable disposed = false
  let nodeCount = int nodes.Header.PrimaryCount
  let stringCount = int stringOffsets.Header.PrimaryCount

  let check () =
    if disposed then
      raise(QueryException ViewDisposed)

  let checkNode index =
    check()

    if index < 0 || index >= nodeCount then
      invalid "node" "Index out of range"

  let checkStringRef name (index: uint32) =
    if index >= uint32 stringCount then
      corrupt name "String reference out of range"

    int index

  let language name code =
    let value = Language.ofCode code

    if Language.toCode value <> code then
      corrupt name "Unknown language code"

    value

  let flags name code =
    if code &&& ~~~ 8191u <> 0u then
      corrupt name "Unknown node flags"

    LanguagePrimitives.EnumOfValue<uint32, NodeFlags> code

  let fileRecord (fileIndex: uint32) =
    check()

    if fileIndex >= uint32 files.Header.PrimaryCount then
      invalid "fileIndex" "Index out of range"

    let record =
      files.Payload.Slice(int fileIndex * Format.RecordLength, Format.RecordLength)

    language "files" (BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(Format.FileRecord.LanguageOffset, 2)))
    |> ignore

    let encoding =
      BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(Format.FileRecord.EncodingOffset, 2))

    if encoding < 1us || encoding > 12us then
      corrupt "files" "Unknown encoding code"

    flags "files" (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.FileRecord.FlagsOffset, 4)))
    |> ignore

    if
      BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.FileRecord.LineCountOffset, 4)) > uint32
        Int32.MaxValue
      || BinaryPrimitives.ReadInt64LittleEndian(record.Slice(Format.FileRecord.SizeOffset, 8)) < 0L
    then
      corrupt "files" "Invalid file extent"

    record

  member _.Manifest =
    check()
    manifest

  member _.NodeCount =
    check()
    nodeCount

  member _.StringCount =
    check()
    stringCount

  member _.FileCount =
    check()
    int files.Header.PrimaryCount

  member internal _.Lookup =
    check()
    lookup

  /// 成果物が持つエッジ種別。マニフェストの記録ではなく、実際に開けたセグメントで決まる。
  member _.EdgeKinds =
    check()
    edges.Keys |> Seq.sortBy EdgeKind.toCode |> Seq.toArray

  member private _.StringRange(index: int) =
    check()

    if index < 0 || index >= stringCount then
      invalid "string" "Index out of range"

    let offsets = stringOffsets.Payload
    let start = BinaryPrimitives.ReadUInt64LittleEndian(offsets.Slice(index * 8, 8))

    let finish =
      BinaryPrimitives.ReadUInt64LittleEndian(offsets.Slice((index + 1) * 8, 8))

    let blob = strings.Payload

    if finish < start || finish > uint64 blob.Length then
      corrupt "stroffsets" "String offsets out of range"

    if finish - start > uint64 Format.Lookup.MaxKeyBytes then
      corrupt "strings" "String exceeds the supported 1 MiB UTF-8 limit"

    struct (int start, int(finish - start))

  member internal this.StringByteLength(index: int) =
    let struct (_, length) = this.StringRange index
    length

  /// 文字列を UTF-8 のまま取り出す。比較だけが目的なら復号せずに済む。
  member this.StringBytes(index: int) : ReadOnlySpan<byte> =
    let struct (start, length) = this.StringRange index
    let bytes = strings.Payload.Slice(start, length)

    try
      Strings.utf8.GetCharCount bytes |> ignore
    with :? DecoderFallbackException ->
      corrupt "strings" "Invalid UTF-8"

    bytes

  /// 文字列を復元する。必要な分だけ呼ぶこと。
  member this.String(index: int) =
    let bytes = this.StringBytes index
    Strings.utf8.GetString bytes

  member _.Node(index: int) : NodeView =
    checkNode index
    let record = nodes.Payload.Slice(index * Format.RecordLength, Format.RecordLength)

    let kind =
      match NodeKind.ofCode record[Format.NodeRecord.KindOffset] with
      | ValueSome value -> value
      | ValueNone -> corrupt "nodes" "Unknown node kind"

    let languageCode =
      BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(Format.NodeRecord.LanguageOffset, 2))

    let fileIndex =
      BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.FileIndexOffset, 4))

    if
      fileIndex <> Format.NodeRecord.NoFile
      && fileIndex >= uint32 files.Header.PrimaryCount
    then
      corrupt "nodes" "File reference out of range"

    match kind with
    | File when fileIndex = Format.NodeRecord.NoFile -> corrupt "nodes" "File node has no file record"
    | Repository
    | Directory when fileIndex <> Format.NodeRecord.NoFile -> corrupt "nodes" "Global node has a file record"
    | _ -> ()

    let startLine =
      BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.StartLineOffset, 4))

    let endLine =
      BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.EndLineOffset, 4))

    let startByte =
      BinaryPrimitives.ReadInt64LittleEndian(record.Slice(Format.NodeRecord.StartByteOffset, 8))

    let endByte =
      BinaryPrimitives.ReadInt64LittleEndian(record.Slice(Format.NodeRecord.EndByteOffset, 8))

    if
      endLine > uint32 Int32.MaxValue
      || startLine > endLine
      || startByte < 0L
      || endByte < startByte
    then
      corrupt "nodes" "Invalid node extent"

    { Index = index
      Id = NodeId.ofBytes(record.Slice(Format.NodeRecord.IdOffset, Ids.NodeIdLength))
      Kind = kind
      Language = language "nodes" languageCode
      Flags = flags "nodes" (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.FlagsOffset, 4)))
      FileIndex = fileIndex
      NameRef =
        checkStringRef "nodes" (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.NameOffset, 4)))
      QualifiedNameRef =
        checkStringRef
          "nodes"
          (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.QualifiedNameOffset, 4)))
      StartLine = int startLine
      EndLine = int endLine
      Ordinal = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.OrdinalOffset, 4))
      StartByte = startByte
      EndByte = endByte }

  /// ファイル レコードのパス文字列参照。
  member _.FilePathRef(fileIndex: uint32) =
    let record = fileRecord fileIndex
    checkStringRef "files" (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.FileRecord.PathOffset, 4)))

  /// v3 dense order is Repository, Directories, Files, Symbols, ConfigSymbols.
  /// NoFile represents legitimate absence; any other invalid reference is an error.
  member this.FileNodeIndex(fileIndex: uint32) : int voption =
    check()

    if fileIndex = Format.NodeRecord.NoFile then
      ValueNone
    else
      let path = this.FilePathRef fileIndex
      let index = 1L + int64 manifest.Counts.Directories + int64 fileIndex

      if index >= int64 nodeCount then
        corrupt "files" "File node is outside the dense table"

      let node = this.Node(int index)

      if
        node.Kind <> File
        || node.FileIndex <> fileIndex
        || node.QualifiedNameRef <> path
      then
        corrupt "files" "File node disagrees with the dense file schema"

      ValueSome(int index)

  /// ノード ID から密インデックスを引く。`.idmap` は ID の昇順なので二分探索できる。
  member this.TryResolve(id: NodeId) =
    check()
    let payload = idMap.Payload
    let count = int idMap.Header.PrimaryCount
    let indicesOffset = count * Ids.NodeIdLength
    let mutable target = Span<byte>(Array.zeroCreate Ids.NodeIdLength)
    NodeId.writeTo target id
    let key = Span.op_Implicit target: ReadOnlySpan<byte>

    let mutable low = 0
    let mutable high = count - 1
    let mutable found = ValueNone

    while low <= high do
      let middle = low + (high - low) / 2
      let candidate = payload.Slice(middle * Ids.NodeIdLength, Ids.NodeIdLength)

      let index =
        BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(indicesOffset + middle * 4, 4))

      if index >= uint32 nodeCount then
        corrupt "idmap" "Node reference out of range"

      if (this.Node(int index)).Id <> NodeId.ofBytes candidate then
        corrupt "idmap" "Node ID does not match its reference"

      if
        middle > 0
        && payload
             .Slice((middle - 1) * Ids.NodeIdLength, Ids.NodeIdLength)
             .SequenceCompareTo(candidate)
           >= 0
      then
        corrupt "idmap" "IDs are not strictly increasing"

      if
        middle + 1 < count
        && candidate.SequenceCompareTo(payload.Slice((middle + 1) * Ids.NodeIdLength, Ids.NodeIdLength))
           >= 0
      then
        corrupt "idmap" "IDs are not strictly increasing"

      let comparison = candidate.SequenceCompareTo key

      if comparison = 0 then
        found <- ValueSome(int index)
        low <- high + 1
      elif comparison < 0 then
        low <- middle + 1
      else
        high <- middle - 1

    found

  member internal _.Adjacency(index: int, kind: EdgeKind, incoming: bool) =
    checkNode index

    if isNull(box kind) then
      invalid "kind" "Edge kind must not be null"

    match edges.TryGetValue kind with
    | false, _ -> ValueNone
    | true, segments ->
      let segment = if incoming then segments.Backward else segments.Forward
      let payload = segment.Payload
      let start = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(index * 8, 8))

      let finish =
        BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice((index + 1) * 8, 8))

      if finish < start || finish > segment.Header.SecondaryCount then
        corrupt "CSR" "Adjacency offsets out of range"

      ValueSome(struct (segment, (nodeCount + 1) * 8 + int start * 4, int(finish - start)))

  member internal _.Target(segment: Reader.MappedSegment, offset: int, position: int) =
    check()
    let payload = segment.Payload

    let target =
      BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset + position * 4, 4))

    if target >= uint32 nodeCount then
      corrupt "CSR" "Target out of range"

    if
      position > 0
      && BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset + (position - 1) * 4, 4))
         >= target
    then
      corrupt "CSR" "Targets are not strictly increasing"

    int target

  /// Counts one forward/reverse CSR row without materializing or scanning its targets.
  member this.NeighborCount(index: int, kind: EdgeKind, incoming: bool) : int =
    match this.Adjacency(index, kind, incoming) with
    | ValueNone -> 0
    | ValueSome(struct (_, _, count)) -> count

  /// Reads one sorted target from a forward/reverse row. Callers bound their
  /// iteration and check cancellation; no adjacency array or mapped handle escapes.
  member this.NeighborAt(index: int, kind: EdgeKind, incoming: bool, position: int) : int =
    match this.Adjacency(index, kind, incoming) with
    | ValueSome(struct (segment, offset, count)) when position >= 0 && position < count ->
      this.Target(segment, offset, position)
    | _ -> invalid "position" "Adjacency position out of range"

  /// Materializes one adjacency row. Use bounded traversal APIs for graph exploration.
  member this.Neighbors(index: int, kind: EdgeKind, direction: Direction) : int[] =
    if isNull(box direction) then
      invalid "direction" "Direction must not be null"

    let read incoming =
      match this.Adjacency(index, kind, incoming) with
      | ValueNone -> Array.empty
      | ValueSome(struct (segment, offset, count)) ->
        Array.init count (fun position -> this.Target(segment, offset, position))

    match direction with
    | Outgoing -> read false
    | Incoming -> read true
    | Both -> Array.append (read false) (read true) |> Array.distinct |> Array.sort

  interface IDisposable with

    member _.Dispose() =
      if not disposed then
        disposed <- true

        for entry in edges.Values do
          (entry :> IDisposable).Dispose()

        match lookup with
        | ValueSome segment -> (segment :> IDisposable).Dispose()
        | ValueNone -> ()

        (idMap :> IDisposable).Dispose()
        (stringOffsets :> IDisposable).Dispose()
        (strings :> IDisposable).Dispose()
        (files :> IDisposable).Dispose()
        (nodes :> IDisposable).Dispose()

  /// 成果物を開く。
  ///
  /// マニフェストを読んでからセグメントを開くまでの間に公開が起きると、退役した世代を
  /// 参照してしまう。開いた後にマニフェストを読み直し、変化していればやり直す。
  static member Open(outputDirectory: string) : Result<GraphView, QueryError> =
    if
      String.IsNullOrWhiteSpace outputDirectory
      || outputDirectory.IndexOf(char 0) >= 0
    then
      Error(InvalidArgument("outputDirectory", "Expected a nonempty directory path"))
    else

      let mutable attempt = 0
      let mutable outcome = ValueNone

      while outcome.IsNone && attempt < StableOpenAttempts do
        attempt <- attempt + 1

        match Manifest.read outputDirectory with
        | Error error -> outcome <- ValueSome(Error(ArtifactUnreadable error))
        | Ok manifest ->
          let opened = List<IDisposable>()

          let openSegment (suffix: string) (expected: Format.SegmentKind) =
            match
              manifest.Segments
              |> Array.tryFind(fun segment -> segment.Name.EndsWith("." + suffix, StringComparison.Ordinal))
            with
            | None -> Error(SegmentMissing suffix)
            | Some descriptor when
              (manifest.Segments
               |> Array.filter(fun segment -> segment.Name.EndsWith("." + suffix, StringComparison.Ordinal)))
                .Length
              <> 1
              ->
              Error(SegmentCorrupt(descriptor.Name, "Duplicate segment role"))
            | Some descriptor ->
              match Artifact.tryResolveSegment outputDirectory descriptor.Name with
              | Error error -> Error(SegmentCorrupt(descriptor.Name, Artifact.PathError.describe error))
              | Ok path ->
                let mapped =
                  try
                    Reader.MappedSegment.Open path
                  with
                  | :? IO.IOException as error -> Error(Reader.OpenFailed(path, error.Message))
                  | :? UnauthorizedAccessException as error -> Error(Reader.OpenFailed(path, error.Message))

                match mapped with
                | Error error -> Error(SegmentUnavailable(descriptor.Name, error))
                | Ok segment ->
                  opened.Add segment

                  if segment.Header.Kind <> expected then
                    Error(SegmentCorrupt(suffix, "Unexpected segment kind"))
                  elif int64 segment.ByteLength <> descriptor.ByteLength then
                    Error(SegmentCorrupt(suffix, "Manifest length mismatch"))
                  else
                    Ok segment

          let result =
            match
              openSegment "nodes" Format.Nodes,
              openSegment "files" Format.Files,
              openSegment "strings" Format.Strings,
              openSegment "stroffsets" Format.StringOffsets,
              openSegment "idmap" Format.IdMap
            with
            | Ok nodes, Ok files, Ok strings, Ok stringOffsets, Ok idMap ->
              let edges = Dictionary<EdgeKind, EdgeSegments>()
              let mutable failure = ValueNone

              let fail name detail =
                failure <- ValueSome(SegmentCorrupt(name, detail))

              let counts = manifest.Counts

              if
                nodes.Header.PrimaryCount <> uint64 counts.Nodes
                || files.Header.PrimaryCount <> uint64 counts.Files
                || strings.Header.PrimaryCount <> uint64 counts.Strings
                || strings.Header.SecondaryCount <> uint64 counts.StringBytes
                || stringOffsets.Header.PrimaryCount <> strings.Header.PrimaryCount
                || stringOffsets.Header.SecondaryCount <> strings.Header.SecondaryCount
                || idMap.Header.PrimaryCount <> nodes.Header.PrimaryCount
                || 1L + int64 counts.Directories + int64 counts.Files > int64 counts.Nodes
              then
                fail "segments" "Segment counts disagree with the manifest"

              if
                BinaryPrimitives.ReadUInt64LittleEndian(stringOffsets.Payload.Slice(0, 8))
                <> 0UL
                || BinaryPrimitives.ReadUInt64LittleEndian(
                     stringOffsets.Payload.Slice(int stringOffsets.Header.PrimaryCount * 8, 8)
                   )
                   <> strings.Header.SecondaryCount
              then
                fail "stroffsets" "Invalid first or terminal string offset"

              // 成果物が持つ種別だけを開く。マニフェストが数えている種別を辿ることで、
              // 種別が増えても照会側の対応が漏れない。
              for entry in manifest.Counts.EdgeKinds do
                if failure.IsNone then
                  match EdgeKind.all |> Array.tryFind(fun kind -> EdgeKind.name kind = entry.Kind) with
                  | None -> fail "manifest" $"Unknown edge kind {entry.Kind}"
                  | Some kind ->
                    match
                      openSegment $"edges.{entry.Kind}" Format.AdjacencyCsr,
                      openSegment $"redges.{entry.Kind}" Format.AdjacencyCsr
                    with
                    | Ok forward, Ok backward ->
                      for segment in [| forward; backward |] do
                        if
                          segment.Header.PrimaryCount <> nodes.Header.PrimaryCount
                          || segment.Header.SecondaryCount <> uint64 entry.Count
                        then
                          fail entry.Kind "CSR counts disagree"
                        elif
                          BinaryPrimitives.ReadUInt64LittleEndian(segment.Payload.Slice(0, 8)) <> 0UL
                          || BinaryPrimitives.ReadUInt64LittleEndian(
                               segment.Payload.Slice(int nodes.Header.PrimaryCount * 8, 8)
                             )
                             <> segment.Header.SecondaryCount
                        then
                          fail entry.Kind "Invalid first or terminal CSR offset"

                      if edges.ContainsKey kind then
                        fail entry.Kind "Duplicate edge kind"
                      else
                        edges[kind] <- new EdgeSegments(forward, backward)
                    | Error error, _
                    | _, Error error -> failure <- ValueSome error

              let mutable lookup = ValueNone

              if
                manifest.Segments
                |> Array.exists(fun segment -> segment.Name.EndsWith(".lookup", StringComparison.Ordinal))
              then
                match openSegment "lookup" Format.LexicalLookup with
                | Error error -> failure <- ValueSome error
                | Ok segment ->
                  let data = segment.Payload
                  let blobBytes = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(16, 8))

                  let minimum =
                    32UL + segment.Header.PrimaryCount * 24UL + segment.Header.SecondaryCount * 8UL

                  if
                    BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4))
                    <> Format.Lookup.Version
                    || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) <> uint32 counts.Nodes
                    || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)) <> uint32 counts.Files
                    || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4))
                       <> uint32 counts.Strings
                    || blobBytes <> uint64 data.Length - minimum
                    || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24, 4)) = 0u
                    || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24, 4))
                       >= uint32 segment.Header.PrimaryCount
                    || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(28, 4)) <> 0u
                  then
                    fail "lookup" "Invalid lookup prelude or counts"
                  else
                    lookup <- ValueSome segment

              match failure with
              | ValueSome error -> Error error
              | ValueNone -> Ok(new GraphView(manifest, nodes, files, strings, stringOffsets, idMap, edges, lookup))
            | Error error, _, _, _, _
            | _, Error error, _, _, _
            | _, _, Error error, _, _
            | _, _, _, Error error, _
            | _, _, _, _, Error error -> Error error

          match result with
          | Error error ->
            for item in opened do
              item.Dispose()

            match Manifest.read outputDirectory with
            | Ok current when current <> manifest -> ()
            | Ok _ -> outcome <- ValueSome(Error error)
            | Error changed -> outcome <- ValueSome(Error(ArtifactUnreadable changed))
          | Ok view ->
            // 開いている間に公開が起きていないことを確かめる。
            match Manifest.read outputDirectory with
            | Ok current when current = manifest -> outcome <- ValueSome(Ok view)
            | Ok _ -> (view :> IDisposable).Dispose()
            // 世代が変わった。新しいマニフェストで開き直す。
            | Error error ->
              (view :> IDisposable).Dispose()
              outcome <- ValueSome(Error(ArtifactUnreadable error))

      match outcome with
      | ValueSome result -> result
      | ValueNone -> Error GenerationChanged

// --- 検索 -------------------------------------------------------------------

/// 一致の強さ。docs/query-and-cli.md 3 の 1 段目。
type MatchStrength =
  | Exact
  | Prefix
  | Substring

module MatchStrength =

  /// 強い一致ほど小さい値。順位付けの鍵に使う。
  let rank strength =
    match strength with
    | Exact -> 0
    | Prefix -> 1
    | Substring -> 2

  let name strength =
    match strength with
    | Exact -> "exact"
    | Prefix -> "prefix"
    | Substring -> "substring"

/// 一致した対象。docs/query-and-cli.md 3 の 2 段目。
type MatchTarget =
  | Name
  | QualifiedName
  | Path

module MatchTarget =

  let rank target =
    match target with
    | Name -> 0
    | QualifiedName -> 1
    | Path -> 2

  let name target =
    match target with
    | Name -> "name"
    | QualifiedName -> "qualifiedName"
    | Path -> "path"

[<Struct>]
type SearchHit =
  { Node: int
    Strength: MatchStrength
    Target: MatchTarget }

[<Literal>]
let MaxScannedNodes = 50_000

/// Separate bounds for the scanned key region and decoded posting strings.
[<Literal>]
let MaxLookupBytes = 268_435_456

[<Literal>]
let MaxLookupPostings = 200_000

[<Literal>]
let MaxLookupCandidates = 200_000

[<Literal>]
let private LookupScanChunkBytes = 1_048_576

type SearchOutcome =
  {
    Hits: SearchHit[]
    /// 上限に達して走査を打ち切った。
    Truncated: bool
    /// True only when the on-disk lexical lookup was used.
    UsedIndex: bool
    ScannedNodes: int
    OmittedCount: int
    OmittedCountIsLowerBound: bool
    Diagnostics: string[]
  }

let private lookupRangeAt (segment: Reader.MappedSegment) (index: int) =
  let data = segment.Payload
  let keyCount = int segment.Header.PrimaryCount
  let postingCount = int segment.Header.SecondaryCount
  let blobStart = 32 + keyCount * 24 + postingCount * 8
  let record = data.Slice(32 + index * 24, 24)
  let offset = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(0, 8))
  let length = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(8, 4))
  let first = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(12, 4))
  let count = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(16, 4))

  if
    offset > uint64(data.Length - blobStart)
    || uint64 length > uint64(data.Length - blobStart) - offset
    || length = 0u
    || length > uint32 Format.Lookup.MaxKeyBytes
    || count = 0u
    || uint64 first + uint64 count > uint64 postingCount
    || BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(20, 4)) <> 0u
  then
    corrupt "lookup" "Invalid key or posting range"

  if index = 0 && (offset <> 0UL || first <> 0u) then
    corrupt "lookup" "Invalid initial key range"

  if index + 1 < keyCount then
    let next = data.Slice(32 + (index + 1) * 24, 24)

    if
      BinaryPrimitives.ReadUInt64LittleEndian(next.Slice(0, 8))
      <> offset + uint64 length
      || BinaryPrimitives.ReadUInt32LittleEndian(next.Slice(12, 4)) <> first + count
    then
      corrupt "lookup" "Noncontiguous key or posting ranges"
  elif
    offset + uint64 length <> uint64(data.Length - blobStart)
    || first + count <> uint32 postingCount
  then
    corrupt "lookup" "Invalid terminal key range"

  struct (blobStart + int offset, int length, int first, int count)

let private lookupKeyAt (segment: Reader.MappedSegment) (index: int) =
  let struct (offset, length, first, count) = lookupRangeAt segment index

  let normalCount =
    int(BinaryPrimitives.ReadUInt32LittleEndian(segment.Payload.Slice(24, 4)))

  let text =
    try
      Strings.utf8.GetString(segment.Payload.Slice(offset, length))
    with :? DecoderFallbackException ->
      corrupt "lookup" "Invalid UTF-8"

  if not(String.Equals(text, Strings.lookupKey (index >= normalCount) text, StringComparison.Ordinal)) then
    corrupt "lookup" "Key is not normalized"

  struct (text, first, count, length)

let private lookupPostingAt (view: GraphView) (segment: Reader.MappedSegment) offset previous =
  let record = segment.Payload.Slice(offset, Format.Lookup.PostingLength)
  let index = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0, 4))
  let targetCode = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(4, 4))

  if index >= uint32 view.NodeCount || targetCode > 2u then
    corrupt "lookup" "Invalid posting reference or target"

  let pair = struct (int index, int targetCode)

  if compare pair previous <= 0 then
    corrupt "lookup" "Postings are not strictly increasing"

  pair

let private lookupPostingReference (view: GraphView) (struct (index, targetCode)) =
  let node = view.Node index

  match targetCode with
  | 0 -> struct (Name, node.NameRef)
  | 1 -> struct (QualifiedName, node.QualifiedNameRef)
  | 2 ->
    if node.FileIndex = Format.NodeRecord.NoFile then
      corrupt "lookup" "Path posting has no file"

    struct (Path, view.FilePathRef node.FileIndex)
  | _ -> corrupt "lookup" "Invalid posting target"

let private checkLookupReference ignoreCase text (bytes: ReadOnlySpan<byte>) =
  if Strings.lookupKey ignoreCase (Strings.utf8.GetString bytes) <> text then
    corrupt "lookup" "Posting key disagrees with its node"

/// Validate every lexical key and posting, including complete source-field coverage.
/// Does not apply search work/result caps or take ownership of the view. Cancellation
/// propagates; missing lookup or corrupt data raises QueryException.
let validateLookup (view: GraphView) (cancellation: CancellationToken) : unit =
  cancellation.ThrowIfCancellationRequested()

  let segment =
    match view.Lookup with
    | ValueSome value -> value
    | ValueNone -> raise(QueryException SearchIndexRequired)

  let mutable expectedPostings = 0L

  let countReference reference =
    if view.StringByteLength reference > 0 then
      expectedPostings <- expectedPostings + 1L

  for index in 0 .. view.NodeCount - 1 do
    if index &&& 1023 = 0 then
      cancellation.ThrowIfCancellationRequested()

    let node = view.Node index
    countReference node.NameRef
    countReference node.QualifiedNameRef

    if node.FileIndex <> Format.NodeRecord.NoFile then
      countReference(view.FilePathRef node.FileIndex)

  let normalCount =
    int(BinaryPrimitives.ReadUInt32LittleEndian(segment.Payload.Slice(24, 4)))

  let postingBase = 32 + int segment.Header.PrimaryCount * Format.Lookup.KeyLength
  let validatedReferences = HashSet<int>()
  let mutable previousKey = ""
  let mutable normalPostings = 0L

  for index in 0 .. int segment.Header.PrimaryCount - 1 do
    cancellation.ThrowIfCancellationRequested()
    let struct (text, first, count, _) = lookupKeyAt segment index

    if index <> 0 && index <> normalCount && String.CompareOrdinal(previousKey, text) >= 0 then
      corrupt "lookup" "Keys are not strictly increasing"

    previousKey <- text
    validatedReferences.Clear()
    let mutable previous = struct (-1, -1)

    for position in 0 .. count - 1 do
      if position &&& 1023 = 0 then
        cancellation.ThrowIfCancellationRequested()

      let posting =
        lookupPostingAt view segment (postingBase + (first + position) * Format.Lookup.PostingLength) previous

      previous <- posting
      let struct (_, reference) = lookupPostingReference view posting

      if validatedReferences.Add reference then
        cancellation.ThrowIfCancellationRequested()
        checkLookupReference (index >= normalCount) text (view.StringBytes reference)

        // Bound memoization, not verification work; uncached references are still checked.
        if validatedReferences.Count >= MaxLookupPostings then
          validatedReferences.Clear()

    if index < normalCount then
      normalPostings <- normalPostings + int64 count

  // Unique keys and matching, ordered postings allow each source field at most once per region.
  if
    normalPostings <> expectedPostings
    || int64 segment.Header.SecondaryCount - normalPostings <> expectedPostings
  then
    corrupt "lookup" "Posting coverage disagrees with the source fields"

let private lookupContainingKey (segment: Reader.MappedSegment) firstKey endKey position =
  let mutable low = firstKey
  let mutable high = endKey - 1
  let mutable found = -1

  while low <= high do
    let middle = low + (high - low) / 2
    let struct (offset, length, _, _) = lookupRangeAt segment middle

    if position < offset then
      high <- middle - 1
    elif position >= offset + length then
      low <- middle + 1
    else
      found <- middle
      low <- high + 1

  if found < 0 then
    corrupt "lookup" "Byte match is outside the key ranges"

  found

let private searchCore
  (exactNamesOnly: bool)
  (view: GraphView)
  (needle: string)
  (ignoreCase: bool)
  (cancellation: CancellationToken)
  =
  cancellation.ThrowIfCancellationRequested()

  if String.IsNullOrEmpty needle then
    invalid "text" "Search text must not be empty"

  if needle.Length > Format.Lookup.MaxKeyBytes then
    invalid "text" "Search text exceeds the lookup key limit"

  let normalized =
    try
      Strings.utf8.GetByteCount needle |> ignore
      Strings.lookupKey ignoreCase needle
    with :? EncoderFallbackException ->
      invalid "text" "Invalid Unicode"

  if Strings.utf8.GetByteCount normalized > Format.Lookup.MaxKeyBytes then
    invalid "text" "Search text exceeds the UTF-8 lookup key limit"

  let segment =
    match view.Lookup with
    | ValueSome value -> value
    | ValueNone -> raise(QueryException SearchIndexRequired)

  let normalCount =
    int(BinaryPrimitives.ReadUInt32LittleEndian(segment.Payload.Slice(24, 4)))

  let firstKey = if ignoreCase then normalCount else 0

  let endKey =
    if ignoreCase then
      int segment.Header.PrimaryCount
    else
      normalCount

  let postingBase = 32 + int segment.Header.PrimaryCount * 24

  let checkedKey index =
    let struct (text, _, _, _) as key = lookupKeyAt segment index

    if index > firstKey then
      let struct (previous, _, _, _) = lookupKeyAt segment (index - 1)

      if String.CompareOrdinal(previous, text) >= 0 then
        corrupt "lookup" "Keys are not strictly increasing"

    key

  let mutable low = firstKey
  let mutable high = endKey

  while low < high do
    cancellation.ThrowIfCancellationRequested()
    let middle = low + (high - low) / 2
    let struct (text, _, _, _) = checkedKey middle

    if String.CompareOrdinal(text, normalized) < 0 then
      low <- middle + 1
    else
      high <- middle

  let hits = Dictionary<int, SearchHit>()
  let validatedReferences = HashSet<int>()
  let mutable decodedBytes = 0L
  let mutable work = 0
  let mutable omitted = 0
  let mutable truncated = false

  let addKey strength (struct (text: string, first: int, count: int, _: int)) =
    validatedReferences.Clear()
    let mutable position = 0
    let mutable previous = struct (-1, -1)

    while position < count && not truncated do
      if work >= MaxLookupPostings then
        truncated <- true
      else
        if work &&& 1023 = 0 then
          cancellation.ThrowIfCancellationRequested()

        let struct (index, targetCode) as pair =
          lookupPostingAt view segment (postingBase + (first + position) * 8) previous

        previous <- pair

        if not exactNamesOnly || targetCode <> 2 then
          let struct (target, reference) = lookupPostingReference view pair

          if validatedReferences.Add reference then
            let bytes = view.StringBytes reference

            if int64 bytes.Length > int64 MaxLookupBytes - decodedBytes then
              truncated <- true
            else
              decodedBytes <- decodedBytes + int64 bytes.Length

              checkLookupReference ignoreCase text bytes

          if not truncated then
            let hit =
              { Node = index
                Strength = strength
                Target = target }

            match hits.TryGetValue hit.Node with
            | true, existing ->
              if
                compare
                  (MatchStrength.rank strength, MatchTarget.rank target)
                  (MatchStrength.rank existing.Strength, MatchTarget.rank existing.Target) < 0
              then
                hits[hit.Node] <- hit
            | false, _ ->
              if hits.Count >= MaxScannedNodes then
                omitted <- 1
                truncated <- true
              else
                hits.Add(hit.Node, hit)

        position <- position + 1
        work <- work + 1

  // Exact and prefix keys are sought before spending any substring work budget.
  let prefixStart = low
  let mutable cursor = prefixStart
  let mutable prefix = true

  while cursor < endKey
        && prefix
        && not truncated
        && (not exactNamesOnly || cursor = prefixStart) do
    cancellation.ThrowIfCancellationRequested()
    let struct (text, _, _, _) as key = checkedKey cursor

    if
      text = normalized
      || (not exactNamesOnly && text.StartsWith(normalized, StringComparison.Ordinal))
    then
      addKey (if text = normalized then Exact else Prefix) key
      cursor <- cursor + 1
    else
      prefix <- false

  let prefixEnd = cursor

  if not exactNamesOnly && not truncated && firstKey < endKey then
    let needleBytes = Strings.utf8.GetBytes normalized
    let struct (regionStart, _, _, _) = lookupRangeAt segment firstKey
    let struct (lastOffset, lastLength, _, _) = lookupRangeAt segment (endKey - 1)
    let regionEnd = lastOffset + lastLength
    let budgetEnd = regionStart + min MaxLookupBytes (regionEnd - regionStart)
    let mutable validatedEnd = regionStart
    let mutable searchPosition = regionStart
    let mutable candidates = 0

    // Scan mmap bytes rather than allocating one string per dictionary key. Keep
    // needleLength-1 bytes across windows (at most one extra window of work);
    // validate UTF-8 at complete scalar boundaries.
    // Only byte matches seek/decode a containing key, and cross-key matches are rejected.
    while validatedEnd < budgetEnd && not truncated do
      cancellation.ThrowIfCancellationRequested()

      let mutable finish =
        validatedEnd + min LookupScanChunkBytes (budgetEnd - validatedEnd)

      if finish < regionEnd then
        let mutable continuationBytes = 0

        while finish > validatedEnd
              && segment.Payload[finish] &&& 0xC0uy = 0x80uy
              && continuationBytes < 3 do
          finish <- finish - 1
          continuationBytes <- continuationBytes + 1

        if segment.Payload[finish] &&& 0xC0uy = 0x80uy then
          corrupt "lookup" "Invalid UTF-8"

      if finish = validatedEnd then
        truncated <- true
      else
        try
          Strings.utf8.GetCharCount(segment.Payload.Slice(validatedEnd, finish - validatedEnd))
          |> ignore
        with :? DecoderFallbackException ->
          corrupt "lookup" "Invalid UTF-8"

        validatedEnd <- finish
        let mutable searching = true

        while searching
              && not truncated
              && needleBytes.Length <= validatedEnd - searchPosition do
          cancellation.ThrowIfCancellationRequested()

          let relative =
            segment.Payload
              .Slice(searchPosition, validatedEnd - searchPosition)
              .IndexOf(ReadOnlySpan needleBytes)

          if relative < 0 then
            searchPosition <- validatedEnd - needleBytes.Length + 1
            searching <- false
          elif candidates >= MaxLookupCandidates then
            truncated <- true
          else
            candidates <- candidates + 1
            let position = searchPosition + relative
            let index = lookupContainingKey segment firstKey endKey position
            let struct (offset, length, _, _) = lookupRangeAt segment index
            let keyEnd = offset + length

            if
              needleBytes.Length <= keyEnd - position
              && (index < prefixStart || index >= prefixEnd)
            then
              if keyEnd > budgetEnd then
                truncated <- true
              else
                let key = checkedKey index

                let strength =
                  if position <> offset then Substring
                  elif needleBytes.Length = length then Exact
                  else Prefix

                addKey strength key

            searchPosition <- keyEnd

    if budgetEnd < regionEnd then
      truncated <- true

  { Hits = hits.Values |> Seq.sortBy(fun hit -> hit.Node) |> Seq.toArray
    Truncated = truncated
    UsedIndex = true
    ScannedNodes = hits.Count + omitted
    OmittedCount = omitted
    OmittedCountIsLowerBound = truncated
    Diagnostics =
      if truncated then
        [| "Lexical lookup work limit reached; unseen node hits are not counted. omittedCount is a lower bound." |]
      else
        Array.empty }

/// Binary exact/prefix lookup plus bounded UTF-8 key-blob scanning for substrings.
/// No node-table fallback and no writes. Omission counts always count nodes, not keys/postings.
let search (view: GraphView) (needle: string) (ignoreCase: bool) (cancellation: CancellationToken) : SearchOutcome =
  searchCore false view needle ignoreCase cancellation

/// Exact Name/QualifiedName matches only, deduplicated by node. Owning-file Path
/// postings do not count as names. File/directory logical paths remain searchable
/// through QualifiedName. No prefix/substring work is performed.
/// A nontruncated result proves the complete exact-name candidate set; callers must
/// still reject uniqueness when the posting/node work limits truncate this lookup.
let searchExactNames
  (view: GraphView)
  (needle: string)
  (ignoreCase: bool)
  (cancellation: CancellationToken)
  : SearchOutcome =
  searchCore true view needle ignoreCase cancellation

/// 検索結果の順位。docs/query-and-cli.md 3 の辞書式に従う。
///
/// 中心性（5 段目）は M6 で導入するため、現時点では常に 0 として扱う。順位が一意に
/// 定まることは 6 段目のパス順と、最後の ID 順で保証する。
let compareHits (view: GraphView) (left: SearchHit) (right: SearchHit) =
  let byStrength =
    compare (MatchStrength.rank left.Strength) (MatchStrength.rank right.Strength)

  if byStrength <> 0 then
    byStrength
  else
    let byTarget =
      compare (MatchTarget.rank left.Target) (MatchTarget.rank right.Target)

    if byTarget <> 0 then
      byTarget
    else
      let leftNode = view.Node left.Node
      let rightNode = view.Node right.Node

      // 定義を宣言より上に置く。
      let definitionRank (flags: NodeFlags) =
        if flags.HasFlag NodeFlags.Definition then 0
        elif flags.HasFlag NodeFlags.DeclarationOnly then 1
        else 2

      let byDefinition =
        compare (definitionRank leftNode.Flags) (definitionRank rightNode.Flags)

      if byDefinition <> 0 then
        byDefinition
      else
        // フラグによる減点。順位を下げるだけで、除外はしない。
        let penalty (flags: NodeFlags) =
          (if flags.HasFlag NodeFlags.Vendored then 1 else 0)
          + (if flags.HasFlag NodeFlags.Generated then 1 else 0)
          + (if flags.HasFlag NodeFlags.Test then 1 else 0)

        let byPenalty = compare (penalty leftNode.Flags) (penalty rightNode.Flags)

        if byPenalty <> 0 then
          byPenalty
        else
          let path node =
            if node.FileIndex <> Format.NodeRecord.NoFile then
              view.String(view.FilePathRef node.FileIndex)
            elif node.Kind = Directory then
              view.String node.QualifiedNameRef
            else
              ""

          let byKind =
            compare (NodeKind.toCode leftNode.Kind) (NodeKind.toCode rightNode.Kind)

          let byPath =
            if byKind <> 0 then
              byKind
            else
              String.CompareOrdinal(path leftNode, path rightNode)

          if byPath <> 0 then
            byPath
          else
            // 同点の最終解消。ID は決定的なので順序も決定的になる。
            compare leftNode.Id rightNode.Id

// --- 近傍と経路 ---------------------------------------------------------------

/// 近傍探索で辿るノード数の上限。無制限 BFS を許さない（backlog 028）。
[<Literal>]
let MaxVisitedNodes = 200_000

[<Literal>]
let MaxExploredEdges = 400_000

[<Literal>]
let MaxTraversalDepth = 64

[<Struct>]
type EdgeView = { From: int; To: int; Kind: EdgeKind }

type Traversal =
  {
    /// 起点からの距離つきで訪れたノード。距離の昇順、同距離は密インデックス昇順。
    Nodes: (struct (int * int))[]
    /// 辿ったエッジ。始点・種別コード・終点の昇順。
    Edges: EdgeView[]
    /// 上限に達して打ち切った。
    Truncated: bool
    OmittedCount: int
    OmittedEdgeCount: int
    OmittedCountIsLowerBound: bool
    Diagnostics: string[]
  }

type PathOutcome =
  { Path: int[] voption
    Edges: EdgeView[]
    Truncated: bool
    OmittedCount: int
    OmittedCountIsLowerBound: bool
    Diagnostics: string[] }

let private directions direction =
  match direction with
  | Outgoing -> [| false |]
  | Incoming -> [| true |]
  | Both -> [| false; true |]

let private validateTraversal (view: GraphView) (starts: int[]) (kinds: EdgeKind[]) direction depth =
  if isNull(box starts) || starts.Length = 0 then
    invalid "starts" "At least one start is required"

  if starts.Length > MaxVisitedNodes then
    invalid "starts" "Too many starting nodes"

  if isNull(box kinds) then
    invalid "kinds" "Kinds must not be null"

  if kinds.Length > EdgeKind.all.Length then
    invalid "kinds" "Too many edge kinds"

  for kind in kinds do
    if isNull(box kind) then
      invalid "kind" "Edge kind must not be null"

  if isNull(box direction) then
    invalid "direction" "Direction must not be null"

  if depth < 0 || depth > MaxTraversalDepth then
    invalid "depth" $"Expected 0..{MaxTraversalDepth}"

  for start in starts do
    if start < 0 || start >= view.NodeCount then
      invalid "node" "Index out of range"

    view.Node start |> ignore

  kinds |> Array.distinct |> Array.sortBy EdgeKind.toCode

let private compareEdges (left: EdgeView) (right: EdgeView) =
  let byFrom = compare left.From right.From

  if byFrom <> 0 then
    byFrom
  else
    let byKind = compare (EdgeKind.toCode left.Kind) (EdgeKind.toCode right.Kind)
    if byKind <> 0 then byKind else compare left.To right.To

/// Does not materialize adjacency rows, including an adversarial high-degree row.
let private visitEdges
  (view: GraphView)
  current
  (kinds: EdgeKind[])
  direction
  (cancellation: CancellationToken)
  remaining
  (visit: int -> EdgeView -> bool)
  =
  let orientations = directions direction
  let mutable examined = 0
  let mutable complete = true
  let mutable kindIndex = 0

  while complete && kindIndex < kinds.Length do
    let kind = kinds[kindIndex]
    let mutable orientation = 0

    while complete && orientation < orientations.Length do
      let incoming = orientations[orientation]

      match view.Adjacency(current, kind, incoming) with
      | ValueNone -> ()
      | ValueSome(struct (segment, offset, count)) ->
        let mutable position = 0

        while complete && position < count do
          if examined >= remaining then
            complete <- false
          else
            if examined &&& 1023 = 0 then
              cancellation.ThrowIfCancellationRequested()

            let next = view.Target(segment, offset, position)

            let edge =
              if incoming then
                { From = next
                  To = current
                  Kind = kind }
              else
                { From = current
                  To = next
                  Kind = kind }

            examined <- examined + 1
            complete <- visit next edge
            position <- position + 1

      orientation <- orientation + 1

    kindIndex <- kindIndex + 1

  struct (examined, complete)

/// 幅優先で近傍を辿る。
///
/// Requested depth filters the traversal; boundary nodes are not expanded.
/// Only safety caps truncate the result or contribute omitted candidates.
let neighborsFrom
  (view: GraphView)
  (starts: int[])
  (kinds: EdgeKind[])
  (direction: Direction)
  (depth: int)
  (cancellation: CancellationToken)
  : Traversal =
  cancellation.ThrowIfCancellationRequested()
  let kinds = validateTraversal view starts kinds direction depth
  let distance = Dictionary<int, int>()
  let edges = HashSet<EdgeView>()
  let omittedNodes = HashSet<int>()
  let omittedEdges = HashSet<EdgeView>()
  let queue = Queue<int>()
  let seeds = starts |> Array.distinct |> Array.sort

  for start in seeds do
    if distance.Count < MaxVisitedNodes then
      distance.Add(start, 0)
      queue.Enqueue start
    else
      omittedNodes.Add start |> ignore

  let mutable stopped = omittedNodes.Count > 0
  let mutable work = 0

  while not stopped && queue.Count > 0 do
    cancellation.ThrowIfCancellationRequested()
    let current = queue.Dequeue()
    let currentDistance = distance[current]

    if currentDistance < depth then
      let struct (examined, complete) =
        visitEdges view current kinds direction cancellation (MaxExploredEdges - work) (fun next edge ->
          if distance.ContainsKey next then
            edges.Add edge |> ignore
            true
          elif distance.Count >= MaxVisitedNodes then
            omittedNodes.Add next |> ignore
            omittedEdges.Add edge |> ignore
            false
          else
            distance.Add(next, currentDistance + 1)
            queue.Enqueue next
            edges.Add edge |> ignore
            true)

      work <- work + examined
      stopped <- not complete

  let orderedNodes =
    distance
    |> Seq.map(fun entry -> struct (entry.Key, entry.Value))
    |> Seq.sortWith(fun (struct (leftNode, leftDistance)) (struct (rightNode, rightDistance)) ->
      let byDistance = compare leftDistance rightDistance

      if byDistance <> 0 then
        byDistance
      else
        compare leftNode rightNode)
    |> Seq.toArray

  let orderedEdges = edges |> Seq.toArray
  Array.sortInPlaceWith compareEdges orderedEdges
  let truncated = stopped || omittedNodes.Count > 0 || omittedEdges.Count > 0

  { Nodes = orderedNodes
    Edges = orderedEdges
    Truncated = truncated
    OmittedCount = omittedNodes.Count
    OmittedEdgeCount = omittedEdges.Count
    OmittedCountIsLowerBound = truncated
    Diagnostics =
      if stopped then
        [| "Traversal work limit reached; omitted node/edge counts are lower bounds." |]
      else
        Array.empty }

let neighbors view start kinds direction depth cancellation =
  neighborsFrom view [| start |] kinds direction depth cancellation

/// 2 ノード間の最短経路。深さ上限つきの幅優先で解く。
///
/// A complete no-match outcome refers only to paths within the requested depth.
/// Routes outside that query boundary are not omitted candidates.
///
/// Ties use the first intersection in the cheaper frontier's stable node,
/// edge-kind, direction, and CSR order.
let shortestPath
  (view: GraphView)
  (source: int)
  (target: int)
  (kinds: EdgeKind[])
  (direction: Direction)
  (maxDepth: int)
  (cancellation: CancellationToken)
  : PathOutcome =
  cancellation.ThrowIfCancellationRequested()
  let kinds = validateTraversal view [| source; target |] kinds direction maxDepth

  let outcome path edges truncated omitted diagnostics =
    { Path = path
      Edges = edges
      Truncated = truncated
      OmittedCount = omitted
      OmittedCountIsLowerBound = truncated
      Diagnostics = diagnostics }

  if source = target then
    outcome (ValueSome [| source |]) Array.empty false 0 Array.empty
  elif maxDepth = 0 then
    outcome ValueNone Array.empty false 0 Array.empty
  else
    let reverse =
      match direction with
      | Outgoing -> Incoming
      | Incoming -> Outgoing
      | Both -> Both

    let forwardDistances = Dictionary<int, int>()
    let backwardDistances = Dictionary<int, int>()
    let forwardParents = Dictionary<int, struct (int * EdgeView)>()
    let backwardParents = Dictionary<int, struct (int * EdgeView)>()
    let seen = HashSet<int>()
    seen.Add source |> ignore
    seen.Add target |> ignore
    forwardDistances.Add(source, 0)
    backwardDistances.Add(target, 0)
    let mutable forward = [| source |]
    let mutable backward = [| target |]
    let mutable forwardDepth = 0
    let mutable backwardDepth = 0
    let mutable best = Int32.MaxValue
    let mutable meeting = -1
    let mutable work = 0
    let mutable stopped = false
    let mutable omitted = 0

    let cost frontier orientation =
      let mutable total = 0L

      for node in frontier do
        cancellation.ThrowIfCancellationRequested()

        for kind in kinds do
          for incoming in directions orientation do
            match view.Adjacency(node, kind, incoming) with
            | ValueNone -> ()
            | ValueSome(struct (_, _, count)) -> total <- total + int64 count

      total

    let mutable forwardCost = cost forward direction
    let mutable backwardCost = cost backward reverse

    // The completed distance balls are disjoint before each layer. Their first
    // intersection proves a shortest route; finishing the layer only explores ties.
    while not stopped
          && meeting < 0
          && forward.Length > 0
          && backward.Length > 0
          && forwardDepth + backwardDepth < maxDepth do
      cancellation.ThrowIfCancellationRequested()
      let fromSource = forwardCost <= backwardCost

      let frontier, distances, others, parents, orientation, level =
        if fromSource then
          forward, forwardDistances, backwardDistances, forwardParents, direction, forwardDepth
        else
          backward, backwardDistances, forwardDistances, backwardParents, reverse, backwardDepth

      let nextLayer = ResizeArray<int>()
      let mutable position = 0

      while position < frontier.Length && not stopped && meeting < 0 do
        let current = frontier[position]

        let struct (examined, complete) =
          visitEdges view current kinds orientation cancellation (MaxExploredEdges - work) (fun next edge ->
            if not(distances.ContainsKey next) then
              if not(seen.Contains next) && seen.Count >= MaxVisitedNodes then
                omitted <- 1
                stopped <- true
              else
                seen.Add next |> ignore
                distances.Add(next, level + 1)
                parents.Add(next, struct (current, edge))
                nextLayer.Add next

                match others.TryGetValue next with
                | true, otherDistance ->
                  best <- level + 1 + otherDistance
                  meeting <- next
                | false, _ -> ()

            not stopped && meeting < 0)

        work <- work + examined
        stopped <- stopped || (not complete && meeting < 0)
        position <- position + 1

      if not stopped && meeting < 0 then
        let ordered = nextLayer.ToArray()
        Array.sortInPlace ordered

        if fromSource then
          forward <- ordered
          forwardDepth <- forwardDepth + 1
        else
          backward <- ordered
          backwardDepth <- backwardDepth + 1

        if forwardDepth + backwardDepth < maxDepth then
          if fromSource then
            forwardCost <- cost forward direction
          else
            backwardCost <- cost backward reverse

    if not stopped && meeting >= 0 && best <= maxDepth then
      let nodes = ResizeArray<int>()
      let edges = ResizeArray<EdgeView>()
      let mutable cursor = meeting
      nodes.Add cursor

      while cursor <> source do
        let struct (parent, edge) = forwardParents[cursor]
        edges.Add edge
        cursor <- parent
        nodes.Add cursor

      nodes.Reverse()
      edges.Reverse()
      cursor <- meeting

      while cursor <> target do
        let struct (parent, edge) = backwardParents[cursor]
        edges.Add edge
        cursor <- parent
        nodes.Add cursor

      outcome (ValueSome(nodes.ToArray())) (edges.ToArray()) false 0 Array.empty
    else
      let diagnostics =
        if stopped then
          [| "Path exploration work limit reached; no shortest-path or no-route conclusion was proved." |]
        else
          Array.empty

      outcome ValueNone Array.empty stopped omitted diagnostics
