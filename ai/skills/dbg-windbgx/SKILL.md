---
name: dbg-windbgx
description: >-
  DbgEng COM API を使用してダンプファイル (.dmp) や TTD トレース (.run) を
  解析するスキル。F# Script (windbg.fsx) を使い、WinDbg コマンドを対話的に
  実行してクラッシュ原因やメモリ状態を調査する。Use when: dump analysis,
  ダンプ解析, クラッシュ解析, .dmp, .run, TTD, Time Travel Debugging,
  crash dump, memory dump, WinDbg, デバッグ, post-mortem debugging,
  blue screen, BSOD, hang analysis, ハング解析, exception analysis.
argument-hint: '解析対象の .dmp/.run ファイルパス、または調査内容を記述してください'
---

# DbgEng Dump Analyzer

DbgEng COM API を使用したダンプファイル / TTD トレース解析スキル。
[windbg.fsx](./scripts/windbg.fsx) を実行し、WinDbg コマンドで対話的にダンプを調査する。

> **重要**: ダンプ解析には必ず `windbg.fsx` を使用すること。
> `cdb.exe` や `windbg.exe` を直接呼び出してはならない。

> **MEX 拡張**: MEX デバッガ拡張 (`!mex.*`) が自動ロードされる。
> MEX は出力が構造化され、DML 対応で、多くの操作がワンコマンドで完結するため **推奨** だが、
> 標準 WinDbg コマンド (`k`, `lm`, `!analyze -v` 等) も引き続き有効。
> 状況に応じて使い分けること。詳細は [MEX リファレンス](./references/mex-extension.md) を参照。

## When to Use

- クラッシュダンプ (.dmp) の原因を調査したいとき
- TTD トレース (.run) を使ったタイムトラベルデバッグを行いたいとき
- ブルースクリーン (BSOD) のバグチェックコードを分析したいとき
- アプリケーションハングの原因をスレッドスタックから特定したいとき
- メモリリークやヒープ破壊を調査したいとき
- 例外情報やコールスタックを確認したいとき
- マルチプロセスアプリ (Chromium 系ブラウザ等) のプロセス間連携を調査したいとき

## Prerequisites

- **Windows** 環境であること
- **.NET SDK** がインストールされていること (`dotnet fsi` が利用可能)
- **WinDbg Preview** がインストールされていること
  - `%temp%\WinDbg.Slow\amd64\dbgeng.dll` が必要
  - Microsoft Store または `winget install Microsoft.WinDbg` で導入
- ダンプファイルに対応する **シンボルファイル** (推奨)
  - `_NT_SYMBOL_PATH` 環境変数が設定されている場合はそれを使用
  - 未設定の場合は `srv*%Temp%\Symbols*https://symweb.azurefd.net` にフォールバック

## Procedure

### Phase 1: ダンプファイルを開く

```bash
dotnet fsi ./scripts/windbg.fsx <path-to-dump>
```

対応形式: `.dmp` (クラッシュダンプ)、`.run` (TTD トレース)。
`.run` ファイルは自動的にバイナリ/テキスト判定され、バイナリなら TTD トレースとして、
テキストなら WDS スクリプトとして処理される。

スクリプト起動後、ダンプが自動的に読み込まれ、以下の初期化コマンドが自動実行される:

| コマンド | 目的 |
|---------|------|
| `.lines -d` | ソース行情報の表示を無効化 (スタック表示を簡潔に) |
| `l-t` | ソース行のステップ実行モードを無効化 |
| `aS dv dv /V /i /t` | `dv` コマンドを `/V /i /t` オプション付きにエイリアス |
| `.load mex` | MEX デバッガ拡張をロード |

初期化完了後、REPL (`dbg>`) が表示される。

### Phase 2: 初期情報の収集

REPL で以下のコマンドを順に実行して概要を把握する。
MEX コマンドは出力が構造化されており見やすいため **推奨** だが、
標準 WinDbg コマンドでも同等の情報が得られる。状況に応じて使い分けること:

| MEX コマンド (推奨) | 標準コマンド | 目的 |
|-------------------|------------|------|
| `!mex.di` | `vertarget` | ダンプ情報 (OS バージョン、ダンプ種別) |
| `!mex.crash` | `!analyze -v` | クラッシュの初期解析 (MEX 版はより高速・要点重視) |
| `!mex.mods` | `lm` | ロードされたモジュール一覧 (MEX 版は DML 対応) |
| `!mex.us` | `~*k` | 全スレッドのスタック (MEX 版はユニークスタックとしてグループ化) |
| `!mex.lt` | `~*` | スレッド一覧 (MEX 版は状態・待機理由付き) |
| `!mex.cl` | `!peb` | プロセスのコマンドラインを表示 |

#### TTD トレースの場合の注意点

TTD トレース (.run) を開いた直後は **トレース先頭位置** にいるため、
`LdrInitializeThunk` 等の初期化コードしか見えない。
意味のあるスタックを得るには、先に **`!tt` で位置を移動** する必要がある:

| コマンド | 目的 |
|---------|------|
| `!tt 100` | トレース末尾 (最終位置) に移動 |
| `!tt 50`  | トレース中間位置に移動 |
| `!tt 0`   | トレース先頭に戻る |

```
$$ TTD 解析の典型的な流れ
!tt 100        $$ まずトレース末尾に移動して最終状態を確認
!mex.us        $$ ユニークスタック (全スレッドをグループ化) — 標準: ~*k
!mex.lt        $$ スレッド一覧 (状態・待機理由付き) — 標準: ~*
!mex.ex        $$ トレース内の例外を一覧表示
!tt 50         $$ 中間位置に移動して処理中のスタックを確認
~0 k           $$ メインスレッドのスタック
```

### Phase 3: 詳細調査

初期情報を元に、以下のコマンドで深掘りする。
MEX と標準コマンドの両方を記載しているので、好みや状況に応じて選択すること:

**例外・クラッシュ解析:**
| MEX コマンド (推奨) | 標準コマンド | 目的 |
|-------------------|------------|------|
| `!mex.mexr` | `.exr -1` | 例外レコードの管理・表示 |
| | `.ecxr` | 例外コンテキストに切り替え |
| `!mex.crash` | `!analyze -v` | クラッシュ初期解析 |
| | `k` | 現在のスレッドのコールスタック |
| | `kb` | コールスタック (最初の3引数付き) |
| `!mex.f N` | `.frame N` | スタックフレーム N に移動 (MEX 版はコンテキスト自動設定) |
| | `dv` | ローカル変数を表示 |
| `!mex.dss` | | スタック上の文字列をすべて表示 (MEX 専用) |
| `!mex.gle` | | GetLastError の値を TEB から取得 (MEX 専用) |

**メモリ解析:**
| MEX コマンド (推奨) | 標準コマンド | 目的 |
|-------------------|------------|------|
| `!mex.mi` | `!address` | メモリアドレス情報 |
| `!mex.mheap` | `!heap -s` | ヒープの概要 (MEX 版は DML 対応) |
| `!mex.nhl` | | ネイティブヒープリーク検出 (MEX 専用) |
| | `dd <address>` | メモリ内容をDWORD表示 |
| `!mex.du` | `du <address>` | Unicode 文字列を表示 |
| `!mex.p` | `!peb` | プロセス情報 |
| `!mex.strings` | | アドレス範囲の可読文字列を表示 (MEX 専用) |

**カーネルダンプ:**
| コマンド | 目的 |
|---------|------|
| `!process 0 0` | 全プロセス一覧 |
| `!thread` | 現在のスレッド情報 |
| `!irql` | 現在の IRQL |
| `!pool <address>` | プールタグ情報 |
| `!drivers` | ドライバ一覧 |

### Phase 3b: マルチプロセスアプリの調査 (Chromium 系等)

Chromium 系ブラウザ (Edge, Chrome 等) は多数のプロセスで構成される。
複数の `.run` / `.dmp` がある場合、まず **プロセス種別を一括特定** してから
対象プロセスを絞り込むこと。

#### ステップ 1: プロセス種別の高速特定

TTD トレースの `.run` ファイルはバイナリだが、コマンドライン文字列が
Unicode で埋め込まれている。**デバッガで開くよりバイナリ直接読みが高速**:

```powershell
# PowerShell でバイナリの先頭100MBからコマンドラインを抽出
Get-ChildItem "*.run" | Sort-Object Name | ForEach-Object {
    $fs = [System.IO.File]::OpenRead($_.FullName)
    try {
        $sz = [int][Math]::Min(100*1024*1024, [long]$fs.Length)
        $buf = New-Object byte[] $sz
        [void]$fs.Read($buf, 0, $sz)
        $text = [System.Text.Encoding]::Unicode.GetString($buf, 0, $sz)
        $m = [regex]::Match($text, 'msedge\.exe"?\s+([^\x00]{10,300})')
        if ($m.Success -and $m.Groups[1].Value -match '--type=(\S+)') {
            "$($_.Name) : $($Matches[1])"
        } else { "$($_.Name) : browser" }
    } finally { $fs.Close() }
}
```

> **注意**: `[System.IO.File]::ReadAllBytes()` は 2GB 以上のファイルで失敗する。
> ストリーム読み込み (`OpenRead` + `Read`) を使うこと。

#### ステップ 2: Chromium プロセス種別の見分け方

| `--type=` 値 | 役割 | 調査優先度 |
|-------------|------|----------|
| (なし / browser) | ブラウザ本体 — 全体制御・UI | 高 |
| `renderer` | ウェブページのレンダリング | 高 (拡張は低) |
| `gpu-process` | GPU レンダリング | 中 |
| `utility --utility-sub-type=X` | 各種サービス | X による |
| `ppapi` | PPAPI プラグイン (PDF等) | 中 |
| `crashpad-handler` | クラッシュハンドラ | 低 |

**レンダラーの細分化**:
- `--extension-process --renderer-sub-type=extension` → 拡張機能 (通常低優先度)
- `--renderer-client-id=N` (拡張なし) → ウェブページ用 (高優先度)
- `--dlp-protection-type=N` → DLP 保護付きページ

**ユーティリティの主要サブタイプ**:
- `printing.mojom.PrintCompositor` → 印刷合成
- `network.mojom.NetworkService` → ネットワーク
- `storage.mojom.StorageService` → ストレージ
- `audio.mojom.AudioService` → オーディオ

#### ステップ 3: デバッガでプロセス種別を確認する方法

バイナリ直接読みが使えない場合、デバッガで `!mex.cl` を実行してコマンドラインを確認
(**`!peb` より簡潔で高速**):

```
!mex.cl
$$ コマンドラインのみを直接表示
$$ 例: "msedge.exe" --type=renderer --renderer-client-id=29

$$ Edge 専用の MEX コマンドも利用可能:
!mex.edgeversion   $$ Edge バージョン情報
!mex._edgetriage   $$ 包括的トリアージレポート
!mex.edgeurls      $$ 開いている URL 一覧
```

### Phase 4: スクリプト実行 (オプション)

定型の解析手順を `.wds` / `.run` ファイルにまとめて一括実行できる:

```bash
dotnet fsi ./scripts/windbg.fsx crash.dmp analysis.wds
```

`.wds` / `.run` ファイルの書式:
```
$$ analysis.wds — 基本解析スクリプト
$$ コメントは $$ または * で開始
vertarget
!analyze -v
~*k
lm
```

> **注意**: スクリプト内に `q` や `quit` を書いてはならない。
> これらはスクリプト実行中に自動的にスキップされるが、
> セッション終了は REPL で行うこと。

### Phase 5: 終了

REPL で `quit` または `q` を入力して終了。

## Script Details

### windbg.fsx のアーキテクチャ

```
┌─────────────────────────────────────────┐
│  windbg.fsx (F# Script)                │
│                                         │
│  NativeLibrary.Load                     │
│  (%temp%\WinDbg.Slow\amd64\dbgeng.dll)  │
│       │                                 │
│  ┌────▼─────┐   QueryInterface          │
│  │DebugCreate├──────────┬───────┐       │
│  └────┬─────┘          │       │       │
│       │                ▼       ▼       │
│  ┌────▼──────┐  ┌──────────┐ ┌──────┐  │
│  │IDebugClient│  │IDebugCtrl│ │IDebug│  │
│  │  (COM)     │  │  (COM)   │ │Symbols│  │
│  └────┬───────┘  └────┬─────┘ └──┬───┘  │
│       │ SetOutput     │ Execute  │ Set  │
│       │ Callbacks     │ WaitFor  │ Sym  │
│       ▼               │ Event    │ Path │
│  ┌──────────────┐     │          │      │
│  │OutputCapture  │◄────┘          │      │
│  │ (CCW→COM)    │  出力テキスト   │      │
│  └──────┬───────┘                │      │
│         │ Console.Write          │      │
│         ▼                        │      │
│  ┌──────────┐                    │      │
│  │  REPL    │ ← stdin (dbg>)    │      │
│  └──────────┘                    │      │
└──────────────────────────────────────────┘
```

### COM インターフェース vtable マッピング

スクリプトは dbgeng.dll の COM vtable を直接呼び出す。
vtable スロット計算: `slot = 3 (IUnknown) + method_index`

| Interface | Method | Index | Slot |
|-----------|--------|-------|------|
| IDebugClient | OpenDumpFile | 16 | 19 |
| IDebugClient | EndSession | 23 | 26 |
| IDebugClient | SetOutputCallbacks | 31 | 34 |
| IDebugClient | SetOutputMask | 33 | 36 |
| IDebugSymbols | SetSymbolPath | 38 | 41 |
| IDebugControl | Execute | 63 | 66 |
| IDebugControl | ExecuteCommandFile | 64 | 67 |
| IDebugControl | WaitForEvent | 90 | 93 |

詳細は [DbgEng API リファレンス](./references/dbgeng-api.md) を参照。
MEX コマンドの詳細は [MEX リファレンス](./references/mex-extension.md) を参照。

## Troubleshooting

| 症状 | 対処 |
|------|------|
| `dbgeng.dll not found` | `%temp%\WinDbg.Slow\amd64\dbgeng.dll` が存在するか確認。WinDbg Preview をインストール |
| `DebugCreate failed` | dbgeng.dll のバージョン不一致の可能性。WinDbg Preview を最新版に更新 |
| `OpenDumpFile failed` | ファイルパスが正しいか確認。ファイルが破損していないか確認 |
| シンボルが解決されない | `_NT_SYMBOL_PATH` 環境変数を設定するか、REPL で `.sympath srv*%Temp%\Symbols*https://symweb.azurefd.net` を実行 |
| `WaitForEvent failed` | ダンプファイルが不完全か破損している可能性。別のダンプで試行 |

## Performance Tips — 遅い操作を避ける

大規模なダンプや TTD トレースで以下の操作は非常に遅くなる。回避策を参照:

| 遅い操作 | 理由 | 回避策 |
|---------|------|--------|
| `~*k` (大量スレッド) | 全スレッドのスタックを解決 | **`!mex.us` を使う** (ユニークスタックでグループ化)  または `!mex.lt` でスレッド一覧→ `~N k` で個別取得 |
| `x module!*Name*` | ワイルドカードが広すぎると大量マッチ | パターンを絞る: `x msedge!*PrintCompositor*` のように具体的に。`*Print*` のような短いパターンは `StringPrintf`, `vsnprintf` 等にもマッチして膨大な結果を返す |
| `!analyze -v` (TTD) | TTD トレースでは非常に遅い場合がある | **`!mex.crash`** を使う。または `!tt 100` + `!mex.us` で概要を掌む |
| `lm` (シンボル読み込み) | 初回は全モジュールのシンボルをダウンロード | 初回は時間がかかるが、キャッシュ後は高速。`lm m <pattern>` で対象を限定 |
| 大量の .run を順次解析 | 各トレースのオープンに 1-2 分かかる | バイナリ直接読みで先にプロセス種別を特定し、対象を絞る (Phase 3b 参照) |

### WDS スクリプトのベストプラクティス

```
$$ GOOD: MEX を使った軽量・高速解析
!tt 100
!mex.us         $$ ユニークスタックで全スレッドをグループ化
!mex.lt         $$ スレッド一覧 (状態付き)
!mex.mods       $$ モジュール一覧

$$ BAD: 不必要に遅い
!tt 100
~*k            $$ スレッドが多いと遅い — !mex.us を使う
lm             $$ OK だが !mex.mods がより見やすい
x msedge!*Print*  $$ パターンが広すぎる — StringPrintf 等に大量マッチ
!analyze -v    $$ TTD では非常に遅い — !mex.crash を使う
```
