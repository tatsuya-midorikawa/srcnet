/// 抽出結果の言語非依存モデル。
///
/// T1（行指向）と T2（構文）は同じ中間表現へ落とす。段階ごとに別の表現を持つと、
/// 書き出しと解決の経路が段階の数だけ増える。docs/extraction.md 2 を参照。
///
/// 本文（原文バイト列）はこのモデルへ持ち込まない。抽出中だけ借用し、結果には
/// 位置と切り出した文字列だけを残す。原文を保持すると常駐メモリが入力規模に比例する。
module Srcnet.Extraction.Model

open Srcnet.Core.Graph

/// 抽出段階。docs/extraction.md 2 の表に対応する。
type Tier =
  /// 走査のみ。パス、サイズ、内容ハッシュ、言語判定。
  | Structure
  /// 行指向走査による取り込み、定義候補、根拠コメント。
  | LineOriented
  /// 構文解析による定義・参照・呼び出し・継承。
  | Syntax
  /// ビルド構成に基づく高精度解決。未実装。
  | BuildAware

module Tier =

  /// 成果物へ書き出す安定コード。`--tier <n>` の数値と一致させる。
  let toCode tier =
    match tier with
    | Structure -> 0uy
    | LineOriented -> 1uy
    | Syntax -> 2uy
    | BuildAware -> 3uy

  let ofCode code =
    match code with
    | 0uy -> ValueSome Structure
    | 1uy -> ValueSome LineOriented
    | 2uy -> ValueSome Syntax
    | 3uy -> ValueSome BuildAware
    | _ -> ValueNone

  let name tier =
    match tier with
    | Structure -> "T0"
    | LineOriented -> "T1"
    | Syntax -> "T2"
    | BuildAware -> "T3"

  /// 段階の順序比較。`Structure < LineOriented < Syntax < BuildAware`。
  let inline rank tier = int (toCode tier)

/// 抽出の上限。いずれも資源枯渇に対する安全弁であり、超過は失敗ではなく打ち切りである。
///
/// `MaxFileSizeBytes`（走査の上限）とは別に持つのは、「ハッシュは取れるが抽出はしない」
/// 状態を表現するためである。内容ハッシュは逐次読みで有界だが、抽出は本文を保持する。
module Limits =

  /// 抽出対象にするファイルの上限。これを超えるファイルはハッシュだけを取る。
  [<Literal>]
  let DefaultMaxExtractionBytes = 8_388_608L

  /// 1 ファイルあたりのシンボル数の上限。
  [<Literal>]
  let MaxSymbolsPerFile = 65_536

  /// 1 ファイルあたりの参照候補数の上限。
  [<Literal>]
  let MaxReferencesPerFile = 262_144

  /// T1 が走査する 1 行の上限（バイト）。超えた分は読み飛ばす。
  [<Literal>]
  let MaxLineBytes = 65_536

  /// T1 が走査する行数の上限。
  [<Literal>]
  let MaxLines = 4_000_000

  /// 前処理条件の入れ子の上限。
  [<Literal>]
  let MaxConditionDepth = 256

  /// 1 ファイルあたりの前処理条件の上限。
  [<Literal>]
  let MaxConditionsPerFile = 65_536

  /// 条件式の原文として保持する最大文字数。原文をそのまま持つが、無制限にはしない。
  [<Literal>]
  let MaxConditionTextLength = 512

  /// 識別子・修飾名として受け入れる最大文字数。
  [<Literal>]
  let MaxNameLength = 1_024

  /// 根拠コメントの本文として保持する最大文字数。
  [<Literal>]
  let MaxNoteTextLength = 256

  /// 生成物マーカーを探すファイル先頭の行数。
  /// 全体を対象にすると、本文に `DO NOT EDIT` を含むだけのファイルまで生成扱いになる。
  [<Literal>]
  let GeneratedMarkerScanLines = 32

  /// 生成物マーカーを探すファイル先頭の範囲（バイト）。行数の上限と併せて使い、
  /// 極端に長い行が並ぶファイルでも走査量を有界に保つ。
  [<Literal>]
  let GeneratedMarkerScanBytes = 8_192

/// 原文中のバイト範囲。`Start = End` は「範囲なし」を表す。
[<Struct; StructuralEquality; StructuralComparison>]
type ByteRange =
  { Start: int
    End: int }

  member this.IsEmpty = this.End <= this.Start

module ByteRange =

  let empty = { Start = 0; End = 0 }

  let create (start: int) (finish: int) =
    if finish <= start then empty else { Start = start; End = finish }

/// 抽出したシンボル。
///
/// `Parent` は同一ファイル内のシンボル添字で、ファイル直下は -1。位置ではなく添字で
/// 持つのは、書き出し時に密インデックスへ写し替えるためである。
type ExtractedSymbol =
  { Kind: NodeKind
    /// 短い名前。NFC 正規化済み。
    Name: string
    /// 修飾名。NFC 正規化済み。
    QualifiedName: string
    /// 親スコープのシンボル添字。ファイル直下は -1。
    Parent: int
    /// 同一ファイル内で `(種別, 修飾名)` が衝突する場合の序数。出現位置順。
    Ordinal: uint32
    StartLine: int
    EndLine: int
    StartByte: int
    EndByte: int
    Flags: NodeFlags
    /// 直前のドキュメント コメントの原文範囲。要約は生成しない。
    Doc: ByteRange
    /// 条件付きコンパイル下にある場合の条件式の原文。評価はしない。
    Condition: string }

/// 抽出した参照候補。解決は M3 で行う。docs/extraction.md 5 を参照。
type ExtractedReference =
  { /// 発生元シンボルの添字。ファイル自身が発生元の場合は -1。
    Source: int
    Kind: EdgeKind
    /// 対象の生テキスト（修飾名、短い名前、取り込みの綴りのいずれか）。
    Target: string
    /// 修飾ヒント。囲みスコープや名前空間など、解決の材料になる文字列。
    Qualifier: string
    Language: Language
    StartByte: int
    EndByte: int
    Line: int
    /// 根拠段階（docs/extraction.md 5.2 の段階番号）。未解決は 0。
    Stage: byte
    Confidence: Confidence }

/// 抽出を行わなかった理由。診断へ写す。
type SkipReason =
  /// 抽出の上限を超えた。
  | TooLargeToExtract of bytes: int64
  /// テキストとして解釈できない。
  | NotText
  /// この言語の文法を同梱していない、または解析器が無い。
  | NoGrammar
  /// 解析が時間の上限を超えた。
  | ParseTimedOut
  /// 解析器が木を返さなかった。
  | ParseUnavailable of detail: string

module SkipReason =

  let describe reason =
    match reason with
    | TooLargeToExtract bytes -> $"抽出の上限を超えています ({bytes} バイト)"
    | NotText -> "テキストとして解釈できません"
    | NoGrammar -> "対応する文法がありません"
    | ParseTimedOut -> "解析が時間の上限を超えました"
    | ParseUnavailable detail -> detail

/// 1 ファイル分の抽出結果。
type ExtractedFile =
  { /// 実際に適用した段階。
    Tier: Tier
    /// バイト位置の昇順、同位置は外側のスコープが先。
    Symbols: ExtractedSymbol[]
    /// 発生元の添字 → エッジ種別コード → バイト位置の昇順。
    References: ExtractedReference[]
    /// 抽出中に判明したファイル属性（生成コード、打ち切りなど）。走査結果へ合成する。
    FileFlags: NodeFlags
    /// 復号後に確定した行数。走査時に確定していれば `ValueNone`。
    LineCount: int voption
    /// 上限に達して結果を切り捨てた。
    Truncated: bool
    /// 抽出を行わなかった、または縮退した理由。
    Skipped: SkipReason voption }

module ExtractedFile =

  let empty tier =
    { Tier = tier
      Symbols = Array.empty
      References = Array.empty
      FileFlags = NodeFlags.None
      LineCount = ValueNone
      Truncated = false
      Skipped = ValueNone }

  let skipped tier reason =
    { empty tier with Skipped = ValueSome reason }

/// シンボルの決定的な並び。
///
/// バイト位置の昇順、同位置では外側のスコープ（終端が後ろのもの）を先にする。
/// 外側を先にすることで、親の添字が必ず子より小さくなる。
let compareSymbols (left: ExtractedSymbol) (right: ExtractedSymbol) =
  let byStart = compare left.StartByte right.StartByte

  if byStart <> 0 then byStart
  else
    let byEnd = compare right.EndByte left.EndByte

    if byEnd <> 0 then byEnd
    else
      let byKind = compare (NodeKind.toCode left.Kind) (NodeKind.toCode right.Kind)

      if byKind <> 0 then byKind
      else
        let byName = System.String.CompareOrdinal(left.QualifiedName, right.QualifiedName)
        if byName <> 0 then byName else compare left.Ordinal right.Ordinal

/// 参照候補の決定的な並び。docs/extraction.md 5、backlog 016 の並び順に対応する。
let compareReferences (left: ExtractedReference) (right: ExtractedReference) =
  let bySource = compare left.Source right.Source

  if bySource <> 0 then bySource
  else
    let byKind = compare (EdgeKind.toCode left.Kind) (EdgeKind.toCode right.Kind)

    if byKind <> 0 then byKind
    else
      let byStart = compare left.StartByte right.StartByte

      if byStart <> 0 then byStart
      else
        let byTarget = System.String.CompareOrdinal(left.Target, right.Target)

        if byTarget <> 0 then byTarget
        else System.String.CompareOrdinal(left.Qualifier, right.Qualifier)

/// シンボルと参照候補を決定的な順序へ整える。
///
/// 抽出器は出現順に追記してよい。並べ替えに伴う添字の付け替えをここへ集約することで、
/// 各抽出器が順序の不変条件を個別に守る必要がなくなる。
let normalize (symbols: ExtractedSymbol[]) (references: ExtractedReference[]) =
  let count = symbols.Length
  let order = Array.init count id

  // 安定性は元の添字を最後の鍵にすることで保証する。`Array.sortInPlaceWith` は
  // 安定でないため、比較関数側で全順序を作る。
  Array.sortInPlaceWith
    (fun (a: int) (b: int) ->
      let byValue = compareSymbols symbols[a] symbols[b]
      if byValue <> 0 then byValue else compare a b)
    order

  let newIndexOf = Array.zeroCreate<int> count

  for position in 0 .. count - 1 do
    newIndexOf[order[position]] <- position

  let inline remap (index: int) =
    if index >= 0 && index < count then newIndexOf[index] else -1

  let sortedSymbols =
    Array.init count (fun position ->
      let symbol = symbols[order[position]]
      { symbol with Parent = remap symbol.Parent })

  let sortedReferences = references |> Array.map (fun reference -> { reference with Source = remap reference.Source })

  Array.sortInPlaceWith
    (fun (a: ExtractedReference) (b: ExtractedReference) -> compareReferences a b)
    sortedReferences

  struct (sortedSymbols, sortedReferences)
