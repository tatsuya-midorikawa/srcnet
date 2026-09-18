---
name: dbg-procmon
description: >-
  Process Monitor (Procmon) の PML ログファイルをバイナリ解析するスキル。
  F# Script (.fsx) で PML v9 を直接パースし、プロセス一覧・エラーイベント・
  ACCESS DENIED・ライフサイクルイベントを抽出する。
  オプションで dbghelp.dll P/Invoke によるスタックトレースのシンボル解決も可能。
  Use when: PML 解析, Process Monitor, Procmon, プロセスモニタ, PML ログ,
  .pml ファイル, プロセス一覧, エラー解析, ACCESS DENIED, レジストリエラー,
  ファイルシステムエラー, FAST_IO_DISALLOWED, WebView2 起動不可, アプリ起動不可,
  procmon log analysis, process monitor log, file system filter driver
argument-hint: '解析対象の .pml ファイルパスと、調査したいプロセス名やエラー種別を指定してください'
---

# PML Analyzer

Process Monitor (Procmon) の `.PML` ファイルを F# Script でバイナリ解析するスキル。
外部ライブラリは不要。.NET SDK のみで 1GB 超の大規模ログも解析可能。

> **重要**: PML はバイナリ形式のため、テキストエディタや `cat` では読めない。
> 必ず [parse_pml.fsx](./scripts/parse_pml.fsx) を使用すること。

## When to Use

- Process Monitor で取得した `.PML` ログを解析するとき
- アプリ起動不可・クラッシュの原因を I/O やレジストリアクセスから調査するとき
- ACCESS DENIED やファイル/レジストリの NOT FOUND パターンを特定したいとき
- セキュリティソフト (McAfee, HIBUN 等) のフィルタドライバ影響を調べたいとき
- WebView2 / Edge / Teams / Citrix 等のプロセスライフサイクルを追跡したいとき
- スタックトレースのシンボル解決が必要なとき

## Prerequisites

- **.NET SDK** がインストールされていること (`dotnet fsi` が利用可能)
- (シンボル解決時のみ) **WinDbg Preview** (`dbghelp.dll`)

## Procedure

### Phase 1: PML ファイルの基本解析

```bash
dotnet fsi ./scripts/parse_pml.fsx <file.pml> --all
```

これにより以下がすべて出力される:
- ヘッダ情報 (マシン名、OS、イベント数)
- プロセス一覧 (PID, PPID, セッション, ユーザー, コマンドライン)
- プロセスライフサイクル (Create/Start/Exit)
- エラーサマリ (種別×回数、サンプルパス)
- ACCESS DENIED 詳細

#### オプション一覧

| オプション | 説明 |
|-----------|------|
| `--all` | すべての解析を実行 |
| `--processes` | プロセス一覧のみ |
| `--errors` | エラーイベントサマリ |
| `--lifecycle` | プロセスライフサイクル |
| `--access-denied` | ACCESS DENIED の詳細 |
| `--filter <name>` | プロセス名フィルタ (部分一致、カンマ区切り) |
| `--stacktrace` | スタックアドレスも収集 |
| `--limit <n>` | サンプルパス最大数 (デフォルト: 5) |
| `--output <file>` | ファイルに出力 |

### Phase 2: 特定プロセスに絞った解析

問題のあるプロセスが特定できたら、フィルタを使って絞り込む:

```bash
# WebView2 関連のみ
dotnet fsi ./scripts/parse_pml.fsx <file.pml> --all --filter msedgewebview2,msedge,teams

# Citrix 関連のみ
dotnet fsi ./scripts/parse_pml.fsx <file.pml> --all --filter citrix,receiver,selfservice,authman
```

### Phase 3: エラーパターンの分析

出力されたエラーサマリから、以下のパターンに注目する:

| エラー | 意味 | 典型的な原因 |
|--------|------|-------------|
| **ACCESS DENIED** (RegCreateKey) | レジストリ書き込み拒否 | GPO、セキュリティソフト、UWP 権限不足 |
| **ACCESS DENIED** (CreateFile) | ファイルアクセス拒否 | ACL、セキュリティソフト、ファイルロック |
| **FAST IO DISALLOWED** (0xC01C0004) | フィルタドライバが Fast I/O 拒否 | McAfee, HIBUN 等のカーネルフィルタ。通常 I/O にフォールバックするため直接障害にはならないが、大量発生時はタイムアウトの原因になりうる |
| **NAME NOT FOUND** (RegOpenKey) | レジストリキー不在 | 通常は正常動作 (プローブ) だが大量発生時は注意 |
| **PATH NOT FOUND** (CreateFile) | ファイルパスが存在しない | インストール不完全、プロファイル問題 |
| **SHARING VIOLATION** | ファイルロック競合 | 複数プロセスの同時アクセス |
| **0x368** (RegOpenKey/RegCreateKey) | 非標準エラー — SRP/AppLocker 関連 | Software Restriction Policies (Safer\CodeIdentifiers) によるブロック。GPO で SRP が配信されている環境で発生 |
| **0xC0000061** (CreateFile) | PRIVILEGE_NOT_HELD | MSIX/UWP サンドボックス内でのファイル操作権限不足 (SE_RESTORE_PRIVILEGE 等) |

#### 特に注意すべきレジストリパス

| パス | 影響 |
|------|------|
| `HKLM\SOFTWARE\Microsoft\SecurityManager\CapAuthz` | UWP アプリの Capability Authorization。ACCESS DENIED → UWP/MSIX アプリの権限チェックが全面失敗 |
| `HKLM\SOFTWARE\Microsoft\IdentityCRL\ClockData` | Microsoft 認証基盤のクロック同期。ACCESS DENIED → Azure AD / Microsoft アカウント認証不可 |
| `HKLM\SOFTWARE\Microsoft\SystemCertificates` | 証明書ストア。ACCESS DENIED → SSL/TLS 検証障害 |
| `HKLM\System\CurrentControlSet\Services\WinSock2` | ネットワークスタック。ACCESS DENIED → `WSCEnumProtocolsEx` 失敗でネットワーク通信全面不可 |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers` | Software Restriction Policies。0x368 エラー → プロセス実行制限 |

### Phase 4: プロセスライフサイクル分析

プロセスの「起動→即終了」パターンは異常終了を示す:

```
Process_Start: msedgewebview2.exe (PID:38604) result=SUCCESS
Process_Exit:  msedgewebview2.exe (PID:38604) result=SUCCESS
Process_Start: msedgewebview2.exe (PID:33776) result=SUCCESS  ← リトライ
Process_Exit:  msedgewebview2.exe (PID:33776) result=SUCCESS
```

> **注意**: Process_Exit の result が SUCCESS でも、プロセスが「即座に終了」しているなら
> 異常動作。プロセスの起動時刻と終了時刻の差で判断する。

### Phase 5: スタックトレースのシンボル解決 (オプション)

PML にはスタックトレースのアドレスが記録されている。
`resolve_symbols.fsx` に PML ファイルを直接渡すと、モジュール情報の抽出・PE のシンボルサーバーからのダウンロード・シンボル解決を一括で行う:

```bash
# エラーイベントのスタックをシンボル解決 (フィルタ付き)
dotnet fsi ./scripts/resolve_symbols.fsx <file.pml> --filter msedgewebview2,msedge,ms-teams --limit 3
```

> **注意**: `--stacktrace` オプションは `--errors` と組み合わせたときのみスタックアドレスを出力する。
> `--lifecycle` 単独ではスタックデータは付与されない (ライフサイクルイベント自体にはスタックが記録されないため)。
> 起動失敗の原因を調べる場合は `--errors --stacktrace --filter <name>` でエラーイベントのスタックを収集すること。

#### resolve_symbols.fsx のオプション

| オプション | 説明 |
|-----------|------|
| `<file.pml>` | 解析対象の PML ファイル (必須) |
| `--filter <name>` | プロセス名フィルタ (部分一致、カンマ区切り) |
| `--limit <n>` | カテゴリあたりのスタック出力数 (デフォルト: 3) |

#### シンボルサーバーの設定

シンボル解決には `dbghelp.dll` が必要:

| 優先度 | パス |
|--------|------|
| 1 | `%TEMP%\WinDbg.Slow\amd64\dbghelp.dll` (WinDbg Preview) |
| 2 | `%TEMP%\WinDbg\amd64\dbghelp.dll` |
| 3 | `C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\dbghelp.dll` |
| 4 | `C:\Windows\System32\dbghelp.dll` (フォールバック) |

シンボルパスは以下の順で決定:
1. `_NT_SYMBOL_PATH` 環境変数が設定されていればそれを使用
2. 未設定の場合は `srv*%Temp%\Symbols*https://symweb.azurefd.net` にフォールバック

#### スタック分析の着目ポイント

シンボル解決後のスタックから、以下のパターンで障害の段階を特定する:

| スタックパターン | 意味 | 典型的な原因 |
|-----------------|------|-------------|
| すべてが `ntdll!LdrInitializeThunk` 内 | プロセスが **DLL ロード前に終了** | SRP (ソフトウェア制限ポリシー)、AppLocker、Shim Engine 障害 |
| `LdrInitShimEngineDynamic` | アプリ互換性データベース (Shim Engine) 初期化中 | 互換性設定の問題、SRP チェック中のエラー |
| `ws2_32!WSCEnumProtocolsEx` で ACCESS DENIED | **WinSock2 初期化失敗** — ネットワーク通信不可 | `WinSock2\Parameters` の ACL 変更 |
| `Windows.Security.Authentication.OnlineId` で ACCESS DENIED | **Microsoft 認証基盤の初期化失敗** | `IdentityCRL\ClockData`, `CapAuthz` の ACL 変更 |
| `KernelBase!ReplaceFileW` で 0xC0000061 | ファイルのアトミック置換で **権限不足** | MSIX サンドボックスの SE_RESTORE_PRIVILEGE 欠落 |
| `sqlite3` → `ReadFile/WriteFile` で FAST_IO_DISALLOWED | DB I/O がフィルタドライバで遅延 | McAfee/HIBUN 等。通常 I/O にフォールバックするが遅延要因 |

> **ポイント**: プロセスが「起動→即終了」している場合、エラースタックが `LdrInitializeThunk` 内に
> 閉じているかどうかを確認する。閉じている場合、プロセスはローダ初期化段階で強制終了されており、
> アプリケーションコードは一切実行されていない。

### Phase 6: 報告書の作成

解析結果を HTML レポートにまとめる際の推奨構成:

1. **概要** — 事象の説明、解析対象ファイル、イベント数
2. **環境情報** — OS、ソフトウェア構成 (セキュリティソフト、VDI 等)
3. **プロセスライフサイクル** — 起動→終了のタイムライン
4. **エラー分析** — ACCESS DENIED、FAST_IO_DISALLOWED の詳細
5. **原因分析** — エラーパターンからの推定原因
6. **プロセスツリー** — 関連プロセスの親子関係
7. **スタックトレース分析** — シンボル解決済みスタックの解説 (実施した場合)
8. **対処方法** — 推奨アクション

#### スタックトレースの表示形式

スタックトレースは Procmon のスタックダイアログと同様のテーブル形式で表示すると読みやすい:

| 列 | 内容 |
|----|------|
| **Frame** | `K 0`〜(カーネル) / `U N`〜(ユーザーモード) |
| **Module** | DLL / EXE 名 |
| **Location** | 関数名 + オフセット |
| **Address** | 仮想アドレス (カーネルフレームのみ表示でも可) |
| **Path** | モジュールのフルパス |

- 重要なフレーム行はハイライト背景で強調する
- 注目すべき関数名は太字にし、行末に `← 説明` で注釈を付ける
- ダークテーマ (背景 `#1e293b` / 文字 `#e2e8f0`) が Procmon に近い見た目になる

## PML フォーマット詳細

PML v9 のバイナリ構造の詳細は [PML フォーマット仕様](./references/pml-format.md) を参照。

### 主要構造のサマリ

```
Header (0x3A8 bytes)
  ├── Signature: "PML_" (4 bytes)
  ├── Version: uint32 (must be 9)
  ├── Is64Bit: uint32
  ├── ComputerName: wchar[16]
  ├── SystemRoot: wchar[260]
  ├── NumberOfEvents: uint32 @ 0x234
  ├── EventsOffset: uint64 @ 0x240
  ├── EventsOffsetsArrayOffset: uint64 @ 0x248
  ├── ProcessTableOffset: uint64 @ 0x250
  └── StringsTableOffset: uint64 @ 0x258

Event (52 bytes header + variable)
  ├── ProcessIndex: uint32 @ 0x00
  ├── EventClass: uint32 @ 0x08
  ├── Operation: uint16 @ 0x0C
  ├── Result (NTSTATUS): uint32 @ 0x24
  ├── StacktraceDepth: uint16 @ 0x28
  └── DetailsSize: uint32 @ 0x2C

Process Record
  ├── ProcessIndex: uint32 @ 0x00
  ├── PID: uint32 @ 0x04
  ├── ParentPID: uint32 @ 0x08
  ├── Session: uint32 @ 0x18
  ├── ProcessNameIndex: uint32 @ 0x40 → Strings[]
  ├── ImagePathIndex: uint32 @ 0x44 → Strings[]
  └── CommandLineIndex: uint32 @ 0x48 → Strings[]
```

## Troubleshooting

| 症状 | 対処 |
|------|------|
| `Not a PML file` | 拡張子が `.PML` であることを確認。CSV エクスポートではなく PML 形式で保存する |
| `PML version N` | v9 以外はサポート外。Procmon v3.x 以降で取得し直す |
| メモリ不足 | 通常は .NET のデフォルト設定で十分。必要なら `DOTNET_GCHeapCount` を調整 |
| 解析が遅い | `--filter` でプロセスを絞る。全イベント走査は大きなファイルで数分かかる |
| スタックが解決できない | シンボルパスとモジュールパスを確認。WinDbg Preview をインストール |
| `symweb.azurefd.net unreachable` | VPN (MSFT-AzVPN-Manual) に接続されていない。resolve_symbols.fsx はスキップして終了する |
| 大量の `local mismatch` | ローカル端末と PML 採取端末の OS ビルドが異なる。シンボルサーバーから正しいビルドの PE がダウンロードされれば OK |
| ライフサイクルにスタックが出ない | `--stacktrace` は `--errors` にのみ有効。ライフサイクルイベント (Process_Start/Exit) にはスタックが記録されない |

## Performance Tips

| 操作 | 所要時間 (1.5GB PML) | 備考 |
|------|---------------------|------|
| ヘッダ + プロセス一覧 | 1-2 秒 | 固定テーブルのみ読み込み |
| 全イベントスキャン | 1-3 分 | 369万イベントの場合 |
| フィルタ付きスキャン | 1-3 分 | スキャン自体は全件、マッチのみ処理 |
| シンボル解決 | 数秒/モジュール | 初回はシンボルダウンロードで遅い |
| resolve_symbols.fsx 全体 | 1-5 分 | モジュール数・シンボルサーバー接続速度に依存。193 モジュール/1.5GB PML で約 3 分 |
