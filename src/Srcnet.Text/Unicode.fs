/// Unicode 正規化、書記素境界での切り詰め、東アジア文字幅の計算。
///
/// NFC 正規化は移植性ではなく決定性の要件である。macOS の APFS は分解形に近い
/// ファイル名を返し、Windows の NTFS は合成形を返すため、正規化しなければ同じ
/// リポジトリから異なるノード ID が生成される。docs/platform-and-i18n.md 1.2 を参照。
module Srcnet.Text.Unicode

open System
open System.Globalization
open System.Text

/// U+0000..U+007F だけで構成されるか。ASCII は定義上 NFC であるため、
/// 正規化の判定と実行を丸ごと省ける。大半のソースはこの経路を通る。
let inline isAscii (text: string) = Ascii.IsValid text

/// 正規化できない、対になっていないサロゲートを含むか。
let hasUnpairedSurrogate (text: string) =
  let mutable found = false
  let mutable i = 0

  while not found && i < text.Length do
    let c = text[i]

    if Char.IsHighSurrogate c then
      if i + 1 >= text.Length || not(Char.IsLowSurrogate text[i + 1]) then
        found <- true
      else
        i <- i + 1
    elif Char.IsLowSurrogate c then
      found <- true

    i <- i + 1

  found

/// NFC へ正規化する。ASCII と既に NFC の文字列は入力をそのまま返し、割り当てを避ける。
let normalize (text: string) =
  if text.Length = 0 || Ascii.IsValid text then text
  elif text.IsNormalized NormalizationForm.FormC then text
  else text.Normalize NormalizationForm.FormC

/// 大文字小文字を無視する比較のための畳み込み。
/// 文化圏依存の変換はトルコ語の `i` 問題を招くため既定にしない。
/// docs/platform-and-i18n.md 2.3 を参照。
let caseFold (text: string) : string = text.ToLowerInvariant()

/// 東アジア文字幅が Wide (W) または Fullwidth (F) のスカラー値の範囲。
/// 開始値の昇順で、範囲どうしは重ならない。二分探索の前提条件。
let private wideRanges: struct (int * int)[] =
  [| struct (0x1100, 0x115F)
     struct (0x231A, 0x231B)
     struct (0x2329, 0x232A)
     struct (0x23E9, 0x23EC)
     struct (0x23F0, 0x23F0)
     struct (0x23F3, 0x23F3)
     struct (0x25FD, 0x25FE)
     struct (0x2614, 0x2615)
     struct (0x2630, 0x2637)
     struct (0x2648, 0x2653)
     struct (0x267F, 0x267F)
     struct (0x268A, 0x268F)
     struct (0x2693, 0x2693)
     struct (0x26A1, 0x26A1)
     struct (0x26AA, 0x26AB)
     struct (0x26BD, 0x26BE)
     struct (0x26C4, 0x26C5)
     struct (0x26CE, 0x26CE)
     struct (0x26D4, 0x26D4)
     struct (0x26EA, 0x26EA)
     struct (0x26F2, 0x26F3)
     struct (0x26F5, 0x26F5)
     struct (0x26FA, 0x26FA)
     struct (0x26FD, 0x26FD)
     struct (0x2705, 0x2705)
     struct (0x270A, 0x270B)
     struct (0x2728, 0x2728)
     struct (0x274C, 0x274C)
     struct (0x274E, 0x274E)
     struct (0x2753, 0x2755)
     struct (0x2757, 0x2757)
     struct (0x2795, 0x2797)
     struct (0x27B0, 0x27B0)
     struct (0x27BF, 0x27BF)
     struct (0x2B1B, 0x2B1C)
     struct (0x2B50, 0x2B50)
     struct (0x2B55, 0x2B55)
     struct (0x2E80, 0x2E99)
     struct (0x2E9B, 0x2EF3)
     struct (0x2F00, 0x2FD5)
     struct (0x2FF0, 0x2FFB)
     struct (0x3000, 0x303E)
     struct (0x3041, 0x3096)
     struct (0x3099, 0x30FF)
     struct (0x3105, 0x312F)
     struct (0x3131, 0x318E)
     struct (0x3190, 0x31E3)
     struct (0x31F0, 0x321E)
     struct (0x3220, 0x3247)
     struct (0x3250, 0x4DBF)
     struct (0x4E00, 0xA48C)
     struct (0xA490, 0xA4C6)
     struct (0xA960, 0xA97C)
     struct (0xAC00, 0xD7A3)
     struct (0xF900, 0xFAFF)
     struct (0xFE10, 0xFE19)
     struct (0xFE30, 0xFE52)
     struct (0xFE54, 0xFE66)
     struct (0xFE68, 0xFE6B)
     struct (0xFF01, 0xFF60)
     struct (0xFFE0, 0xFFE6)
     struct (0x16FE0, 0x16FE4)
     struct (0x16FF0, 0x16FF1)
     struct (0x17000, 0x187F7)
     struct (0x18800, 0x18CD5)
     struct (0x18D00, 0x18D08)
     struct (0x1AFF0, 0x1AFF3)
     struct (0x1AFF5, 0x1AFFB)
     struct (0x1AFFD, 0x1AFFE)
     struct (0x1B000, 0x1B122)
     struct (0x1B132, 0x1B132)
     struct (0x1B150, 0x1B152)
     struct (0x1B155, 0x1B155)
     struct (0x1B164, 0x1B167)
     struct (0x1B170, 0x1B2FB)
     struct (0x1F004, 0x1F004)
     struct (0x1F0CF, 0x1F0CF)
     struct (0x1F18E, 0x1F18E)
     struct (0x1F191, 0x1F19A)
     struct (0x1F200, 0x1F202)
     struct (0x1F210, 0x1F23B)
     struct (0x1F240, 0x1F248)
     struct (0x1F250, 0x1F251)
     struct (0x1F260, 0x1F265)
     struct (0x1F300, 0x1F320)
     struct (0x1F32D, 0x1F335)
     struct (0x1F337, 0x1F37C)
     struct (0x1F37E, 0x1F393)
     struct (0x1F3A0, 0x1F3CA)
     struct (0x1F3CF, 0x1F3D3)
     struct (0x1F3E0, 0x1F3F0)
     struct (0x1F3F4, 0x1F3F4)
     struct (0x1F3F8, 0x1F43E)
     struct (0x1F440, 0x1F440)
     struct (0x1F442, 0x1F4FC)
     struct (0x1F4FF, 0x1F53D)
     struct (0x1F54B, 0x1F54E)
     struct (0x1F550, 0x1F567)
     struct (0x1F57A, 0x1F57A)
     struct (0x1F595, 0x1F596)
     struct (0x1F5A4, 0x1F5A4)
     struct (0x1F5FB, 0x1F64F)
     struct (0x1F680, 0x1F6C5)
     struct (0x1F6CC, 0x1F6CC)
     struct (0x1F6D0, 0x1F6D2)
     struct (0x1F6D5, 0x1F6D7)
     struct (0x1F6DC, 0x1F6DF)
     struct (0x1F6EB, 0x1F6EC)
     struct (0x1F6F4, 0x1F6FC)
     struct (0x1F7E0, 0x1F7EB)
     struct (0x1F7F0, 0x1F7F0)
     struct (0x1F90C, 0x1F93A)
     struct (0x1F93C, 0x1F945)
     struct (0x1F947, 0x1F9FF)
     struct (0x1FA70, 0x1FA7C)
     struct (0x1FA80, 0x1FA89)
     struct (0x1FA8F, 0x1FAC6)
     struct (0x1FACE, 0x1FADC)
     struct (0x1FADF, 0x1FAE9)
     struct (0x1FAF0, 0x1FAF8)
     struct (0x20000, 0x2FFFD)
     struct (0x30000, 0x3FFFD) |]

let private isWideScalar (scalar: int) =
  // 範囲表は開始値昇順で不重複。この不変条件が崩れると二分探索が誤る。
  let mutable lo = 0
  let mutable hi = wideRanges.Length - 1
  let mutable found = false

  while lo <= hi do
    let mid = lo + ((hi - lo) >>> 1)
    let struct (s, e) = wideRanges[mid]

    if scalar < s then
      hi <- mid - 1
    elif scalar > e then
      lo <- mid + 1
    else
      found <- true
      lo <- hi + 1

  found

/// 端末上での 1 スカラー値の表示桁数。結合文字と書式文字は 0 桁とする。
let scalarWidth (rune: Rune) =
  let scalar = rune.Value

  if scalar = 0 then
    0
  elif scalar < 0x20 || (scalar >= 0x7F && scalar < 0xA0) then
    0
  else
    match Rune.GetUnicodeCategory rune with
    | UnicodeCategory.NonSpacingMark
    | UnicodeCategory.EnclosingMark
    | UnicodeCategory.Format -> 0
    | _ -> if isWideScalar scalar then 2 else 1

/// 端末上での表示桁数。`String.Length` は UTF-16 コード単位数であり桁揃えに使えない。
let displayWidth (text: string) =
  let mutable width = 0

  for rune in text.EnumerateRunes() do
    width <- width + scalarWidth rune

  width

/// 書記素クラスタの個数。結合文字・絵文字連結・異体字セレクタを 1 個として数える。
let graphemeCount (text: string) =
  let mutable count = 0
  let mutable index = 0

  while index < text.Length do
    index <- index + StringInfo.GetNextTextElementLength(text.AsSpan index)
    count <- count + 1

  count

/// 表示桁数が `maxWidth` を超えないよう、書記素クラスタ境界で切り詰める。
/// UTF-16 コード単位や UTF-8 バイトで切ると、CJK と絵文字で不正な境界が生じる。
let truncateToWidth (maxWidth: int) (text: string) =
  if maxWidth <= 0 then
    ""
  elif text.Length = 0 then
    text
  else
    let mutable width = 0
    let mutable index = 0
    let mutable cut = -1

    while cut < 0 && index < text.Length do
      let length = StringInfo.GetNextTextElementLength(text.AsSpan index)
      let mutable clusterWidth = 0

      for rune in text.AsSpan(index, length).EnumerateRunes() do
        clusterWidth <- clusterWidth + scalarWidth rune

      if width + clusterWidth > maxWidth then
        cut <- index
      else
        width <- width + clusterWidth
        index <- index + length

    if cut < 0 then text else text.Substring(0, cut)
