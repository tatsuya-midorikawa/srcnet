module Srcnet.Storage.ExternalSort

open System
open System.Collections.Generic
open System.IO
open System.Threading

exception ResourceLimitExceeded of string

[<Sealed>]
type TemporaryBudget(limit: int64) =
  let gate = obj()
  let mutable used = 0L
  let mutable peak = 0L

  do
    if limit <= 0L then
      invalidArg (nameof limit) "Temporary disk limit must be positive"

  member _.Used = lock gate (fun () -> used)
  member _.Peak = lock gate (fun () -> peak)

  member _.Reserve(bytes: int64) =
    lock gate (fun () ->
      if bytes < 0L || bytes > limit - used then
        raise(ResourceLimitExceeded $"Temporary disk limit exceeded ({limit} bytes)")

      used <- used + bytes
      peak <- max peak used)

  member _.Release(bytes: int64) =
    lock gate (fun () -> used <- max 0L (used - bytes))

[<Sealed>]
type BudgetedStream(inner: Stream, budget: TemporaryBudget) =
  inherit Stream()
  override _.CanRead = false
  override _.CanSeek = false
  override _.CanWrite = true
  override _.Length = inner.Length

  override _.Position
    with get () = inner.Position
    and set _ = raise(NotSupportedException())

  override _.Flush() = inner.Flush()
  override _.Read(_, _, _) = raise(NotSupportedException())
  override _.Seek(_, _) = raise(NotSupportedException())
  override _.SetLength _ = raise(NotSupportedException())

  override _.Write(buffer: byte[], offset: int, count: int) =
    budget.Reserve(int64 count)
    inner.Write(buffer, offset, count)

  override _.Write(buffer: ReadOnlySpan<byte>) =
    budget.Reserve(int64 buffer.Length)
    inner.Write buffer

  override _.WriteByte(value: byte) =
    budget.Reserve 1L
    inner.WriteByte value

  override _.Dispose(disposing) =
    if disposing then
      inner.Dispose()

    base.Dispose disposing

let private newWriter path budget =
  let stream =
    new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536)

  new BinaryWriter(new BudgetedStream(stream, budget), Text.UTF8Encoding(false, true))

[<Sealed>]
type Sorter<'Item>
  (
    directory: string,
    bufferBytes: int64,
    budget: TemporaryBudget,
    compareItems: 'Item -> 'Item -> int,
    writeItem: BinaryWriter -> 'Item -> unit,
    readItem: BinaryReader -> 'Item,
    cancellation: CancellationToken
  ) =
  let root = Path.Combine(directory, "sort-" + Guid.NewGuid().ToString "N")
  let items = ResizeArray<'Item>()
  let runs = ResizeArray<string>()
  let mutable bytes = 0L
  let mutable serial = 0
  let mutable completed = ValueNone
  let mutable readOnly = false
  let mutable disposed = false

  do
    if bufferBytes < 64L then
      invalidArg (nameof bufferBytes) "Sort buffer must be at least 64 bytes"

    Directory.CreateDirectory root |> ignore

  let nextPath () =
    serial <- serial + 1
    Path.Combine(root, serial.ToString(Globalization.CultureInfo.InvariantCulture) + ".run")

  let remove path =
    let length = FileInfo(path).Length
    File.Delete path
    budget.Release length

  let flush () =
    if items.Count > 0 then
      cancellation.ThrowIfCancellationRequested()
      let mutable comparisons = 0

      let comparison left right =
        comparisons <- comparisons + 1

        if comparisons &&& 8191 = 0 then
          cancellation.ThrowIfCancellationRequested()

        compareItems left right

      try
        items.Sort(Comparison comparison)
      with :? InvalidOperationException as error when (error.InnerException :? OperationCanceledException) ->
        cancellation.ThrowIfCancellationRequested()
        reraise()

      let path = nextPath()
      use writer = newWriter path budget

      for item in items do
        cancellation.ThrowIfCancellationRequested()
        writeItem writer item

      runs.Add path
      items.Clear()
      bytes <- 0L

  let merge (paths: string[]) =
    let output = nextPath()
    let readers = ResizeArray<BinaryReader>()

    try
      use writer = newWriter output budget

      let queue =
        PriorityQueue<struct ('Item * int), 'Item>(Comparer<'Item>.Create(Comparison compareItems))

      for path in paths do
        let reader =
          new BinaryReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536),
            Text.UTF8Encoding(false, true)
          )

        let index = readers.Count
        readers.Add reader

        if reader.BaseStream.Position < reader.BaseStream.Length then
          let item = readItem reader
          queue.Enqueue(struct (item, index), item)

      while queue.Count > 0 do
        cancellation.ThrowIfCancellationRequested()
        let struct (item, index) = queue.Dequeue()
        writeItem writer item
        let reader = readers[index]

        if reader.BaseStream.Position < reader.BaseStream.Length then
          let next = readItem reader
          queue.Enqueue(struct (next, index), next)
    finally
      for reader in readers do
        reader.Dispose()

    for path in paths do
      remove path

    output

  member _.Add(item: 'Item, retainedBytes: int64) =
    ObjectDisposedException.ThrowIf(disposed, typeof<Sorter<'Item>>)
    cancellation.ThrowIfCancellationRequested()

    if completed.IsSome || readOnly then
      invalidOp "Sort is already complete"

    if retainedBytes < 0L then
      invalidArg (nameof retainedBytes) "Retained size must be nonnegative"

    if retainedBytes > bufferBytes - bytes then
      flush()

    items.Add item
    bytes <- bytes + retainedBytes

    if bytes >= bufferBytes then
      flush()

  member _.Finish() =
    ObjectDisposedException.ThrowIf(disposed, typeof<Sorter<'Item>>)

    match completed with
    | ValueSome path -> path
    | ValueNone ->
      flush()

      if runs.Count = 0 then
        let path = nextPath()
        use writer = newWriter path budget
        writer.Flush()
        runs.Add path

      while runs.Count > 1 do
        let next = ResizeArray<string>()
        let mutable offset = 0

        while offset < runs.Count do
          cancellation.ThrowIfCancellationRequested()
          let count = min 32 (runs.Count - offset)

          if count = 1 then
            next.Add runs[offset]
          else
            next.Add(merge(Array.init count (fun index -> runs[offset + index])))

          offset <- offset + count

        runs.Clear()
        runs.AddRange next

      let path = runs[0]
      completed <- ValueSome path
      path

  member this.Read() =
    if runs.Count = 0 && completed.IsNone then
      if not readOnly then
        let mutable comparisons = 0

        let comparison left right =
          comparisons <- comparisons + 1

          if comparisons &&& 8191 = 0 then
            cancellation.ThrowIfCancellationRequested()

          compareItems left right

        try
          items.Sort(Comparison comparison)
        with :? InvalidOperationException as error when (error.InnerException :? OperationCanceledException) ->
          cancellation.ThrowIfCancellationRequested()
          reraise()

        readOnly <- true

      seq {
        for item in items do
          cancellation.ThrowIfCancellationRequested()
          yield item
      }
    else
      let path = this.Finish()

      seq {
        use reader =
          new BinaryReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536),
            Text.UTF8Encoding(false, true)
          )

        while reader.BaseStream.Position < reader.BaseStream.Length do
          cancellation.ThrowIfCancellationRequested()
          yield readItem reader
      }

  interface IDisposable with
    member _.Dispose() =
      if not disposed then
        disposed <- true
        items.Clear()

        if Directory.Exists root then
          for path in Directory.EnumerateFiles root do
            remove path

          Directory.Delete root
