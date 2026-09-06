/// ファイル内容の読み取り、内容ハッシュ、符号化判定、行数計数。
///
/// 対象リポジトリのファイルは外部が更新し得るため memory mapping を使わない。
/// 切り詰めによる致命的シグナルは .NET で捕捉できないためである。docs/storage.md 8 を参照。
module Srcnet.Discovery.Content

open System
open System.Buffers
open System.IO
open System.Threading
open Srcnet.Core
open Srcnet.Core.Graph
open Srcnet.Text

/// 逐次読み取りのバッファ長。全体をメモリに載せないことでメモリ使用量を入力サイズから切り離す。
[<Literal>]
let ReadBufferBytes = 65536

type ReadError =
  | TooLarge of sizeBytes: int64
  | AccessDenied
  | NotFound
  | IoFailure of message: string

module ReadError =

  let describe error =
    match error with
    | TooLarge size -> $"ファイルが処理上限を超えています ({size} バイト)"
    | AccessDenied -> "ファイルを読み取る権限がありません"
    | NotFound -> "走査後にファイルが消えました"
    | IoFailure message -> message

type ContentSummary =
  { /// 内容ハッシュ (SHA-256) の 32 バイト ダイジェスト。
    Hash: byte[]
    Encoding: Encodings.DetectedEncoding
    /// 構造規則を満たした符号化候補。単一に確定した場合は空。
    /// 検出器が持っていた不確実性を discovery より下流でも失わないために保持する。
    EncodingCandidates: Encodings.DetectedEncoding[]
    /// 改行種別によらない論理的な行数。判定できない場合は 0。
    LineCount: int
    Flags: NodeFlags
    /// 抽出へ渡せる復号済み UTF-8 の長さ。渡せない場合は 0。
    /// 実体は `ContentReader.Decoded` にあり、次の `Read` まで有効である。
    DecodedLength: int
    /// 抽出へ渡せる本文を保持しているか。復号できない符号化では false。
    Decoded: bool }

let private isLineFeed = 0x0Auy
let private isCarriageReturn = 0x0Duy
let private crlf = [| 0x0Duy; 0x0Auy |]

/// ワーカーごとに 1 つ持つ読み取り器。バッファとハッシュ器を再利用し、
/// ファイルごとの割り当てをダイジェストの 32 バイトだけに抑える。
[<Sealed>]
type ContentReader(maxFileSizeBytes: int64, maxRetainedBytes: int64) =
  let hasher = new Hashing.Hasher()
  let buffer = ArrayPool<byte>.Shared.Rent ReadBufferBytes
  let prefix = Array.zeroCreate<byte> Encodings.DetectionPrefixBytes
  // 抽出へ渡す本文。走査の 1 回読みで貯め、ファイルを二度開かないためにある
  // （backlog 014）。ワーカーごとに 1 つで、次の `Read` まで有効。
  let mutable retained: byte[] = Array.Empty()
  // 変換が要る符号化のための書き出し先。UTF-8 のファイルでは使わない。
  let mutable converted: byte[] = Array.Empty()
  let mutable decodedIsConverted = false
  let mutable disposed = false

  /// 本文を保持しない読み取り器。段階 0（走査のみ）で使う。
  new(maxFileSizeBytes: int64) = new ContentReader(maxFileSizeBytes, 0L)

  member _.MaxFileSizeBytes = maxFileSizeBytes

  member _.MaxRetainedBytes = maxRetainedBytes

  /// 復号済み UTF-8 の本文。有効な長さは直前の `Read` が返した `DecodedLength`。
  /// 次の `Read` で上書きされるため、結果へ参照を残してはならない。
  member _.Decoded = if decodedIsConverted then converted else retained

  /// ファイルを読み、内容ハッシュ・符号化・行数を返す。
  ///
  /// `knownLength` は走査時に得たサイズ。0 のファイルは**開かない**。
  /// FIFO、ソケット、キャラクタ デバイスは `stat` 上のサイズが 0 で、通常ファイルと
  /// managed API では区別できない。これらを開くと `open` 自体が無期限に blocking し、
  /// 取り消しも効かないため走査が停止する。空ファイルの結果は開かずに決定できるので、
  /// サイズ 0 を一律に「開かない」ことで、特殊ファイルによる停止を構造的に排除する。
  member _.Read(physicalPath: string, knownLength: int64, cancellation: CancellationToken) : Result<ContentSummary, ReadError> =
    ObjectDisposedException.ThrowIf(disposed, typeof<ContentReader>)

    if knownLength > maxFileSizeBytes then Error(TooLarge knownLength)
    elif knownLength = 0L then
      Ok
        { Hash = Hashing.hash ReadOnlySpan.Empty
          Encoding = Encodings.Utf8
          EncodingCandidates = Array.empty
          LineCount = 0
          Flags = NodeFlags.None
          DecodedLength = 0
          Decoded = true }
    else

    let openOptions =
      FileStreamOptions(
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        // 走査中に他プロセスが編集・削除し得るため、共有を最大限許可する。
        Share = (FileShare.ReadWrite ||| FileShare.Delete),
        BufferSize = 0,
        Options = FileOptions.SequentialScan
      )

    try
      use stream = new FileStream(physicalPath, openOptions)
      let length = stream.Length

      if length > maxFileSizeBytes then Error(TooLarge length)
      else

      hasher.Reset()

      // 抽出へ渡す本文を保持するかは、開始前のサイズで決める。判定を読み取り中に
      // 変えると、途中まで貯めたバッファを捨てることになる。
      let retaining = maxRetainedBytes > 0L && length <= maxRetainedBytes

      if retaining && int64 retained.Length < length then
        if retained.Length > 0 then ArrayPool<byte>.Shared.Return retained
        retained <- ArrayPool<byte>.Shared.Rent(int length)

      let mutable retainedLength = 0
      let mutable prefixLength = 0
      let mutable total = 0L
      let mutable lineFeeds = 0
      let mutable carriageReturns = 0
      let mutable pairs = 0
      let mutable pendingCarriageReturn = false
      let mutable lastByte = 0uy
      let mutable reading = true
      let mutable overflowed = false

      while reading do
        cancellation.ThrowIfCancellationRequested()
        let read = stream.Read(Span(buffer, 0, ReadBufferBytes))

        if read = 0 then reading <- false
        else
          let span = ReadOnlySpan(buffer, 0, read)
          hasher.Update span
          total <- total + int64 read

          // 読み取り中に伸びるファイルや、事前のサイズ報告が当てにならない対象でも
          // 処理量を有界に保つ。上限の判断を開始前のサイズだけに委ねない。
          if total > maxFileSizeBytes then
            overflowed <- true
            reading <- false
          else

          // 保持は上限内でのみ行う。読み取り中に伸びたファイルではバッファを超えるため、
          // 超えた時点で保持をあきらめ、ハッシュと行数の計数だけを続ける。
          if retaining && retainedLength >= 0 then
            if retainedLength + read <= retained.Length then
              span.CopyTo(Span(retained, retainedLength, read))
              retainedLength <- retainedLength + read
            else retainedLength <- -1

          if prefixLength < prefix.Length then
            let take = min (prefix.Length - prefixLength) read
            span.Slice(0, take).CopyTo(Span(prefix, prefixLength, take))
            prefixLength <- prefixLength + take

          // 改行の計数はいずれも SIMD 化された走査に委ねる。
          lineFeeds <- lineFeeds + MemoryExtensions.Count(span, isLineFeed)
          carriageReturns <- carriageReturns + MemoryExtensions.Count(span, isCarriageReturn)
          pairs <- pairs + MemoryExtensions.Count(span, ReadOnlySpan crlf)

          // バッファ境界をまたぐ CRLF は、直前の CR を単独の改行として数えないよう補正する。
          if pendingCarriageReturn && span[0] = isLineFeed then pairs <- pairs + 1

          lastByte <- span[read - 1]
          pendingCarriageReturn <- lastByte = isCarriageReturn

      if overflowed then Error(TooLarge total)
      else

      let detection = Encodings.detect (ReadOnlySpan(prefix, 0, prefixLength)) total

      let mutable flags = NodeFlags.None

      if detection.Ambiguous then flags <- flags ||| NodeFlags.AmbiguousEncoding

      let lineCount =
        match detection.Encoding with
        | Encodings.Binary ->
          flags <- flags ||| NodeFlags.Binary
          0
        | Encodings.Utf16Le
        | Encodings.Utf16Be ->
          // UTF-16 はバイト単位の改行計数が成立しない。抽出段で復号したうえで数える。
          0
        | Encodings.Undetermined ->
          flags <- flags ||| NodeFlags.UndeterminedEncoding
          0
        | Encodings.Utf8
        | Encodings.Utf8WithBom
        | Encodings.ShiftJis
        | Encodings.EucJp
        | Encodings.Iso2022Jp
        | Encodings.Gb18030
        | Encodings.Big5
        | Encodings.EucKr ->
          let terminators = lineFeeds + (carriageReturns - pairs)
          let endsWithTerminator = lastByte = isLineFeed || lastByte = isCarriageReturn

          if total = 0L then 0
          elif endsWithTerminator then terminators
          else terminators + 1

      let digest = Array.zeroCreate<byte> Hashing.HashLength
      hasher.Finish(Span digest)

      // --- 抽出へ渡す本文を用意する ---
      // 復号は抽出の直前に行い、抽出器へは UTF-8 だけを渡す（docs/storage.md 5）。
      // 原文はファイル側に残るため、ここでの変換は成果物の内容を変えない。
      let mutable decodedLength = 0
      let mutable decodable = false
      let mutable decodedLineCount = lineCount
      decodedIsConverted <- false

      if retaining && retainedLength >= 0 && Decoding.isSupported detection.Encoding then
        let source = ReadOnlySpan(retained, 0, retainedLength)

        if Decoding.isUtf8 detection.Encoding then
          // BOM を持つ場合だけ、抽出器へ渡す前に取り除く。複製は避ける。
          let offset = Decoding.bomLengthOf detection.Encoding source

          if offset > 0 then Array.blit retained offset retained 0 (retainedLength - offset)

          decodedLength <- retainedLength - offset
          decodable <- true
        else
          let required = Decoding.maxUtf8Bytes retainedLength

          if converted.Length < required then
            if converted.Length > 0 then ArrayPool<byte>.Shared.Return converted
            converted <- ArrayPool<byte>.Shared.Rent required

          match Decoding.toUtf8 detection.Encoding source (Span converted) with
          | Decoding.Converted written ->
            decodedIsConverted <- true
            decodedLength <- written
            decodable <- true
            // バイト単位で数えられない符号化は、復号してから行数を確定させる。
            decodedLineCount <- Decoding.countLines (ReadOnlySpan(converted, 0, written))
          | Decoding.AlreadyUtf8 _
          | Decoding.Unsupported
          | Decoding.NotText -> ()

      Ok
        { Hash = digest
          Encoding = detection.Encoding
          EncodingCandidates = detection.Candidates
          LineCount = decodedLineCount
          Flags = flags
          DecodedLength = decodedLength
          Decoded = decodable }
    with
    | :? UnauthorizedAccessException -> Error AccessDenied
    | :? FileNotFoundException -> Error NotFound
    | :? DirectoryNotFoundException -> Error NotFound
    | :? IOException as ex -> Error(IoFailure ex.Message)

  interface IDisposable with

    member _.Dispose() =
      if not disposed then
        disposed <- true
        (hasher :> IDisposable).Dispose()
        ArrayPool<byte>.Shared.Return buffer
        if converted.Length > 0 then ArrayPool<byte>.Shared.Return converted
        if retained.Length > 0 then ArrayPool<byte>.Shared.Return retained
        converted <- Array.Empty()
        retained <- Array.Empty()
