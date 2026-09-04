/// 重複排除した文字列 blob の構築。
///
/// 同一のパス片や識別子が大量に重複するため、重複排除の効果が大きい。
/// ノード レコードは固定長を保ち、文字列は blob へのオフセットで参照する。
/// docs/storage.md 5 を参照。
module Srcnet.Storage.Strings

open System
open System.Collections.Generic
open System.Text

/// 文字列表。追加順が索引値を決めるため、呼び出し側は決定的な順序で追加すること。
[<Sealed>]
type StringTable() =
  let indexOf = Dictionary<string, int>(StringComparer.Ordinal)
  let values = List<string>()
  let byteLengths = List<int>()
  let mutable totalBytes = 0L

  do
    // 索引 0 は空文字列に固定する。「名前なし」を特別扱いせずに表現できる。
    indexOf[""] <- 0
    values.Add ""
    byteLengths.Add 0

  member _.Count = values.Count

  member _.TotalBytes = totalBytes

  /// 文字列を登録し、索引を返す。既出の文字列には同じ索引を返す。
  member _.Intern(text: string) =
    match indexOf.TryGetValue text with
    | true, existing -> existing
    | false, _ ->
      let index = values.Count
      let length = Encoding.UTF8.GetByteCount text
      indexOf[text] <- index
      values.Add text
      byteLengths.Add length
      totalBytes <- totalBytes + int64 length
      index

  member _.Item
    with get (index: int) = values[index]

  member _.ByteLength(index: int) = byteLengths[index]

  /// blob 内の各文字列の開始オフセット。長さは `Count + 1` で、末尾は blob 全体の長さ。
  member _.Offsets() =
    let offsets = Array.zeroCreate<uint64> (values.Count + 1)
    let mutable running = 0UL

    for index in 0 .. values.Count - 1 do
      offsets[index] <- running
      running <- running + uint64 byteLengths[index]

    offsets[values.Count] <- running
    offsets

  member _.Values = values :> IReadOnlyList<string>
