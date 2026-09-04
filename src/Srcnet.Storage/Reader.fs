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
open System.IO
open System.IO.MemoryMappedFiles
open Microsoft.FSharp.NativeInterop

type OpenError =
  | SegmentNotFound of path: string
  | SegmentTooLarge of path: string * byteLength: int64
  | InvalidFormat of path: string * error: Format.FormatError
  | OpenFailed of path: string * message: string

module OpenError =

  let describe error =
    match error with
    | SegmentNotFound path -> $"セグメントが見つかりません: {path}"
    | SegmentTooLarge(path, byteLength) ->
      $"セグメント {path} が {byteLength} バイトで、単一ビューの上限を超えています"
    | InvalidFormat(path, error) -> $"{path}: {Format.FormatError.describe error}"
    | OpenFailed(path, message) -> $"{path}: {message}"

/// マップされた 1 セグメント。`Payload` の有効期間はこのインスタンスの生存期間に一致する。
[<Sealed>]
type MappedSegment
  private (file: MemoryMappedFile, view: MemoryMappedViewAccessor, pointer: nativeptr<byte>, length: int, header: Format.Header)
  =
  let mutable disposed = false

  member _.Header = header

  member _.ByteLength = length

  /// ヘッダーを除いた本体。範囲はヘッダーが宣言した長さで確定しており、
  /// ファイル長との一致は `Format.tryReadHeader` で検証済みである。
  member _.Payload: ReadOnlySpan<byte> =
    ObjectDisposedException.ThrowIf(disposed, typeof<MappedSegment>)
    ReadOnlySpan<byte>(NativePtr.toVoidPtr(NativePtr.add pointer Format.HeaderLength), length - Format.HeaderLength)

  static member Open(path: string) : Result<MappedSegment, OpenError> =
    if not (File.Exists path) then Error(SegmentNotFound path)
    else

    let byteLength = FileInfo(path).Length

    // 単一の Span で扱える上限を超える場合は、黙って切り詰めず明示的に失敗させる。
    // 分割ビューによる読み取りはセグメントが 2 GiB を超える規模で必要になる。
    if byteLength > int64 Int32.MaxValue then Error(SegmentTooLarge(path, byteLength))
    elif byteLength < int64 Format.HeaderLength then
      Error(InvalidFormat(path, Format.TooShort))
    else

    let mutable file = Unchecked.defaultof<MemoryMappedFile>
    let mutable view = Unchecked.defaultof<MemoryMappedViewAccessor>
    let mutable acquired = false
    let mutable pointer = NativePtr.nullPtr<byte>

    try
      try
        file <- MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read)
        view <- file.CreateViewAccessor(0L, byteLength, MemoryMappedFileAccess.Read)
        view.SafeMemoryMappedViewHandle.AcquirePointer &pointer
        acquired <- true
        // ビューの先頭がページ境界へ切り下げられる場合があるため、必ず補正する。
        let basePointer = NativePtr.add pointer (int view.PointerOffset)
        let length = int byteLength

        let headerSpan = ReadOnlySpan<byte>(NativePtr.toVoidPtr basePointer, Format.HeaderLength)

        match Format.tryReadHeader headerSpan byteLength with
        | Error error -> Error(InvalidFormat(path, error))
        | Ok header ->
          let segment = new MappedSegment(file, view, basePointer, length, header)
          file <- Unchecked.defaultof<MemoryMappedFile>
          view <- Unchecked.defaultof<MemoryMappedViewAccessor>
          acquired <- false
          Ok segment
      with
      | :? IOException as ex -> Error(OpenFailed(path, ex.Message))
      | :? UnauthorizedAccessException -> Error(OpenFailed(path, "読み取り権限がありません"))
    finally
      // 成功時は所有権が MappedSegment へ移るため、ここでは解放しない。
      if acquired then view.SafeMemoryMappedViewHandle.ReleasePointer()
      if not (isNull (box view)) then view.Dispose()
      if not (isNull (box file)) then file.Dispose()

  interface IDisposable with

    member _.Dispose() =
      if not disposed then
        disposed <- true
        view.SafeMemoryMappedViewHandle.ReleasePointer()
        view.Dispose()
        file.Dispose()
