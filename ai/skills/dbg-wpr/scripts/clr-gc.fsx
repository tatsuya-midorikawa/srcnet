#r "nuget: Microsoft.Diagnostics.Tracing.TraceEvent"

open System
open System.IO
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.Tracing.Parsers
open Microsoft.Diagnostics.Tracing.Parsers.Clr

let usage = """
Usage:
    dotnet fsi ./scripts/clr-gc.fsx <trace.etl> [--filter <process-or-pid>] [--max-events <n>]

Prints CLR GC start/stop events when .NET runtime provider data is present.
"""

let args = fsi.CommandLineArgs |> Array.skip 1

let hasFlag name =
    args |> Array.exists (fun arg -> String.Equals(arg, name, StringComparison.OrdinalIgnoreCase))

let optionValue name defaultValue =
    args
    |> Array.tryFindIndex (fun arg -> String.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
    |> Option.bind (fun index -> if index + 1 < args.Length then Some args[index + 1] else None)
    |> Option.defaultValue defaultValue

let optionInt64 name defaultValue =
    match Int64.TryParse(optionValue name "") with
    | true, value -> value
    | false, _ -> defaultValue

if args.Length = 0 || hasFlag "--help" || hasFlag "-h" then
    printf "%s" usage
    exit 0

let etlPath = args[0]
let filter = optionValue "--filter" ""
let maxEvents = optionInt64 "--max-events" 0L

if not (File.Exists(etlPath)) then
    failwithf "Trace file not found: %s" etlPath

let matchesFilter pid =
    String.IsNullOrWhiteSpace(filter) || string pid = filter

let source = new ETWTraceEventSource(etlPath)
let mutable scannedEvents = 0L

source.Dynamic.add_All(fun _ ->
    scannedEvents <- scannedEvents + 1L
    if maxEvents > 0L && scannedEvents >= maxEvents then
        source.StopProcessing()
)

let clr = ClrTraceEventParser(source)

clr.add_GCStart(fun data ->
    if matchesFilter data.ProcessID then
        printfn "GC Start %O PID=%d Reason=%A Type=%A Depth=%d"
            data.TimeStamp data.ProcessID data.Reason data.Type data.Depth
)

clr.add_GCStop(fun data ->
    if matchesFilter data.ProcessID then
        printfn "GC Stop  %O PID=%d" data.TimeStamp data.ProcessID
)

source.Process() |> ignore
printfn "Scanned events: %d" scannedEvents
