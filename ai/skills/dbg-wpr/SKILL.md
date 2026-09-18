---
name: dbg-wpr
description: >-
  Windows Performance Recorder (WPR) / Windows Performance Analyzer (WPA) で採取した
  ETW トレース .etl ログを F# Script (.fsx) と .NET で解析するスキル。
  Microsoft.Diagnostics.Tracing.TraceEvent、ETW Provider/Event の棚卸し、CPU sampling、
  process/thread、disk/file I/O、CLR GC、stack collection、PDB symbol resolution、
  _NT_SYMBOL_PATH、.etlx 変換、Microsoft Performance Toolkit SDK の使い分けを扱う。
  Use when: WPR, WPA, Windows Performance Recorder, Windows Performance Analyzer, ETW,
  ETL, .etl, .etlx, TraceEvent, dotnet fsi, F# Script, .fsx, CPU sampling, SampledProfile,
  stack walk, stack collection, シンボル解決, PDB, SymbolReader, パフォーマンスログ解析,
  Windows performance trace analysis, wpr -start, wpr -stop
argument-hint: '解析対象の .etl ファイルパス、症状、見たい観点、再現時刻、シンボル解決の要否を指定してください'
---

# Windows Performance Analyzer

WPR/WPA で採取した `.etl` を、F# Script (`.fsx`) と .NET で解析するためのスキル。
主に `Microsoft.Diagnostics.Tracing.TraceEvent` を使い、必要に応じて `.etlx` 変換、
シンボル解決、Microsoft Performance Toolkit SDK、`tracerpt`/`xperf` へのフォールバックを選ぶ。

## When to Use

- WPR で採取した `.etl` の中身を自動集計したいとき
- WPA で見ている CPU、Disk I/O、Process/Thread、GC、Provider/Event 情報をスクリプトで抽出したいとき
- F# Script (`dotnet fsi`) で一時的な解析ツールを作りたいとき
- CPU sample のスタックや PDB シンボル解決を行いたいとき
- `.etl` に目的の ETW Provider/Event/Stack が含まれているか確認したいとき
- 解析結果を CSV、JSON、Markdown、HTML レポート用の表に変換したいとき

## Inputs

不足している必須情報だけ確認する。

| 入力 | 確認内容 |
|---|---|
| Trace file | `.etl` パス、サイズ、圧縮有無、取得端末と解析端末が同じか |
| 症状 | CPU 高騰、hang、起動遅延、I/O 遅延、GC、特定プロセスなど |
| Time window | 再現操作の開始/終了時刻、問題が起きたおおよその秒数 |
| Capture profile | WPR profile、stack collection の有無、対象 provider |
| Symbol policy | Microsoft Symbol Server、社内 symbol server、ローカル PDB を使ってよいか |
| Output | 画面要約、CSV/JSON、上位 N 件、調査報告、再採取条件 |

## Core Rules

- `.etl` はバイナリ ETW トレースとして扱う。テキストとして読んだり、独自バイナリパーサーを作らない。
- まず `Dynamic.All` などで Provider/Event の棚卸しを行い、目的のデータが採取済みか確認する。
- `.etl` に入っていない Provider、stack、payload は後から復元できない。足りない場合は再採取条件を出す。
- CPU sample 数は相対的な CPU 使用傾向であり、厳密な CPU 時間ではない。必要ならサンプル間隔と trace 期間も併記する。
- スタック解析やシンボル解決は、WPR 採取時に stack collection が有効だった場合のみ行う。
- シンボル解決では `_NT_SYMBOL_PATH` または明示的な symbol path を使う。`%Temp%` のような未展開変数は避け、展開済みパスを使う。
- 巨大 `.etl` では raw event dump を避け、件数、上位 N 件、対象プロセス、対象 window で絞る。
- 大容量ファイルを扱う場合、`File.ReadAllBytes` で全読み込みしない。TraceEvent の reader または `FileStream` の範囲読みを使う。
- URL、ファイルパス、ユーザー名、コマンドライン、環境変数は privacy-sensitive として扱い、必要最小限だけ出力する。

## Decision Flow

| 目的 | 推奨ルート |
|---|---|
| どんなイベントが入っているか知りたい | `ETWTraceEventSource` + `Dynamic.All` |
| 特定 Provider/Event を数えたい | `TraceEvent` の parser または dynamic payload |
| Process/Thread/CPU/Disk/GC を簡易集計したい | `TraceEvent` kernel/CLR parser |
| CPU スタック、ホットパス、シンボル解決をしたい | `.etl` → `.etlx` + `TraceLog` + `SymbolReader` |
| WPA の表に近い高レベル分析をしたい | Microsoft Performance Toolkit SDK |
| Python や BI ツールに渡したい | F# Script で CSV/JSON に変換 |
| TraceEvent で読みにくい形式に変換したい | `tracerpt` / `xperf` を補助的に使う |

## Prerequisites

- Windows 環境
- .NET SDK (`dotnet fsi` が利用可能)
- NuGet package: `Microsoft.Diagnostics.Tracing.TraceEvent`
- シンボル解決時のみ、PDB を取得できる symbol path
- WPA 的な高レベル処理を行う場合のみ、Microsoft Performance Toolkit SDK

## Bundled Scripts

通常は本文中のコード断片を新規作成せず、同梱スクリプトを実行する。

| Script | 用途 |
|---|---|
| [inventory-etl.fsx](./scripts/inventory-etl.fsx) | Provider/Event の件数棚卸し、CSV 出力 |
| [process-lifecycle.fsx](./scripts/process-lifecycle.fsx) | Process start/stop の一覧化 |
| [cpu-samples.fsx](./scripts/cpu-samples.fsx) | CPU sample の PID/Process 別集計 |
| [disk-io.fsx](./scripts/disk-io.fsx) | Disk read/write bytes の PID/Process 別集計 |
| [clr-gc.fsx](./scripts/clr-gc.fsx) | CLR GC Start/Stop の一覧化 |
| [resolve-symbols.fsx](./scripts/resolve-symbols.fsx) | `.etlx` 変換、CPU sample stack のシンボル解決 |

巨大ログで読み取り可否だけ確認する場合は、各スクリプトに `--max-events <n>` を付けて先頭 N イベントで停止する。

## Workflow

### Phase 1: ファイルと環境の確認

1. `.etl` の存在、サイズ、拡張子、読み取り権限を確認する。
2. `dotnet fsi` が使えるか確認する。
3. 解析対象の症状、プロセス、時刻 window、必要な出力形式を確認する。
4. stack/symbol が必要な場合、WPR 採取時に stack collection が有効だったか確認する。

PowerShell で確認する例:

```powershell
Get-Item .\trace.etl | Select-Object FullName, Length, LastWriteTime
dotnet fsi --version
```

### Phase 2: ETL の棚卸し

最初に Provider/Event の分布を確認する。まず [inventory-etl.fsx](./scripts/inventory-etl.fsx) を使う。

```powershell
dotnet fsi .\.github\skills\dbg-wpr\scripts\inventory-etl.fsx .\trace.etl --top 100

# 巨大ログでまず読めるかだけ確認
dotnet fsi .\.github\skills\dbg-wpr\scripts\inventory-etl.fsx .\trace.etl --top 50 --max-events 50000

# 必要に応じて CSV に保存
dotnet fsi .\.github\skills\dbg-wpr\scripts\inventory-etl.fsx .\trace.etl --csv .\etl-events.csv
```

棚卸し結果で、目的の情報が含まれているか確認する。

| 見たい情報 | 代表的な確認対象 |
|---|---|
| CPU sample | `PerfInfo/SampledProfile`、kernel profile events |
| Process lifecycle | Process start/stop events |
| Disk I/O | DiskIO read/write events |
| File I/O | FileIO events、file name events |
| CLR GC | .NET Runtime / CLR GC events |
| Custom provider | 対象 provider 名と event 名 |
| Stack | `CallStack()` が取れる sample/event があるか |

### Phase 3: 目的別に集計する

#### Process lifecycle

プロセスの起動/終了、PID、コマンドラインを追う。起動遅延や即終了調査の入口にする。

```powershell
dotnet fsi .\.github\skills\dbg-wpr\scripts\process-lifecycle.fsx .\trace.etl

# 特定プロセスだけを見る
dotnet fsi .\.github\skills\dbg-wpr\scripts\process-lifecycle.fsx .\trace.etl --filter msedge

# コマンドラインも必要な場合のみ出力
dotnet fsi .\.github\skills\dbg-wpr\scripts\process-lifecycle.fsx .\trace.etl --filter msedge --include-command-line
```

#### CPU sampling

CPU sample を PID/ProcessName 別に集計する。相対的な CPU 使用の上位を出す。

```powershell
dotnet fsi .\.github\skills\dbg-wpr\scripts\cpu-samples.fsx .\trace.etl --top 30

# PID またはプロセス名で絞る
dotnet fsi .\.github\skills\dbg-wpr\scripts\cpu-samples.fsx .\trace.etl --filter msedge --top 20
```

#### Disk I/O

Disk read/write bytes を PID 別に集計する。I/O 遅延や大量書き込みの入口にする。

```powershell
dotnet fsi .\.github\skills\dbg-wpr\scripts\disk-io.fsx .\trace.etl --top 20

# PID またはプロセス名で絞る
dotnet fsi .\.github\skills\dbg-wpr\scripts\disk-io.fsx .\trace.etl --filter 1234
```

#### CLR GC

WPR で CLR/.NET Runtime provider が採取されている場合に GC イベントを見る。

```powershell
dotnet fsi .\.github\skills\dbg-wpr\scripts\clr-gc.fsx .\trace.etl

# PID で絞る
dotnet fsi .\.github\skills\dbg-wpr\scripts\clr-gc.fsx .\trace.etl --filter 1234
```

### Phase 4: Stack とシンボル解決

CPU の関数単位ホットパスを見る場合は `.etlx` と `TraceLog` を使う。
採取時に stack collection がない場合、この phase は実行せず再採取条件を出す。

推奨 symbol path 例:

```powershell
$symbolCache = Join-Path $env:USERPROFILE 'Symbols'
$env:_NT_SYMBOL_PATH = "srv*$symbolCache*https://msdl.microsoft.com/download/symbols"
```

社内/自社アプリの PDB がある場合は、ローカル PDB フォルダーまたは社内 symbol server を追加する。

```powershell
$env:_NT_SYMBOL_PATH = "srv*$symbolCache*https://msdl.microsoft.com/download/symbols;C:\MyApp\symbols"
```

シンボル解決は [resolve-symbols.fsx](./scripts/resolve-symbols.fsx) を使う。

```powershell
dotnet fsi .\.github\skills\dbg-wpr\scripts\resolve-symbols.fsx .\trace.etl --max-samples 20 --max-depth 64

# PID またはプロセス名で絞る
dotnet fsi .\.github\skills\dbg-wpr\scripts\resolve-symbols.fsx .\trace.etl --filter msedge --max-samples 10

# 明示的な symbol path を使う
dotnet fsi .\.github\skills\dbg-wpr\scripts\resolve-symbols.fsx .\trace.etl --symbol-path "srv*C:\Symbols*https://msdl.microsoft.com/download/symbols"
```

実務では全 sample の全 frame を解決すると遅くなる。まず対象プロセス、時間 window、上位 N sample に絞る。

シンボル解決で見るポイント:

| 観点 | 確認内容 |
|---|---|
| PDB が見つかるか | `module!function` になっているか、アドレスのみでないか |
| 自社アプリ | 自社 PDB が symbol path に含まれているか |
| OS DLL | Microsoft Symbol Server にアクセスできるか |
| 初回の遅さ | symbol cache に PDB/PE をダウンロードしているだけか |
| Stack quality | `CallStack()` が null でないか、フレームが途中で切れていないか |

### Phase 5: Microsoft Performance Toolkit SDK を検討する

WPA に近い高レベルな表や analyzer を作る場合は、TraceEvent だけでなく SDK を検討する。

```powershell
dotnet add package Microsoft.Windows.EventTracing.Processing.All
```

使い分け:

- Event 単位の抽出や小さな自動集計は TraceEvent を優先する。
- WPA のテーブルに近い model、stack、CPU、disk、process の横断分析が必要なら Performance Toolkit SDK を検討する。
- SDK の API はバージョン差があるため、使用中の package version の公式サンプルに合わせる。

### Phase 6: 解析結果をまとめる

回答やレポートは、次の順で出す。

1. 結論: 何が最も怪しいか、確度、影響範囲
2. Trace metadata: ファイル名、期間、イベント数、主要 provider
3. Evidence: process/thread、event name、timestamp、duration、sample count、bytes、stack frame
4. Timeline: 問題 window の前後関係
5. Gaps: 採取されていない provider、stack 不足、シンボル未解決、privacy 制約
6. Next capture: 再採取に必要な WPR profile、stack、provider、duration、buffer

## Re-capture Guidance

必要なデータが `.etl` にない場合は、断定せず再採取条件を提案する。

| 欲しい情報 | 採取条件の例 |
|---|---|
| CPU の上位プロセス | CPU sampling を含む WPR profile |
| 関数単位の CPU ホットパス | CPU sampling + stack collection |
| Disk/File I/O | Disk I/O / File I/O provider |
| .NET GC | CLR/.NET Runtime provider |
| 特定アプリの ETW | 対象 provider GUID/name を WPRP に追加 |
| 長時間の sporadic issue | buffer/recording mode と duration を調整 |

汎用例:

```powershell
wpr -start GeneralProfile
# reproduce
wpr -stop .\trace.etl
```

CPU stack や特定 provider が必要な場合は、WPR UI または `.wprp` で明示的に有効化する。

## Troubleshooting

| 症状 | 対処 |
|---|---|
| `dotnet fsi` が見つからない | .NET SDK をインストール、または PATH を確認 |
| NuGet restore が失敗する | プロキシ、NuGet source、社内 feed、証明書を確認 |
| イベントがほとんど出ない | `.etl` が空、別形式、または WPR 停止前ファイルでないか確認 |
| CPU sample が出ない | CPU sampling が採取されていない。再採取条件を出す |
| `CallStack()` が null | stack collection が採取されていない。再採取条件を出す |
| 関数名が出ずアドレスだけ | PDB 不足、symbol path 不備、symbol server 未到達 |
| 初回解析が遅い | `.etlx` 作成または PDB ダウンロード中。cache 後は速くなることが多い |
| 巨大ログで出力が多すぎる | 対象 PID、time window、上位 N 件、event 名で絞る |
| `%Temp%` を含む symbol path で失敗 | 展開済みの実パスを `_NT_SYMBOL_PATH` に指定する |

## Quality Checks

完了前に確認する。

- `.etl` の中に目的の Provider/Event/Stack が含まれるか確認した
- 採取されていない情報を推測で補っていない
- CPU sample、I/O bytes、GC event、stack frame などの根拠を分けて示した
- シンボル解決が必要な場合、symbol path と PDB の到達可否を確認した
- 大きな raw dump ではなく、上位 N 件や対象 window で要約した
- privacy-sensitive なコマンドライン、パス、URL、ユーザー名を不用意に出していない
- 不足データがある場合は、次回 WPR 採取条件を明示した

## Example Prompts

- `この WPR の .etl から CPU を使っているプロセス上位を F# Script で出して`
- `trace.etl にどの ETW Provider/Event が入っているか棚卸しして`
- `WPR ログの SampledProfile stack をシンボル解決してホットパスを見たい`
- `Disk I/O が多いプロセスを .fsx で集計するスクリプトを作って`
- `.etl に GC イベントが含まれるか確認して、GC Start/Stop を一覧化して`
- `WPA で見るような CPU スタック解析を .NET で自動化したい`