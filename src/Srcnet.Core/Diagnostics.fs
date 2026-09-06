/// 診断の収集。
///
/// 超大規模リポジトリでは一部ファイルの失敗が正常系であり、単一ファイルの失敗で
/// 全体を中断してはならない。診断は種別ごとに集計し、上限を超えたら要約する。
/// docs/architecture.md 5 を参照。
module Srcnet.Core.Diagnostics

open System
open System.Collections.Generic

/// `Error` が `Result` のケースを隠さないよう、必ず修飾して参照する。
[<RequireQualifiedAccess>]
type Severity =
  | Info
  | Warning
  | Error

type DiagnosticKind =
  | FileUnreadable
  | PermissionDenied
  | SymbolicLinkSkipped
  | SymbolicLinkEscapesRoot
  | DirectoryCycle
  | DepthLimitExceeded
  | PathRejected
  | FileTooLarge
  | BinaryContent
  | UndeterminedEncoding
  | AmbiguousEncoding
  | CaseOnlyCollision
  | NodeIdCollision
  | IgnoreFileUnreadable
  | EntryLimitExceeded
  /// 抽出の上限に達して結果を切り捨てた。
  | ExtractionTruncated
  /// 要求した抽出段階へ届かず、下の段階で実行した。
  | ExtractionDegraded

module DiagnosticKind =

  let toCode kind =
    match kind with
    | FileUnreadable -> 1us
    | PermissionDenied -> 2us
    | SymbolicLinkSkipped -> 3us
    | SymbolicLinkEscapesRoot -> 4us
    | DirectoryCycle -> 5us
    | DepthLimitExceeded -> 6us
    | PathRejected -> 7us
    | FileTooLarge -> 8us
    | BinaryContent -> 9us
    | UndeterminedEncoding -> 10us
    | AmbiguousEncoding -> 11us
    | CaseOnlyCollision -> 12us
    | NodeIdCollision -> 13us
    | IgnoreFileUnreadable -> 14us
    | EntryLimitExceeded -> 15us
    | ExtractionTruncated -> 16us
    | ExtractionDegraded -> 17us

  let name kind =
    match kind with
    | FileUnreadable -> "file-unreadable"
    | PermissionDenied -> "permission-denied"
    | SymbolicLinkSkipped -> "symlink-skipped"
    | SymbolicLinkEscapesRoot -> "symlink-escapes-root"
    | DirectoryCycle -> "directory-cycle"
    | DepthLimitExceeded -> "depth-limit-exceeded"
    | PathRejected -> "path-rejected"
    | FileTooLarge -> "file-too-large"
    | BinaryContent -> "binary-content"
    | UndeterminedEncoding -> "undetermined-encoding"
    | AmbiguousEncoding -> "ambiguous-encoding"
    | CaseOnlyCollision -> "case-only-collision"
    | NodeIdCollision -> "node-id-collision"
    | IgnoreFileUnreadable -> "ignore-file-unreadable"
    | EntryLimitExceeded -> "entry-limit-exceeded"
    | ExtractionTruncated -> "extraction-truncated"
    | ExtractionDegraded -> "extraction-degraded"

  /// 成果物を信用できなくする診断か。これが 1 件でもあれば完全な成果物とは呼べない。
  let severity kind =
    match kind with
    | FileUnreadable -> Severity.Warning
    | PermissionDenied -> Severity.Warning
    | SymbolicLinkSkipped -> Severity.Info
    | SymbolicLinkEscapesRoot -> Severity.Warning
    | DirectoryCycle -> Severity.Warning
    | DepthLimitExceeded -> Severity.Warning
    | PathRejected -> Severity.Warning
    | FileTooLarge -> Severity.Warning
    | BinaryContent -> Severity.Info
    | UndeterminedEncoding -> Severity.Warning
    | AmbiguousEncoding -> Severity.Info
    | CaseOnlyCollision -> Severity.Warning
    | NodeIdCollision -> Severity.Error
    | IgnoreFileUnreadable -> Severity.Warning
    | EntryLimitExceeded -> Severity.Warning
    // 抽出の打ち切りは、そのファイルの結果が不完全であるという事実である。
    | ExtractionTruncated -> Severity.Warning
    // 縮退は条件の違いであって破損ではない。走査自体は完全に成立している。
    | ExtractionDegraded -> Severity.Info

[<Struct>]
type Diagnostic =
  { Kind: DiagnosticKind
    /// 対象の論理パス。対象が特定できない場合は空文字列。
    Path: string
    /// 追加情報。対象リポジトリ由来の文字列を含み得るため、表示前に無害化すること。
    Detail: string }

  member this.Severity = DiagnosticKind.severity this.Kind

/// 種別・パス・詳細の順で決まる全順序。並列に収集した診断を決定的に整列するために使う。
type DiagnosticComparer() =

  interface IComparer<Diagnostic> with

    member _.Compare(left, right) =
      let byKind = compare (DiagnosticKind.toCode left.Kind) (DiagnosticKind.toCode right.Kind)

      if byKind <> 0 then byKind
      else
        let byPath = String.CompareOrdinal(left.Path, right.Path)
        if byPath <> 0 then byPath else String.CompareOrdinal(left.Detail, right.Detail)

/// 診断の収集先。
///
/// 種別ごとに、整列順で先頭 `maxSamplesPerKind` 件の**相異なる**診断だけを保持する。
/// 保持集合は「その種別で最も小さい N 件」と定義されるため、到着順や並列度が変わっても
/// 結果は変わらない。件数の集計は打ち切らないので、要約は常に正確である。
[<Sealed>]
type DiagnosticSink(maxSamplesPerKind: int) =
  let sync = obj ()
  let comparer = DiagnosticComparer()
  // 種別ごとに独立した整列集合を持つ。全体を走査せずに最大要素を落とせる。
  let samplesByKind = Dictionary<DiagnosticKind, SortedSet<Diagnostic>>()
  let counts = Dictionary<DiagnosticKind, int>()
  let mutable total = 0
  let mutable errors = 0

  new() = DiagnosticSink 32

  member _.MaxSamplesPerKind = maxSamplesPerKind

  member _.Add(kind: DiagnosticKind, path: string, detail: string) =
    let diagnostic =
      { Kind = kind
        Path = path
        Detail = detail }

    lock sync (fun () ->
      total <- total + 1

      match DiagnosticKind.severity kind with
      | Severity.Error -> errors <- errors + 1
      | Severity.Info
      | Severity.Warning -> ()

      counts[kind] <-
        match counts.TryGetValue kind with
        | true, existing -> existing + 1
        | false, _ -> 1

      let samples =
        match samplesByKind.TryGetValue kind with
        | true, existing -> existing
        | false, _ ->
          let created = SortedSet<Diagnostic>(comparer)
          samplesByKind[kind] <- created
          created

      if samples.Add diagnostic && samples.Count > maxSamplesPerKind then
        samples.Remove samples.Max |> ignore)

  member _.Total = lock sync (fun () -> total)

  member _.ErrorCount = lock sync (fun () -> errors)

  member _.HasAny = lock sync (fun () -> total > 0)

  /// 保持している標本を、種別コード順・整列順で返す。
  member _.Samples() =
    lock sync (fun () ->
      samplesByKind
      |> Seq.sortBy (fun entry -> DiagnosticKind.toCode entry.Key)
      |> Seq.collect (fun entry -> entry.Value)
      |> Seq.toArray)

  /// 種別ごとの正確な件数を、種別コード順で返す。
  member _.Counts() =
    lock sync (fun () ->
      counts
      |> Seq.map (fun entry -> struct (entry.Key, entry.Value))
      |> Seq.sortBy (fun (struct (kind, _)) -> DiagnosticKind.toCode kind)
      |> Seq.toArray)
