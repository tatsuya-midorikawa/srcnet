/// レガシー符号化の復号器。
///
/// 対象は Shift_JIS / EUC-JP / ISO-2022-JP / GB18030 / Big5 / EUC-KR
/// （[設計判断](../../docs/decisions.md) ADR-9）。製品コードへ NuGet を追加しない原則を守るため、
/// `System.Text.Encoding.CodePages` は使わず、変換表を同梱して自前で復号する。
///
/// 変換表は `tools/generate_encoding_tables.py` が生成し、Deflate で圧縮した埋め込み資源として
/// 持つ。実行時に外部ファイルを読む経路は作らない（docs/security.md C-1）。表の展開は
/// 実際にその符号化のファイルへ出会ったときだけ行うため、UTF-8 だけのリポジトリでは
/// 追加のメモリを使わない。
module Srcnet.Text.LegacyEncodings

open System
open System.IO
open System.IO.Compression
open System.Reflection

/// 復号できないバイト列を置き換える文字。走査を止めず、欠落を明示する。
[<Literal>]
let ReplacementChar = 0xFFFD

/// 2 バイト表の要素数。先頭バイト × 後続バイトで引く。
[<Literal>]
let private TableEntries = 256 * 256

let private assembly = typeof<Encodings.DetectedEncoding>.Assembly

/// 埋め込み資源を展開する。資源が無い構成では空を返し、復号を「未対応」に落とす。
let private inflate (name: string) =
  match assembly.GetManifestResourceStream $"Srcnet.Text.Tables.{name}" with
  | null -> Array.empty<byte>
  | stream ->
    use stream = stream
    use inflater = new ZLibStream(stream, CompressionMode.Decompress)
    use buffer = new MemoryStream()
    inflater.CopyTo buffer
    buffer.ToArray()

/// 2 バイト表を `uint16` の配列として読む。
let private loadTable (name: string) =
  lazy
    (let raw = inflate name

     if raw.Length <> TableEntries * 2 then Array.empty<uint16>
     else
       let table = Array.zeroCreate<uint16> TableEntries
       Buffer.BlockCopy(raw, 0, table, 0, raw.Length)
       table)

/// GB18030 の 4 バイト列から基本多言語面への写像。連続する区間の列で持つ。
[<Struct>]
type private LinearRun =
  { Linear: uint32
    Scalar: uint32
    Length: uint32 }

let private gb18030Runs =
  lazy
    (let raw = inflate "gb18030_4byte.bin"

     if raw.Length < 4 then Array.empty<LinearRun>
     else
       let count = int (Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan raw))

       if raw.Length <> 4 + count * 12 then Array.empty<LinearRun>
       else
         Array.init count (fun index ->
           let span = ReadOnlySpan(raw, 4 + index * 12, 12)

           { Linear = Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian span
             Scalar = Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4))
             Length = Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4)) }))

let private shiftJisTable = loadTable "shift_jis.bin"
let private eucJpTable = loadTable "euc_jp.bin"
let private gb18030Table = loadTable "gb18030.bin"
let private big5Table = loadTable "big5.bin"
let private eucKrTable = loadTable "euc_kr.bin"

let private tableFor (encoding: Encodings.DetectedEncoding) =
  match encoding with
  | Encodings.ShiftJis -> shiftJisTable.Value
  | Encodings.EucJp
  // ISO-2022-JP は JIS X 0208 を使う。EUC-JP の表は同じ集合を上位ビット付きで持つ。
  | Encodings.Iso2022Jp -> eucJpTable.Value
  | Encodings.Gb18030 -> gb18030Table.Value
  | Encodings.Big5 -> big5Table.Value
  | Encodings.EucKr -> eucKrTable.Value
  | _ -> Array.empty

/// この符号化を復号できるか。変換表を同梱していない構成では false になる。
let isSupported (encoding: Encodings.DetectedEncoding) =
  match encoding with
  | Encodings.ShiftJis
  | Encodings.EucJp
  | Encodings.Iso2022Jp
  | Encodings.Gb18030
  | Encodings.Big5
  | Encodings.EucKr -> (tableFor encoding).Length = TableEntries
  | _ -> false

/// 復号後に必要な UTF-8 のバイト数の上限。
///
/// 基本多言語面のスカラー値は UTF-8 で最大 3 バイトになる。半角カタカナのように
/// 1 バイトが 3 バイトへ広がる場合が最悪なので、入力長の 3 倍を上限とする。
let maxUtf8Bytes (sourceLength: int) = sourceLength * 3 + 3

/// スカラー値を UTF-8 として書き出す。書けなければ 0 を返す。
let inline private writeScalar (destination: Span<byte>) (position: int) (scalar: int) =
  if scalar < 0x80 then
    if position + 1 > destination.Length then 0
    else
      destination[position] <- byte scalar
      1
  elif scalar < 0x800 then
    if position + 2 > destination.Length then 0
    else
      destination[position] <- byte (0xC0 ||| (scalar >>> 6))
      destination[position + 1] <- byte (0x80 ||| (scalar &&& 0x3F))
      2
  else if position + 3 > destination.Length then 0
  else
    destination[position] <- byte (0xE0 ||| (scalar >>> 12))
    destination[position + 1] <- byte (0x80 ||| ((scalar >>> 6) &&& 0x3F))
    destination[position + 2] <- byte (0x80 ||| (scalar &&& 0x3F))
    3

/// GB18030 の 4 バイト列を復号する。
let private gb18030FourByte (b1: byte) (b2: byte) (b3: byte) (b4: byte) =
  let linear =
    (int b1 - 0x81) * 12600 + (int b2 - 0x30) * 1260 + (int b3 - 0x81) * 10 + (int b4 - 0x30)

  // 上位の線形位置は補助面へ線形に写る。表を持たずに計算だけで決まる。
  if linear >= 189_000 then
    let scalar = 0x10000 + (linear - 189_000)
    if scalar <= 0x10FFFF then scalar else ReplacementChar
  else
    let runs = gb18030Runs.Value
    let mutable low = 0
    let mutable high = runs.Length - 1
    let mutable found = ReplacementChar

    while low <= high do
      let middle = low + (high - low) / 2
      let run = runs[middle]

      if uint32 linear < run.Linear then high <- middle - 1
      elif uint32 linear >= run.Linear + run.Length then low <- middle + 1
      else
        found <- int (run.Scalar + (uint32 linear - run.Linear))
        low <- high + 1

    found

/// 補助面のスカラー値を UTF-8 として書き出す。
let inline private writeSupplementary (destination: Span<byte>) (position: int) (scalar: int) =
  if position + 4 > destination.Length then 0
  else
    destination[position] <- byte (0xF0 ||| (scalar >>> 18))
    destination[position + 1] <- byte (0x80 ||| ((scalar >>> 12) &&& 0x3F))
    destination[position + 2] <- byte (0x80 ||| ((scalar >>> 6) &&& 0x3F))
    destination[position + 3] <- byte (0x80 ||| (scalar &&& 0x3F))
    4

/// ISO-2022-JP の指示子。エスケープ列で切り替わる。
[<Struct>]
type private JisMode =
  | Ascii
  | JisX0208
  /// JIS X 0201 のローマ字集合。ASCII とほぼ同じで、`\` と `~` だけが違う。
  | JisRoman

/// `source` を UTF-8 へ復号し、`destination` へ書き出す。書き出したバイト数を返す。
///
/// 変換表を持たない構成では -1 を返す。不正なバイト列は置換文字にして継続し、
/// 復号の失敗で走査を止めない。
///
/// 1 反復で「スカラー値 1 つと消費バイト数」を決め、書き出しを 1 か所へ集約する。
/// span は closure へ渡せないため、書き出しを内部関数に切り出せないという制約もある。
let decode (encoding: Encodings.DetectedEncoding) (source: ReadOnlySpan<byte>) (destination: Span<byte>) =
  let table = tableFor encoding

  if table.Length <> TableEntries then -1
  else

  let mutable position = 0
  let mutable index = 0
  let mutable mode = Ascii

  while index < source.Length do
    let b = source[index]
    // -1 は「文字を生まない」（エスケープ列）を表す。
    let mutable scalar = ReplacementChar
    let mutable consumed = 1

    match encoding with
    | Encodings.Iso2022Jp ->
      if b = 0x1Buy && index + 2 < source.Length then
        let second = source[index + 1]
        let third = source[index + 2]

        if second = byte '$' && (third = byte 'B' || third = byte '@') then
          mode <- JisX0208
          scalar <- -1
          consumed <- 3
        elif second = byte '(' && third = byte 'B' then
          mode <- Ascii
          scalar <- -1
          consumed <- 3
        elif second = byte '(' && (third = byte 'J' || third = byte 'I') then
          mode <- JisRoman
          scalar <- -1
          consumed <- 3
      else
        match mode with
        | JisX0208 ->
          if index + 1 < source.Length && b >= 0x21uy && b <= 0x7Euy then
            // JIS X 0208 の区点は EUC-JP では上位ビットを立てた位置に入る。
            let mapped = int table[(int b ||| 0x80) * 256 + (int source[index + 1] ||| 0x80)]
            scalar <- (if mapped = 0 then ReplacementChar else mapped)
            consumed <- 2
        | Ascii
        | JisRoman -> if b < 0x80uy then scalar <- int b

    | Encodings.ShiftJis ->
      if b < 0x80uy then scalar <- int b
      elif b >= 0xA1uy && b <= 0xDFuy then
        // 半角カタカナ。表を引かずに位置で決まる。
        scalar <- 0xFF61 + int b - 0xA1
      elif index + 1 < source.Length then
        let mapped = int table[int b * 256 + int source[index + 1]]
        scalar <- (if mapped = 0 then ReplacementChar else mapped)
        consumed <- 2

    | Encodings.Gb18030 ->
      if b < 0x80uy then scalar <- int b
      elif index + 1 < source.Length then
        let second = source[index + 1]

        if second >= 0x30uy && second <= 0x39uy then
          if index + 3 < source.Length then
            scalar <- gb18030FourByte b second source[index + 2] source[index + 3]
            consumed <- 4
        else
          let mapped = int table[int b * 256 + int second]
          scalar <- (if mapped = 0 then ReplacementChar else mapped)
          consumed <- 2

    | _ ->
      // EUC-JP / Big5 / EUC-KR。いずれも ASCII 透過の 2 バイト符号化である。
      if b < 0x80uy then scalar <- int b
      elif index + 1 < source.Length then
        let mapped = int table[int b * 256 + int source[index + 1]]

        if mapped = 0 then
          // EUC-JP の 3 バイト列（`0x8F` 始まり、JIS X 0212）は表に持たない。
          // 補助漢字はソース コードでほぼ使われないため、置換して継続する。
          consumed <- (if b = 0x8Fuy then 3 else 2)
        else
          scalar <- mapped
          consumed <- 2

    if scalar < 0 then index <- index + consumed
    else
      let written =
        if scalar > 0xFFFF then writeSupplementary destination position scalar
        else writeScalar destination position scalar

      if written = 0 then
        // 書き出し先が尽きた。切り捨てを黙って続けず、そこで終える。
        index <- source.Length
      else
        position <- position + written
        index <- index + consumed

  position
