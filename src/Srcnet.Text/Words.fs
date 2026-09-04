/// 識別子の語分割と n-gram 生成。
///
/// 語分割はコミュニティ命名（docs/graph-model.md 6.2）に、n-gram は部分一致索引
/// （docs/storage.md 7.2）に使う。いずれも Unicode スカラー値の列に対して構成する。
/// UTF-16 コード単位や UTF-8 バイトで切ると CJK と絵文字で不正な境界が生じる。
module Srcnet.Text.Words

open System
open System.Collections.Generic
open System.Globalization
open System.Text

/// ラテン文字・数字・記号に用いる n-gram の n。トライグラムの選択性で足りる。
[<Literal>]
let LatinGram = 3

/// CJK に用いる n-gram の n。CJK は 2 文字で語を成すことが多く、3 では再現率が落ちる。
[<Literal>]
let CjkGram = 2

/// 表意文字・仮名・ハングルなど、1 文字が語を成し得る文字か。
let isCjkScalar (scalar: int) =
  (scalar >= 0x2E80 && scalar <= 0x2EFF) // CJK 部首補助
  || (scalar >= 0x3040 && scalar <= 0x30FF) // 仮名
  || (scalar >= 0x3100 && scalar <= 0x312F) // 注音
  || (scalar >= 0x3130 && scalar <= 0x318F) // ハングル字母
  || (scalar >= 0x3400 && scalar <= 0x4DBF) // CJK 拡張 A
  || (scalar >= 0x4E00 && scalar <= 0x9FFF) // CJK 統合漢字
  || (scalar >= 0xA960 && scalar <= 0xA97F) // ハングル字母拡張 A
  || (scalar >= 0xAC00 && scalar <= 0xD7AF) // ハングル音節
  || (scalar >= 0xF900 && scalar <= 0xFAFF) // CJK 互換漢字
  || (scalar >= 0x20000 && scalar <= 0x3FFFD) // CJK 拡張 B 以降

let private isWordScalar (scalar: int) =
  if scalar < 128 then
    (scalar >= int 'a' && scalar <= int 'z')
    || (scalar >= int 'A' && scalar <= int 'Z')
    || (scalar >= int '0' && scalar <= int '9')
  else
    match CharUnicodeInfo.GetUnicodeCategory scalar with
    | UnicodeCategory.UppercaseLetter
    | UnicodeCategory.LowercaseLetter
    | UnicodeCategory.TitlecaseLetter
    | UnicodeCategory.ModifierLetter
    | UnicodeCategory.OtherLetter
    | UnicodeCategory.DecimalDigitNumber
    | UnicodeCategory.LetterNumber
    | UnicodeCategory.OtherNumber -> true
    | _ -> false

let private isUpper (scalar: int) =
  scalar < 0x10000 && Char.IsUpper(char scalar)

let private isLower (scalar: int) =
  scalar < 0x10000 && Char.IsLower(char scalar)

let private isDigit (scalar: int) =
  scalar >= int '0' && scalar <= int '9'

/// 識別子を語へ分割する。
///
/// - `snake_case` / `kebab-case` / `dot.case` は区切り文字で分ける
/// - `camelCase` / `PascalCase` は小文字→大文字の遷移で分ける
/// - `HTTPServer` のような連続大文字は、最後の大文字を次語の先頭とする
/// - 数字と文字の境界で分ける
/// - CJK の連続は 1 語としてまとめる（さらに細かい分割は n-gram が担う）
///
/// 戻り値はケース フォールド済みで、出現順を保つ。
let split (identifier: string) : string[] =
  if identifier.Length = 0 then Array.empty
  else

  let scalars = List<int>(identifier.Length)

  for rune in identifier.EnumerateRunes() do
    scalars.Add rune.Value

  let words = List<string>()
  let builder = StringBuilder(16)

  let flush () =
    if builder.Length > 0 then
      words.Add(builder.ToString().ToLowerInvariant())
      builder.Clear() |> ignore

  let appendScalar (scalar: int) =
    builder.Append(Rune(scalar).ToString()) |> ignore

  let mutable i = 0

  while i < scalars.Count do
    let current = scalars[i]

    if not (isWordScalar current) then
      flush ()
      i <- i + 1
    else
      let previous = if i > 0 then ValueSome scalars[i - 1] else ValueNone
      let next = if i + 1 < scalars.Count then ValueSome scalars[i + 1] else ValueNone

      let boundary =
        match previous with
        | ValueNone -> false
        | ValueSome p ->
          if not (isWordScalar p) then false
          elif isCjkScalar p <> isCjkScalar current then true
          elif isDigit p <> isDigit current then true
          elif isLower p && isUpper current then true
          elif isUpper p && isUpper current then
            match next with
            | ValueSome n -> isLower n
            | ValueNone -> false
          else false

      if boundary then flush ()

      appendScalar current
      i <- i + 1

  flush ()
  words.ToArray()

/// 部分一致索引用の n-gram を、文字種に応じた n で生成する。
/// 重複を除いた昇順の配列を返すため、結果は決定的である。
let ngrams (text: string) : string[] =
  if text.Length = 0 then Array.empty
  else

  let folded = text.ToLowerInvariant()
  let scalars = List<int>(folded.Length)

  for rune in folded.EnumerateRunes() do
    scalars.Add rune.Value

  let unique = SortedSet<string>(StringComparer.Ordinal)
  let builder = StringBuilder(8)

  for start in 0 .. scalars.Count - 1 do
    let n = if isCjkScalar scalars[start] then CjkGram else LatinGram

    if start + n <= scalars.Count then
      builder.Clear() |> ignore

      for offset in 0 .. n - 1 do
        builder.Append(Rune(scalars[start + offset]).ToString()) |> ignore

      unique.Add(builder.ToString()) |> ignore

  // n 未満の短い入力からも索引できるよう、全体を 1 つの gram として加える。
  if scalars.Count < LatinGram then unique.Add folded |> ignore

  let result = Array.zeroCreate unique.Count
  unique.CopyTo result
  result
