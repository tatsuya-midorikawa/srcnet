#!/usr/bin/env dotnet fsi

// windbg.fsx — DbgEng COM API を使ったダンプ解析ツール
//
// Usage: dotnet fsi windbg.fsx <file.dmp> [file.run]
//        dotnet fsi windbg.fsx <file.run>
//
// 起動時に .dmp ファイルを開き、WinDbg コマンドの REPL を提供します。
// .run ファイルが指定された場合、そのコマンドを実行した後 REPL に入ります。
// REPL で 'quit' を入力すると終了します。

open System
open System.IO
open System.Runtime.InteropServices

// ========== Platform Check ==========
if not (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) then
    eprintfn "Error: This tool requires Windows (dbgeng.dll)."
    exit 1

// ========== DbgEng Constants ==========
module DbgConst =
    let S_OK = 0
    let DEBUG_END_PASSIVE = 0u
    let DEBUG_OUTCTL_THIS_CLIENT = 1u
    let DEBUG_EXECUTE_DEFAULT = 0u
    let INFINITE = 0xFFFFFFFFu

    // Execution status values
    let DEBUG_STATUS_NO_DEBUGGEE    = 0u
    let DEBUG_STATUS_BREAK          = 6u   // target is stopped

    // Output mask flags
    let DEBUG_OUTPUT_NORMAL            = 0x1u
    let DEBUG_OUTPUT_ERROR             = 0x2u
    let DEBUG_OUTPUT_WARNING           = 0x4u
    let DEBUG_OUTPUT_VERBOSE           = 0x8u
    let DEBUG_OUTPUT_PROMPT            = 0x10u
    let DEBUG_OUTPUT_PROMPT_REGISTERS  = 0x20u
    let DEBUG_OUTPUT_EXTENSION_WARNING = 0x40u
    let DEBUG_OUTPUT_DEBUGGEE          = 0x80u
    let DEBUG_OUTPUT_DEBUGGEE_PROMPT   = 0x100u
    let DEBUG_OUTPUT_SYMBOLS           = 0x200u
    let DEBUG_OUTPUT_ALL               = 0x3FFu

    let IID_IDebugClient  = Guid("27fe5639-8407-4f47-8364-ee118fb08ac8")
    let IID_IDebugControl = Guid("5182e668-105e-416e-ad92-24ef800424ba")
    let IID_IDebugSymbols = Guid("8c31e98c-983a-48a5-9016-6fe5d667a950")

// ========== DbgEng DLL Loading ==========
// %temp%\WinDbg.Slow\amd64 から dbgeng.dll を優先ロードする。
// NativeLibrary.Load で先にロードしておけば、P/Invoke 時にそのハンドルが使われる。
let private dbgEngDir =
    let temp = Environment.GetEnvironmentVariable("TEMP")
    Path.Combine(temp, @"WinDbg.Slow\amd64")

let private dbgEngPath = Path.Combine(dbgEngDir, "dbgeng.dll")

if not (File.Exists(dbgEngPath)) then
    eprintfn "Error: dbgeng.dll not found at: %s" dbgEngPath
    eprintfn "Install WinDbg Preview or place dbgeng.dll in the expected path."
    exit 1

// dbgeng.dll が依存する DLL も同じディレクトリから解決されるよう
// DLL 検索パスにディレクトリを追加
[<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
extern bool AddDllDirectory(string newDirectory)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool SetDefaultDllDirectories(uint32 directoryFlags)

// LOAD_LIBRARY_SEARCH_DEFAULT_DIRS (0x1000) を設定し、AddDllDirectory を有効化
SetDefaultDllDirectories(0x1000u) |> ignore
AddDllDirectory(dbgEngDir) |> ignore

// dbgeng.dll を明示パスからプリロード
let private _dbgEngHandle = NativeLibrary.Load(dbgEngPath)
printfn "[*] Loaded dbgeng.dll from: %s" dbgEngPath

// ========== P/Invoke ==========
[<DllImport("dbgeng.dll")>]
extern int DebugCreate(Guid& iid, nativeint& iface)

// ========== COM Vtable Helper ==========
/// COM vtable の指定スロットからメソッドデリゲートを取得する。
/// slot 0 = QueryInterface, 1 = AddRef, 2 = Release, 3+ = interface methods
module VTable =
    let getMethod<'D when 'D :> Delegate> (comPtr: nativeint) (slot: int) : 'D =
        let vtable = Marshal.ReadIntPtr(comPtr)
        let fnPtr  = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size)
        Marshal.GetDelegateForFunctionPointer<'D>(fnPtr)

// ========== COM Delegate Types ==========
//
// IUnknown (slots 0-2)
type QueryInterfaceDelegate =
    delegate of nativeint * Guid byref * nativeint byref -> int
type ReleaseDelegate =
    delegate of nativeint -> uint32

// ---------- IDebugClient ----------
// Vtable slot = 3 + (method index in interface)
//
// Method index reference (from dbgeng.h IDebugClient declaration order):
//   0  AttachKernel                        16 OpenDumpFile
//   1  GetKernelConnectionOptions          17 WriteDumpFile
//   2  SetKernelConnectionOptions          18 ConnectSession
//   3  StartProcessServer                  19 StartServer
//   4  ConnectProcessServer                20 OutputServers
//   5  DisconnectProcessServer             21 TerminateProcesses
//   6  GetRunningProcessSystemIds          22 DetachProcesses
//   7  GetRunningProcessSystemIdByExe...   23 EndSession
//   8  GetRunningProcessDescription        24 GetExitCode
//   9  AttachProcess                       25 DispatchCallbacks
//  10  CreateProcess                       26 ExitDispatch
//  11  CreateProcessAndAttach              27 CreateClient
//  12  GetProcessOptions                   28 GetInputCallbacks
//  13  AddProcessOptions                   29 SetInputCallbacks
//  14  RemoveProcessOptions                30 GetOutputCallbacks
//  15  SetProcessOptions                   31 SetOutputCallbacks
//                                          32 GetOutputMask
//                                          33 SetOutputMask
//                                          34-44 (other methods)

/// IDebugClient::OpenDumpFile (slot 19 = 3+16)
type OpenDumpFileDelegate =
    delegate of self: nativeint * [<MarshalAs(UnmanagedType.LPStr)>] path: string -> int

/// IDebugClient::EndSession (slot 26 = 3+23)
type EndSessionDelegate =
    delegate of nativeint * uint32 -> int

/// IDebugClient::SetOutputCallbacks (slot 34 = 3+31)
type SetOutputCallbacksDelegate =
    delegate of nativeint * nativeint -> int

/// IDebugClient::SetOutputMask (slot 36 = 3+33)
type SetOutputMaskDelegate =
    delegate of nativeint * uint32 -> int

// ---------- IDebugSymbols ----------
// SetSymbolPath is method index 38 in IDebugSymbols vtable (slot 41 = 3+38)

/// IDebugSymbols::SetSymbolPath (slot 41 = 3+38)
type SetSymbolPathDelegate =
    delegate of self: nativeint * [<MarshalAs(UnmanagedType.LPStr)>] path: string -> int

// ---------- IDebugControl ----------
// Vtable slot = 3 + (method index in interface)
//
// Method index reference (from dbgeng.h IDebugControl declaration order):
//   0  GetInterrupt           32 GetActualProcessorType
//   1  SetInterrupt           33 GetExecutingProcessorType
//   2  GetInterruptTimeout    ...
//   3  SetInterruptTimeout    46 GetExecutionStatus
//   4  GetLogFile             47 SetExecutionStatus
//   5  OpenLogFile            48 GetCodeLevel
//   6  CloseLogFile           49 SetCodeLevel
//   7  GetLogMask             50 GetEngineOptions
//   8  SetLogMask             51 AddEngineOptions
//   9  Input                  52 RemoveEngineOptions
//  10  ReturnInput            53 SetEngineOptions
//  11  Output                 54-59 SystemError/TextMacro/Radix
//  12  OutputVaList           60 Evaluate
//  13  ControlledOutput       61 CoerceValue
//  14  ControlledOutputVaList 62 CoerceValues
//  15  OutputPrompt           63 Execute              ← ★
//  16  OutputPromptVaList     64 ExecuteCommandFile   ← ★
//  17  GetPromptText          65-70 Breakpoints
//  18  OutputCurrentState     71-77 Extensions
//  19  OutputVersionInfo      78-89 EventFilters
//  20  GetNotifyEventHandle   90 WaitForEvent         ← ★
//  21  SetNotifyEventHandle   91 GetLastEventInformation
//  22-31 Asm/Disasm/Stack/Debuggee/Processor info

/// IDebugControl::Execute (slot 66 = 3+63)
type ExecuteDelegate =
    delegate of self: nativeint * outputControl: uint32 * [<MarshalAs(UnmanagedType.LPStr)>] command: string * flags: uint32 -> int

/// IDebugControl::ExecuteCommandFile (slot 67 = 3+64)
type ExecuteCommandFileDelegate =
    delegate of self: nativeint * outputControl: uint32 * [<MarshalAs(UnmanagedType.LPStr)>] commandFile: string * flags: uint32 -> int

/// IDebugControl::GetExecutionStatus (slot 49 = 3+46)
type GetExecutionStatusDelegate =
    delegate of nativeint * nativeint -> int

/// IDebugControl::WaitForEvent (slot 93 = 3+90)
type WaitForEventDelegate =
    delegate of nativeint * uint32 * uint32 -> int

// ========== IDebugOutputCallbacks ==========
// GUID: 4bf58045-d654-4c40-b0af-683090f356dc
// IUnknown を継承し、Output メソッドのみを持つ。
// マネージド実装を CCW (COM-Callable Wrapper) 経由で DbgEng に渡す。

[<Guid("4bf58045-d654-4c40-b0af-683090f356dc")>]
[<InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
[<ComVisible(true)>]
type IDebugOutputCallbacks =
    /// デバッガエンジンからの出力を受け取る
    [<PreserveSig>]
    abstract Output: mask: uint32 * text: nativeint -> int

/// デバッガ出力をコンソールに表示するコールバック実装
[<ComVisible(true)>]
type OutputCapture() =
    interface IDebugOutputCallbacks with
        member _.Output(mask, textPtr) =
            if textPtr <> IntPtr.Zero then
                let text = Marshal.PtrToStringAnsi(textPtr)
                if not (String.IsNullOrEmpty(text)) then
                    if mask &&& DbgConst.DEBUG_OUTPUT_ERROR <> 0u then
                        Console.ForegroundColor <- ConsoleColor.Red
                        Console.Write(text)
                        Console.ResetColor()
                    elif mask &&& DbgConst.DEBUG_OUTPUT_WARNING <> 0u then
                        Console.ForegroundColor <- ConsoleColor.Yellow
                        Console.Write(text)
                        Console.ResetColor()
                    else
                        Console.Write(text)
            DbgConst.S_OK

// ========== DbgEng Client Wrapper ==========
type DbgEngClient() =
    let mutable clientPtr   = IntPtr.Zero
    let mutable controlPtr  = IntPtr.Zero
    let mutable callbackCcw = IntPtr.Zero
    let mutable disposed    = false

    /// DbgEng COM を初期化し、IDebugClient / IDebugControl を取得
    member _.Initialize() =
        // DebugCreate で IDebugClient を生成
        let mutable iid = DbgConst.IID_IDebugClient
        let mutable ptr = IntPtr.Zero
        let hr = DebugCreate(&iid, &ptr)
        if hr <> DbgConst.S_OK then
            failwithf "DebugCreate failed: 0x%08X" hr
        clientPtr <- ptr

        // QueryInterface で IDebugControl を取得
        let qi = VTable.getMethod<QueryInterfaceDelegate> clientPtr 0
        let mutable ctrlIid = DbgConst.IID_IDebugControl
        let mutable ctrlPtr = IntPtr.Zero
        let hr = qi.Invoke(clientPtr, &ctrlIid, &ctrlPtr)
        if hr <> DbgConst.S_OK then
            failwithf "QueryInterface(IDebugControl) failed: 0x%08X" hr
        controlPtr <- ctrlPtr

        // 出力コールバック (CCW) を設定
        let capture = OutputCapture()
        callbackCcw <- Marshal.GetComInterfaceForObject(capture, typeof<IDebugOutputCallbacks>)
        let setCallbacks = VTable.getMethod<SetOutputCallbacksDelegate> clientPtr 34
        let hr = setCallbacks.Invoke(clientPtr, callbackCcw)
        if hr <> DbgConst.S_OK then
            failwithf "SetOutputCallbacks failed: 0x%08X" hr

        // 全種別の出力を受信
        let setMask = VTable.getMethod<SetOutputMaskDelegate> clientPtr 36
        let hr = setMask.Invoke(clientPtr, DbgConst.DEBUG_OUTPUT_ALL)
        if hr <> DbgConst.S_OK then
            eprintfn "Warning: SetOutputMask failed: 0x%08X" hr

        // TTD 拡張パスを設定 (dbgeng.dll と同じディレクトリの TTD/, winext/ を追加)
        let ttdDir = Path.Combine(dbgEngDir, "TTD")
        let winextDir = Path.Combine(dbgEngDir, "winext")
        // TTD 拡張 DLL も同じ DLL 検索パスに追加
        if Directory.Exists(ttdDir) then
            AddDllDirectory(ttdDir) |> ignore
        if Directory.Exists(winextDir) then
            AddDllDirectory(winextDir) |> ignore

        // シンボルパスを設定 (_NT_SYMBOL_PATH が未設定の場合のみフォールバック)
        let ntSymPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH")
        if String.IsNullOrWhiteSpace(ntSymPath) then
            let symCachePath = Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "Symbols")
            let symPath = sprintf "srv*%s*https://symweb.azurefd.net" symCachePath
            let qi2 = VTable.getMethod<QueryInterfaceDelegate> clientPtr 0
            let mutable symIid = DbgConst.IID_IDebugSymbols
            let mutable symPtr = IntPtr.Zero
            let hr = qi2.Invoke(clientPtr, &symIid, &symPtr)
            if hr = DbgConst.S_OK then
                let setSymPath = VTable.getMethod<SetSymbolPathDelegate> symPtr 41
                let hr = setSymPath.Invoke(symPtr, symPath)
                if hr <> DbgConst.S_OK then
                    eprintfn "Warning: SetSymbolPath failed: 0x%08X" hr
                else
                    printfn "[*] Symbol path (fallback): %s" symPath
                let relSym = VTable.getMethod<ReleaseDelegate> symPtr 2
                relSym.Invoke(symPtr) |> ignore
            else
                eprintfn "Warning: QueryInterface(IDebugSymbols) failed: 0x%08X" hr
        else
            printfn "[*] Symbol path (_NT_SYMBOL_PATH): %s" ntSymPath

    /// ダンプファイルを開き、読み込み完了を待機
    member this.OpenDump(path: string) =
        let fullPath = Path.GetFullPath(path)
        let openDump = VTable.getMethod<OpenDumpFileDelegate> clientPtr 19
        let hr = openDump.Invoke(clientPtr, fullPath)
        if hr <> DbgConst.S_OK then
            failwithf "OpenDumpFile failed: 0x%08X (path: %s)" hr fullPath

        // ダンプ読み込み完了を待機
        let wait = VTable.getMethod<WaitForEventDelegate> controlPtr 93
        let hr = wait.Invoke(controlPtr, 0u, DbgConst.INFINITE)
        if hr <> DbgConst.S_OK then
            failwithf "WaitForEvent failed after OpenDumpFile: 0x%08X" hr

        // TTD 拡張パスを .extpath で設定 (ダンプオープン後でないと機能しない)
        let ttdDir = Path.Combine(dbgEngDir, "TTD")
        let winextDir = Path.Combine(dbgEngDir, "winext")
        if Directory.Exists(ttdDir) || Directory.Exists(winextDir) then
            let extpath = sprintf ".extpath+ %s;%s" ttdDir winextDir
            this.ExecuteQuiet(extpath)
            printfn "[*] Extension path: %s; %s" ttdDir winextDir

    /// WinDbg コマンドを実行 (出力なし、内部用)
    member _.ExecuteQuiet(command: string) =
        let exec = VTable.getMethod<ExecuteDelegate> controlPtr 66
        exec.Invoke(controlPtr, DbgConst.DEBUG_OUTCTL_THIS_CLIENT,
                    command, DbgConst.DEBUG_EXECUTE_DEFAULT) |> ignore

    /// 実行ステータスを確認し、ターゲットが実行中なら Break まで待機
    member _.DrainEvents() =
        let statusBuf = Marshal.AllocHGlobal(4)
        try
            let getStatus = VTable.getMethod<GetExecutionStatusDelegate> controlPtr 49
            let hr = getStatus.Invoke(controlPtr, statusBuf)
            if hr = DbgConst.S_OK then
                let status = Marshal.ReadInt32(statusBuf) |> uint32
                // BREAK (6) でもNO_DEBUGGEE (0) でもない場合、イベントを待機
                if status <> DbgConst.DEBUG_STATUS_BREAK && status <> DbgConst.DEBUG_STATUS_NO_DEBUGGEE then
                    let wait = VTable.getMethod<WaitForEventDelegate> controlPtr 93
                    wait.Invoke(controlPtr, 0u, DbgConst.INFINITE) |> ignore
        finally
            Marshal.FreeHGlobal(statusBuf)

    /// WinDbg コマンドを実行
    member this.Execute(command: string) =
        let exec = VTable.getMethod<ExecuteDelegate> controlPtr 66
        let hr = exec.Invoke(controlPtr, DbgConst.DEBUG_OUTCTL_THIS_CLIENT,
                             command, DbgConst.DEBUG_EXECUTE_DEFAULT)
        if hr <> DbgConst.S_OK then
            eprintfn "Command failed (0x%08X): %s" hr command
        // Execute 後、ターゲットが実行中 (TTD 位置変更等) なら Break まで待機
        this.DrainEvents()

    /// コマンドファイル (.run) を実行
    member _.ExecuteCommandFile(path: string) =
        let fullPath = Path.GetFullPath(path)
        let execFile = VTable.getMethod<ExecuteCommandFileDelegate> controlPtr 67
        let hr = execFile.Invoke(controlPtr, DbgConst.DEBUG_OUTCTL_THIS_CLIENT,
                                 fullPath, DbgConst.DEBUG_EXECUTE_DEFAULT)
        if hr <> DbgConst.S_OK then
            eprintfn "ExecuteCommandFile failed (0x%08X): %s" hr fullPath

    /// .run / .wds ファイルを1行ずつ読み込んで実行 (コメント・空行をスキップ)
    /// q / quit コマンドはセッション終了を防ぐためスキップする
    member this.ExecuteScript(path: string) =
        let lines = File.ReadAllLines(path)
        for line in lines do
            let trimmed = line.Trim()
            // 空行・コメント (*で始まる行、$$で始まる行) をスキップ
            if not (String.IsNullOrEmpty(trimmed))
               && not (trimmed.StartsWith("*"))
               && not (trimmed.StartsWith("$$")) then
                // q / quit はスクリプト内では無視 (セッション終了を防止)
                let lower = trimmed.ToLowerInvariant()
                if lower = "q" || lower = "quit" then
                    printfn ">>> %s  [skipped — use REPL to quit]" trimmed
                else
                    printfn ">>> %s" trimmed
                    this.Execute(trimmed)

    /// COM リソースを解放
    member _.Dispose() =
        if not disposed then
            disposed <- true
            if callbackCcw <> IntPtr.Zero then
                Marshal.Release(callbackCcw) |> ignore
                callbackCcw <- IntPtr.Zero
            if controlPtr <> IntPtr.Zero then
                let relCtrl = VTable.getMethod<ReleaseDelegate> controlPtr 2
                relCtrl.Invoke(controlPtr) |> ignore
                controlPtr <- IntPtr.Zero
            if clientPtr <> IntPtr.Zero then
                let endSess = VTable.getMethod<EndSessionDelegate> clientPtr 26
                endSess.Invoke(clientPtr, DbgConst.DEBUG_END_PASSIVE) |> ignore
                let relClient = VTable.getMethod<ReleaseDelegate> clientPtr 2
                relClient.Invoke(clientPtr) |> ignore
                clientPtr <- IntPtr.Zero

    interface IDisposable with
        member this.Dispose() = this.Dispose()

// ========== Entry Point ==========
let args = fsi.CommandLineArgs |> Array.tail // 先頭のスクリプト名を除外

if args.Length = 0 then
    eprintfn "Usage: dotnet fsi windbg.fsx <dump> [script.wds]"
    eprintfn "       dotnet fsi windbg.fsx <script.wds>"
    eprintfn ""
    eprintfn "Arguments:"
    eprintfn "  dump       ダンプ/TTDトレース (.dmp/.run 等)"
    eprintfn "  script     デバッガスクリプト (.wds)"
    eprintfn ""
    eprintfn "REPL Commands:"
    eprintfn "  <any>      WinDbg コマンドを実行"
    eprintfn "  quit / q   終了"
    exit 1

// .run ファイルがバイナリ (TTD トレース/ダンプ) かテキスト (スクリプト) か判定
let private isBinaryFile (path: string) =
    try
        use fs = new FileStream(path, FileMode.Open, FileAccess.Read)
        let buf = Array.zeroCreate<byte> 64
        let bytesRead = fs.Read(buf, 0, buf.Length)
        // NULL バイトが含まれていればバイナリ
        buf |> Array.take bytesRead |> Array.exists (fun b -> b = 0uy)
    with _ -> false

// 引数をファイル種別ごとに分類
let mutable dmpFile: string option = None
let mutable scriptFile: string option = None

for arg in args do
    if not (File.Exists(arg)) then
        eprintfn "Error: File not found: %s" arg
        exit 1
    let ext = Path.GetExtension(arg).ToLowerInvariant()
    match ext with
    | ".dmp" | ".mdmp" | ".hdmp" | ".kdmp" ->
        dmpFile <- Some arg
    | ".wds" ->
        // .wds は常にスクリプト
        scriptFile <- Some arg
    | ".run" ->
        // .run はバイナリならダンプ (TTD トレース)、テキストならスクリプト
        if isBinaryFile arg then
            dmpFile <- Some arg
        else
            scriptFile <- Some arg
    | _ ->
        // 拡張子不明の場合はバイナリ判定
        if isBinaryFile arg then
            dmpFile <- Some arg
        else
            scriptFile <- Some arg

// DbgEng 初期化
let client = new DbgEngClient()
client.Initialize()

// ダンプファイルを開く
match dmpFile with
| Some path ->
    printfn "[*] Opening dump: %s" path
    client.OpenDump(path)
    printfn "[*] Dump loaded."

    // 初期化コマンド: ソース行情報無効化、エイリアス設定、mex 拡張ロード
    printfn "[*] Running initialization commands..."
    client.ExecuteQuiet(".lines -d")
    client.ExecuteQuiet("l-t")
    client.ExecuteQuiet("aS dv dv /V /i /t")
    client.Execute(".load mex")
    printfn ""
| None -> ()

// スクリプトファイルを実行
match scriptFile with
| Some path ->
    printfn "[*] Running script: %s" path
    client.ExecuteScript(path)
    printfn ""
| None -> ()

// REPL ループ
printfn "========================================"
printfn " DbgEng REPL — WinDbg Command Console"
printfn " Type 'quit' to exit"
printfn "========================================"
printfn ""

let mutable running = true
while running do
    printf "dbg> "
    Console.Out.Flush()
    match Console.ReadLine() with
    | null | "quit" | "q" ->
        running <- false
    | cmd when String.IsNullOrWhiteSpace(cmd) ->
        () // 空行はスキップ
    | cmd ->
        try
            client.Execute(cmd.Trim())
        with ex ->
            eprintfn "Error: %s" ex.Message
        printfn ""

printfn "Goodbye."
client.Dispose()
