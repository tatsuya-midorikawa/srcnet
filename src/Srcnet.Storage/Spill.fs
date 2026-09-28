module Srcnet.Storage.Spill

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.IO.MemoryMappedFiles
open System.Threading
open Srcnet.Core.Graph
open Srcnet.Core.Paths
open Srcnet.Extraction.Model

let private pathOf text =
  match tryCreate text with
  | Ok path -> path
  | Error error -> raise(InvalidDataException(PathError.describe error))

[<Sealed>]
type private ItemReader(path: string) =
  let reader =
    new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536), Strings.utf8)

  let mutable buffer = ArrayPool<byte>.Shared.Rent 4096
  member _.Reader = reader

  member _.Text() =
    let length = reader.Read7BitEncodedInt()

    if length < 0 || length > Format.Lookup.MaxKeyBytes then
      raise(InvalidDataException "Invalid spill string length")

    if length > buffer.Length then
      ArrayPool<byte>.Shared.Return buffer
      buffer <- ArrayPool<byte>.Shared.Rent length

    reader.BaseStream.ReadExactly(Span(buffer, 0, length))
    Strings.utf8.GetString(buffer, 0, length)

  member _.Count(limit: int) =
    let count = reader.ReadInt32()

    if count < 0 || count > limit then
      raise(InvalidDataException "Invalid spill item count")

    count

  interface IDisposable with
    member _.Dispose() =
      reader.Dispose()
      ArrayPool<byte>.Shared.Return buffer

let private writeFile (writer: BinaryWriter) (file: Writer.FileInput) =
  writer.Write(value file.Path)
  writer.Write file.SizeBytes
  writer.Write(Language.toCode file.Language)
  writer.Write file.EncodingCode
  writer.Write(uint32 file.Flags)
  writer.Write file.LineCount
  writer.Write file.ContentHash
  writer.Write file.Symbols.Length

  for symbol in file.Symbols do
    writer.Write(NodeKind.toCode symbol.Kind)
    writer.Write symbol.Name
    writer.Write symbol.QualifiedName
    writer.Write symbol.Parent
    writer.Write symbol.Ordinal
    writer.Write symbol.StartLine
    writer.Write symbol.EndLine
    writer.Write symbol.StartByte
    writer.Write symbol.EndByte
    writer.Write(uint32 symbol.Flags)

  writer.Write file.References.Length

  for reference in file.References do
    writer.Write reference.Source
    writer.Write(EdgeKind.toCode reference.Kind)
    writer.Write reference.Target
    writer.Write reference.Qualifier
    writer.Write(Language.toCode reference.Language)
    writer.Write reference.StartByte
    writer.Write reference.EndByte
    writer.Write reference.Line
    writer.Write reference.Stage
    writer.Write(Confidence.toCode reference.Confidence)

let private readFile (input: ItemReader) : Writer.FileInput =
  let reader = input.Reader
  let path = pathOf(input.Text())
  let size = reader.ReadInt64()
  let language = Language.ofCode(reader.ReadUInt16())
  let encoding = reader.ReadUInt16()
  let flags = LanguagePrimitives.EnumOfValue<uint32, NodeFlags>(reader.ReadUInt32())
  let lines = reader.ReadInt32()
  let hash = reader.ReadBytes 32

  if hash.Length <> 32 then
    raise(EndOfStreamException())

  let symbols =
    Array.init (input.Count Limits.MaxSymbolsPerFile) (fun _ ->
      let kind =
        match NodeKind.ofCode(reader.ReadByte()) with
        | ValueSome kind -> kind
        | ValueNone -> raise(InvalidDataException "Invalid spill symbol kind")

      { Writer.Kind = kind
        Writer.Name = input.Text()
        Writer.QualifiedName = input.Text()
        Writer.Parent = reader.ReadInt32()
        Writer.Ordinal = reader.ReadUInt32()
        Writer.StartLine = reader.ReadInt32()
        Writer.EndLine = reader.ReadInt32()
        Writer.StartByte = reader.ReadInt32()
        Writer.EndByte = reader.ReadInt32()
        Writer.Flags = LanguagePrimitives.EnumOfValue<uint32, NodeFlags>(reader.ReadUInt32()) })

  let references =
    Array.init (input.Count Limits.MaxReferencesPerFile) (fun _ ->
      let source = reader.ReadInt32()

      let kind =
        match EdgeKind.ofCode(reader.ReadByte()) with
        | ValueSome kind -> kind
        | ValueNone -> raise(InvalidDataException "Invalid spill reference kind")

      let target = input.Text()
      let qualifier = input.Text()
      let language = Language.ofCode(reader.ReadUInt16())
      let startByte = reader.ReadInt32()
      let endByte = reader.ReadInt32()
      let line = reader.ReadInt32()
      let stage = reader.ReadByte()

      let confidence =
        match Confidence.ofCode(reader.ReadByte()) with
        | ValueSome confidence -> confidence
        | ValueNone -> raise(InvalidDataException "Invalid spill confidence")

      let reference: Writer.ReferenceInput =
        { Source = source
          Kind = kind
          Target = target
          Qualifier = qualifier
          Language = language
          StartByte = startByte
          EndByte = endByte
          Line = line
          Stage = stage
          Confidence = confidence }

      reference)

  { Path = path
    SizeBytes = size
    Language = language
    EncodingCode = encoding
    Flags = flags
    LineCount = lines
    ContentHash = hash
    Symbols = symbols
    References = references }

[<Sealed>]
type private DiskList<'Item>(data: string, offsets: string, count: int, read: ItemReader -> 'Item) =
  let reader = new ItemReader(data)

  let mapping =
    if count = 0 then
      ValueNone
    else
      let mapped =
        MemoryMappedFile.CreateFromFile(offsets, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read)

      let view = mapped.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read)
      ValueSome(struct (mapped, view))

  let mutable cached = ValueNone
  let mutable disposed = false

  member private _.Read(index: int) =
    ObjectDisposedException.ThrowIf(disposed, typeof<DiskList<'Item>>)

    if index < 0 || index >= count then
      invalidArg (nameof index) "Spill index out of range"

    match cached with
    | ValueSome(struct (previous, item)) when index = previous -> item
    | _ ->
      match mapping with
      | ValueNone -> invalidOp "Empty spill index"
      | ValueSome(struct (_, view)) ->
        reader.Reader.BaseStream.Position <- view.ReadInt64(int64 index * 8L)
        let item = read reader
        cached <- ValueSome(struct (index, item))
        item

  interface IReadOnlyList<'Item> with
    member this.Item
      with get index = this.Read index

    member _.Count = count

  interface IEnumerable<'Item> with
    member this.GetEnumerator() =
      (seq {
        for index in 0 .. count - 1 do
          yield this.Read index
      })
        .GetEnumerator()

  interface Collections.IEnumerable with
    member this.GetEnumerator() =
      (this :> IEnumerable<'Item>).GetEnumerator() :> Collections.IEnumerator

  interface IDisposable with
    member _.Dispose() =
      if not disposed then
        disposed <- true
        cached <- ValueNone
        (reader :> IDisposable).Dispose()

        match mapping with
        | ValueNone -> ()
        | ValueSome(struct (mapped, view)) ->
          view.Dispose()
          mapped.Dispose()

[<Sealed>]
type Input(directory: string, bufferBytes: int64, budget: ExternalSort.TemporaryBudget, cancellation: CancellationToken)
  =
  let root = Path.Combine(directory, "input-" + Guid.NewGuid().ToString "N")
  let gate = obj()
  do Directory.CreateDirectory root |> ignore
  let data = Path.Combine(root, "data")

  let writer =
    new BinaryWriter(
      new ExternalSort.BudgetedStream(
        new FileStream(data, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536),
        budget
      ),
      Strings.utf8
    )

  let compareKey (struct (left: string, _)) (struct (right: string, _)) = String.CompareOrdinal(left, right)

  let writeKey (output: BinaryWriter) (struct (path: string, offset: int64)) =
    output.Write path
    output.Write offset

  let readKey (input: BinaryReader) =
    struct (input.ReadString(), input.ReadInt64())

  let files =
    new ExternalSort.Sorter<struct (string * int64)>(
      root,
      bufferBytes / 2L,
      budget,
      compareKey,
      writeKey,
      readKey,
      cancellation
    )

  let directories =
    new ExternalSort.Sorter<struct (string * int64)>(
      root,
      bufferBytes / 2L,
      budget,
      compareKey,
      writeKey,
      readKey,
      cancellation
    )

  let readers = ResizeArray<IDisposable>()
  let mutable finished = false
  let mutable disposed = false
  let mutable fileCount = 0
  let mutable directoryCount = 0
  let cachedFiles = ResizeArray<Writer.FileInput>()
  let cachedDirectories = ResizeArray<LogicalPath>()
  let mutable retained = 0L
  let mutable spilled = false

  let appendFile (file: Writer.FileInput) =
    let offset = writer.BaseStream.Position
    writeFile writer file
    files.Add(struct (value file.Path, offset), 64L + 2L * int64 (value file.Path).Length)

  let appendDirectory path =
    let offset = writer.BaseStream.Position
    writer.Write(value path)
    directories.Add(struct (value path, offset), 64L + 2L * int64 (value path).Length)

  let spillCached () =
    if not spilled then
      spilled <- true

      for path in cachedDirectories do
        appendDirectory path

      cachedDirectories.Clear()

      for file in cachedFiles do
        appendFile file

      cachedFiles.Clear()
      retained <- 0L

  let retainedBytes (file: Writer.FileInput) =
    let mutable size = 256L + int64 (value file.Path).Length * 2L

    for symbol in file.Symbols do
      size <- size + 192L + int64(symbol.Name.Length + symbol.QualifiedName.Length) * 2L

    for reference in file.References do
      size <- size + 192L + int64(reference.Target.Length + reference.Qualifier.Length) * 2L

    size

  let offsets name (sorter: ExternalSort.Sorter<struct (string * int64)>) =
    let path = Path.Combine(root, name)

    use output =
      new BinaryWriter(
        new ExternalSort.BudgetedStream(
          new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536),
          budget
        )
      )

    for struct (_, offset) in sorter.Read() do
      output.Write offset

    path

  member _.AddFile(file: Writer.FileInput) =
    lock gate (fun () ->
      cancellation.ThrowIfCancellationRequested()

      if finished || disposed then
        invalidOp "Spill input is closed"

      if file.ContentHash.Length <> 32 then
        invalidArg (nameof file) "Content hash must contain 32 bytes"

      let size = retainedBytes file

      if not spilled && size > bufferBytes - retained then
        spillCached()

      if spilled then
        appendFile file
      else
        cachedFiles.Add file
        retained <- retained + size

      fileCount <- fileCount + 1)

  member _.AddDirectory(path: LogicalPath) =
    lock gate (fun () ->
      cancellation.ThrowIfCancellationRequested()

      if finished || disposed then
        invalidOp "Spill input is closed"

      let size = 64L + 2L * int64 (value path).Length

      if not spilled && size > bufferBytes - retained then
        spillCached()

      if spilled then
        appendDirectory path
      else
        cachedDirectories.Add path
        retained <- retained + size

      directoryCount <- directoryCount + 1)

  member _.Finish(repository: Srcnet.Core.Ids.RepositoryId) : Writer.IndexSource =
    if finished || disposed then
      invalidOp "Spill input is closed"

    finished <- true
    writer.Dispose()

    if not spilled then
      let fileList = cachedFiles.ToArray()
      let directoryList = cachedDirectories.ToArray()
      cachedFiles.Clear()
      cachedDirectories.Clear()
      Array.sortInPlaceWith (fun (left: Writer.FileInput) right -> comparePath left.Path right.Path) fileList
      Array.sortInPlaceWith comparePath directoryList

      { Repository = repository
        Files = fileList
        Directories = directoryList }
    else
      let fileOffsets = offsets "files" files
      let directoryOffsets = offsets "directories" directories

      let fileList =
        new DiskList<Writer.FileInput>(data, fileOffsets, fileCount, readFile)

      readers.Add fileList

      let directoryList =
        new DiskList<LogicalPath>(data, directoryOffsets, directoryCount, (fun input -> pathOf(input.Text())))

      readers.Add directoryList

      { Repository = repository
        Files = fileList
        Directories = directoryList }

  interface IDisposable with
    member _.Dispose() =
      if not disposed then
        disposed <- true
        cachedFiles.Clear()
        cachedDirectories.Clear()

        for reader in readers do
          reader.Dispose()

        writer.Dispose()
        (files :> IDisposable).Dispose()
        (directories :> IDisposable).Dispose()

        if Directory.Exists root then
          for path in Directory.EnumerateFiles root do
            let length = FileInfo(path).Length
            File.Delete path
            budget.Release length

          Directory.Delete root
