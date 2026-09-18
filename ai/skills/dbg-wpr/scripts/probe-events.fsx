#r "nuget: Microsoft.Diagnostics.Tracing.TraceEvent"
open System
open System.Collections.Generic
open Microsoft.Diagnostics.Tracing.Etlx

let args = fsi.CommandLineArgs |> Array.skip 1
let etl = args[0]
let traceLog = TraceLog.OpenOrConvert(etl, TraceLogOptions())
printfn "Loaded etlx. EventCount=%d" traceLog.EventCount

let counts = Dictionary<string,int>()
let withStack = Dictionary<string,int>()
let events : seq<Microsoft.Diagnostics.Tracing.TraceEvent> = traceLog.Events :> _
let mutable n = 0
for ev in events do
    n <- n + 1
    let name = ev.EventName
    counts[name] <- (if counts.ContainsKey name then counts[name] else 0) + 1
    if not (isNull (ev.CallStack())) then
        withStack[name] <- (if withStack.ContainsKey name then withStack[name] else 0) + 1
    if n % 5000000 = 0 then eprintfn "scanned=%d" n

printfn "Scanned=%d" n
printfn ""
printfn "Top 30 events:"
counts |> Seq.sortByDescending (fun kv -> kv.Value) |> Seq.truncate 30
|> Seq.iter (fun kv -> printfn "  %10d  %s" kv.Value kv.Key)

printfn ""
printfn "Top 20 events WITH stack:"
withStack |> Seq.sortByDescending (fun kv -> kv.Value) |> Seq.truncate 20
|> Seq.iter (fun kv -> printfn "  %10d  %s" kv.Value kv.Key)
