# PML v9 バイナリフォーマット仕様

Process Monitor (Procmon) が出力する `.PML` ファイルのバイナリ構造。
この文書は `procmon-parser` Python ライブラリのソースコードと
リバースエンジニアリングの結果に基づく。

## ファイル全体構造

```
┌──────────────────────────┐  0x000
│  Header (0x3A8 bytes)    │
├──────────────────────────┤  EventsOffset
│  Event Records           │  (可変長のイベントが連続)
│  ...                     │
├──────────────────────────┤  EventsOffsetsArrayOffset
│  Event Offsets Array     │  (各イベントの先頭オフセット, 5 bytes/entry)
├──────────────────────────┤  ProcessTableOffset
│  Process Table           │  (プロセス情報の構造体配列)
├──────────────────────────┤  StringsTableOffset
│  Strings Table           │  (共有文字列テーブル)
├──────────────────────────┤  IconTableOffset
│  Icon Table              │  (プロセスアイコン)
├──────────────────────────┤  HostsAndPortsTablesOffset
│  Hostnames Table         │  (IP → ホスト名マッピング)
│  Ports Table             │  (ポート番号 → サービス名)
└──────────────────────────┘  EOF
```

## Header (0x3A8 bytes)

| Offset | Size   | Type        | Field |
|--------|--------|-------------|-------|
| 0x000  | 4      | char[4]     | Signature (`"PML_"`) |
| 0x004  | 4      | uint32      | Version (`9`) |
| 0x008  | 4      | uint32      | Is64Bit (0 or 1) |
| 0x00C  | 0x20   | wchar[16]   | ComputerName |
| 0x02C  | 0x208  | wchar[260]  | SystemRoot |
| 0x234  | 4      | uint32      | NumberOfEvents |
| 0x238  | 8      |             | (unknown) |
| 0x240  | 8      | uint64      | EventsOffset |
| 0x248  | 8      | uint64      | EventsOffsetsArrayOffset |
| 0x250  | 8      | uint64      | ProcessTableOffset |
| 0x258  | 8      | uint64      | StringsTableOffset |
| 0x260  | 8      | uint64      | IconTableOffset |
| 0x268  | 12     |             | (unknown) |
| 0x274  | 4      | uint32      | WindowsMajorNumber |
| 0x278  | 4      | uint32      | WindowsMinorNumber |
| 0x27C  | 4      | uint32      | WindowsBuildNumber |
| 0x280  | 4      | uint32      | WindowsBuildDecimal |
| 0x284  | 0x32   | wchar[25]   | ServicePackName |
| ...    | 0xD6   |             | (unknown) |
| ...    | 4      | uint32      | NumberOfLogicalProcessors |
| ...    | 8      | uint64      | RAMMemorySize |
| ...    | 8      | uint64      | HeaderSize (= 0x3A8) |
| ...    | 8      | uint64      | HostsAndPortsTablesOffset |

## Event Offsets Array

各イベントへの絶対ファイルオフセットの配列。

| Size | Type   | Field |
|------|--------|-------|
| 4    | uint32 | EventOffset (絶対ファイルオフセット) |
| 1    | uint8  | Flags (unknown) |

エントリ数 = `NumberOfEvents`、合計サイズ = `NumberOfEvents × 5`

## Event Record (CommonEventStruct, 52 bytes + 可変長)

| Offset | Size | Type   | Field |
|--------|------|--------|-------|
| 0x00   | 4    | uint32 | ProcessIndex (→ Process Table) |
| 0x04   | 4    | uint32 | ThreadId |
| 0x08   | 4    | uint32 | EventClass (1=Process, 2=Registry, 3=FileSystem, 4=Profiling, 5=Network) |
| 0x0C   | 2    | uint16 | Operation (class-specific) |
| 0x0E   | 2    | uint16 | (unknown) |
| 0x10   | 4    | uint32 | (unknown) |
| 0x14   | 8    | uint64 | Duration (100ns units) |
| 0x1C   | 8    | uint64 | Date (FILETIME) |
| 0x24   | 4    | uint32 | Result (NTSTATUS) |
| 0x28   | 2    | uint16 | StacktraceDepth |
| 0x2A   | 2    | uint16 | (unknown) |
| 0x2C   | 4    | uint32 | DetailsSize |
| 0x30   | 4    | uint32 | ExtraDetailsOffset |

ヘッダの後に:
1. **Stacktrace** (`StacktraceDepth × pvoid_size` bytes) — スタックアドレスの配列
2. **Details** (`DetailsSize` bytes) — イベント固有の詳細データ
3. **ExtraDetails** — (ExtraDetailsOffset > 0 の場合)

## Strings Table

| Offset | Size | Type | Field |
|--------|------|------|-------|
| 0      | 4    | uint32 | NumberOfStrings |
| 4      | N×4  | uint32[] | StringOffsets (先頭からの相対オフセット) |

各文字列エントリ (StringOffsets[i] の位置):
| Size | Type | Field |
|------|------|-------|
| 4    | uint32 | StringSize (バイト数) |
| N    | wchar[] | String (UTF-16LE, null 終端含む) |

## Process Table

| Offset | Size | Type | Field |
|--------|------|------|-------|
| 0      | 4    | uint32 | NumberOfProcesses |
| 4      | N×4  | uint32[] | ProcessIndexArray (スキップ可) |
| +N×4   | N×4  | uint32[] | ProcessOffsetsArray (先頭からの相対) |

各プロセスレコード (ProcessOffsetsArray[i] の位置):

| Offset | Size | Type   | Field |
|--------|------|--------|-------|
| 0x00   | 4    | uint32 | ProcessIndex |
| 0x04   | 4    | uint32 | PID |
| 0x08   | 4    | uint32 | ParentPID |
| 0x0C   | 4    |        | (unknown) |
| 0x10   | 8    | uint64 | AuthenticationID |
| 0x18   | 4    | uint32 | Session |
| 0x1C   | 4    |        | (unknown) |
| 0x20   | 8    | uint64 | StartTime (FILETIME) |
| 0x28   | 8    | uint64 | EndTime (FILETIME) |
| 0x30   | 4    | uint32 | Virtualized |
| 0x34   | 4    | uint32 | Is64Bit |
| 0x38   | 4    | uint32 | IntegrityIndex → Strings[] |
| 0x3C   | 4    | uint32 | UserIndex → Strings[] |
| 0x40   | 4    | uint32 | ProcessNameIndex → Strings[] |
| 0x44   | 4    | uint32 | ImagePathIndex → Strings[] |
| 0x48   | 4    | uint32 | CommandLineIndex → Strings[] |
| 0x4C   | 4    | uint32 | CompanyIndex → Strings[] |
| 0x50   | 4    | uint32 | VersionIndex → Strings[] |
| 0x54   | 4    | uint32 | DescriptionIndex → Strings[] |
| 0x58   | 4    | uint32 | IconIndexSmall |
| 0x5C   | 4    | uint32 | IconIndexBig |
| 0x60   | pvoid |       | (unknown) |
| +pvoid | 4    | uint32 | NumberOfModules |

モジュール配列 (NumberOfModules 個):
| Size   | Type   | Field |
|--------|--------|-------|
| pvoid  |        | (unknown) |
| pvoid  | pvoid  | BaseAddress |
| 4      | uint32 | Size |
| 4      | uint32 | ImagePathIndex → Strings[] |
| 4      | uint32 | VersionIndex → Strings[] |
| 4      | uint32 | CompanyIndex → Strings[] |
| 4      | uint32 | DescriptionIndex → Strings[] |
| 4      | uint32 | Timestamp |
| 0x18   |        | (unknown) |

## Operation Codes

### EventClass 1: Process
| Value | Name |
|-------|------|
| 0 | Process_Defined |
| 1 | Process_Create |
| 2 | Process_Exit |
| 3 | Thread_Create |
| 4 | Thread_Exit |
| 5 | Load_Image |
| 7 | Process_Start |
| 8 | Process_Statistics |

### EventClass 2: Registry
| Value | Name |
|-------|------|
| 0 | RegOpenKey |
| 1 | RegCreateKey |
| 2 | RegCloseKey |
| 3 | RegQueryKey |
| 4 | RegSetValue |
| 5 | RegQueryValue |
| 6 | RegEnumValue |
| 7 | RegEnumKey |
| 9 | RegDeleteKey |
| 10 | RegDeleteValue |

### EventClass 3: FileSystem
| Value | Name |
|-------|------|
| 19 | CreateFileMapping |
| 20 | CreateFile |
| 23 | ReadFile |
| 24 | WriteFile |
| 25 | QueryInformationFile |
| 26 | SetInformationFile |
| 32 | DirectoryControl |
| 33 | FileSystemControl |
| 34 | DeviceIoControl |
| 37 | LockUnlockFile |
| 38 | CloseFile |

### EventClass 5: Network
| Value | Name |
|-------|------|
| 2 | Send |
| 3 | Receive |
| 4 | Accept |
| 5 | Connect |
| 6 | Disconnect |

## 主要 NTSTATUS コード

| Code | Name | 意味 |
|------|------|------|
| 0x00000000 | SUCCESS | 成功 |
| 0xC0000022 | ACCESS DENIED | アクセス拒否 |
| 0xC0000034 | NAME NOT FOUND | 名前が見つからない |
| 0xC000003A | PATH NOT FOUND | パスが見つからない |
| 0xC0000043 | SHARING VIOLATION | 共有違反 |
| 0xC01C0004 | FAST IO DISALLOWED | フィルタドライバが Fast I/O を拒否 |
| 0x00000367 | WAIT FOR OPLOCK | Oplock 待機 |

## 参考

- [procmon-parser (Python)](https://github.com/eronnen/procmon-parser) — PML パーサーの参考実装
- [NTSTATUS values](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55)
- [FILETIME](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-filetime) — 1601/01/01 からの 100ns 単位
