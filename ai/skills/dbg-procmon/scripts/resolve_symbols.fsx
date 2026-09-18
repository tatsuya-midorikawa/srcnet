#!/usr/bin/env dotnet fsi

// resolve_symbols.fsx — PML スタックトレースの dbghelp.dll シンボル解決
//
// Usage:
//   dotnet fsi resolve_symbols.fsx <file.pml> [--filter <name>] [--limit <n>]
//
// PML ファイルからプロセスのモジュール情報 (BaseAddress, Size, ImagePath, Timestamp)
// を抽出し、Timestamp (= PE TimeDateStamp) と Size (= SizeOfImage) を使って
// シンボルサーバーから同じビルドの PE をダウンロードし、正しい PDB でシンボル解決する。
//
// dbghelp.dll 検索順:
//   1. %TEMP%\WinDbg.Slow\amd64\dbghelp.dll  (WinDbg Preview)
//   2. %TEMP%\WinDbg\amd64\dbghelp.dll
//   3. Windows SDK Debuggers
//   4. C:\Windows\System32\dbghelp.dll (フォールバック、symsrv.dll が無い場合あり)
//
// シンボルパス:
//   1. _NT_SYMBOL_PATH 環境変数
//   2. srv*%TEMP%\Symbols*https://symweb.azurefd.net (フォールバック)
//
// 仕組み:
//   PML のモジュール → TimeDateStamp + SizeOfImage
//     → SymFindFileInPathW で同じビルドの PE をシンボルサーバーからダウンロード
//     → SymLoadModuleExW でダウンロードした PE をロード
//     → dbghelp が PE のデバッグディレクトリから PDB GUID/Age を読み取り
//     → 正しい PDB を自動ダウンロード
//     → SymFromAddrW でアドレスを関数名に解決

open System
open System.IO
open System.Text
open System.Collections.Generic
open System.Runtime.InteropServices

if not (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) then
    eprintfn "Error: This tool requires Windows (dbghelp.dll)."
    exit 1

// ========== CLI ==========
let cliArgs = fsi.CommandLineArgs |> Array.skip 1

if cliArgs.Length = 0 || cliArgs.[0] = "--help" then
    eprintfn "Usage: dotnet fsi resolve_symbols.fsx <file.pml> [--filter <name>] [--limit <n>]"
    eprintfn ""
    eprintfn "PML のモジュール Timestamp を使い、シンボルサーバーから同じビルドの PE/PDB を"
    eprintfn "ダウンロードしてスタックトレースをシンボル解決します。"
    exit 1

let pmlPath = cliArgs.[0]
if not (File.Exists pmlPath) then eprintfn $"Error: File not found: {pmlPath}"; exit 1

let getArgVal name =
    let idx = cliArgs |> Array.tryFindIndex ((=) name)
    match idx with Some i when i+1 < cliArgs.Length -> Some cliArgs.[i+1] | _ -> None

let filters =
    match getArgVal "--filter" with
    | Some s -> s.ToLowerInvariant().Split(',') |> Array.map (fun x -> x.Trim()) |> Array.toList
    | None -> []
let limitN = getArgVal "--limit" |> Option.map int |> Option.defaultValue 3

// ========== Binary Helpers ==========
let pmlFs = File.Open(pmlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

let readBuf (offset: int64) (length: int) =
    let buf = Array.zeroCreate<byte> length
    pmlFs.Seek(offset, SeekOrigin.Begin) |> ignore
    let mutable n = 0
    while n < length do
        let r = pmlFs.Read(buf, n, length - n)
        if r = 0 then failwith $"EOF at 0x{offset:X}"
        n <- n + r
    buf
let u32 (b: byte[]) o = BitConverter.ToUInt32(b, o)
let u16 (b: byte[]) o = BitConverter.ToUInt16(b, o)
let u64 (b: byte[]) o = BitConverter.ToUInt64(b, o)
let utf16 (b: byte[]) o len =
    let sb = StringBuilder()
    let mutable i = 0
    while i < len do
        let c = BitConverter.ToUInt16(b, o+i)
        if c = 0us then i <- len else sb.Append(char c) |> ignore
        i <- i + 2
    sb.ToString()

// ========== Parse PML ==========
let header = readBuf 0L 0x3A8
if Encoding.ASCII.GetString(header, 0, 4) <> "PML_" then failwith "Not a PML file"
let is64bit    = u32 header 8 <> 0u
let numEvents  = u32 header 0x234 |> int
let evtOffsOff = u64 header 0x248 |> int64
let procTblOff = u64 header 0x250 |> int64
let strTblOff  = u64 header 0x258 |> int64
let pvoidSize  = if is64bit then 8 else 4
eprintfn $"PML: {numEvents:N0} events, 64-bit={is64bit}"

// Strings
let numStrings = readBuf strTblOff 4 |> fun b -> u32 b 0 |> int
let strOffBuf  = readBuf (strTblOff + 4L) (numStrings * 4)
let strings = Array.init numStrings (fun i ->
    let off = u32 strOffBuf (i*4) |> int64
    let sz = readBuf (strTblOff+off) 4 |> fun b -> u32 b 0 |> int
    readBuf (strTblOff+off+4L) sz |> fun d -> utf16 d 0 sz)
let strAt (i: uint32) = if int i < strings.Length then strings.[int i] else ""

// ========== Module / Process Info ==========
type ModuleInfo = {
    BaseAddress: uint64; Size: uint32; ImagePath: string
    Timestamp: uint32  // PE TimeDateStamp — シンボルサーバーからの PE 取得に使用
}

type ProcInfo = { Pidx: int; Pid: int; Name: string; Modules: ModuleInfo[] }

let numProcs = readBuf procTblOff 4 |> fun b -> u32 b 0 |> int
let poStart  = procTblOff + 4L + int64(numProcs * 4)
let poBuf    = readBuf poStart (numProcs * 4)

let processes = ResizeArray<ProcInfo>()
for i in 0..numProcs-1 do
    let off = u32 poBuf (i*4) |> int64
    let absOff = procTblOff + off
    let fixedSize = 0x58 + 4 + 4 + pvoidSize + 4
    let pb = readBuf absOff (max 200 fixedSize)
    let pidx = u32 pb 0x00 |> int
    let pid  = u32 pb 0x04 |> int
    let name = strAt (u32 pb 0x40)
    let modCountOff = 0x58 + 4 + 4 + pvoidSize
    let nMods = if modCountOff+4 <= pb.Length then u32 pb modCountOff |> int else 0
    // Module record: pvoid(unk) + pvoid(base) + u32(size) + u32(pathIdx) + u32(verIdx)
    //                + u32(compIdx) + u32(descIdx) + u32(timestamp) + 0x18(unk)
    let modRecSize = pvoidSize*2 + 4 + 4*4 + 4 + 0x18
    let modsStart = modCountOff + 4
    let modules =
        if nMods > 0 then
            let mBuf = readBuf (absOff + int64 modsStart) (nMods * modRecSize)
            [| for m in 0..nMods-1 do
                let mOff = m * modRecSize
                let ba = if pvoidSize=8 then u64 mBuf (mOff+pvoidSize) else uint64(u32 mBuf (mOff+pvoidSize))
                let sz = u32 mBuf (mOff+pvoidSize*2)
                let pIdx = u32 mBuf (mOff+pvoidSize*2+4)
                let ts   = u32 mBuf (mOff+pvoidSize*2+20) // TimeDateStamp
                { BaseAddress=ba; Size=sz; ImagePath=strAt pIdx; Timestamp=ts } |]
        else Array.empty
    processes.Add({ Pidx=pidx; Pid=pid; Name=name; Modules=modules })

// Filter
let targetProcs =
    if filters.IsEmpty then processes |> Seq.toArray
    else processes |> Seq.filter (fun p -> filters |> List.exists (p.Name.ToLowerInvariant().Contains)) |> Seq.toArray
let targetPidxSet = targetProcs |> Array.map (fun p -> p.Pidx) |> HashSet
eprintfn $"Target processes: {targetProcs.Length}"

// Collect unique modules (by imagePath, keep first occurrence)
let allModules = Dictionary<string, ModuleInfo>()
for p in targetProcs do
    for m in p.Modules do
        let key = m.ImagePath.ToLowerInvariant()
        if not (allModules.ContainsKey key) then allModules.[key] <- m
eprintfn $"Unique modules: {allModules.Count}"

pmlFs.Close()

// ========== dbghelp.dll P/Invoke ==========
module DbgHelp =
    // SetDllDirectory: dbghelp.dll と同じフォルダに symsrv.dll を見つけられるようにする
    [<DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)>]
    extern bool SetDllDirectoryW(string lpPathName)

    let private findPath () =
        let temp = Environment.GetEnvironmentVariable("TEMP")
        [ Path.Combine(temp, @"WinDbg.Slow\amd64\dbghelp.dll")
          Path.Combine(temp, @"WinDbg\amd64\dbghelp.dll")
          @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\dbghelp.dll"
          @"C:\Program Files\Windows Kits\10\Debuggers\x64\dbghelp.dll"
          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dbghelp.dll") ]
        |> List.tryFind File.Exists

    let mutable private loaded = false
    let ensureLoaded () =
        if not loaded then
            match findPath() with
            | Some p ->
                // symsrv.dll を見つけられるよう、dbghelp.dll のフォルダを DLL 検索パスに追加
                let dir = Path.GetDirectoryName(p)
                SetDllDirectoryW(dir) |> ignore
                eprintfn $"DLL search path: {dir}"
                NativeLibrary.Load(p) |> ignore
                eprintfn $"dbghelp.dll: {p}"
            | None ->
                NativeLibrary.Load("dbghelp.dll") |> ignore
                eprintfn "dbghelp.dll: system"
            loaded <- true

    // --- Core symbol APIs ---
    [<DllImport("dbghelp.dll", CharSet=CharSet.Unicode, SetLastError=true)>]
    extern bool SymInitializeW(IntPtr hProcess, string searchPath, bool fInvade)
    [<DllImport("dbghelp.dll", SetLastError=true)>]
    extern bool SymCleanup(IntPtr hProcess)
    [<DllImport("dbghelp.dll", SetLastError=true)>]
    extern uint32 SymSetOptions(uint32 opts)
    [<DllImport("dbghelp.dll", CharSet=CharSet.Unicode, SetLastError=true)>]
    extern uint64 SymLoadModuleExW(IntPtr hProcess, IntPtr hFile, string imageName,
        string moduleName, uint64 baseOfDll, uint32 sizeOfDll, IntPtr data, uint32 flags)
    [<DllImport("dbghelp.dll", SetLastError=true)>]
    extern bool SymUnloadModule64(IntPtr hProcess, uint64 baseOfDll)

    // --- SymFindFileInPathW: PE をシンボルサーバーからダウンロード ---
    // id = &TimeDateStamp, two = SizeOfImage, three = 0, flags = SSRVOPT_DWORDPTR (0x02)
    [<DllImport("dbghelp.dll", CharSet=CharSet.Unicode, SetLastError=true)>]
    extern bool SymFindFileInPathW(IntPtr hProcess, string searchPath, string fileName,
        uint32& id, uint32 two, uint32 three, uint32 flags,
        StringBuilder foundFile, IntPtr callback, IntPtr context)

    // --- SymFromAddrW ---
    [<StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)>]
    type SYMBOL_INFOW =
        struct
            val mutable SizeOfStruct: uint32
            val mutable TypeIndex: uint32
            [<MarshalAs(UnmanagedType.ByValArray, SizeConst=2)>]
            val mutable Reserved: uint64[]
            val mutable Index: uint32
            val mutable Size: uint32
            val mutable ModBase: uint64
            val mutable Flags: uint32
            val mutable Value: uint64
            val mutable Address: uint64
            val mutable Register: uint32
            val mutable Scope: uint32
            val mutable Tag: uint32
            val mutable NameLen: uint32
            val mutable MaxNameLen: uint32
            [<MarshalAs(UnmanagedType.ByValTStr, SizeConst=512)>]
            val mutable Name: string
        end

    [<DllImport("dbghelp.dll", CharSet=CharSet.Unicode, SetLastError=true)>]
    extern bool SymFromAddrW(IntPtr hProcess, uint64 address, uint64& displacement, SYMBOL_INFOW& symbol)

    // --- Options ---
    let SYMOPT_DEFERRED_LOADS   = 0x00000004u
    let SYMOPT_UNDNAME          = 0x00000002u
    let SYMOPT_NO_PROMPTS       = 0x00080000u
    let SYMOPT_FAVOR_COMPRESSED = 0x00800000u

    // SSRVOPT_DWORDPTR: id パラメータが DWORD* (PE TimeDateStamp) であることを示す
    let SSRVOPT_DWORDPTR = 0x00000002u

// ========== VPN / symweb 接続チェック ==========
let symwebUrl = "https://symweb.azurefd.net"

/// symweb.azurefd.net への接続テスト (MSFT-AzVPN-Manual VPN が必要)
let checkSymwebConnectivity () : bool =
    eprintfn $"Checking connectivity to {symwebUrl} ..."
    let client = new System.Net.Http.HttpClient()
    client.Timeout <- TimeSpan.FromSeconds(10.0)
    try
        // HEAD リクエストで接続確認 (401 Unauthorized も「接続できている」とみなす)
        let req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, symwebUrl)
        let resp = client.Send(req)
        let code = int resp.StatusCode
        client.Dispose()
        if code = 200 || code = 301 || code = 302 || code = 401 || code = 403 then
            eprintfn $"  OK: symweb is reachable (HTTP {code})"
            true
        else
            eprintfn $"  NG: symweb returned HTTP {code}"
            false
    with ex ->
        client.Dispose()
        eprintfn $"  NG: Cannot connect to symweb — {ex.GetType().Name}: {ex.Message}"
        eprintfn $""
        eprintfn $"  *** MSFT-AzVPN-Manual (VPN) に接続されていません。***"
        eprintfn $"  *** VPN に接続してから再実行してください。***"
        eprintfn $"  *** シンボル解決フェーズをスキップします。***"
        false

let symwebAvailable = checkSymwebConnectivity()

if not symwebAvailable then
    eprintfn ""
    eprintfn "=== シンボル解決スキップ ==="
    eprintfn "symweb.azurefd.net に接続できないため、スタックトレースのシンボル解決はスキップされました。"
    eprintfn "MSFT-AzVPN-Manual VPN に接続してから再実行してください。"
    Console.OutputEncoding <- Encoding.UTF8
    printfn "=== Resolved Stacks ==="
    printfn ""
    printfn "(symweb.azurefd.net unreachable — VPN not connected. Symbol resolution skipped.)"
    exit 0

// ========== Initialize Symbol Handler ==========
let getSymbolPath () =
    let temp = Environment.GetEnvironmentVariable("TEMP")
    // symweb.azurefd.net のみ使用 (パブリックシンボルサーバーは使用しない)
    let path = $"srv*{temp}\\Symbols*{symwebUrl}"
    eprintfn $"Symbol path: {path}"
    path

DbgHelp.ensureLoaded()
let hProcess = IntPtr(0x1234ABCD)
DbgHelp.SymSetOptions(
    DbgHelp.SYMOPT_DEFERRED_LOADS ||| DbgHelp.SYMOPT_UNDNAME |||
    DbgHelp.SYMOPT_NO_PROMPTS ||| DbgHelp.SYMOPT_FAVOR_COMPRESSED) |> ignore

let symPath = getSymbolPath()
if not (DbgHelp.SymInitializeW(hProcess, symPath, false)) then
    eprintfn $"SymInitialize failed: error {Marshal.GetLastWin32Error()}"; exit 1
eprintfn "Symbol handler initialized"

// ========== PE TimeDateStamp Reader ==========
let readPeTimestamp (path: string) : uint32 option =
    try
        use fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let dosHdr = Array.zeroCreate<byte> 64
        if fs.Read(dosHdr, 0, 64) < 64 then None
        elif BitConverter.ToUInt16(dosHdr, 0) <> 0x5A4Dus then None
        else
            let peOffset = BitConverter.ToInt32(dosHdr, 60)
            fs.Seek(int64 peOffset, SeekOrigin.Begin) |> ignore
            let buf = Array.zeroCreate<byte> 12
            if fs.Read(buf, 0, 12) < 12 then None
            elif BitConverter.ToUInt32(buf, 0) <> 0x00004550u then None
            else Some (BitConverter.ToUInt32(buf, 8))
    with _ -> None

// ========== HTTP PE Downloader ==========
// シンボルサーバーの URL フォーマット: {server}/{filename}/{TimeDateStamp:X8}{SizeOfImage:x}/{filename}
let httpClient = new System.Net.Http.HttpClient()
httpClient.Timeout <- TimeSpan.FromSeconds(30.0)

let symServers = [|
    symwebUrl
|]

let symCacheDir =
    let temp = Environment.GetEnvironmentVariable("TEMP")
    Path.Combine(temp, "Symbols")

/// シンボルサーバーから PE を直接 HTTP ダウンロードしてキャッシュに保存
let downloadPeFromSymServer (fileName: string) (timestamp: uint32) (sizeOfImage: uint32) : string option =
    let subDir = $"{timestamp:X8}{sizeOfImage:x}"
    let cacheDir = Path.Combine(symCacheDir, fileName, subDir)
    let cachePath = Path.Combine(cacheDir, fileName)

    // キャッシュに既にあればそれを返す
    if File.Exists(cachePath) then
        Some cachePath
    else
        let mutable result = None
        for server in symServers do
            if result.IsNone then
                // 非圧縮版を試行
                let url = $"{server}/{fileName}/{subDir}/{fileName}"
                try
                    let resp = httpClient.GetAsync(url).Result
                    if resp.IsSuccessStatusCode then
                        Directory.CreateDirectory(cacheDir) |> ignore
                        use fileStream = File.Create(cachePath)
                        resp.Content.CopyToAsync(fileStream).Wait()
                        result <- Some cachePath
                with _ -> ()

                // 圧縮版 (filename_ = last char replaced with _) を試行
                if result.IsNone then
                    let compName = fileName.Substring(0, fileName.Length - 1) + "_"
                    let compUrl = $"{server}/{fileName}/{subDir}/{compName}"
                    try
                        let resp = httpClient.GetAsync(compUrl).Result
                        if resp.IsSuccessStatusCode then
                            // 圧縮ファイルはそのまま保存（dbghelp/symsrv が展開する場合がある）
                            Directory.CreateDirectory(cacheDir) |> ignore
                            let compPath = Path.Combine(cacheDir, compName)
                            use fileStream = File.Create(compPath)
                            resp.Content.CopyToAsync(fileStream).Wait()
                            // expand を試行（cab 圧縮の場合）
                            try
                                let psi = Diagnostics.ProcessStartInfo("expand", $"\"{compPath}\" \"{cachePath}\"")
                                psi.UseShellExecute <- false
                                psi.CreateNoWindow <- true
                                psi.RedirectStandardOutput <- true
                                psi.RedirectStandardError <- true
                                let p = Diagnostics.Process.Start(psi)
                                p.WaitForExit(10000) |> ignore
                                if File.Exists(cachePath) then
                                    result <- Some cachePath
                                    File.Delete(compPath)
                                else
                                    // expand 失敗時はそのまま使う（非圧縮の場合もある）
                                    File.Move(compPath, cachePath)
                                    result <- Some cachePath
                            with _ ->
                                if File.Exists(compPath) && not (File.Exists(cachePath)) then
                                    File.Move(compPath, cachePath)
                                    result <- Some cachePath
                    with _ -> ()
        result

// ========== Load Modules ==========
eprintfn ""
eprintfn "Loading modules..."

let mutable loadedCount = 0
let mutable downloadedCount = 0
let mutable localMatchCount = 0
let mutable localMismatchCount = 0
let mutable skipCount = 0
let loadedBases = ResizeArray<uint64>()

for kv in allModules do
    let m = kv.Value
    let fileName = Path.GetFileName(m.ImagePath)
    let modName  = Path.GetFileNameWithoutExtension(m.ImagePath)

    // Step 1: SymFindFileInPathW を試行 (symsrv.dll 経由)
    let mutable timestamp = m.Timestamp
    let foundPath = StringBuilder(1024)
    let mutable imagePath = ""
    let mutable method = ""

    let symSrvOk =
        if m.Timestamp <> 0u then
            DbgHelp.SymFindFileInPathW(
                hProcess, symPath, fileName,
                &timestamp, m.Size, 0u, DbgHelp.SSRVOPT_DWORDPTR,
                foundPath, IntPtr.Zero, IntPtr.Zero)
        else false

    if symSrvOk then
        imagePath <- foundPath.ToString()
        method <- "SymSrv"
        downloadedCount <- downloadedCount + 1
    else
        // Step 2: HTTP 直接ダウンロードを試行
        match downloadPeFromSymServer fileName m.Timestamp m.Size with
        | Some path ->
            imagePath <- path
            method <- "HTTP"
            downloadedCount <- downloadedCount + 1
        | None ->
            // Step 3: ローカルファイルにフォールバック
            if File.Exists(m.ImagePath) then
                let localTs = readPeTimestamp m.ImagePath
                match localTs with
                | Some ts when ts = m.Timestamp ->
                    imagePath <- m.ImagePath
                    method <- "LocalMatch"
                    localMatchCount <- localMatchCount + 1
                | Some ts ->
                    imagePath <- m.ImagePath
                    method <- "LocalMismatch"
                    localMismatchCount <- localMismatchCount + 1
                | None ->
                    imagePath <- m.ImagePath
                    method <- "LocalUnknown"
                    localMismatchCount <- localMismatchCount + 1
            else
                imagePath <- m.ImagePath
                method <- "NotFound"
                skipCount <- skipCount + 1

    match method with
    | "SymSrv" | "HTTP" ->
        eprintfn $"  OK: {modName,-25} TS=0x{m.Timestamp:X8} <- {method}: {imagePath}"
    | "LocalMatch" ->
        eprintfn $"  OK: {modName,-25} TS=0x{m.Timestamp:X8} (local match)"
    | "LocalMismatch" ->
        let localTs = readPeTimestamp m.ImagePath |> Option.map (fun t -> $"0x{t:X8}") |> Option.defaultValue "?"
        eprintfn $"  NG: {modName,-25} PML=0x{m.Timestamp:X8} local={localTs} (mismatch)"
    | "LocalUnknown" ->
        eprintfn $"  NG: {modName,-25} TS=0x{m.Timestamp:X8} (cannot verify)"
    | _ ->
        eprintfn $"  --: {modName,-25} TS=0x{m.Timestamp:X8} (not found)"

    // Step 4: SymLoadModuleExW でロード → dbghelp が PDB を自動取得
    let result = DbgHelp.SymLoadModuleExW(
        hProcess, IntPtr.Zero, imagePath, modName, m.BaseAddress, m.Size, IntPtr.Zero, 0u)
    if result <> 0UL then
        loadedCount <- loadedCount + 1
        loadedBases.Add(m.BaseAddress)

eprintfn ""
eprintfn $"Module loading: {loadedCount}/{allModules.Count} loaded"
eprintfn $"  Downloaded (correct build): {downloadedCount}"
eprintfn $"  Local match: {localMatchCount}"
eprintfn $"  Local mismatch (symbols may be wrong): {localMismatchCount}"
eprintfn $"  Not available: {skipCount}"

// ========== Symbol Resolver ==========
let resolve (addr: uint64) =
    let mutable sym = DbgHelp.SYMBOL_INFOW()
    // SizeOfStruct = ネイティブヘッダサイズ (88 bytes, Name フィールドを含まない)
    sym.SizeOfStruct <- 88u
    sym.MaxNameLen <- 512u
    let mutable disp = 0UL
    if DbgHelp.SymFromAddrW(hProcess, addr, &disp, &sym) then
        let modPrefix =
            allModules.Values
            |> Seq.tryFind (fun m -> addr >= m.BaseAddress && addr < m.BaseAddress + uint64 m.Size)
            |> Option.map (fun m -> Path.GetFileNameWithoutExtension(m.ImagePath) + "!")
            |> Option.defaultValue ""
        if disp > 0UL then $"{modPrefix}{sym.Name}+0x{disp:X}"
        else $"{modPrefix}{sym.Name}"
    else
        // 未解決 — せめてモジュール名は表示
        allModules.Values
        |> Seq.tryFind (fun m -> addr >= m.BaseAddress && addr < m.BaseAddress + uint64 m.Size)
        |> Option.map (fun m -> $"{Path.GetFileNameWithoutExtension(m.ImagePath)}!0x{addr - m.BaseAddress:X}")
        |> Option.defaultValue $"0x{addr:X}"

// ========== Scan Events for Error Stacks ==========
eprintfn ""
eprintfn $"Scanning {numEvents:N0} events..."

let pmlFs2 = File.Open(pmlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
let readBuf2 (offset: int64) (length: int) =
    let buf = Array.zeroCreate<byte> length
    pmlFs2.Seek(offset, SeekOrigin.Begin) |> ignore
    let mutable n = 0
    while n < length do
        let r = pmlFs2.Read(buf, n, length - n)
        if r = 0 then failwith $"EOF at 0x{offset:X}"
        n <- n + r
    buf

let evtOffBuf = readBuf2 evtOffsOff (numEvents * 5)

type StackEntry = { ProcName: string; Pid: int; Category: string; Addresses: uint64[] }
let collectedStacks = ResizeArray<StackEntry>()
let stackKeys = HashSet<string>()

let isNoise r =
    r=0u || r=0x103u || r=0x104u || r=0x105u || r=0x80000005u || r=0x80000006u ||
    r=0x8000001Au || r=0x10Bu || r=0x10Cu || r=0x12Au || r=0x12Bu || r=0x108u || r=0x367u

let errorNames = dict [
    0xC0000022u,"ACCESS_DENIED"; 0xC0000034u,"NAME_NOT_FOUND"; 0xC000003Au,"PATH_NOT_FOUND"
    0xC0000043u,"SHARING_VIOLATION"; 0xC01C0004u,"FAST_IO_DISALLOWED"; 0xC0000005u,"ACCESS_VIOLATION"
    0xC000009Au,"INSUFFICIENT_RESOURCES"; 0xC0000225u,"NOT_FOUND"; 0xC000000Du,"INVALID_PARAMETER"
    0xC0000035u,"NAME_COLLISION"; 0xC0000023u,"BUFFER_TOO_SMALL"
    0xC00000BAu,"IS_DIRECTORY"; 0xC0000121u,"CANNOT_DELETE"; 0xC0000120u,"CANCELLED"
    0xC0000011u,"END_OF_FILE"; 0xC00000BBu,"NOT_SUPPORTED" ]
let ecNames = dict [ 1u,"Proc"; 2u,"Reg"; 3u,"FS"; 4u,"Prof"; 5u,"Net" ]
let regOps  = dict [ 0us,"RegOpenKey"; 1us,"RegCreateKey"; 5us,"RegQueryValue" ]
let fsOps   = dict [
    20us,"CreateFile"; 23us,"ReadFile"; 24us,"WriteFile"; 25us,"QueryInfoFile"
    32us,"DirControl"; 33us,"FSControl"; 34us,"DeviceIoCtl"; 37us,"LockFile" ]

for i in 0..numEvents-1 do
    let offset = u32 evtOffBuf (i*5) |> int64
    if offset <> 0L then
        let evtBuf = readBuf2 offset 52
        let procIdx = u32 evtBuf 0x00 |> int
        if targetPidxSet.Contains(procIdx) then
            let eventClass = u32 evtBuf 0x08
            let operation  = u16 evtBuf 0x0C
            let result     = u32 evtBuf 0x24
            let stackDepth = u16 evtBuf 0x28 |> int
            if not (isNoise result) && stackDepth > 0 then
                let errStr = match errorNames.TryGetValue(result) with true,v -> v | _ -> $"0x{result:X}"
                let ecStr  = match ecNames.TryGetValue(eventClass) with true,v -> v | _ -> $"C{eventClass}"
                let opStr  =
                    match eventClass with
                    | 2u -> match regOps.TryGetValue(operation) with true,v -> v | _ -> $"RegOp{operation}"
                    | 3u -> match fsOps.TryGetValue(operation) with true,v -> v | _ -> $"FileOp{operation}"
                    | _  -> $"Op{operation}"
                let proc = targetProcs |> Array.tryFind (fun p -> p.Pidx = procIdx)
                let pName = proc |> Option.map (fun p -> p.Name) |> Option.defaultValue "?"
                let pid   = proc |> Option.map (fun p -> p.Pid) |> Option.defaultValue 0
                let category = $"{ecStr}/{opStr}:{errStr}"

                let stackBuf = readBuf2 (offset + 52L) (stackDepth * pvoidSize)
                let addrs = [| for s in 0..stackDepth-1 do
                                if pvoidSize=8 then u64 stackBuf (s*8)
                                else uint64(u32 stackBuf (s*4)) |]

                let keyAddrs = addrs |> Array.truncate 3 |> Array.map (fun a -> $"{a:X}") |> String.concat ","
                let key = $"{pName}|{category}|{keyAddrs}"
                if not (stackKeys.Contains key) && collectedStacks.Count < limitN * 100 then
                    stackKeys.Add(key) |> ignore
                    collectedStacks.Add({ ProcName=pName; Pid=pid; Category=category; Addresses=addrs })

pmlFs2.Close()
eprintfn $"Collected {collectedStacks.Count} unique stacks"

// ========== Resolve and Print ==========
Console.OutputEncoding <- Encoding.UTF8
printfn "=== Resolved Stacks ==="

let grouped =
    collectedStacks
    |> Seq.groupBy (fun s -> $"{s.ProcName} {s.Category}")
    |> Seq.sortByDescending (fun (_, items) -> items |> Seq.length)

for (key, stacks) in grouped do
    let arr = stacks |> Seq.truncate limitN |> Seq.toArray
    printfn ""
    printfn $"--- {key} ({arr.Length} unique stacks) ---"
    for si in 0..arr.Length-1 do
        let s = arr.[si]
        printfn $"  Stack #{si+1} ({s.ProcName} PID:{s.Pid}):"
        for addr in s.Addresses do
            printfn $"    {resolve addr}"

// ========== Cleanup ==========
for ba in loadedBases do DbgHelp.SymUnloadModule64(hProcess, ba) |> ignore
DbgHelp.SymCleanup(hProcess) |> ignore
eprintfn ""
eprintfn "Done."
