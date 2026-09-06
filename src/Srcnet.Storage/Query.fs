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

module QueryError =

  let describe error =
    match error with
    | ArtifactUnreadable error -> Manifest.ManifestError.describe error
    | SegmentUnavailable(name, error) -> $"{name}: {Reader.OpenError.describe error}"
    | SegmentMissing name -> $"セグメントがありません: {name}"
    | SegmentCorrupt(name, detail) -> $"{name}: {detail}"
    | GenerationChanged -> "読み取り中に成果物が更新されました。もう一度実行してください"

/// 1 ノード分の属性。文字列は参照のままで、復元は必要な分だけ行う。
[<Struct>]
type NodeView =
  { Index: int
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
    EndByte: int64 }

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
    edges: Dictionary<EdgeKind, EdgeSegments>
  ) =
  let mutable disposed = false
  let nodeCount = int nodes.Header.PrimaryCount
  let stringCount = int stringOffsets.Header.PrimaryCount

  let check () =
    ObjectDisposedException.ThrowIf(disposed, typeof<GraphView>)

  member _.Manifest = manifest

  member _.NodeCount = nodeCount

  member _.StringCount = stringCount

  member _.FileCount = int files.Header.PrimaryCount

  /// 成果物が持つエッジ種別。マニフェストの記録ではなく、実際に開けたセグメントで決まる。
  member _.EdgeKinds =
    edges.Keys
    |> Seq.sortBy EdgeKind.toCode
    |> Seq.toArray

  /// 文字列を UTF-8 のまま取り出す。比較だけが目的なら復号せずに済む。
  member _.StringBytes(index: int) : ReadOnlySpan<byte> =
    check ()

    if index < 0 || index >= stringCount then ReadOnlySpan.Empty
    else
      let offsets = stringOffsets.Payload
      let start = BinaryPrimitives.ReadUInt64LittleEndian(offsets.Slice(index * 8, 8))
      let finish = BinaryPrimitives.ReadUInt64LittleEndian(offsets.Slice((index + 1) * 8, 8))
      let blob = strings.Payload

      // 破損した成果物でも範囲外へ出ない。検証は `verify` の責務だが、
      // 照会側でも境界を確かめる（docs/security.md C-6）。
      if finish < start || finish > uint64 blob.Length then ReadOnlySpan.Empty
      else blob.Slice(int start, int (finish - start))

  /// 文字列を復元する。必要な分だけ呼ぶこと。
  member this.String(index: int) =
    let bytes = this.StringBytes index
    if bytes.Length = 0 then "" else Encoding.UTF8.GetString bytes

  member _.Node(index: int) : NodeView =
    check ()

    if index < 0 || index >= nodeCount then
      { Index = -1
        Id = { High = 0UL; Low = 0UL }
        Kind = Repository
        Language = Unknown
        Flags = NodeFlags.None
        FileIndex = Format.NodeRecord.NoFile
        NameRef = 0
        QualifiedNameRef = 0
        StartLine = 0
        EndLine = 0
        Ordinal = 0u
        StartByte = 0L
        EndByte = 0L }
    else

    let record = nodes.Payload.Slice(index * Format.RecordLength, Format.RecordLength)

    let kind =
      match NodeKind.ofCode record[Format.NodeRecord.KindOffset] with
      | ValueSome value -> value
      | ValueNone -> Repository

    let languageCode = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(Format.NodeRecord.LanguageOffset, 2))

    { Index = index
      Id = NodeId.ofBytes (record.Slice(Format.NodeRecord.IdOffset, Ids.NodeIdLength))
      Kind = kind
      Language = Language.ofCode languageCode
      Flags =
        LanguagePrimitives.EnumOfValue<uint32, NodeFlags>(
          BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.FlagsOffset, 4))
        )
      FileIndex = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.FileIndexOffset, 4))
      NameRef = int (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.NameOffset, 4)))
      QualifiedNameRef =
        int (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.QualifiedNameOffset, 4)))
      StartLine = int (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.StartLineOffset, 4)))
      EndLine = int (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.EndLineOffset, 4)))
      Ordinal = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.NodeRecord.OrdinalOffset, 4))
      StartByte = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(Format.NodeRecord.StartByteOffset, 8))
      EndByte = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(Format.NodeRecord.EndByteOffset, 8)) }

  /// ファイル レコードのパス文字列参照。
  member _.FilePathRef(fileIndex: uint32) =
    check ()

    if fileIndex = Format.NodeRecord.NoFile || int fileIndex >= int files.Header.PrimaryCount then -1
    else
      let record = files.Payload.Slice(int fileIndex * Format.RecordLength, Format.RecordLength)
      int (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.FileRecord.PathOffset, 4)))

  /// ノード ID から密インデックスを引く。`.idmap` は ID の昇順なので二分探索できる。
  member _.TryResolve(id: NodeId) =
    check ()
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
      let comparison = candidate.SequenceCompareTo key

      if comparison = 0 then
        found <- ValueSome(int (BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(indicesOffset + middle * 4, 4))))
        low <- high + 1
      elif comparison < 0 then low <- middle + 1
      else high <- middle - 1

    found

  /// 指定した種別・向きの隣接ノードを返す。
  ///
  /// CSR の範囲は書き出し時に昇順・重複なしで整えてあるため、返る配列もその性質を保つ。
  member _.Neighbors(index: int, kind: EdgeKind, direction: Direction) : int[] =
    check ()

    if index < 0 || index >= nodeCount then Array.empty
    else
      match edges.TryGetValue kind with
      | false, _ -> Array.empty
      | true, segments ->
        let read (segment: Reader.MappedSegment) =
          let payload = segment.Payload
          let count = int segment.Header.PrimaryCount

          if index >= count then Array.empty
          else
            let targetsOffset = (count + 1) * 8
            let start = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(index * 8, 8))
            let finish = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice((index + 1) * 8, 8))

            if finish < start || finish > segment.Header.SecondaryCount then Array.empty
            else
              let length = int (finish - start)
              let result = Array.zeroCreate<int> length

              for offset in 0 .. length - 1 do
                result[offset] <-
                  int (
                    BinaryPrimitives.ReadUInt32LittleEndian(
                      payload.Slice(targetsOffset + (int start + offset) * 4, 4)
                    )
                  )

              result

        match direction with
        | Outgoing -> read segments.Forward
        | Incoming -> read segments.Backward
        | Both ->
          // 両向きの和集合。昇順・重複なしを保つために併合する。
          let forward = read segments.Forward
          let backward = read segments.Backward
          let merged = SortedSet<int>()
          merged.UnionWith forward
          merged.UnionWith backward
          let result = Array.zeroCreate merged.Count
          merged.CopyTo result
          result

  interface IDisposable with

    member _.Dispose() =
      if not disposed then
        disposed <- true

        for entry in edges.Values do
          (entry :> IDisposable).Dispose()

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
    let mutable attempt = 0
    let mutable outcome = ValueNone

    while outcome.IsNone && attempt < StableOpenAttempts do
      attempt <- attempt + 1

      match Manifest.read outputDirectory with
      | Error error -> outcome <- ValueSome(Error(ArtifactUnreadable error))
      | Ok manifest ->
        let opened = List<IDisposable>()

        let openSegment (suffix: string) =
          match
            manifest.Segments
            |> Array.tryFind (fun segment -> segment.Name.EndsWith("." + suffix, StringComparison.Ordinal))
          with
          | None -> Error(SegmentMissing suffix)
          | Some descriptor ->
            match Artifact.tryResolveSegment outputDirectory descriptor.Name with
            | Error error -> Error(SegmentCorrupt(descriptor.Name, Artifact.PathError.describe error))
            | Ok path ->
              match Reader.MappedSegment.Open path with
              | Error error -> Error(SegmentUnavailable(descriptor.Name, error))
              | Ok segment ->
                opened.Add segment
                Ok segment

        let result =
          match
            openSegment "nodes",
            openSegment "files",
            openSegment "strings",
            openSegment "stroffsets",
            openSegment "idmap"
          with
          | Ok nodes, Ok files, Ok strings, Ok stringOffsets, Ok idMap ->
            let edges = Dictionary<EdgeKind, EdgeSegments>()
            let mutable failure = ValueNone

            // 成果物が持つ種別だけを開く。マニフェストが数えている種別を辿ることで、
            // 種別が増えても照会側の対応が漏れない。
            for entry in manifest.Counts.EdgeKinds do
              if failure.IsNone then
                match EdgeKind.all |> Array.tryFind (fun kind -> EdgeKind.name kind = entry.Kind) with
                | None -> ()
                | Some kind ->
                  match openSegment $"edges.{entry.Kind}", openSegment $"redges.{entry.Kind}" with
                  | Ok forward, Ok backward -> edges[kind] <- new EdgeSegments(forward, backward)
                  | Error error, _
                  | _, Error error -> failure <- ValueSome error

            match failure with
            | ValueSome error -> Error error
            | ValueNone ->
              Ok(new GraphView(manifest, nodes, files, strings, stringOffsets, idMap, edges))
          | Error error, _, _, _, _
          | _, Error error, _, _, _
          | _, _, Error error, _, _
          | _, _, _, Error error, _
          | _, _, _, _, Error error -> Error error

        match result with
        | Error error ->
          for item in opened do
            item.Dispose()

          outcome <- ValueSome(Error error)
        | Ok view ->
          // 開いている間に公開が起きていないことを確かめる。
          match Manifest.read outputDirectory with
          | Ok current when Manifest.generationIn current = Manifest.generationIn manifest ->
            outcome <- ValueSome(Ok view)
          | Ok _ ->
            (view :> IDisposable).Dispose()
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

/// 走査するノード数の上限。
///
/// M2 の成果物には検索索引がない。全走査へ黙って退行させず、上限に達したら
/// 打ち切りとして報告する（backlog 028）。索引は M5 で追加する。
[<Literal>]
let MaxScannedNodes = 20_000_000

type SearchOutcome =
  { Hits: SearchHit[]
    /// 上限に達して走査を打ち切った。
    Truncated: bool
    /// 検索索引を使わず全走査したか。M2 の成果物では常に true。
    UsedIndex: bool
    ScannedNodes: int }

/// 文字列表を 1 回走査して、一致の強さを求める。
///
/// ノードごとに文字列を復元すると、ノード数ぶんの割り当てが起きる。文字列表は重複排除
/// 済みで要素数が少ないため、先に表側で判定してからノードを走査するほうが速い。
let private matchStringTable (view: GraphView) (needle: string) (ignoreCase: bool) =
  let strengths = Array.zeroCreate<byte> view.StringCount

  if ignoreCase then
    // 大文字小文字を畳む場合は復号が要る。ロケール依存の比較はしない。
    let folded = Srcnet.Text.Unicode.caseFold needle

    for index in 0 .. view.StringCount - 1 do
      let text = Srcnet.Text.Unicode.caseFold(view.String index)

      strengths[index] <-
        if String.Equals(text, folded, StringComparison.Ordinal) then 3uy
        elif text.StartsWith(folded, StringComparison.Ordinal) then 2uy
        elif text.Contains(folded, StringComparison.Ordinal) then 1uy
        else 0uy
  else
    // 比較はバイト単位で行う。UTF-8 は自己同期的なので、部分列の一致が
    // 文字境界を跨いだ誤検出になることはない。
    let needleBytes = Encoding.UTF8.GetBytes needle

    for index in 0 .. view.StringCount - 1 do
      let bytes = view.StringBytes index

      strengths[index] <-
        if bytes.SequenceEqual(ReadOnlySpan needleBytes) then 3uy
        elif bytes.StartsWith(ReadOnlySpan needleBytes) then 2uy
        elif bytes.IndexOf(ReadOnlySpan needleBytes) >= 0 then 1uy
        else 0uy

  strengths

let private strengthOf (code: byte) =
  match code with
  | 3uy -> ValueSome Exact
  | 2uy -> ValueSome Prefix
  | 1uy -> ValueSome Substring
  | _ -> ValueNone

/// 名前・修飾名・パスに対して字句一致で検索する。
///
/// 順位付けは呼び出し側が行う。ここは「どのノードが、どの対象に、どの強さで一致したか」
/// までを決める。
let search (view: GraphView) (needle: string) (ignoreCase: bool) (cancellation: CancellationToken) =
  // 検索キーは NFC 正規化する。抽出時の名前も NFC なので、合成の違いで外れない。
  let normalized = Srcnet.Text.Unicode.normalize needle

  if normalized.Length = 0 then
    { Hits = Array.empty
      Truncated = false
      UsedIndex = false
      ScannedNodes = 0 }
  else

  let strengths = matchStringTable view normalized ignoreCase
  let hits = List<SearchHit>()
  let limit = min view.NodeCount MaxScannedNodes
  let mutable scanned = 0

  for index in 0 .. limit - 1 do
    if index &&& 0xFFFF = 0 then cancellation.ThrowIfCancellationRequested()
    scanned <- scanned + 1
    let node = view.Node index

    // 一致の強さは「名前 → 修飾名 → パス」の順で最も強いものを採る。
    let nameStrength = if node.NameRef < strengths.Length then strengths[node.NameRef] else 0uy

    let qualifiedStrength =
      if node.QualifiedNameRef < strengths.Length then strengths[node.QualifiedNameRef] else 0uy

    let pathRef = view.FilePathRef node.FileIndex
    let pathStrength = if pathRef >= 0 && pathRef < strengths.Length then strengths[pathRef] else 0uy

    // 強さを先に比較し、同点では名前 → 修飾名 → パスの順を保つ。
    let struct (target, strength) =
      if nameStrength >= qualifiedStrength && nameStrength >= pathStrength then struct (Name, nameStrength)
      elif qualifiedStrength >= pathStrength then struct (QualifiedName, qualifiedStrength)
      else struct (Path, pathStrength)

    match strengthOf strength with
    | ValueNone -> ()
    | ValueSome value ->
      hits.Add
        { Node = index
          Strength = value
          Target = target }

  { Hits = hits.ToArray()
    Truncated = view.NodeCount > limit
    UsedIndex = false
    ScannedNodes = scanned }

/// 検索結果の順位。docs/query-and-cli.md 3 の辞書式に従う。
///
/// 中心性（5 段目）は M6 で導入するため、現時点では常に 0 として扱う。順位が一意に
/// 定まることは 6 段目のパス順と、最後の ID 順で保証する。
let compareHits (view: GraphView) (left: SearchHit) (right: SearchHit) =
  let byStrength = compare (MatchStrength.rank left.Strength) (MatchStrength.rank right.Strength)

  if byStrength <> 0 then byStrength
  else
    let byTarget = compare (MatchTarget.rank left.Target) (MatchTarget.rank right.Target)

    if byTarget <> 0 then byTarget
    else
      let leftNode = view.Node left.Node
      let rightNode = view.Node right.Node

      // 定義を宣言より上に置く。
      let definitionRank (flags: NodeFlags) =
        if flags.HasFlag NodeFlags.Definition then 0
        elif flags.HasFlag NodeFlags.DeclarationOnly then 1
        else 2

      let byDefinition = compare (definitionRank leftNode.Flags) (definitionRank rightNode.Flags)

      if byDefinition <> 0 then byDefinition
      else
        // フラグによる減点。順位を下げるだけで、除外はしない。
        let penalty (flags: NodeFlags) =
          (if flags.HasFlag NodeFlags.Vendored then 1 else 0)
          + (if flags.HasFlag NodeFlags.Generated then 1 else 0)
          + (if flags.HasFlag NodeFlags.Test then 1 else 0)

        let byPenalty = compare (penalty leftNode.Flags) (penalty rightNode.Flags)

        if byPenalty <> 0 then byPenalty
        else
          let leftPath = view.String(view.FilePathRef leftNode.FileIndex)
          let rightPath = view.String(view.FilePathRef rightNode.FileIndex)
          let byPath = String.CompareOrdinal(leftPath, rightPath)

          if byPath <> 0 then byPath
          else
            // 同点の最終解消。ID は決定的なので順序も決定的になる。
            compare leftNode.Id rightNode.Id

// --- 近傍と経路 ---------------------------------------------------------------

/// 近傍探索で辿るノード数の上限。無制限 BFS を許さない（backlog 028）。
[<Literal>]
let MaxVisitedNodes = 200_000

[<Struct>]
type EdgeView =
  { From: int
    To: int
    Kind: EdgeKind }

type Traversal =
  { /// 起点からの距離つきで訪れたノード。距離の昇順、同距離は密インデックス昇順。
    Nodes: (struct (int * int))[]
    /// 辿ったエッジ。始点・種別コード・終点の昇順。
    Edges: EdgeView[]
    /// 上限に達して打ち切った。
    Truncated: bool }

/// 幅優先で近傍を辿る。
///
/// 深さと訪問数の両方に上限を設ける。どちらかに達したら打ち切り、結果に印を残す。
let neighbors
  (view: GraphView)
  (start: int)
  (kinds: EdgeKind[])
  (direction: Direction)
  (depth: int)
  (cancellation: CancellationToken)
  : Traversal =
  if start < 0 || start >= view.NodeCount then
    { Nodes = Array.empty
      Edges = Array.empty
      Truncated = false }
  else

  let distance = Dictionary<int, int>()
  let edges = List<EdgeView>()
  let queue = Queue<int>()
  distance[start] <- 0
  queue.Enqueue start
  let mutable truncated = false

  while queue.Count > 0 do
    cancellation.ThrowIfCancellationRequested()
    let current = queue.Dequeue()
    let currentDistance = distance[current]

    if currentDistance < depth then
      for kind in kinds do
        for next in view.Neighbors(current, kind, direction) do
          if distance.Count >= MaxVisitedNodes then truncated <- true
          else
            edges.Add { From = current; To = next; Kind = kind }

            if not (distance.ContainsKey next) then
              distance[next] <- currentDistance + 1
              queue.Enqueue next

  let orderedNodes =
    distance
    |> Seq.map (fun entry -> struct (entry.Key, entry.Value))
    |> Seq.sortWith (fun (struct (leftNode, leftDistance)) (struct (rightNode, rightDistance)) ->
      let byDistance = compare leftDistance rightDistance
      if byDistance <> 0 then byDistance else compare leftNode rightNode)
    |> Seq.toArray

  let orderedEdges = edges.ToArray()

  Array.sortInPlaceWith
    (fun (left: EdgeView) (right: EdgeView) ->
      let byFrom = compare left.From right.From

      if byFrom <> 0 then byFrom
      else
        let byKind = compare (EdgeKind.toCode left.Kind) (EdgeKind.toCode right.Kind)
        if byKind <> 0 then byKind else compare left.To right.To)
    orderedEdges

  { Nodes = orderedNodes
    Edges = orderedEdges
    Truncated = truncated }

/// 2 ノード間の最短経路。深さ上限つきの幅優先で解く。
///
/// 経路が複数ある場合は、密インデックスの昇順で最初に見つかったものを返す。CSR の
/// 隣接が昇順に整列しているため、この選択は決定的である。
let shortestPath
  (view: GraphView)
  (source: int)
  (target: int)
  (kinds: EdgeKind[])
  (direction: Direction)
  (maxDepth: int)
  (cancellation: CancellationToken)
  : int[] voption =
  if source < 0 || target < 0 || source >= view.NodeCount || target >= view.NodeCount then ValueNone
  elif source = target then ValueSome [| source |]
  else

  let previous = Dictionary<int, int>()
  let queue = Queue<struct (int * int)>()
  previous[source] <- -1
  queue.Enqueue(struct (source, 0))
  let mutable found = false

  while not found && queue.Count > 0 do
    cancellation.ThrowIfCancellationRequested()
    let struct (current, currentDepth) = queue.Dequeue()

    if currentDepth < maxDepth && previous.Count < MaxVisitedNodes then
      // 種別をまたぐ場合も、隣接は種別コード順・添字順で決まる。
      for kind in kinds do
        for next in view.Neighbors(current, kind, direction) do
          if not found && not (previous.ContainsKey next) then
            previous[next] <- current

            if next = target then found <- true
            else queue.Enqueue(struct (next, currentDepth + 1))

  if not found then ValueNone
  else
    let path = List<int>()
    let mutable cursor = target

    while cursor >= 0 do
      path.Add cursor
      cursor <- previous[cursor]

    path.Reverse()
    ValueSome(path.ToArray())
