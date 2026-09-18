# DbgEng COM API リファレンス

windbg.fsx が使用する DbgEng COM インターフェースの詳細。

## インターフェース概要

| Interface | GUID | 用途 |
|-----------|------|------|
| IDebugClient | `27fe5639-8407-4f47-8364-ee118fb08ac8` | デバッグセッション管理、ダンプファイルの読み込み |
| IDebugControl | `5182e668-105e-416e-ad92-24ef800424ba` | コマンド実行、イベント待機、実行制御 |
| IDebugOutputCallbacks | `4bf58045-d654-4c40-b0af-683090f356dc` | デバッガ出力のキャプチャ (コールバック) |

## IDebugClient vtable レイアウト

IUnknown (slots 0-2) に続く IDebugClient 固有メソッド (slot 3+):

| Slot | Index | Method | Signature |
|------|-------|--------|-----------|
| 3 | 0 | AttachKernel | `(ULONG flags, PCSTR options) → HRESULT` |
| 4 | 1 | GetKernelConnectionOptions | `(PSTR buf, ULONG size, PULONG optSize) → HRESULT` |
| 5 | 2 | SetKernelConnectionOptions | `(PCSTR options) → HRESULT` |
| 6 | 3 | StartProcessServer | `(ULONG flags, PCSTR options, PVOID reserved) → HRESULT` |
| 7 | 4 | ConnectProcessServer | `(PCSTR remote, PULONG64 server) → HRESULT` |
| 8 | 5 | DisconnectProcessServer | `(ULONG64 server) → HRESULT` |
| 9 | 6 | GetRunningProcessSystemIds | `(ULONG64 server, PULONG ids, ULONG count, PULONG actual) → HRESULT` |
| 10 | 7 | GetRunningProcessSystemIdByExecutableName | `(ULONG64 server, PCSTR exe, ULONG flags, PULONG id) → HRESULT` |
| 11 | 8 | GetRunningProcessDescription | `(ULONG64 server, ULONG sysId, ULONG flags, ...) → HRESULT` |
| 12 | 9 | AttachProcess | `(ULONG64 server, ULONG pid, ULONG flags) → HRESULT` |
| 13 | 10 | CreateProcess | `(ULONG64 server, PSTR cmdline, ULONG flags) → HRESULT` |
| 14 | 11 | CreateProcessAndAttach | `(ULONG64 server, PSTR cmdline, ULONG createFlags, ULONG pid, ULONG attachFlags) → HRESULT` |
| 15 | 12 | GetProcessOptions | `(PULONG options) → HRESULT` |
| 16 | 13 | AddProcessOptions | `(ULONG options) → HRESULT` |
| 17 | 14 | RemoveProcessOptions | `(ULONG options) → HRESULT` |
| 18 | 15 | SetProcessOptions | `(ULONG options) → HRESULT` |
| **19** | **16** | **OpenDumpFile** | **`(PCSTR filename) → HRESULT`** |
| 20 | 17 | WriteDumpFile | `(PCSTR filename, ULONG qualifier) → HRESULT` |
| 21 | 18 | ConnectSession | `(ULONG flags, ULONG historyLimit) → HRESULT` |
| 22 | 19 | StartServer | `(PCSTR options) → HRESULT` |
| 23 | 20 | OutputServers | `(ULONG outCtrl, PCSTR machine, ULONG flags) → HRESULT` |
| 24 | 21 | TerminateProcesses | `() → HRESULT` |
| 25 | 22 | DetachProcesses | `() → HRESULT` |
| **26** | **23** | **EndSession** | **`(ULONG flags) → HRESULT`** |
| 27 | 24 | GetExitCode | `(PULONG code) → HRESULT` |
| 28 | 25 | DispatchCallbacks | `(ULONG timeout) → HRESULT` |
| 29 | 26 | ExitDispatch | `(IDebugClient* client) → HRESULT` |
| 30 | 27 | CreateClient | `(IDebugClient** client) → HRESULT` |
| 31 | 28 | GetInputCallbacks | `(IDebugInputCallbacks** cb) → HRESULT` |
| 32 | 29 | SetInputCallbacks | `(IDebugInputCallbacks* cb) → HRESULT` |
| 33 | 30 | GetOutputCallbacks | `(IDebugOutputCallbacks** cb) → HRESULT` |
| **34** | **31** | **SetOutputCallbacks** | **`(IDebugOutputCallbacks* cb) → HRESULT`** |
| 35 | 32 | GetOutputMask | `(PULONG mask) → HRESULT` |
| **36** | **33** | **SetOutputMask** | **`(ULONG mask) → HRESULT`** |
| 37 | 34 | GetOtherOutputMask | `(IDebugClient* client, PULONG mask) → HRESULT` |
| 38 | 35 | SetOtherOutputMask | `(IDebugClient* client, ULONG mask) → HRESULT` |
| 39 | 36 | GetOutputWidth | `(PULONG columns) → HRESULT` |
| 40 | 37 | SetOutputWidth | `(ULONG columns) → HRESULT` |
| 41 | 38 | GetOutputLinePrefix | `(PSTR buf, ULONG size, PULONG prefixSize) → HRESULT` |
| 42 | 39 | SetOutputLinePrefix | `(PCSTR prefix) → HRESULT` |
| 43 | 40 | GetIdentity | `(PSTR buf, ULONG size, PULONG identitySize) → HRESULT` |
| 44 | 41 | OutputIdentity | `(ULONG outCtrl, ULONG flags, PCSTR format) → HRESULT` |
| 45 | 42 | GetEventCallbacks | `(IDebugEventCallbacks** cb) → HRESULT` |
| 46 | 43 | SetEventCallbacks | `(IDebugEventCallbacks* cb) → HRESULT` |
| 47 | 44 | FlushCallbacks | `() → HRESULT` |

## IDebugControl vtable レイアウト

IUnknown (slots 0-2) に続く IDebugControl 固有メソッド (slot 3+):

| Slot | Index | Method |
|------|-------|--------|
| 3 | 0 | GetInterrupt |
| 4 | 1 | SetInterrupt |
| 5 | 2 | GetInterruptTimeout |
| 6 | 3 | SetInterruptTimeout |
| 7 | 4 | GetLogFile |
| 8 | 5 | OpenLogFile |
| 9 | 6 | CloseLogFile |
| 10 | 7 | GetLogMask |
| 11 | 8 | SetLogMask |
| 12 | 9 | Input |
| 13 | 10 | ReturnInput |
| 14 | 11 | Output (variadic) |
| 15 | 12 | OutputVaList |
| 16 | 13 | ControlledOutput (variadic) |
| 17 | 14 | ControlledOutputVaList |
| 18 | 15 | OutputPrompt (variadic) |
| 19 | 16 | OutputPromptVaList |
| 20 | 17 | GetPromptText |
| 21 | 18 | OutputCurrentState |
| 22 | 19 | OutputVersionInformation |
| 23 | 20 | GetNotifyEventHandle |
| 24 | 21 | SetNotifyEventHandle |
| 25 | 22 | Assemble |
| 26 | 23 | Disassemble |
| 27 | 24 | GetDisassembleEffectiveOffset |
| 28 | 25 | OutputDisassembly |
| 29 | 26 | OutputDisassemblyLines |
| 30 | 27 | GetNearInstruction |
| 31 | 28 | GetStackTrace |
| 32 | 29 | GetReturnOffset |
| 33 | 30 | OutputStackTrace |
| 34 | 31 | GetDebuggeeType |
| 35 | 32 | GetActualProcessorType |
| 36 | 33 | GetExecutingProcessorType |
| 37 | 34 | GetNumberPossibleExecutingProcessorTypes |
| 38 | 35 | GetPossibleExecutingProcessorTypes |
| 39 | 36 | GetNumberProcessors |
| 40 | 37 | GetSystemVersion |
| 41 | 38 | GetPageSize |
| 42 | 39 | IsPointer64Bit |
| 43 | 40 | ReadBugCheckData |
| 44 | 41 | GetNumberSupportedProcessorTypes |
| 45 | 42 | GetSupportedProcessorTypes |
| 46 | 43 | GetProcessorTypeNames |
| 47 | 44 | GetEffectiveProcessorType |
| 48 | 45 | SetEffectiveProcessorType |
| 49 | 46 | GetExecutionStatus |
| 50 | 47 | SetExecutionStatus |
| 51 | 48 | GetCodeLevel |
| 52 | 49 | SetCodeLevel |
| 53 | 50 | GetEngineOptions |
| 54 | 51 | AddEngineOptions |
| 55 | 52 | RemoveEngineOptions |
| 56 | 53 | SetEngineOptions |
| 57 | 54 | GetSystemErrorControl |
| 58 | 55 | SetSystemErrorControl |
| 59 | 56 | GetTextMacro |
| 60 | 57 | SetTextMacro |
| 61 | 58 | GetRadix |
| 62 | 59 | SetRadix |
| 63 | 60 | Evaluate |
| 64 | 61 | CoerceValue |
| 65 | 62 | CoerceValues |
| **66** | **63** | **Execute** |
| **67** | **64** | **ExecuteCommandFile** |
| 68 | 65 | GetNumberBreakpoints |
| 69 | 66 | GetBreakpointByIndex |
| 70 | 67 | GetBreakpointById |
| 71 | 68 | GetBreakpointParameters |
| 72 | 69 | AddBreakpoint |
| 73 | 70 | RemoveBreakpoint |
| 74 | 71 | AddExtension |
| 75 | 72 | RemoveExtension |
| 76 | 73 | GetExtensionByPath |
| 77 | 74 | CallExtension |
| 78 | 75 | GetExtensionFunction |
| 79 | 76 | GetWindbgExtensionApis32 |
| 80 | 77 | GetWindbgExtensionApis64 |
| 81 | 78 | GetNumberEventFilters |
| 82 | 79 | GetEventFilterText |
| 83 | 80 | GetEventFilterCommand |
| 84 | 81 | SetEventFilterCommand |
| 85 | 82 | GetSpecificFilterParameters |
| 86 | 83 | SetSpecificFilterParameters |
| 87 | 84 | GetSpecificFilterArgument |
| 88 | 85 | SetSpecificFilterArgument |
| 89 | 86 | GetExceptionFilterParameters |
| 90 | 87 | SetExceptionFilterParameters |
| 91 | 88 | GetExceptionFilterSecondCommand |
| 92 | 89 | SetExceptionFilterSecondCommand |
| **93** | **90** | **WaitForEvent** |
| 94 | 91 | GetLastEventInformation |

## IDebugOutputCallbacks

コールバックインターフェース。マネージド実装を CCW 経由で渡す。

```
GUID: 4bf58045-d654-4c40-b0af-683090f356dc
Base: IUnknown

Slot 3 (Index 0): Output(ULONG mask, PCSTR text) → HRESULT
```

### Output Mask Flags

| Flag | Value | 説明 |
|------|-------|------|
| DEBUG_OUTPUT_NORMAL | 0x001 | 通常出力 |
| DEBUG_OUTPUT_ERROR | 0x002 | エラー出力 |
| DEBUG_OUTPUT_WARNING | 0x004 | 警告出力 |
| DEBUG_OUTPUT_VERBOSE | 0x008 | 詳細出力 |
| DEBUG_OUTPUT_PROMPT | 0x010 | プロンプト |
| DEBUG_OUTPUT_PROMPT_REGISTERS | 0x020 | レジスタプロンプト |
| DEBUG_OUTPUT_EXTENSION_WARNING | 0x040 | 拡張機能警告 |
| DEBUG_OUTPUT_DEBUGGEE | 0x080 | デバッギ出力 |
| DEBUG_OUTPUT_DEBUGGEE_PROMPT | 0x100 | デバッギプロンプト |
| DEBUG_OUTPUT_SYMBOLS | 0x200 | シンボル出力 |

## F# COM Interop パターン

### vtable 直接呼び出し

COM インターフェースの各メソッドは vtable (仮想関数テーブル) に格納される。
F# から直接呼び出すパターン:

```fsharp
// 1. COM ポインタから vtable アドレスを読む
let vtable = Marshal.ReadIntPtr(comPtr)

// 2. vtable の slot N のメソッドポインタを読む
let fnPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size)

// 3. デリゲートに変換して呼び出す
let fn = Marshal.GetDelegateForFunctionPointer<MyDelegate>(fnPtr)
let hr = fn.Invoke(comPtr, args...)
```

### CCW (COM-Callable Wrapper) によるコールバック

マネージドクラスを COM コールバックとして渡すパターン:

```fsharp
// 1. COM インターフェースを定義
[<Guid("...")>]
[<InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
[<ComVisible(true)>]
type IMyCallback =
    [<PreserveSig>]
    abstract MyMethod: arg1: uint32 * arg2: nativeint -> int

// 2. マネージドクラスで実装
type MyCallbackImpl() =
    interface IMyCallback with
        member _.MyMethod(a, b) = 0 // S_OK

// 3. CCW ポインタを取得して COM メソッドに渡す
let impl = MyCallbackImpl()
let ccw = Marshal.GetComInterfaceForObject(impl, typeof<IMyCallback>)
// → ccw を SetOutputCallbacks 等に渡す
// 使用後: Marshal.Release(ccw)
```

## DebugCreate エントリポイント

```c
// dbgeng.dll からエクスポートされるファクトリ関数
HRESULT WINAPI DebugCreate(REFIID riid, void **out);
```

F# P/Invoke:
```fsharp
[<DllImport("dbgeng.dll")>]
extern int DebugCreate(Guid& iid, nativeint& iface)
```

通常は `IID_IDebugClient` を指定して呼び出し、返された `IDebugClient` から
`QueryInterface` で他のインターフェース (`IDebugControl` 等) を取得する。
