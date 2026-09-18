#r "nuget: Microsoft.Diagnostics.Tracing.TraceEvent"

open System
open System.IO
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.Tracing.Parsers.Kernel

let usage = """
Usage:
    dotnet fsi ./scripts/process-lifecycle.fsx <trace.etl> [--filter <text>] [--include-command-line] [--max-events <n>]

Prints process start/stop events from a WPR .etl trace.
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
let includeCommandLine = hasFlag "--include-command-line"
let maxEvents = optionInt64 "--max-events" 0L

if not (File.Exists(etlPath)) then
    failwithf "Trace file not found: %s" etlPath

let matchesFilter (processName: string) (commandLine: string) =
    String.IsNullOrWhiteSpace(filter)
    || (not (isNull processName) && processName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
    || (not (isNull commandLine) && commandLine.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)

let source = new ETWTraceEventSource(etlPath)
let mutable scannedEvents = 0L

source.Dynamic.add_All(fun _ ->
    scannedEvents <- scannedEvents + 1L
    if maxEvents > 0L && scannedEvents >= maxEvents then
        source.StopProcessing()
)

source.Kernel.add_ProcessStart(fun data ->
    if matchesFilter data.ProcessName data.CommandLine then
        if includeCommandLine then
            printfn "START %O PID=%d PPID=%d Name=%s CommandLine=%s"
                data.TimeStamp data.ProcessID data.ParentID data.ProcessName data.CommandLine
        else
            printfn "START %O PID=%d PPID=%d Name=%s"
                data.TimeStamp data.ProcessID data.ParentID data.ProcessName
)

source.Kernel.add_ProcessDCStart(fun data ->
    if matchesFilter data.ProcessName data.CommandLine then
        if includeCommandLine then
            printfn "DCSTART %O PID=%d PPID=%d Name=%s CommandLine=%s"
                data.TimeStamp data.ProcessID data.ParentID data.ProcessName data.CommandLine
        else
            printfn "DCSTART %O PID=%d PPID=%d Name=%s"
                data.TimeStamp data.ProcessID data.ParentID data.ProcessName
)

source.Kernel.add_ProcessStop(fun data ->
    if matchesFilter data.ProcessName "" then
        printfn "STOP  %O PID=%d Name=%s ExitCode=%d"
            data.TimeStamp data.ProcessID data.ProcessName data.ExitStatus
)

source.Kernel.add_ProcessDCStop(fun data ->
    if matchesFilter data.ProcessName "" then
        printfn "DCSTOP %O PID=%d Name=%s ExitCode=%d"
            data.TimeStamp data.ProcessID data.ProcessName data.ExitStatus
)

source.Process() |> ignore
printfn "Scanned events: %d" scannedEvents
