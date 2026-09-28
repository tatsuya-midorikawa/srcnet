module Srcnet.Cli.AgentContextCommands

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Srcnet.Storage

[<Literal>]
let FileName = "agent-context.json"

type private Context =
  { Executable: string
    SourceRoot: string
    IndexDirectory: string }

let private readObject path maxBytes =
  if Artifact.isLink path then
    Error "Context metadata must not be a symbolic link"
  else
    try
      use stream =
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read ||| FileShare.Delete)

      if stream.Length > maxBytes then
        Error "Context metadata exceeds its size limit"
      else
        let bytes = Array.zeroCreate<byte>(int stream.Length)
        stream.ReadExactly bytes

        match JsonNode.Parse(ReadOnlySpan bytes) with
        | :? JsonObject as value -> Ok value
        | _ -> Error "Context metadata must be a JSON object"
    with
    | :? IOException as error -> Error error.Message
    | :? UnauthorizedAccessException as error -> Error error.Message
    | :? JsonException as error -> Error error.Message

let private contextOf (value: JsonObject) =
  let get (name: string) =
    match value[name] with
    | :? JsonValue as field ->
      match field.TryGetValue<string>() with
      | _, null -> Error $"{name} must not be null"
      | true, path ->
        if
          not(String.IsNullOrWhiteSpace path)
          && path.Length <= 32768
          && Path.IsPathFullyQualified path
        then
          match Args.validatePath name path with
          | ValueNone -> Ok path
          | ValueSome error -> Error(Args.ParseError.describe error)
        else
          Error $"{name} must be a literal absolute path"
      | false, _ -> Error $"{name} must be a literal absolute path"
    | _ -> Error $"Missing context field: {name}"

  match get "SRCNET", get "SOURCE_ROOT", get "INDEX_DIR" with
  | Ok executable, Ok root, Ok directory ->
    Ok
      { Executable = executable
        SourceRoot = root
        IndexDirectory = directory }
  | Error error, _, _
  | _, Error error, _
  | _, _, Error error -> Error error

let private store directory (context: Context) =
  let destination = Path.Combine(directory, FileName)

  let temporary =
    Path.Combine(directory, ".agent-context-" + Guid.NewGuid().ToString("N") + ".tmp")

  match Artifact.ensureNotLink destination with
  | Error error -> Error(Artifact.PathError.describe error)
  | Ok() ->
    try
      try
        do
          let options =
            FileStreamOptions(Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None)

          if not(OperatingSystem.IsWindows()) then
            options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

          use stream = new FileStream(temporary, options)
          use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
          writer.WriteStartObject()
          writer.WriteNumber("schemaVersion", 1)
          writer.WriteString("SRCNET", context.Executable)
          writer.WriteString("SOURCE_ROOT", context.SourceRoot)
          writer.WriteString("INDEX_DIR", context.IndexDirectory)
          writer.WriteEndObject()
          writer.Flush()
          stream.Flush true

        match Artifact.ensureNotLink destination with
        | Error error -> Error(Artifact.PathError.describe error)
        | Ok() ->
          Artifact.replaceFile temporary destination
          Ok()
      with
      | :? IOException as error -> Error error.Message
      | :? UnauthorizedAccessException as error -> Error error.Message
    finally
      if File.Exists temporary then
        File.Delete temporary

let private update directory (manifest: Manifest.Manifest) (arguments: Args.AgentContextArguments) =
  match arguments.Executable, arguments.RootPath with
  | ValueSome executable, ValueSome root ->
    let context =
      { Executable = Path.GetFullPath executable
        SourceRoot = Path.GetFullPath root
        IndexDirectory = directory }

    if not(File.Exists context.Executable) then
      Error "The explicitly selected executable does not exist"
    elif not(Directory.Exists context.SourceRoot) then
      Error "The explicitly selected source root does not exist"
    else
      let migration =
        if not arguments.Migrate then
          Ok()
        else
          match readObject (Path.Combine(directory, Manifest.FileName)) 67108864L with
          | Error error -> Error error
          | Ok original ->
            for name in [ "SRCNET"; "SOURCE_ROOT"; "INDEX_DIR" ] do
              original.Remove name |> ignore

            let canonical = JsonNode.Parse(ReadOnlySpan(Manifest.serialize manifest))

            if JsonNode.DeepEquals(original, canonical) then
              Ok()
            else
              Error "Migration would change other metadata; the manifest was preserved"

      match migration with
      | Error error -> Error error
      | Ok() ->
        match store directory context with
        | Error error -> Error error
        | Ok() ->
          if arguments.Migrate then
            Manifest.write directory manifest
            |> Result.mapError Artifact.PathError.describe
            |> Result.map(fun () -> struct (context, "sidecar"))
          else
            Ok(struct (context, "sidecar"))
  | _ -> Error "Writing context requires --root and --executable"

let private execute (arguments: Args.AgentContextArguments) =
  let directory = Commands.locateArtifact arguments.OutputDirectory ValueNone

  let fail code message =
    Commands.writeManagementError "agent-context" code message arguments.Json

  match Manifest.read directory with
  | Error error -> fail Commands.ExitCode.MissingArtifact (Manifest.ManifestError.describe error)
  | Ok _ ->
    let outcome =
      if arguments.Write then
        match Artifact.ensureNotLink directory with
        | Error error -> Error(Artifact.PathError.describe error)
        | Ok() ->
          match Manifest.acquireWriter directory with
          | Error error -> Error(Artifact.PathError.describe error)
          | Ok lease ->
            use _lease = lease

            match Manifest.read directory with
            | Error error -> Error(Manifest.ManifestError.describe error)
            | Ok manifest -> update directory manifest arguments
      else
        let path = Path.Combine(directory, FileName)

        let source, path, limit =
          if File.Exists path || Artifact.isLink path then
            "sidecar", path, 1048576L
          else
            "legacy-manifest", Path.Combine(directory, Manifest.FileName), 67108864L

        match readObject path limit with
        | Error error -> Error error
        | Ok value ->
          let version =
            if source = "legacy-manifest" then
              Ok()
            else
              match value["schemaVersion"] with
              | :? JsonValue as field ->
                try
                  if field.GetValue<int>() = 1 then
                    Ok()
                  else
                    Error "Unsupported context schemaVersion"
                with
                | :? InvalidOperationException
                | :? FormatException
                | :? OverflowException -> Error "Invalid context schemaVersion"
              | _ -> Error "Missing context schemaVersion"

          version
          |> Result.bind(fun () -> contextOf value)
          |> Result.map(fun context -> struct (context, source))

    match outcome with
    | Error message -> fail Commands.ExitCode.UserError message
    | Ok(struct (context, source)) ->
      let comparison =
        if OperatingSystem.IsWindows() then
          StringComparison.OrdinalIgnoreCase
        else
          StringComparison.Ordinal

      let bindingMatches =
        String.Equals(
          Path.TrimEndingDirectorySeparator(Path.GetFullPath context.IndexDirectory),
          Path.TrimEndingDirectorySeparator directory,
          comparison
        )

      let code = if bindingMatches then 0 else 4

      if arguments.Json then
        Commands.writeJson(fun writer ->
          writer.WriteString("command", "agent-context")
          writer.WriteNumber("exitCode", code)
          writer.WriteBoolean("hasResult", true)
          writer.WriteString("source", source)
          writer.WriteString("SRCNET", context.Executable)
          writer.WriteString("SOURCE_ROOT", context.SourceRoot)
          writer.WriteString("INDEX_DIR", context.IndexDirectory)
          writer.WriteString("selectedIndexDirectory", directory)
          writer.WriteBoolean("indexBindingMatches", bindingMatches)
          writer.WriteBoolean("pathsChecked", arguments.Write)
          writer.WriteString("executableTrust", "not-inferred"))
      else
        Terminal.resultLine $"設定: {source}"
        Terminal.resultLine $"SRCNET: {context.Executable}"
        Terminal.resultLine $"SOURCE_ROOT: {context.SourceRoot}"
        Terminal.resultLine $"INDEX_DIR: {context.IndexDirectory}"
        Terminal.resultLine $"選択中の索引: {directory}"
        Terminal.outLine "設定を読み取っただけでは実行ファイルを信頼しません。"

        if code <> 0 then
          Terminal.outLine "設定の場所が選択中の索引と一致しません。明示的に再設定してください。"

      code

let run (arguments: Args.AgentContextArguments) =
  try
    execute arguments
  with
  | :? ArgumentException as error ->
    Commands.writeManagementError "agent-context" Commands.ExitCode.UserError error.Message arguments.Json
  | :? JsonException as error ->
    Commands.writeManagementError "agent-context" Commands.ExitCode.UserError error.Message arguments.Json
  | :? IOException as error ->
    Commands.writeManagementError "agent-context" Commands.ExitCode.UserError error.Message arguments.Json
  | :? UnauthorizedAccessException as error ->
    Commands.writeManagementError "agent-context" Commands.ExitCode.UserError error.Message arguments.Json
