/// `.gitignore` および `.srcnetignore` のパターン照合。
///
/// 正規表現は使わない。入力依存のバックトラッキングによる実行時間爆発を避けるため、
/// セグメント単位は動的計画法、セグメント内は二点走査で照合する。いずれも入力長に対して
/// 多項式時間で、病的な入力でも指数的に劣化しない。docs/security.md C-4 を参照。
module Srcnet.Discovery.Ignore

open System
open System.Buffers
open System.Collections.Generic
open Srcnet.Core.Paths

/// セグメント内のグロブ トークン。パターン解析時に一度だけ構築する。
type private GlobToken =
  | Exact of char
  | AnyChar
  | AnyRun
  | CharacterClass of ranges: struct (char * char)[] * negated: bool

/// パス セグメント 1 つ分の照合規則。
type private SegmentRule =
  /// `**`。0 個以上のセグメントに一致する。
  | AnyDepth
  /// ワイルドカードを含まないセグメント。序数比較で足りる。
  | LiteralSegment of string
  | GlobSegment of GlobToken[]

/// 1 行分のパターン。
type Rule =
  private
    { Negated: bool
      DirectoryOnly: bool
      Segments: SegmentRule[] }

/// 1 つの無視ファイル。`BaseDepth` は無視ファイルが置かれたディレクトリの階層の深さで、
/// 照合はそこからの相対セグメント列に対して行う。
type RuleSet =
  private
    { BaseDepth: int
      Rules: Rule[]
      Truncated: bool }

  /// 規則数の上限に達し、一部の規則を読み捨てたか。
  member this.IsTruncated = this.Truncated

/// 適用結果。最後に一致した規則が結論を決める。
type Decision =
  | NotMatched
  | Ignored
  | Reincluded

let private parseClass (pattern: string) (start: int) =
  // `[` の次から `]` までを読む。閉じ括弧がなければクラスとして扱わない。
  let mutable index = start
  let negated = index < pattern.Length && (pattern[index] = '!' || pattern[index] = '^')
  if negated then index <- index + 1

  let ranges = List<struct (char * char)>()
  let mutable closed = false

  // POSIX と同様、先頭の `]` はリテラルとして扱う。
  let mutable first = true

  while not closed && index < pattern.Length do
    if pattern[index] = ']' && not first then
      closed <- true
      index <- index + 1
    else
      first <- false
      let low = pattern[index]

      if index + 2 < pattern.Length && pattern[index + 1] = '-' && pattern[index + 2] <> ']' then
        ranges.Add(struct (low, pattern[index + 2]))
        index <- index + 3
      else
        ranges.Add(struct (low, low))
        index <- index + 1

  if closed then ValueSome(struct (ranges.ToArray(), negated, index)) else ValueNone

let private tokenize (segment: string) =
  let tokens = List<GlobToken>()
  let mutable index = 0

  while index < segment.Length do
    match segment[index] with
    | '*' ->
      // `**` はセグメント内では単一の `*` と同じ意味になる。
      while index < segment.Length && segment[index] = '*' do
        index <- index + 1

      tokens.Add AnyRun
    | '?' ->
      tokens.Add AnyChar
      index <- index + 1
    | '[' ->
      match parseClass segment (index + 1) with
      | ValueSome(struct (ranges, negated, next)) ->
        tokens.Add(CharacterClass(ranges, negated))
        index <- next
      | ValueNone ->
        tokens.Add(Exact '[')
        index <- index + 1
    | '\\' when index + 1 < segment.Length ->
      tokens.Add(Exact segment[index + 1])
      index <- index + 2
    | c ->
      tokens.Add(Exact c)
      index <- index + 1

  tokens.ToArray()

let private hasWildcard (segment: string) =
  segment.IndexOfAny [| '*'; '?'; '['; '\\' |] >= 0

let private matchClass (ranges: struct (char * char)[]) (negated: bool) (c: char) =
  let mutable inside = false

  for struct (low, high) in ranges do
    if c >= low && c <= high then inside <- true

  inside <> negated

let inline private matchToken (token: GlobToken) (c: char) =
  match token with
  | Exact expected -> expected = c
  | AnyChar -> true
  | CharacterClass(ranges, negated) -> matchClass ranges negated c
  | AnyRun -> false

/// セグメント内のグロブ照合。`*` の位置を 1 つだけ記憶する二点走査で、
/// 最悪でも O(パターン長 × 入力長) に収まる。
let private matchGlob (tokens: GlobToken[]) (text: string) =
  let mutable t = 0
  let mutable s = 0
  let mutable starToken = -1
  let mutable starText = -1
  let mutable failed = false

  while not failed && s < text.Length do
    if t < tokens.Length && tokens[t] = AnyRun then
      starToken <- t
      starText <- s
      t <- t + 1
    elif t < tokens.Length && matchToken tokens[t] text[s] then
      t <- t + 1
      s <- s + 1
    elif starToken >= 0 then
      starText <- starText + 1
      t <- starToken + 1
      s <- starText
    else failed <- true

  while not failed && t < tokens.Length && tokens[t] = AnyRun do
    t <- t + 1

  not failed && t = tokens.Length

let inline private matchSegment (rule: SegmentRule) (segment: string) =
  match rule with
  | AnyDepth -> false
  | LiteralSegment literal -> String.Equals(literal, segment, StringComparison.Ordinal)
  | GlobSegment tokens -> matchGlob tokens segment

/// セグメント列の照合。`**` は 0 個以上のセグメントに一致する。
/// 動的計画法で解くため、`**` が複数あってもバックトラッキングは起きない。
///
/// 走査中のあらゆるファイル項目 × 規則数だけ呼ばれる最も熱い経路なので、
/// 作業配列はプールから借りて割り当てを避ける。
let private matchSegments (rules: SegmentRule[]) (segments: string[]) (offset: int) =
  let ruleCount = rules.Length
  let segmentCount = segments.Length - offset
  let width = ruleCount + 1
  let previous = ArrayPool<bool>.Shared.Rent width
  let current = ArrayPool<bool>.Shared.Rent width

  try
    Array.Clear(previous, 0, width)
    Array.Clear(current, 0, width)
    previous[0] <- true

    for j in 1..ruleCount do
      previous[j] <- previous[j - 1] && rules[j - 1] = AnyDepth

    for i in 1..segmentCount do
      let segment = segments[offset + i - 1]
      current[0] <- false

      for j in 1..ruleCount do
        current[j] <-
          match rules[j - 1] with
          | AnyDepth -> current[j - 1] || previous[j]
          | rule -> previous[j - 1] && matchSegment rule segment

      Array.blit current 0 previous 0 width

    previous[ruleCount]
  finally
    ArrayPool<bool>.Shared.Return previous
    ArrayPool<bool>.Shared.Return current

/// 行末の未エスケープの空白を取り除く。gitignore の規則に合わせる。
let private trimTrailingSpaces (line: string) =
  let mutable last = line.Length

  while last > 0 && line[last - 1] = ' ' && (last < 2 || line[last - 2] <> '\\') do
    last <- last - 1

  if last = line.Length then line else line.Substring(0, last)

let private parseLine (line: string) : Rule voption =
  let trimmed = trimTrailingSpaces line

  if trimmed.Length = 0 || trimmed[0] = '#' then ValueNone
  else

  let negated = trimmed[0] = '!'
  let body = if negated then trimmed.Substring 1 else trimmed

  // `\#` や `\!` のエスケープを解く。
  let body =
    if body.Length >= 2 && body[0] = '\\' && (body[1] = '#' || body[1] = '!') then body.Substring 1
    else body

  if body.Length = 0 then ValueNone
  elif body.StartsWith("../", StringComparison.Ordinal) || body = ".." then
    // ルート外を参照するパターンは受け付けない。docs/security.md C-3 を参照。
    ValueNone
  else

  let directoryOnly = body.EndsWith('/')
  let body = if directoryOnly then body.TrimEnd '/' else body

  if body.Length = 0 then ValueNone
  else

  let anchored = body.IndexOf '/' >= 0
  let body = if body.StartsWith('/') then body.Substring 1 else body
  let parts = body.Split '/'

  let rules =
    parts
    |> Array.choose (fun part ->
      if part.Length = 0 then None
      elif part = "**" then Some AnyDepth
      elif hasWildcard part then Some(GlobSegment(tokenize part))
      else Some(LiteralSegment part))

  if rules.Length = 0 then ValueNone
  else

  // `/` を含まないパターンは任意の階層で一致する。先頭に `**` を補って表現を一本化する。
  let segments =
    if anchored then rules else Array.append [| AnyDepth |] rules

  ValueSome
    { Negated = negated
      DirectoryOnly = directoryOnly
      Segments = segments }

/// 1 つの無視ファイルから読み込む規則数の上限。
///
/// 規則数は照合コストに線形に効き、対象リポジトリが自由に決められる値である。
/// 上限を置かなければ、巨大な `.gitignore` 1 つで走査時間を任意に伸ばせる。
/// docs/security.md C-4 を参照。
[<Literal>]
let MaxRulesPerFile = 10000

/// 無視ファイルの内容から規則集合を構築する。
/// `baseDepth` は無視ファイルが置かれたディレクトリの階層の深さ。
/// 上限を超えた規則は読み捨て、`Truncated` で呼び出し側に知らせる。
let parse (baseDepth: int) (lines: string seq) =
  let rules = List<Rule>()
  let mutable truncated = false

  for line in lines do
    if rules.Count >= MaxRulesPerFile then truncated <- true
    else
      match parseLine line with
      | ValueSome rule -> rules.Add rule
      | ValueNone -> ()

  { BaseDepth = baseDepth
    Rules = rules.ToArray()
    Truncated = truncated }

let isEmpty (ruleSet: RuleSet) = ruleSet.Rules.Length = 0

/// 単一の規則集合に対する判定。同一ファイル内では後の規則が優先するため、逆順に走査する。
let private applyRuleSet (ruleSet: RuleSet) (segments: string[]) (isDirectory: bool) =
  if ruleSet.BaseDepth >= segments.Length then NotMatched
  else
    let mutable decision = NotMatched
    let mutable index = ruleSet.Rules.Length - 1

    while decision = NotMatched && index >= 0 do
      let rule = ruleSet.Rules[index]

      if (not rule.DirectoryOnly || isDirectory) && matchSegments rule.Segments segments ruleSet.BaseDepth then
        decision <- if rule.Negated then Reincluded else Ignored

      index <- index - 1

    decision

/// 階層化された規則集合を適用する。
/// 深い位置の無視ファイルが浅い位置より優先するため、末尾から走査する。
let decide (ruleSets: RuleSet[]) (segments: string[]) (isDirectory: bool) =
  let mutable decision = NotMatched
  let mutable index = ruleSets.Length - 1

  while decision = NotMatched && index >= 0 do
    decision <- applyRuleSet ruleSets[index] segments isDirectory
    index <- index - 1

  decision

/// 論理パスをセグメント列へ分解する。ルートは空配列になる。
let segmentsOf (path: LogicalPath) =
  let value = value path
  if value.Length = 0 then Array.empty else value.Split '/'
