#r "nuget: Microsoft.Diagnostics.Tracing.TraceEvent"

open System
open System.Collections.Generic
open System.IO
open Microsoft.Diagnostics.Tracing.Etlx
open Microsoft.Diagnostics.Symbols

let usage = """
Usage:
    dotnet fsi ./scripts/resolve-symbols.fsx <trace.etl> [--filter <process-or-pid>] [--max-samples <n>] [--max-depth <n>] [--symbol-path <path>] [--max-events <n>]

Converts .etl to .etlx, loads symbols for modules seen in sampled CPU stacks,
and prints SampledProfile stacks. Requires stack collection in the WPR trace.
"""

let args = fsi.CommandLineArgs |> Array.skip 1

let hasFlag name =
    args |> Array.exists (fun arg -> String.Equals(arg, name, StringComparison.OrdinalIgnoreCase))

let optionValue name defaultValue =
    args
    |> Array.tryFindIndex (fun arg -> String.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
    |> Option.bind (fun index -> if index + 1 < args.Length then Some args[index + 1] else None)
    |> Option.defaultValue defaultValue

let optionInt name defaultValue =
    match Int32.TryParse(optionValue name "") with
    | true, value -> value
    | false, _ -> defaultValue

let optionInt64 name defaultValue =
    match Int64.TryParse(optionValue name "") with
    | true, value -> value
    | false, _ -> defaultValue

if args.Length = 0 || hasFlag "--help" || hasFlag "-h" then
    printf "%s" usage
    exit 0

let etlPath = args[0]
let filter = optionValue "--filter" ""
let maxSamples = optionInt "--max-samples" 20
let maxDepth = optionInt "--max-depth" 64
let maxEvents = optionInt64 "--max-events" 0L
let defaultSymbolPath =
    match Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH") with
    | value when not (String.IsNullOrWhiteSpace(value)) -> value
    | _ -> "srv*C:\\Symbols*https://msdl.microsoft.com/download/symbols"
let symbolPath = optionValue "--symbol-path" defaultSymbolPath

if not (File.Exists(etlPath)) then
    failwithf "Trace file not found: %s" etlPath

let matchesFilter (processId: int) (processName: string) =
    String.IsNullOrWhiteSpace(filter)
    || string processId = filter
    || (not (isNull processName) && processName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)

printfn "Creating or opening .etlx index..."
let traceLogOptions = TraceLogOptions()
let traceLog = TraceLog.OpenOrConvert(etlPath, traceLogOptions)

let sampledEvents =
    let events : seq<Microsoft.Diagnostics.Tracing.TraceEvent> = traceLog.Events :> _
    let limitedEvents = if maxEvents > 0L then events |> Seq.truncate (int maxEvents) else events
    let isSampleEvent (name: string) =
        name.IndexOf("SampledProfile", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("PerfInfo/Sample", StringComparison.OrdinalIgnoreCase) >= 0
        || name.Equals("Sample", StringComparison.OrdinalIgnoreCase)
    limitedEvents
    |> Seq.filter (fun event ->
        isSampleEvent event.EventName
        && not (isNull (event.CallStack()))
        && matchesFilter event.ProcessID event.ProcessName
    )
    |> Seq.truncate maxSamples
    |> Seq.toArray

if sampledEvents.Length = 0 then
    printfn "No SampledProfile stacks found. The trace may not include CPU sampling stack collection."
    exit 0

printfn "Resolving symbols with path: %s" symbolPath
let symbolReader = new SymbolReader(Console.Out, symbolPath)
let resolvedModules = HashSet<TraceModuleFile>()

for event in sampledEvents do
    printfn ""
    printfn "Sample %O PID=%d Process=%s" event.TimeStamp event.ProcessID event.ProcessName
    let mutable stack = event.CallStack()
    let mutable depth = 0
    while not (isNull stack) && depth < maxDepth do
        let address = stack.CodeAddress
        if String.IsNullOrWhiteSpace(address.FullMethodName) then
            try
                let moduleFile = address.ModuleFile
                if not (isNull moduleFile) && resolvedModules.Add(moduleFile) then
                    traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, moduleFile)
            with ex ->
                eprintfn "Symbol lookup failed for %s: %s" address.ModuleName ex.Message
        let moduleName = if isNull address.ModuleName then "" else address.ModuleName
        let methodName = if String.IsNullOrWhiteSpace(address.FullMethodName) then address.Address.ToString("x") else address.FullMethodName
        printfn "  %s!%s" moduleName methodName
        stack <- stack.Caller
        depth <- depth + 1
