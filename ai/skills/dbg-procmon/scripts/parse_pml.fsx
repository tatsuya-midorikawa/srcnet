#!/usr/bin/env dotnet fsi

// parse_pml.fsx — Process Monitor Log (PML v9) バイナリパーサー
//
// Usage:
//   dotnet fsi parse_pml.fsx <file.pml> [options]
//
// Options:
//   --processes           プロセス一覧を表示
//   --filter <name>       プロセス名でフィルタ (部分一致、カンマ区切りで複数指定可)
//   --errors              エラーイベントのサマリを表示
//   --lifecycle           プロセスのライフサイクルイベント (Create/Exit/Start) を表示
//   --access-denied       ACCESS DENIED イベントの詳細を表示
//   --all                 すべての解析を実行
//   --limit <n>           サンプルパスの最大表示数 (デフォルト: 5)
//   --stacktrace          イベントのスタックトレースアドレスも収集 (シンボル解決用)
//   --output <file>       結果をファイルに出力
//
// PML v9 フォーマット (Process Monitor v3.x が出力するバイナリ形式) を
// .NET のみで直接解析する。外部ライブラリは不要。

open System
open System.IO
open System.Text
open System.Collections.Generic

// ========== CLI ==========
let args = fsi.CommandLineArgs |> Array.skip 1

if args.Length = 0 || args.[0] = "--help" || args.[0] = "-h" then
    eprintfn "Usage: dotnet fsi parse_pml.fsx <file.pml> [options]"
    eprintfn "Options: --processes --filter <name> --errors --lifecycle --access-denied --all --limit <n> --stacktrace --output <file>"
    exit 1

let pmlPath = args.[0]
if not (File.Exists pmlPath) then
    eprintfn $"Error: File not found: {pmlPath}"
    exit 1

let hasArg name = args |> Array.exists ((=) name)
let getArgValue name =
    let idx = args |> Array.tryFindIndex ((=) name)
    match idx with
    | Some i when i + 1 < args.Length -> Some args.[i + 1]
    | _ -> None

let isAll       = hasArg "--all"
let mutable showProcs   = hasArg "--processes" || isAll
let mutable showErrors  = hasArg "--errors"    || isAll
let mutable showLife    = hasArg "--lifecycle"  || isAll
let mutable showAD      = hasArg "--access-denied" || isAll
let showStack   = hasArg "--stacktrace"
let filterStr   = getArgValue "--filter"
let limitN      = getArgValue "--limit"  |> Option.map int |> Option.defaultValue 5
let outputPath  = getArgValue "--output"

// Default: if nothing specified, show all
if not showProcs && not showErrors && not showLife && not showAD then
    showProcs  <- true
    showErrors <- true
    showLife   <- true
    showAD     <- true

let filters =
    match filterStr with
    | Some s -> s.ToLowerInvariant().Split(',') |> Array.map (fun x -> x.Trim()) |> Array.toList
    | None -> []

// ========== Output Buffer ==========
let out = ResizeArray<string>()
let emit (line: string) = out.Add(line)

// ========== Binary Helpers ==========
let fs = File.Open(pmlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
let fileSize = fs.Length

let readBuf (offset: int64) (length: int) : byte[] =
    let buf = Array.zeroCreate<byte> length
    fs.Seek(offset, SeekOrigin.Begin) |> ignore
    let mutable totalRead = 0
    while totalRead < length do
        let n = fs.Read(buf, totalRead, length - totalRead)
        if n = 0 then failwith $"Unexpected EOF at offset 0x{offset:X} (wanted {length} bytes)"
        totalRead <- totalRead + n
    buf

let u32 (buf: byte[]) off =
    BitConverter.ToUInt32(buf, off)

let u16 (buf: byte[]) off =
    BitConverter.ToUInt16(buf, off)

let u64 (buf: byte[]) off =
    BitConverter.ToUInt64(buf, off)

let utf16 (buf: byte[]) (off: int) (byteLen: int) =
    let sb = StringBuilder()
    let mutable i = 0
    while i < byteLen do
        let c = BitConverter.ToUInt16(buf, off + i)
        if c = 0us then i <- byteLen  // break
        else sb.Append(char c) |> ignore
        i <- i + 2
    sb.ToString()

let filetimeToDate (lo: uint32) (hi: uint32) =
    if lo = 0u && hi = 0u then None
    else
        let ft = (uint64 hi <<< 32) ||| uint64 lo
        try Some (DateTime.FromFileTimeUtc(int64 ft))
        with _ -> None

// ========== Constants ==========
let errorCodes = dict [
    0x00000000u, "SUCCESS"
    0x00000103u, "NO MORE DATA"
    0x00000104u, "REPARSE"
    0x00000105u, "MORE ENTRIES"
    0x00000108u, "OPLOCK BREAK IN PROGRESS"
    0x0000010Bu, "NOTIFY CLEANUP"
    0x0000010Cu, "NOTIFY ENUM DIR"
    0x0000012Au, "FILE LOCKED WITH ONLY READERS"
    0x0000012Bu, "FILE LOCKED WITH WRITERS"
    0x00000367u, "WAIT FOR OPLOCK"
    0x80000005u, "BUFFER OVERFLOW"
    0x80000006u, "NO MORE FILES"
    0x8000001Au, "NO MORE ENTRIES"
    0xC0000001u, "UNSUCCESSFUL"
    0xC0000002u, "NOT IMPLEMENTED"
    0xC0000004u, "INFO LENGTH MISMATCH"
    0xC0000005u, "ACCESS VIOLATION"
    0xC0000008u, "INVALID HANDLE"
    0xC000000Du, "INVALID PARAMETER"
    0xC000000Eu, "NO SUCH DEVICE"
    0xC000000Fu, "NO SUCH FILE"
    0xC0000010u, "INVALID DEVICE REQUEST"
    0xC0000011u, "END OF FILE"
    0xC0000017u, "NO MEMORY"
    0xC0000022u, "ACCESS DENIED"
    0xC0000023u, "BUFFER TOO SMALL"
    0xC0000033u, "NAME INVALID"
    0xC0000034u, "NAME NOT FOUND"
    0xC0000035u, "NAME COLLISION"
    0xC000003Au, "PATH NOT FOUND"
    0xC0000043u, "SHARING VIOLATION"
    0xC0000056u, "DELETE PENDING"
    0xC000007Fu, "DISK FULL"
    0xC0000098u, "FILE INVALID"
    0xC000009Au, "INSUFFICIENT RESOURCES"
    0xC00000BAu, "IS DIRECTORY"
    0xC00000BBu, "NOT SUPPORTED"
    0xC00000D8u, "CANT WAIT"
    0xC0000101u, "NOT EMPTY"
    0xC0000102u, "FILE CORRUPT"
    0xC0000103u, "NOT A DIRECTORY"
    0xC0000120u, "CANCELLED"
    0xC0000121u, "CANNOT DELETE"
    0xC000014Cu, "REGISTRY CORRUPT"
    0xC000017Cu, "KEY DELETED"
    0xC0000225u, "NOT FOUND"
    0xC0000275u, "NOT REPARSE POINT"
    0xC0190001u, "TRANSACTIONAL CONFLICT"
    0xC01C0004u, "FAST IO DISALLOWED"
]

let eventClasses = dict [ 1u, "Process"; 2u, "Registry"; 3u, "FileSystem"; 4u, "Profiling"; 5u, "Network" ]

let processOps = dict [
    0us, "Process_Defined"; 1us, "Process_Create"; 2us, "Process_Exit"
    3us, "Thread_Create"; 4us, "Thread_Exit"; 5us, "Load_Image"
    6us, "Thread_Profile"; 7us, "Process_Start"; 8us, "Process_Statistics"
    9us, "System_Statistics"
]

let registryOps = dict [
    0us, "RegOpenKey"; 1us, "RegCreateKey"; 2us, "RegCloseKey"; 3us, "RegQueryKey"
    4us, "RegSetValue"; 5us, "RegQueryValue"; 6us, "RegEnumValue"; 7us, "RegEnumKey"
    8us, "RegSetInfoKey"; 9us, "RegDeleteKey"; 10us, "RegDeleteValue"
    11us, "RegFlushKey"; 12us, "RegLoadKey"; 13us, "RegUnloadKey"; 14us, "RegRenameKey"
    15us, "RegQueryMultipleValueKey"; 16us, "RegSetKeySecurity"; 17us, "RegQueryKeySecurity"
]

let filesystemOps = dict [
    0us, "VolumeDismount"; 1us, "VolumeMount"
    19us, "CreateFileMapping"; 20us, "CreateFile"; 21us, "CreatePipe"
    22us, "IRP_MJ_CLOSE"; 23us, "ReadFile"; 24us, "WriteFile"
    25us, "QueryInformationFile"; 26us, "SetInformationFile"
    29us, "FlushBuffersFile"; 30us, "QueryVolumeInformation"
    31us, "SetVolumeInformation"; 32us, "DirectoryControl"
    33us, "FileSystemControl"; 34us, "DeviceIoControl"
    37us, "LockUnlockFile"; 38us, "CloseFile"
    40us, "QuerySecurityFile"; 41us, "SetSecurityFile"
]

let networkOps = dict [
    0us, "Unknown"; 1us, "Other"; 2us, "Send"; 3us, "Receive"
    4us, "Accept"; 5us, "Connect"; 6us, "Disconnect"
    7us, "Reconnect"; 8us, "Retransmit"; 9us, "TCPCopy"
]

let getOpName (eventClass: uint32) (operation: uint16) =
    let tryGet (d: IDictionary<uint16, string>) = match d.TryGetValue(operation) with true, v -> Some v | _ -> None
    match eventClass with
    | 1u -> tryGet processOps   |> Option.defaultValue $"ProcOp{operation}"
    | 2u -> tryGet registryOps  |> Option.defaultValue $"RegOp{operation}"
    | 3u -> tryGet filesystemOps|> Option.defaultValue $"FileOp{operation}"
    | 5u -> tryGet networkOps   |> Option.defaultValue $"NetOp{operation}"
    | _  -> $"Op{operation}"

let getErrorName (result: uint32) =
    match errorCodes.TryGetValue(result) with
    | true, v -> v
    | _ -> $"0x{result:X}"

let isNoise (result: uint32) =
    result = 0u || result = 0x103u || result = 0x104u || result = 0x105u ||
    result = 0x80000005u || result = 0x80000006u || result = 0x8000001Au ||
    result = 0x10Bu || result = 0x10Cu || result = 0x108u ||
    result = 0x12Au || result = 0x12Bu

// ========== Process / Module Records ==========
type PmlModule = {
    BaseAddress: uint64; Size: uint32; ImagePath: string
    Version: string; Company: string; Description: string
    Timestamp: uint32
}

type PmlProcess = {
    Pidx: int; Pid: int; ParentPid: int; Session: int; Is64: bool
    Integrity: string; User: string; Name: string; Path: string
    CmdLine: string; Company: string; Version: string; Description: string
    Modules: PmlModule[]
}

// ========== Parse PML Header ==========
let header = readBuf 0L 0x3A8
let signature = Encoding.ASCII.GetString(header, 0, 4)
if signature <> "PML_" then
    eprintfn $"Error: Not a PML file (signature: {signature})"
    exit 1

let version         = u32 header 4
let is64bit         = u32 header 8 <> 0u
let computerName    = utf16 header 0x0C 0x20
let systemRoot      = utf16 header 0x2C 0x208
let numberOfEvents  = u32 header 0x234 |> int
let eventsOffset    = u64 header 0x240 |> int64
let evtOffsArrayOff = u64 header 0x248 |> int64
let procTableOff    = u64 header 0x250 |> int64
let strTableOff     = u64 header 0x258 |> int64
let pvoidSize       = if is64bit then 8 else 4

// ========== Parse Strings Table ==========
let numStrings = readBuf strTableOff 4 |> fun b -> u32 b 0 |> int
let strOffBuf  = readBuf (strTableOff + 4L) (numStrings * 4)
let strings =
    Array.init numStrings (fun i ->
        let off = u32 strOffBuf (i * 4) |> int64
        let szBuf = readBuf (strTableOff + off) 4
        let sz = u32 szBuf 0 |> int
        let dBuf = readBuf (strTableOff + off + 4L) sz
        utf16 dBuf 0 sz
    )

let strAt (idx: uint32) =
    let i = int idx
    if i >= 0 && i < strings.Length then strings.[i] else ""

// ========== Parse Process Table ==========
let numProcesses = readBuf procTableOff 4 |> fun b -> u32 b 0 |> int
let procOffsStart = procTableOff + 4L + int64 (numProcesses * 4)
let procOffBuf = readBuf procOffsStart (numProcesses * 4)

let processMap = Dictionary<int, PmlProcess>()

for i in 0 .. numProcesses - 1 do
    let off  = u32 procOffBuf (i * 4) |> int64
    let absOff = procTableOff + off
    // Read enough for fixed fields + icon indices + pvoid + module count
    let fixedSize = 0x58 + 8 + pvoidSize + 4  // up to NumberOfModules
    let pb = readBuf absOff (max 128 fixedSize)
    let pidx = u32 pb 0x00 |> int

    // Module parsing:
    // After 0x54 (DescriptionIndex): IconSmall(4), IconBig(4), unknown(pvoid), NumberOfModules(4)
    let modulesCountOff = 0x58 + 4 + 4 + pvoidSize  // icon_small + icon_big + unknown_pvoid
    let numModules =
        if modulesCountOff + 4 <= pb.Length then u32 pb modulesCountOff |> int
        else 0

    // Each module: pvoid(unk) + pvoid(base) + u32(size) + u32(pathIdx) + u32(verIdx) + u32(compIdx) + u32(descIdx) + u32(timestamp) + 0x18(unk)
    let moduleRecordSize = pvoidSize + pvoidSize + 4 + 4*4 + 4 + 0x18
    let modulesStartOff = modulesCountOff + 4
    let modulesDataSize = numModules * moduleRecordSize
    let modulesBuf =
        if numModules > 0 then readBuf (absOff + int64 modulesStartOff) modulesDataSize
        else Array.empty

    let modules =
        [| for m in 0 .. numModules - 1 do
            let mOff = m * moduleRecordSize
            let baseAddr =
                if pvoidSize = 8 then u64 modulesBuf (mOff + pvoidSize)
                else uint64 (u32 modulesBuf (mOff + pvoidSize))
            let size = u32 modulesBuf (mOff + pvoidSize * 2)
            let pathIdx = u32 modulesBuf (mOff + pvoidSize * 2 + 4)
            let verIdx  = u32 modulesBuf (mOff + pvoidSize * 2 + 8)
            let compIdx = u32 modulesBuf (mOff + pvoidSize * 2 + 12)
            let descIdx = u32 modulesBuf (mOff + pvoidSize * 2 + 16)
            let timestamp = u32 modulesBuf (mOff + pvoidSize * 2 + 20)
            { BaseAddress = baseAddr; Size = size
              ImagePath = strAt pathIdx; Version = strAt verIdx
              Company = strAt compIdx; Description = strAt descIdx
              Timestamp = timestamp } |]

    processMap.[pidx] <- {
        Pidx      = pidx
        Pid       = u32 pb 0x04 |> int
        ParentPid = u32 pb 0x08 |> int
        Session   = u32 pb 0x18 |> int
        Is64      = u32 pb 0x34 <> 0u
        Integrity = strAt (u32 pb 0x38)
        User      = strAt (u32 pb 0x3C)
        Name      = strAt (u32 pb 0x40)
        Path      = strAt (u32 pb 0x44)
        CmdLine   = strAt (u32 pb 0x48)
        Company   = strAt (u32 pb 0x4C)
        Version   = strAt (u32 pb 0x50)
        Description = strAt (u32 pb 0x54)
        Modules   = modules
    }

// ========== Build Target Process Set ==========
let targetIndices = HashSet<int>()

if filters.IsEmpty then
    for kv in processMap do targetIndices.Add(kv.Key) |> ignore
else
    for kv in processMap do
        let n = kv.Value.Name.ToLowerInvariant()
        let p = kv.Value.Path.ToLowerInvariant()
        if filters |> List.exists (fun f -> n.Contains(f) || p.Contains(f)) then
            targetIndices.Add(kv.Key) |> ignore

// ========== Header Info ==========
emit "=== PML Header ==="
emit $"File: {pmlPath}"
emit $"Size: {float fileSize / 1048576.0:F1} MB"
emit $"Signature: {signature}, Version: {version}, 64-bit: {is64bit}"
emit $"Computer: {computerName}"
emit $"SystemRoot: {systemRoot}"
emit $"Events: {numberOfEvents:N0}"
emit $"Processes: {numProcesses}"
emit $"Strings: {numStrings}"
emit ""

// ========== Process List ==========
if showProcs then
    emit "=== Process List ==="
    let sorted =
        processMap.Values
        |> Seq.filter (fun p -> targetIndices.Contains(p.Pidx))
        |> Seq.sortBy (fun p -> p.Name, p.Pid)

    for p in sorted do
        let bits = if p.Is64 then "64" else "32"
        emit $"  PID:{p.Pid} PPID:{p.ParentPid} Sess:{p.Session} {bits}bit {p.Name}"
        emit $"    Path: {p.Path}"
        if p.User <> "" then emit $"    User: {p.User} [{p.Integrity}]"
        if p.CmdLine <> "" && p.CmdLine <> p.Path then
            emit $"    CmdLine: {p.CmdLine.Substring(0, min 300 p.CmdLine.Length)}"
    emit ""

// ========== Accumulator Types (must be top-level) ==========
type ErrorEntry = {
    mutable Count: int
    ProcName: string; Pid: int; User: string
    EventClass: string; Operation: string; Error: string; Result: uint32
    Samples: ResizeArray<string>
    StackSamples: ResizeArray<string[]>
}

type LifecycleEvent = { Event: int; ProcName: string; Pid: int; Operation: string; Result: uint32 }

// ========== Event Scanning ==========
if showErrors || showLife || showAD then
    emit "=== Scanning Events ==="

    // Event offsets array: 5 bytes per entry (uint32 offset + uint8 flags)
    let evtOffsetsSize = numberOfEvents * 5
    emit $"Reading {numberOfEvents:N0} event offsets ({float evtOffsetsSize / 1048576.0:F1} MB)..."
    let evtOffBuf = readBuf evtOffsArrayOff evtOffsetsSize

    let errorSummary  = Dictionary<string, ErrorEntry>()
    let lifecycleEvts = ResizeArray<LifecycleEvent>()
    let mutable targetEventCount = 0
    let mutable totalErrors = 0

    for i in 0 .. numberOfEvents - 1 do
        let offset = u32 evtOffBuf (i * 5) |> int64
        if offset <> 0L then
            let evtBuf   = readBuf offset 52
            let procIdx  = u32 evtBuf 0x00 |> int

            if targetIndices.Contains(procIdx) then
                targetEventCount <- targetEventCount + 1

                let eventClass = u32 evtBuf 0x08
                let operation  = u16 evtBuf 0x0C
                let result     = u32 evtBuf 0x24
                let stackDepth = u16 evtBuf 0x28 |> int
                let detailsSize= u32 evtBuf 0x2C |> int

                let proc =
                    match processMap.TryGetValue(procIdx) with
                    | true, p -> p
                    | _ -> { Pidx=0; Pid=0; ParentPid=0; Session=0; Is64=false
                             Integrity=""; User=""; Name="?"; Path=""; CmdLine=""
                             Company=""; Version=""; Description=""
                             Modules=Array.empty }

                // Lifecycle
                if showLife && eventClass = 1u && (operation <= 2us || operation = 7us) then
                    let opName = match processOps.TryGetValue(operation) with true, v -> v | _ -> $"op{operation}"
                    lifecycleEvts.Add({ Event=i; ProcName=proc.Name; Pid=proc.Pid; Operation=opName; Result=result })

                // Errors
                if (showErrors || showAD) && not (isNoise result) then
                    totalErrors <- totalErrors + 1
                    let errStr = getErrorName result
                    let ecStr  = match eventClasses.TryGetValue(eventClass) with true, v -> v | _ -> $"Class{eventClass}"
                    let opStr  = getOpName eventClass operation
                    let key    = $"{proc.Name}|{ecStr}|{opStr}|{errStr}"

                    let entry =
                        match errorSummary.TryGetValue(key) with
                        | true, e -> e
                        | _ ->
                            let e : ErrorEntry =
                                { Count=0; ProcName=proc.Name; Pid=proc.Pid; User=proc.User
                                  EventClass=ecStr; Operation=opStr; Error=errStr; Result=result
                                  Samples=ResizeArray(); StackSamples=ResizeArray() }
                            errorSummary.[key] <- e
                            e
                    entry.Count <- entry.Count + 1

                    // Sample paths
                    if entry.Samples.Count < limitN then
                        let detailOffset = offset + 52L + int64 (stackDepth * pvoidSize)
                        try
                            let readLen = min detailsSize 4096
                            let detailBuf = readBuf detailOffset readLen
                            let mutable pathStr = ""

                            if eventClass = 2u && detailBuf.Length >= 2 then // Registry
                                let pathInfo  = u16 detailBuf 0
                                let isAscii   = (pathInfo >>> 15) = 1us
                                let charCount = int (pathInfo &&& 0x7FFFus)
                                let start = if operation = 0us || operation = 1us then 8 else 2
                                if isAscii && start + charCount <= detailBuf.Length then
                                    pathStr <- Encoding.ASCII.GetString(detailBuf, start, charCount)
                                elif not isAscii && start + charCount * 2 <= detailBuf.Length then
                                    pathStr <- utf16 detailBuf start (charCount * 2)

                            elif eventClass = 3u then // FileSystem
                                let detailIoSize = pvoidSize * 5 + 0x14
                                let pathInfoOff  = 4 + detailIoSize
                                if pathInfoOff + 2 <= detailBuf.Length then
                                    let pathInfo  = u16 detailBuf pathInfoOff
                                    let isAscii   = (pathInfo >>> 15) = 1us
                                    let charCount = int (pathInfo &&& 0x7FFFus)
                                    let start = pathInfoOff + 4
                                    if isAscii && start + charCount <= detailBuf.Length then
                                        pathStr <- Encoding.ASCII.GetString(detailBuf, start, charCount)
                                    elif not isAscii && start + charCount * 2 <= detailBuf.Length then
                                        pathStr <- utf16 detailBuf start (charCount * 2)

                            if pathStr.Length > 2 then
                                entry.Samples.Add(pathStr.Substring(0, min 300 pathStr.Length))
                        with _ -> ()

                    // Stack addresses
                    if showStack && entry.StackSamples.Count < 2 && stackDepth > 0 then
                        let stackOff = offset + 52L
                        let stackBuf = readBuf stackOff (stackDepth * pvoidSize)
                        let addrs =
                            [| for s in 0 .. stackDepth - 1 do
                                if pvoidSize = 8 then
                                    $"0x{u64 stackBuf (s * 8):x}"
                                else
                                    $"0x{u32 stackBuf (s * 4):x}" |]
                        entry.StackSamples.Add(addrs)

    emit $"Target events: {targetEventCount:N0}"
    emit $"Total errors (excl. noise): {totalErrors:N0}"
    emit ""

    // Lifecycle
    if showLife && lifecycleEvts.Count > 0 then
        emit "=== Process Lifecycle ==="
        for evt in lifecycleEvts do
            emit $"  {evt.Operation}: {evt.ProcName} PID:{evt.Pid} result={getErrorName evt.Result}"
        emit ""

    // Error summary
    if showErrors then
        let sorted = errorSummary.Values |> Seq.sortByDescending (fun e -> e.Count) |> Seq.truncate 80 |> Seq.toArray
        emit "=== Error Summary (Top 80) ==="
        for e in sorted do
            emit $"  [{e.Count}x] {e.ProcName} PID:{e.Pid} {e.EventClass}/{e.Operation}: {e.Error}"
            for s in e.Samples |> Seq.truncate 3 do
                emit $"    Path: {s}"
            if showStack then
                for stack in e.StackSamples do
                    let preview = stack |> Array.truncate 6 |> String.concat " → "
                    emit $"    Stack: {preview}"
        emit ""

    // ACCESS DENIED
    if showAD then
        let adEntries =
            errorSummary.Values
            |> Seq.filter (fun e -> e.Error = "ACCESS DENIED")
            |> Seq.sortByDescending (fun e -> e.Count)
            |> Seq.toArray

        if adEntries.Length > 0 then
            emit "=== ACCESS DENIED Details ==="
            for e in adEntries do
                emit $"  {e.ProcName} PID:{e.Pid} User:{e.User} {e.EventClass}/{e.Operation} ({e.Count} times)"
                for s in e.Samples do
                    emit $"    {s}"
            emit ""

// ========== Output ==========
fs.Close()

let result = out |> String.concat Environment.NewLine
match outputPath with
| Some path ->
    File.WriteAllText(path, result, Encoding.UTF8)
    eprintfn $"Output written to: {path}"
| None ->
    Console.OutputEncoding <- Encoding.UTF8
    Console.WriteLine(result)
