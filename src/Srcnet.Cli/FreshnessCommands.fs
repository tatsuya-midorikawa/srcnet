module Srcnet.Cli.FreshnessCommands

open System
open System.Buffers
open System.IO
open System.Security.Cryptography
open System.Threading
open Srcnet.Core
open Srcnet.Core.Graph
open Srcnet.Storage

[<Literal>]
let MaxBytes = 268435456L

type private Observation =
  { Path: string
    Status: string
    Reason: string
    Indexed: bool }

let private linkedPath (root: string) (path: string) =
  let mutable current = root
  let mutable linked = false

  for part in path.Split '/' do
    current <- Path.Combine(current, part)
    linked <- linked || Artifact.isLink current

  linked

let private execute (arguments: Args.FreshnessArguments) (cancellation: CancellationToken) =
  task {
    let output =
      Commands.locateArtifact arguments.OutputDirectory (ValueSome arguments.RootPath)

    use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellation
    timeout.CancelAfter(TimeSpan.FromSeconds 10.0)
    let root = Path.GetFullPath arguments.RootPath

    match Query.GraphView.Open output with
    | Error error ->
      return
        Commands.writeManagementError
          "freshness"
          Commands.ExitCode.MissingArtifact
          (Query.QueryError.describe error)
          arguments.Json
    | Ok view ->
      use view = view
      let observations = ResizeArray<Observation>()
      let buffer = ArrayPool<byte>.Shared.Rent 65536
      let mutable bytesRead = 0L
      let mutable checkedFiles = 0

      try
        for rawPath in
          arguments.Paths
          |> Array.distinctBy(fun path -> path.Replace('\\', '/').Normalize(Text.NormalizationForm.FormC)) do
          cancellation.ThrowIfCancellationRequested()
          let path = rawPath.Replace('\\', '/').Normalize(Text.NormalizationForm.FormC)
          let mutable indexed = false

          let! status, reason =
            task {
              if timeout.IsCancellationRequested then
                return "unchecked", "time-limit"
              else
                try
                  let matches = Query.searchExactNames view path false timeout.Token

                  let found =
                    matches.Hits
                    |> Array.tryPick(fun hit ->
                      let node = view.Node hit.Node

                      if node.Kind = File && view.String(view.FilePathRef node.FileIndex) = path then
                        Some node
                      else
                        None)

                  match found with
                  | None -> return "unchecked", (if matches.Truncated then "lookup-limit" else "not-indexed")
                  | Some node ->
                    indexed <- true

                    if node.Flags &&& NodeFlags.Skipped <> NodeFlags.None then
                      return "unchecked", "no-content-hash"
                    elif linkedPath root (rawPath.Replace('\\', '/')) then
                      return "unreadable", "link-rejected"
                    else
                      let physical =
                        Path.Combine(
                          root,
                          rawPath
                            .Replace('/', Path.DirectorySeparatorChar)
                            .Replace('\\', Path.DirectorySeparatorChar)
                        )

                      let info = FileInfo physical
                      let attributes = File.GetAttributes physical

                      if
                        attributes.HasFlag FileAttributes.Directory
                        || attributes.HasFlag FileAttributes.Device
                      then
                        return "unreadable", "not-regular-file"
                      elif info.Length = 0L then
                        checkedFiles <- checkedFiles + 1
                        let digest = SHA256.HashData(ReadOnlySpan<byte>.Empty)

                        return
                          (if digest.AsSpan().SequenceEqual(view.FileContentHash node.FileIndex) then
                             "unchanged"
                           else
                             "changed"),
                          ""
                      elif info.Length > MaxBytes - bytesRead then
                        return "unchecked", "byte-limit"
                      else
                        use stream =
                          new FileStream(
                            physical,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite ||| FileShare.Delete,
                            1,
                            FileOptions.Asynchronous ||| FileOptions.SequentialScan
                          )

                        use hasher = IncrementalHash.CreateHash HashAlgorithmName.SHA256
                        let mutable doneReading = false
                        let mutable limited = false

                        while not doneReading && not limited do
                          let remaining = MaxBytes - bytesRead

                          if remaining = 0L then
                            limited <- stream.Position < stream.Length

                          if not limited then
                            let! count = stream.ReadAsync(buffer.AsMemory(0, int(min 65536L remaining)), timeout.Token)

                            if count = 0 then
                              doneReading <- true
                            else
                              bytesRead <- bytesRead + int64 count
                              hasher.AppendData(buffer, 0, count)

                        if limited then
                          return "unchecked", "byte-limit"
                        elif linkedPath root (rawPath.Replace('\\', '/')) then
                          return "unreadable", "link-rejected"
                        else
                          let digest = hasher.GetHashAndReset()
                          checkedFiles <- checkedFiles + 1

                          return
                            (if digest.AsSpan().SequenceEqual(view.FileContentHash node.FileIndex) then
                               "unchanged"
                             else
                               "changed"),
                            ""
                with
                | :? FileNotFoundException
                | :? DirectoryNotFoundException -> return "deleted", "not-found"
                | :? UnauthorizedAccessException -> return "unreadable", "access-denied"
                | :? IOException -> return "unreadable", "io-error"
                | :? OperationCanceledException when not cancellation.IsCancellationRequested ->
                  return "unchecked", "time-limit"
            }

          observations.Add
            { Path = path
              Status = status
              Reason = reason
              Indexed = indexed }
      finally
        ArrayPool<byte>.Shared.Return buffer

      let unchanged = observations |> Seq.forall(fun item -> item.Status = "unchanged")

      let code =
        if unchanged then
          Commands.ExitCode.Success
        else
          Commands.ExitCode.CompletedWithDiagnostics

      let generation = Manifest.generationIn view.Manifest |> ValueOption.defaultValue ""

      let inspected =
        observations
        |> Seq.filter(fun item -> item.Indexed && item.Status <> "unchecked")
        |> Seq.length

      if arguments.Json then
        Commands.writeJson(fun writer ->
          writer.WriteString("command", "freshness")
          writer.WriteNumber("exitCode", code)
          writer.WriteBoolean("hasResult", true)
          writer.WriteString("repositoryId", view.Manifest.RepositoryId)
          writer.WriteString("generation", generation)
          writer.WriteString("scope", "selected-files")
          writer.WriteNumber("requestedFiles", observations.Count)
          writer.WriteNumber("checkedFiles", checkedFiles)
          writer.WriteNumber("uncheckedFiles", view.FileCount - inspected)
          writer.WriteNumber("bytesRead", bytesRead)
          writer.WriteBoolean("selectedFilesUnchanged", unchanged)
          writer.WriteStartArray "files"

          for item in observations do
            writer.WriteStartObject()
            writer.WriteString("path", item.Path)
            writer.WriteString("status", item.Status)
            writer.WriteString("reason", item.Reason)
            writer.WriteEndObject()

          writer.WriteEndArray())
      else
        Terminal.outLine $"世代: {generation} / 指定ファイルのみを確認"

        for item in observations do
          Terminal.resultLine $"{item.Path}: {item.Status} {item.Reason}"

        Terminal.outLine $"ハッシュ確認: {checkedFiles} / 未確認: {view.FileCount - inspected} / 読取: {bytesRead} バイト"

        if not unchanged then
          Terminal.outLine "変更・未確認があります。必要なら元の生成条件で index を再実行してください。"

      return code
  }

let run (arguments: Args.FreshnessArguments) (cancellation: CancellationToken) =
  task {
    try
      let invalid =
        if
          isNull(box arguments.Paths)
          || arguments.Paths.Length = 0
          || arguments.Paths.Length > 32
        then
          Some "Specify between 1 and 32 relative source files"
        else
          arguments.Paths
          |> Array.tryPick(fun path ->
            match Args.validateQueryText "<file>" path with
            | ValueSome error -> Some(Args.ParseError.describe error)
            | ValueNone ->
              match Paths.tryCreate path with
              | Error error -> Some(Paths.PathError.describe error)
              | Ok logical when Paths.isRoot logical || path.Contains ':' -> Some "Expected a relative file path"
              | Ok _ -> None)

      match invalid with
      | Some message ->
        return Commands.writeManagementError "freshness" Commands.ExitCode.UserError message arguments.Json
      | None -> return! execute arguments cancellation
    with
    | Query.QueryException error ->
      return
        Commands.writeManagementError
          "freshness"
          Commands.ExitCode.MissingArtifact
          (Query.QueryError.describe error)
          arguments.Json
    | :? ArgumentException as error ->
      return Commands.writeManagementError "freshness" Commands.ExitCode.UserError error.Message arguments.Json
    | :? IOException as error ->
      return Commands.writeManagementError "freshness" Commands.ExitCode.MissingArtifact error.Message arguments.Json
    | :? UnauthorizedAccessException as error ->
      return Commands.writeManagementError "freshness" Commands.ExitCode.MissingArtifact error.Message arguments.Json
  }
