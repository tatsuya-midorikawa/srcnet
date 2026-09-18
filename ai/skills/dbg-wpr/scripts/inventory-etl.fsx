#r "nuget: Microsoft.Diagnostics.Tracing.TraceEvent"

open System
open System.Collections.Generic
open System.IO
open Microsoft.Diagnostics.Tracing

let usage = """
Usage:
    dotnet fsi ./scripts/inventory-etl.fsx <trace.etl> [--top <n>] [--filter <text>] [--csv <file>] [--max-events <n>]

Lists ETW provider/event counts in a WPR .etl trace.
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
let top = optionInt "--top" 100
let filter = optionValue "--filter" ""
let csvPath = optionValue "--csv" ""
let maxEvents = optionInt64 "--max-events" 0L

if not (File.Exists(etlPath)) then
    failwithf "Trace file not found: %s" etlPath

let matchesFilter (text: string) =
    String.IsNullOrWhiteSpace(filter)
    || text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0

let counts = Dictionary<string, int64>(StringComparer.OrdinalIgnoreCase)

let increment key =
    let ok, current = counts.TryGetValue(key)
    counts[key] <- if ok then current + 1L else 1L

let source = new ETWTraceEventSource(etlPath)
let mutable scannedEvents = 0L

source.Dynamic.add_All(fun data ->
    scannedEvents <- scannedEvents + 1L
    let providerName = if isNull data.ProviderName then "" else data.ProviderName
    let eventName = if isNull data.EventName then "" else data.EventName
    let key = sprintf "%s/%s" providerName eventName
    if matchesFilter key then
        increment key
    if maxEvents > 0L && scannedEvents >= maxEvents then
        source.StopProcessing()
)

source.Process() |> ignore

printfn "Scanned events: %d" scannedEvents

let ordered =
    counts
    |> Seq.sortByDescending (fun kvp -> kvp.Value)
    |> Seq.toArray

ordered
|> Seq.truncate top
|> Seq.iter (fun kvp -> printfn "%10d  %s" kvp.Value kvp.Key)

if not (String.IsNullOrWhiteSpace(csvPath)) then
    use writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8)
    writer.WriteLine("Count,Provider,Event")
    for kvp in ordered do
        let separator = kvp.Key.IndexOf("/", StringComparison.Ordinal)
        let provider = if separator >= 0 then kvp.Key.Substring(0, separator) else kvp.Key
        let eventName = if separator >= 0 then kvp.Key.Substring(separator + 1) else ""
        let escape (value: string) = "\"" + value.Replace("\"", "\"\"") + "\""
        writer.WriteLine(sprintf "%d,%s,%s" kvp.Value (escape provider) (escape eventName))
