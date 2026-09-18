#r "nuget: Microsoft.Diagnostics.Tracing.TraceEvent"

open System
open System.Collections.Generic
open System.IO
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.Tracing.Parsers.Kernel

let usage = """
Usage:
    dotnet fsi ./scripts/cpu-samples.fsx <trace.etl> [--top <n>] [--filter <process-or-pid>] [--max-events <n>]

Aggregates PerfInfo/SampledProfile CPU samples by process.
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
let top = optionInt "--top" 30
let filter = optionValue "--filter" ""
let maxEvents = optionInt64 "--max-events" 0L

if not (File.Exists(etlPath)) then
    failwithf "Trace file not found: %s" etlPath

let samplesByProcess = Dictionary<int, int64>()
let processNames = Dictionary<int, string>()
let mutable totalSamples = 0L

let increment pid =
    let ok, current = samplesByProcess.TryGetValue(pid)
    samplesByProcess[pid] <- if ok then current + 1L else 1L

let processName pid =
    let ok, name = processNames.TryGetValue(pid)
    if ok then name else ""

let matchesFilter pid =
    let name = processName pid
    String.IsNullOrWhiteSpace(filter)
    || string pid = filter
    || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0

let source = new ETWTraceEventSource(etlPath)
let mutable scannedEvents = 0L

source.Dynamic.add_All(fun data ->
    scannedEvents <- scannedEvents + 1L
    if maxEvents > 0L && scannedEvents >= maxEvents then
        source.StopProcessing()
)

source.Kernel.add_ProcessStart(fun data ->
    if not (String.IsNullOrWhiteSpace(data.ProcessName)) then
        processNames[data.ProcessID] <- data.ProcessName
)

source.Kernel.add_ProcessDCStart(fun data ->
    if not (String.IsNullOrWhiteSpace(data.ProcessName)) then
        processNames[data.ProcessID] <- data.ProcessName
)

source.Kernel.add_PerfInfoSample(fun data ->
    if matchesFilter data.ProcessID then
        totalSamples <- totalSamples + 1L
        increment data.ProcessID
)

source.Process() |> ignore

printfn "Scanned events: %d" scannedEvents
printfn "CPU samples: %d" totalSamples

samplesByProcess
|> Seq.sortByDescending (fun kvp -> kvp.Value)
|> Seq.truncate top
|> Seq.iter (fun kvp ->
    printfn "%10d samples  PID=%6d  %s" kvp.Value kvp.Key (processName kvp.Key)
)
