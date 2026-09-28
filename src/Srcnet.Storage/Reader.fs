/// 生成物の memory-mapped 読み取り。
///
/// 起動時に全体を読まず、必要なページだけを触る。これが起動時間目標の前提である。
/// mmap するのは srcnet 自身が生成した不変セグメントに限る。外部が更新し得るファイルを
/// mmap すると、切り詰め時の致命的シグナルを .NET では捕捉できない。docs/storage.md 8 を参照。
module Srcnet.Storage.Reader

// NativePtr はマップしたビューの先頭アドレスを取得するためだけに使う。
// 取得したポインタは MappedSegment の生存期間に閉じ、範囲は必ずヘッダーの宣言長で検証する。
#nowarn "9"

open System
open System.Buffers
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.IO.MemoryMappedFiles
open System.Runtime.CompilerServices
open System.Threading
open Microsoft.FSharp.NativeInterop
open Srcnet.Core

/// Hash an immutable segment in bounded chunks, including during generation reuse.
let internal checksum (path: string) (cancellation: CancellationToken) =
  use hasher = new Hashing.Hasher()
  let buffer = ArrayPool<byte>.Shared.Rent 262144

  try
    use stream =
      new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read ||| FileShare.Delete,
        1,
        FileOptions.SequentialScan
      )

    let mutable reading = true

    while reading do
      cancellation.ThrowIfCancellationRequested()
      let read = stream.Read(Span buffer)

      if read = 0 then
        reading <- false
      else
        hasher.Update(ReadOnlySpan(buffer, 0, read))

    let digest = Array.zeroCreate<byte> Hashing.HashLength
    hasher.Finish(Span digest)
    Convert.ToHexStringLower digest
  finally
    ArrayPool<byte>.Shared.Return buffer

type OpenError =
  | SegmentNotFound of path: string
  | SegmentTooLarge of path: string * byteLength: int64
  | InvalidFormat of path: string * error: Format.FormatError
  | OpenFailed of path: string * message: string

module OpenError =

  let describe error =
    match error with
    | SegmentNotFound path -> $"セグメントが見つかりません: {path}"
    | SegmentTooLarge(path, byteLength) -> $"セグメント {path} が {byteLength} バイトで、単一ビューの上限を超えています"
    | InvalidFormat(path, error) -> $"{path}: {Format.FormatError.describe error}"
    | OpenFailed(path, message) -> $"{path}: {message}"

[<Literal>]
let DefaultPartBytes = 134217728

[<Literal>]
let MaxParts = 64

let partPath (path: string) (index: int) =
  if index = 0 then
    path
  else
    path
    + ".part-"
    + index.ToString("D6", Globalization.CultureInfo.InvariantCulture)

[<Sealed>]
type internal MappedPart
  private (file: MemoryMappedFile, view: MemoryMappedViewAccessor, pointer: nativeptr<byte>, length: int, offset: int) =
  let mutable disposed = false
  member _.Length = length - offset

  [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
  member _.Slice(start: int, count: int) : ReadOnlySpan<byte> =
    ObjectDisposedException.ThrowIf(disposed, typeof<MappedPart>)

    if start < 0 || count < 0 || start > length - offset - count then
      invalidArg (nameof start) "Part range is invalid"

    ReadOnlySpan<byte>(NativePtr.toVoidPtr(NativePtr.add pointer (offset + start)), count)

  static member Open(path: string, length: int, offset: int) =
    let mutable file = Unchecked.defaultof<MemoryMappedFile>
    let mutable view = Unchecked.defaultof<MemoryMappedViewAccessor>
    let mutable acquired = false
    let mutable pointer = NativePtr.nullPtr<byte>

    try
      file <- MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read)
      view <- file.CreateViewAccessor(0L, int64 length, MemoryMappedFileAccess.Read)
      view.SafeMemoryMappedViewHandle.AcquirePointer &pointer
      acquired <- true
      let basePointer = NativePtr.add pointer (int view.PointerOffset)
      let part = new MappedPart(file, view, basePointer, length, offset)
      file <- Unchecked.defaultof<MemoryMappedFile>
      view <- Unchecked.defaultof<MemoryMappedViewAccessor>
      acquired <- false
      part
    finally
      if acquired then
        view.SafeMemoryMappedViewHandle.ReleasePointer()

      if not(isNull(box view)) then
        view.Dispose()

      if not(isNull(box file)) then
        file.Dispose()

  interface IDisposable with
    member _.Dispose() =
      if not disposed then
        disposed <- true
        view.SafeMemoryMappedViewHandle.ReleasePointer()
        view.Dispose()
        file.Dispose()

[<Sealed>]
type SegmentData internal (parts: MappedPart[], chunkBytes: int, length: int) =
  member _.Length = length

  member private _.SliceParts(offset: int, count: int) : ReadOnlySpan<byte> =
    if offset < 0 || count < 0 || offset > length - count then
      invalidArg (nameof offset) "Segment range is invalid"

    if count = 0 then
      parts[0].Slice(0, 0)
    else
      let index = offset / chunkBytes
      let start = offset % chunkBytes

      if count <= parts[index].Length - start then
        parts[index].Slice(start, count)
      else
        if count > Format.Lookup.MaxKeyBytes then
          invalidArg (nameof count) "Cross-part reads are limited to 1 MiB"

        let result = Array.zeroCreate<byte> count
        let mutable written = 0
        let mutable position = offset

        while written < count do
          let partIndex = position / chunkBytes
          let partOffset = position % chunkBytes
          let copied = min (count - written) (parts[partIndex].Length - partOffset)

          parts[partIndex]
            .Slice(partOffset, copied)
            .CopyTo(result.AsSpan(written, copied))

          written <- written + copied
          position <- position + copied

        ReadOnlySpan result

  [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
  member this.Slice(offset: int, count: int) : ReadOnlySpan<byte> =
    if parts.Length = 1 then
      parts[0].Slice(offset, count)
    else
      this.SliceParts(offset, count)

  member this.Slice(offset: int) = this.Slice(offset, length - offset)

  member this.Item
    with get (offset: int) =
      let bytes = this.Slice(offset, 1)
      bytes[0]

[<Sealed>]
type MappedSegment private (parts: MappedPart[], length: int, header: Format.Header, chunkBytes: int) =
  let data = SegmentData(parts, chunkBytes, int header.PayloadLength)
  let mutable disposed = false
  member _.Header = header
  member _.ByteLength = length

  member _.Data =
    ObjectDisposedException.ThrowIf(disposed, typeof<MappedSegment>)
    data

  member _.Payload =
    ObjectDisposedException.ThrowIf(disposed, typeof<MappedSegment>)
    data.Slice(0, data.Length)

  static member private OpenCore(path: string, allowedParts: ISet<string> voption) : Result<MappedSegment, OpenError> =
    let owned = ResizeArray<MappedPart>()
    let mutable transferred = false

    try
      try
        if not(File.Exists path) then
          Error(SegmentNotFound path)
        elif Artifact.isLink path then
          Error(OpenFailed(path, "Segment links are rejected"))
        else
          let byteLength = FileInfo(path).Length

          if byteLength > int64 Int32.MaxValue then
            Error(SegmentTooLarge(path, byteLength))
          elif byteLength < int64 Format.HeaderLength then
            Error(InvalidFormat(path, Format.TooShort))
          else
            let headerBytes = Array.zeroCreate<byte> Format.HeaderLength

            do
              use stream =
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read ||| FileShare.Delete)

              stream.ReadExactly headerBytes

            let payloadLength =
              BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.AsSpan(40, 8))

            if payloadLength > uint64(Int32.MaxValue - Format.HeaderLength) then
              Error(SegmentTooLarge(path, Int64.MaxValue))
            else
              let logicalLength = int64 payloadLength + int64 Format.HeaderLength

              match Format.tryReadHeader (ReadOnlySpan headerBytes) logicalLength with
              | Error error -> Error(InvalidFormat(path, error))
              | Ok header ->
                let chunkBytes = int byteLength - Format.HeaderLength

                if byteLength > logicalLength || (chunkBytes = 0 && payloadLength > 0UL) then
                  Error(InvalidFormat(path, Format.LengthMismatch(payloadLength, byteLength)))
                else
                  let chunk = max 1 chunkBytes
                  let partCount = max 1 (int((payloadLength + uint64 chunk - 1UL) / uint64 chunk))

                  if partCount > MaxParts then
                    Error(OpenFailed(path, $"Segment needs more than {MaxParts} parts"))
                  else
                    let mutable failure = ValueNone

                    match allowedParts with
                    | ValueNone -> ()
                    | ValueSome names ->
                      if names.Contains(Path.GetFullPath(partPath path partCount)) then
                        failure <- ValueSome(OpenFailed(path, "Unexpected trailing segment part"))

                    for index in 0 .. partCount - 1 do
                      if failure.IsNone then
                        let physical = partPath path index
                        let offset = if index = 0 then Format.HeaderLength else 0
                        let expected = offset + min chunk (int payloadLength - index * chunk)

                        let listed =
                          match allowedParts with
                          | ValueNone -> true
                          | ValueSome names -> names.Contains(Path.GetFullPath physical)

                        if not listed then
                          failure <- ValueSome(OpenFailed(physical, "Segment part is not listed in the manifest"))
                        elif Artifact.isLink physical then
                          failure <- ValueSome(OpenFailed(physical, "Segment part links are rejected"))
                        elif not(File.Exists physical) then
                          failure <- ValueSome(SegmentNotFound physical)
                        elif FileInfo(physical).Length <> int64 expected then
                          failure <- ValueSome(OpenFailed(physical, "Segment part length mismatch"))
                        else
                          owned.Add(MappedPart.Open(physical, expected, offset))

                    match failure with
                    | ValueSome error -> Error error
                    | ValueNone ->
                      transferred <- true
                      Ok(new MappedSegment(owned.ToArray(), int byteLength, header, chunk))
      with
      | :? IOException as error -> Error(OpenFailed(path, error.Message))
      | :? UnauthorizedAccessException -> Error(OpenFailed(path, "読み取り権限がありません"))
    finally
      if not transferred then
        for part in owned do
          (part :> IDisposable).Dispose()

  static member Open(path: string) = MappedSegment.OpenCore(path, ValueNone)

  static member Open(path: string, allowedParts: ISet<string>) =
    MappedSegment.OpenCore(path, ValueSome allowedParts)

  interface IDisposable with
    member _.Dispose() =
      if not disposed then
        disposed <- true

        for part in parts do
          (part :> IDisposable).Dispose()
