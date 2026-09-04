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
  { /// BLAKE3 の 32 バイト ダイジェスト。
    Hash: byte[]
    Encoding: Encodings.DetectedEncoding
    /// 改行種別によらない論理的な行数。判定できない場合は 0。
    LineCount: int
    Flags: NodeFlags }

let private isLineFeed = 0x0Auy
let private isCarriageReturn = 0x0Duy
let private crlf = [| 0x0Duy; 0x0Auy |]

/// ワーカーごとに 1 つ持つ読み取り器。バッファとハッシュ器を再利用し、
/// ファイルごとの割り当てをダイジェストの 32 バイトだけに抑える。
[<Sealed>]
type ContentReader(maxFileSizeBytes: int64) =
  let hasher = Blake3.Hasher()
  let buffer = ArrayPool<byte>.Shared.Rent ReadBufferBytes
  let prefix = Array.zeroCreate<byte> Encodings.DetectionPrefixBytes
  let mutable disposed = false

  member _.MaxFileSizeBytes = maxFileSizeBytes

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
        { Hash = Blake3.hash ReadOnlySpan.Empty
          Encoding = Encodings.Utf8
          LineCount = 0
          Flags = NodeFlags.None }
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

      let digest = Array.zeroCreate<byte> Blake3.HashLength
      hasher.Finish(Span digest)

      Ok
        { Hash = digest
          Encoding = detection.Encoding
          LineCount = lineCount
          Flags = flags }
    with
    | :? UnauthorizedAccessException -> Error AccessDenied
    | :? FileNotFoundException -> Error NotFound
    | :? DirectoryNotFoundException -> Error NotFound
    | :? IOException as ex -> Error(IoFailure ex.Message)

  interface IDisposable with

    member _.Dispose() =
      if not disposed then
        disposed <- true
        ArrayPool<byte>.Shared.Return buffer
