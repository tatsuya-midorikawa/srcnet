#r "nuget: Microsoft.Diagnostics.Tracing.TraceEvent"

open System
open System.Collections.Generic
open System.IO
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.Tracing.Parsers.Kernel

let usage = """
Usage:
    dotnet fsi ./scripts/disk-io.fsx <trace.etl> [--top <n>] [--filter <process-or-pid>] [--max-events <n>]

Aggregates disk read/write transfer sizes by process.
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
let top = optionInt "--top" 20
let filter = optionValue "--filter" ""
let maxEvents = optionInt64 "--max-events" 0L

if not (File.Exists(etlPath)) then
    failwithf "Trace file not found: %s" etlPath

type DiskStats =
    { mutable ReadBytes: int64
      mutable WriteBytes: int64
      mutable ReadCount: int64
      mutable WriteCount: int64 }

let statsByProcess = Dictionary<int, DiskStats>()
let processNames = Dictionary<int, string>()

let statsFor pid =
    let ok, value = statsByProcess.TryGetValue(pid)
    if ok then
        value
    else
        let value = { ReadBytes = 0L; WriteBytes = 0L; ReadCount = 0L; WriteCount = 0L }
        statsByProcess[pid] <- value
        value

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

source.Dynamic.add_All(fun _ ->
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

source.Kernel.add_DiskIORead(fun data ->
    if matchesFilter data.ProcessID then
        let stats = statsFor data.ProcessID
        stats.ReadBytes <- stats.ReadBytes + int64 data.TransferSize
        stats.ReadCount <- stats.ReadCount + 1L
)

source.Kernel.add_DiskIOWrite(fun data ->
    if matchesFilter data.ProcessID then
        let stats = statsFor data.ProcessID
        stats.WriteBytes <- stats.WriteBytes + int64 data.TransferSize
        stats.WriteCount <- stats.WriteCount + 1L
)

source.Process() |> ignore

printfn "Scanned events: %d" scannedEvents

statsByProcess
|> Seq.sortByDescending (fun kvp -> kvp.Value.ReadBytes + kvp.Value.WriteBytes)
|> Seq.truncate top
|> Seq.iter (fun kvp ->
    let stats = kvp.Value
    printfn "PID=%6d  Read=%14d (%8d ops)  Write=%14d (%8d ops)  %s"
        kvp.Key stats.ReadBytes stats.ReadCount stats.WriteBytes stats.WriteCount (processName kvp.Key)
)
