/// 生成物の整合性検証。
///
/// 形式版、チェックサム、参照の閉包を検査する。破損を検出した場合は部分的に読み進めず、
/// 明示的な失敗として扱う。docs/requirements.md FR-6、docs/security.md C-6 を参照。
module Srcnet.Storage.Verify

open System
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
  | UnknownEdgeKind of name: string * code: byte
  /// セグメント名から成果物内のパスへ変換できなかった。
  | SegmentPathRejected of name: string * detail: string
  /// 成果物を生成した文法の版が、現在の実行ファイルの版と違う。
  /// 破損ではないため失敗にはしないが、再生成で結果が変わり得ることを伝える。
  | GrammarVersionDiffers of language: string * recorded: string * current: string

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
    | UnknownEdgeKind(name, code) -> $"{name}: 未知のエッジ種別コード {code} です"
    | SegmentPathRejected(_, detail) -> detail
    | GrammarVersionDiffers(language, recorded, current) ->
      $"{language} の文法が記録された {recorded} ではなく {current} です。再生成すると結果が変わり得ます"

type Report =
  { Issues: Issue[]
    SegmentsChecked: int
    BytesChecked: int64 }

  member this.IsValid =
    this.Issues
    |> Array.forall (function
      | GrammarVersionDiffers _ -> true
      | _ -> false)

let inline private checkCancellation (cancellation: CancellationToken) index =
  if index &&& 8191 = 0 then
    cancellation.ThrowIfCancellationRequested()

/// 文字列オフセット表が単調非減少で、末尾が blob 長に一致することを確かめる。
let private checkStringOffsets
  (cancellation: CancellationToken)
  (name: string)
  (segment: Reader.MappedSegment)
  (issues: ResizeArray<Issue>)
  =
  let payload = segment.Data
  let count = int segment.Header.PrimaryCount
  let expectedLength = (count + 1) * 8

  if payload.Length <> expectedLength then
    issues.Add(LengthMismatch(name, int64 expectedLength, int64 payload.Length))
  else
    let mutable previous = 0UL
    let mutable index = 0

    while index <= count do
      checkCancellation cancellation index
      let offset = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(index * 8, 8))

      if index = 0 && offset <> 0UL then
        issues.Add(OrderViolation(name, "先頭のオフセットが 0 ではありません"))
        index <- count
      elif offset < previous then
        issues.Add(OrderViolation(name, $"オフセット {index} が減少しています"))
        index <- count
      elif index = count && offset <> segment.Header.SecondaryCount then
        issues.Add(CountMismatch(name, int64 segment.Header.SecondaryCount, int64 offset))

      previous <- offset
      index <- index + 1

let private checkStringBytes
  (cancellation: CancellationToken)
  (name: string)
  (strings: Reader.MappedSegment)
  (offsets: Reader.MappedSegment)
  (issues: ResizeArray<Issue>)
  =
  let mutable index = 0
  let mutable invalid = false

  while index < int strings.Header.PrimaryCount && not invalid do
    checkCancellation cancellation index

    let first =
      BinaryPrimitives.ReadUInt64LittleEndian(offsets.Data.Slice(index * 8, 8))

    let last =
      BinaryPrimitives.ReadUInt64LittleEndian(offsets.Data.Slice((index + 1) * 8, 8))

    try
      Strings.utf8.GetCharCount(strings.Data.Slice(int first, int(last - first)))
      |> ignore
    with :? Text.DecoderFallbackException ->
      issues.Add(SegmentUnreadable(name, $"文字列 {index} が UTF-8 ではありません"))
      invalid <- true

    index <- index + 1

let private checkCsr
  (cancellation: CancellationToken)
  (name: string)
  (segment: Reader.MappedSegment)
  (nodeCount: int)
  (expectedEdges: int)
  (issues: ResizeArray<Issue>)
  =
  let payload = segment.Data
  let count = int segment.Header.PrimaryCount
  let edgeCount = int64 segment.Header.SecondaryCount

  if count <> nodeCount then
    issues.Add(CountMismatch(name, int64 nodeCount, int64 count))
  elif edgeCount <> int64 expectedEdges then
    issues.Add(CountMismatch(name, int64 expectedEdges, edgeCount))
  else

    let expectedLength = int64(count + 1) * 8L + edgeCount * 4L

    if int64 payload.Length <> expectedLength then
      issues.Add(LengthMismatch(name, expectedLength, int64 payload.Length))
    else
      let targetsOffset = (count + 1) * 8
      let mutable previous = 0UL
      let mutable index = 0
      let mutable reported = false

      while index <= count && not reported do
        checkCancellation cancellation index
        let offset = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(index * 8, 8))

        if index = 0 && offset <> 0UL then
          issues.Add(OrderViolation(name, "先頭の CSR オフセットが 0 ではありません"))
          reported <- true
        elif offset < previous then
          issues.Add(OrderViolation(name, $"オフセット {index} が減少しています"))
          reported <- true
        elif offset > uint64 edgeCount then
          issues.Add(ReferenceOutOfRange(name, $"オフセット {index} が隣接要素数を超えています"))
          reported <- true

        previous <- offset
        index <- index + 1

      if not reported && previous <> uint64 edgeCount then
        issues.Add(CountMismatch(name, edgeCount, int64 previous))

      let mutable source = 0

      while source < nodeCount && not reported do
        checkCancellation cancellation source

        let first =
          int(BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(source * 8, 8)))

        let finish =
          int(BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice((source + 1) * 8, 8)))

        let mutable target = first
        let mutable previousTarget = 0u

        while target < finish && not reported do
          checkCancellation cancellation target

          let value =
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(targetsOffset + target * 4, 4))

          if value >= uint32 nodeCount then
            issues.Add(ReferenceOutOfRange(name, $"隣接要素 {target} がノード数 {nodeCount} を超えています"))
            reported <- true
          elif target > first && value <= previousTarget then
            issues.Add(OrderViolation(name, $"ノード {source} の隣接要素が重複または降順です"))
            reported <- true

          previousTarget <- value
          target <- target + 1

        source <- source + 1

let private checkIdMap
  (cancellation: CancellationToken)
  (nodes: Reader.MappedSegment)
  (name: string)
  (segment: Reader.MappedSegment)
  (nodeCount: int)
  (issues: ResizeArray<Issue>)
  =
  let payload = segment.Data
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
      checkCancellation cancellation index

      if index > 0 then
        let previous = payload.Slice((index - 1) * Ids.NodeIdLength, Ids.NodeIdLength)
        let current = payload.Slice(index * Ids.NodeIdLength, Ids.NodeIdLength)

        // ID はビッグ エンディアンで格納するため、バイト列の辞書順が数値順と一致する。
        if previous.SequenceCompareTo current >= 0 then
          issues.Add(OrderViolation(name, $"ID {index} が昇順になっていません"))
          reported <- true

      let dense =
        BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(indicesOffset + index * 4, 4))

      if dense >= uint32 nodeCount then
        issues.Add(ReferenceOutOfRange(name, $"密インデックス {dense} がノード数 {nodeCount} を超えています"))
        reported <- true
      elif
        not(
          payload
            .Slice(index * Ids.NodeIdLength, Ids.NodeIdLength)
            .SequenceEqual(nodes.Data.Slice(int dense * Format.RecordLength, Ids.NodeIdLength))
        )
      then
        issues.Add(ReferenceOutOfRange(name, $"ID {index} と密インデックス {dense} のノード ID が一致しません"))
        reported <- true

      index <- index + 1

/// 参照候補の件数・文字列参照・種別コードを検証する。
let private checkReferences
  (cancellation: CancellationToken)
  (name: string)
  (segment: Reader.MappedSegment)
  (nodeCount: int)
  (stringCount: int)
  (issues: ResizeArray<Issue>)
  =
  let payload = segment.Data
  let count = int segment.Header.PrimaryCount
  let expectedLength = count * Format.RecordLength

  if payload.Length <> expectedLength then
    issues.Add(LengthMismatch(name, int64 expectedLength, int64 payload.Length))
  else
    let mutable index = 0
    let mutable reported = false

    while index < count && not reported do
      checkCancellation cancellation index
      let record = payload.Slice(index * Format.RecordLength, Format.RecordLength)

      let source =
        BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.ReferenceRecord.SourceOffset, 4))

      let target =
        BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.ReferenceRecord.TargetOffset, 4))

      let qualifier =
        BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.ReferenceRecord.QualifierOffset, 4))

      let edgeCode = record[Format.ReferenceRecord.EdgeKindOffset]

      if source >= uint32 nodeCount then
        issues.Add(ReferenceOutOfRange(name, $"参照 {index} の発生元 {source} がノード数 {nodeCount} を超えています"))
        reported <- true
      elif target >= uint32 stringCount || qualifier >= uint32 stringCount then
        issues.Add(ReferenceOutOfRange(name, $"参照 {index} の文字列参照が {stringCount} 件を超えています"))
        reported <- true
      elif edgeCode = 0uy || edgeCode > EdgeKind.toCode MemberOf then
        issues.Add(UnknownEdgeKind(name, edgeCode))
        reported <- true
      elif not(Format.ReferenceRecord.hasValidMetadata record) then
        issues.Add(SegmentUnreadable(name, $"参照 {index} の言語・確度・段階または位置が不正です"))
        reported <- true

      index <- index + 1

let private checkGraph (view: Query.GraphView) (cancellation: CancellationToken) =
  let corrupt name detail =
    raise(Query.QueryException(Query.SegmentCorrupt(name, detail)))

  let kinds = Array.zeroCreate<int> 256

  for index in 0 .. view.NodeCount - 1 do
    checkCancellation cancellation index
    let node = view.Node index
    kinds[int(NodeKind.toCode node.Kind)] <- kinds[int(NodeKind.toCode node.Kind)] + 1

    if index = 0 && node.Kind <> Repository then
      corrupt "nodes" "The first node is not a repository"

    if index > 0 && index <= view.Manifest.Counts.Directories && node.Kind <> Directory then
      corrupt "nodes" "Directory nodes disagree with the dense schema"

  for entry in view.Manifest.Counts.NodeKinds do
    let actual =
      kinds
      |> Array.mapi(fun code count ->
        match NodeKind.ofCode(byte code) with
        | ValueSome kind when NodeKind.name kind = entry.Kind -> count
        | _ -> 0)
      |> Array.sum

    if actual <> entry.Count then
      corrupt "nodes" $"Node kind count disagrees: {entry.Kind}"

  for index in 0 .. view.FileCount - 1 do
    checkCancellation cancellation index
    view.FileNodeIndex(uint32 index) |> ignore

  // Sorted rows allow a bounded-memory transpose check without copying the graph.
  for kind in view.EdgeKinds do
    for source in 0 .. view.NodeCount - 1 do
      checkCancellation cancellation source

      match view.Adjacency(source, kind, false) with
      | ValueNone -> ()
      | ValueSome(struct (forward, offset, count)) ->
        for position in 0 .. count - 1 do
          checkCancellation cancellation position
          let target = view.Target(forward, offset, position)
          let mutable found = false

          match view.Adjacency(target, kind, true) with
          | ValueNone -> ()
          | ValueSome(struct (backward, reverseOffset, reverseCount)) ->
            let mutable low = 0
            let mutable high = reverseCount - 1

            while low <= high && not found do
              let middle = low + (high - low) / 2
              let candidate = view.Target(backward, reverseOffset, middle)

              if candidate = source then found <- true
              elif candidate < source then low <- middle + 1
              else high <- middle - 1

          if not found then
            corrupt (EdgeKind.name kind) "Forward and reverse CSR disagree"

/// 成果物へ記録された文法の版と、現在の実行ファイルの版を突き合わせる。
///
/// 違いは破損ではなく条件の違いである。失敗にはせず、再生成で結果が変わり得ることを伝える
/// （backlog 020）。
let private checkGrammars (manifest: Manifest.Manifest) (issues: ResizeArray<Issue>) =
  for recorded in manifest.Options.Grammars do
    match Srcnet.Extraction.GrammarVersions.tryFind recorded.Language with
    | ValueNone -> issues.Add(GrammarVersionDiffers(recorded.Language, recorded.Version, "（同梱していません）"))
    | ValueSome current ->
      if
        not(String.Equals(current.Version, recorded.Version, StringComparison.Ordinal))
        || not(String.Equals(current.Sha256, recorded.Sha256, StringComparison.Ordinal))
      then
        issues.Add(GrammarVersionDiffers(recorded.Language, recorded.Version, current.Version))

let private inspect (outputDirectory: string) (manifest: Manifest.Manifest) (cancellation: CancellationToken) =
  let issues = ResizeArray<Issue>()
  let allowedParts = Manifest.segmentFileNames outputDirectory manifest
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
        if not(File.Exists path) then
          issues.Add(SegmentMissing descriptor.Name)
        else
          let actualLength = FileInfo(path).Length

          if actualLength <> descriptor.ByteLength then
            issues.Add(LengthMismatch(descriptor.Name, descriptor.ByteLength, actualLength))
          else
            let actual = Reader.checksum path cancellation

            if not(String.Equals(actual, descriptor.Checksum, StringComparison.Ordinal)) then
              issues.Add(ChecksumMismatch(descriptor.Name, descriptor.Checksum, actual))

            bytesChecked <- bytesChecked + actualLength
            checkedSegments <- checkedSegments + 1
      with
      | :? FileNotFoundException -> issues.Add(SegmentMissing descriptor.Name)
      | :? DirectoryNotFoundException -> issues.Add(SegmentMissing descriptor.Name)
      | :? IOException as ex -> issues.Add(SegmentUnreadable(descriptor.Name, ex.Message))
      | :? UnauthorizedAccessException -> issues.Add(SegmentUnreadable(descriptor.Name, "読み取り権限がありません"))

  let openSegment
    (suffix: string)
    (kind: Format.SegmentKind)
    (expectedCount: int)
    (check: string -> Reader.MappedSegment -> unit)
    =
    let candidates =
      manifest.Segments
      |> Array.filter(fun segment -> segment.Name.EndsWith("." + suffix, StringComparison.Ordinal))

    match candidates with
    | [||] -> issues.Add(SegmentMissing suffix)
    | [| descriptor |] ->
      match Artifact.tryResolveSegment outputDirectory descriptor.Name with
      | Error error -> issues.Add(SegmentPathRejected(descriptor.Name, Artifact.PathError.describe error))
      | Ok path ->
        match Reader.MappedSegment.Open(path, allowedParts) with
        | Error error -> issues.Add(SegmentUnreadable(descriptor.Name, Reader.OpenError.describe error))
        | Ok segment ->
          use segment = segment

          if segment.Header.Kind <> kind then
            issues.Add(SegmentUnreadable(descriptor.Name, $"セグメント種別が {kind} ではありません"))
          elif segment.Header.PrimaryCount <> uint64 expectedCount then
            issues.Add(CountMismatch(descriptor.Name, int64 expectedCount, int64 segment.Header.PrimaryCount))
          else
            check descriptor.Name segment
    | _ -> issues.Add(SegmentUnreadable(suffix, "同じ役割のセグメントが複数あります"))

  // チェックサムが壊れている状態で構造を読むと、誤った診断が連鎖する。
  if issues.Count = 0 then
    openSegment "strings" Format.Strings manifest.Counts.Strings (fun name strings ->
      if strings.Header.SecondaryCount <> uint64 manifest.Counts.StringBytes then
        issues.Add(CountMismatch(name, manifest.Counts.StringBytes, int64 strings.Header.SecondaryCount))
      else
        openSegment "stroffsets" Format.StringOffsets manifest.Counts.Strings (fun offsetName offsets ->
          if offsets.Header.SecondaryCount <> strings.Header.SecondaryCount then
            issues.Add(
              CountMismatch(offsetName, int64 strings.Header.SecondaryCount, int64 offsets.Header.SecondaryCount)
            )
          else
            let before = issues.Count
            checkStringOffsets cancellation offsetName offsets issues

            if issues.Count = before then
              checkStringBytes cancellation name strings offsets issues))

    openSegment "nodes" Format.Nodes manifest.Counts.Nodes (fun _ segment ->
      openSegment "idmap" Format.IdMap manifest.Counts.Nodes (fun idName idMap ->
        checkIdMap cancellation segment idName idMap manifest.Counts.Nodes issues))

    // エッジ種別ごとに前方・後方の CSR を持つ。種別が増えても検査を取りこぼさないよう、
    // マニフェストが数えている種別をそのまま辿る。
    for entry in manifest.Counts.EdgeKinds do
      openSegment $"edges.{entry.Kind}" Format.AdjacencyCsr manifest.Counts.Nodes (fun name segment ->
        checkCsr cancellation name segment manifest.Counts.Nodes entry.Count issues)

      openSegment $"redges.{entry.Kind}" Format.AdjacencyCsr manifest.Counts.Nodes (fun name segment ->
        checkCsr cancellation name segment manifest.Counts.Nodes entry.Count issues)

    openSegment "refs" Format.References manifest.Counts.ReferenceCandidates (fun name segment ->
      checkReferences cancellation name segment manifest.Counts.Nodes manifest.Counts.Strings issues)

    if issues.Count = 0 then
      match Query.GraphView.Open outputDirectory with
      | Error error -> issues.Add(SegmentUnreadable("graph", Query.QueryError.describe error))
      | Ok view ->
        use view = view

        if view.Manifest <> manifest then
          issues.Add(ManifestUnreadable(Query.QueryError.describe Query.GenerationChanged))
        else
          try
            checkGraph view cancellation

            if
              manifest.Segments
              |> Array.exists(fun segment -> segment.Name.EndsWith(".lookup", StringComparison.Ordinal))
            then
              Query.validateLookup view cancellation
          with Query.QueryException error ->
            issues.Add(SegmentUnreadable("graph", Query.QueryError.describe error))

    checkGrammars manifest issues

  { Issues = issues.ToArray()
    SegmentsChecked = checkedSegments
    BytesChecked = bytesChecked }

/// 出力ディレクトリの成果物を検証する。
///
/// 検証中に再索引が完了すると、退役した世代を参照したまま欠損を報告してしまう。
/// `readStable` で観測の一貫性を確かめ、切替と競合した場合は測り直す。
let run (outputDirectory: string) (cancellation: CancellationToken) : Result<Report, Manifest.ManifestError> =
  Manifest.readStable outputDirectory (fun manifest -> inspect outputDirectory manifest cancellation)
