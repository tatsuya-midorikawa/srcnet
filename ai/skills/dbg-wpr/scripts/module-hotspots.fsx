#r "nuget: Microsoft.Diagnostics.Tracing.TraceEvent"
open System
open System.Collections.Generic
open Microsoft.Diagnostics.Tracing.Etlx

let args = fsi.CommandLineArgs |> Array.skip 1
let etl = args[0]
let pidFilter =
    args |> Array.tryFindIndex (fun a -> a = "--pid")
    |> Option.bind (fun i -> if i+1 < args.Length then Some(int args[i+1]) else None)

let traceLog = TraceLog.OpenOrConvert(etl, TraceLogOptions())

let isSample (n:string) =
    n.IndexOf("PerfInfo/Sample", StringComparison.OrdinalIgnoreCase) >= 0
    || n.IndexOf("SampledProfile", StringComparison.OrdinalIgnoreCase) >= 0

let modCount  = Dictionary<string,int>()  // samples whose stack includes module
let leafCount = Dictionary<string,int>()  // leaf module (innermost frame)
let mutable totalSamples = 0
let mutable filteredSamples = 0

let events : seq<Microsoft.Diagnostics.Tracing.TraceEvent> = traceLog.Events :> _
for ev in events do
    if isSample ev.EventName then
        totalSamples <- totalSamples + 1
        let pidOk = match pidFilter with Some p -> ev.ProcessID = p | None -> true
        if pidOk then
            let cs = ev.CallStack()
            if not (isNull cs) then
                filteredSamples <- filteredSamples + 1
                let seen = HashSet<string>()
                let mutable s = cs
                let mutable isLeaf = true
                while not (isNull s) do
                    let m = s.CodeAddress.ModuleName
                    let m = if isNull m || m = "" then "<unknown>" else m
                    if isLeaf then
                        leafCount[m] <- (if leafCount.ContainsKey m then leafCount[m] else 0) + 1
                        isLeaf <- false
                    if seen.Add m then
                        modCount[m] <- (if modCount.ContainsKey m then modCount[m] else 0) + 1
                    s <- s.Caller

printfn "Total PerfInfo/Sample events: %d" totalSamples
printfn "Filtered (with stack%s): %d"
    (match pidFilter with Some p -> sprintf ", PID=%d" p | None -> "")
    filteredSamples
printfn ""
printfn "Top 40 modules by sample-inclusion (any frame in stack):"
modCount
|> Seq.sortByDescending (fun kv -> kv.Value)
|> Seq.truncate 40
|> Seq.iter (fun kv ->
    let pct = 100.0 * float kv.Value / float (max 1 filteredSamples)
    printfn "  %8d  %6.2f%%  %s" kv.Value pct kv.Key)

printfn ""
printfn "Top 25 LEAF modules (innermost frame = on-CPU):"
leafCount
|> Seq.sortByDescending (fun kv -> kv.Value)
|> Seq.truncate 25
|> Seq.iter (fun kv ->
    let pct = 100.0 * float kv.Value / float (max 1 filteredSamples)
    printfn "  %8d  %6.2f%%  %s" kv.Value pct kv.Key)
