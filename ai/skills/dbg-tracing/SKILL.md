---
name: dbg-tracing
description: "Use when: analyzing logs captured by edge://tracing or chrome://tracing Record, Perfetto UI traces, Chrome JSON trace files, protobuf .perfetto-trace/.pftrace/.pftrace.gz files, gzip trace expansion on macOS/Linux/Windows PowerShell, Trace Processor SQL, slice/track/args tables, Chrome stdlib modules, startup, navigation, rendering jank, input latency, CPU hang, memory, network, print/PDF, Edge feature trace analysis. 採取済み trace ログを解析する。"
argument-hint: "trace file path or attachment, symptom, reproduction window, platform, selected categories, privacy constraints"
---

# Trace Log Analysis

edge://tracing / chrome://tracing の Record 機能で採取済みの trace ログを解析し、原因候補、根拠 event、次の採取改善案をまとめるスキルです。

## When to Use

- ユーザーが edge://tracing / chrome://tracing の採取済みログを解析したい
- Perfetto UI、Trace Processor、Chrome JSON trace、protobuf `.perfetto-trace` の読み方や SQL を求めている
- startup、navigation、page load、input latency、scroll jank、rendering、GPU、CPU hang、memory、network、WebRTC、print/PDF、Edge 固有機能の trace から原因を切り分けたい
- trace に含まれる event/category/track/process/thread/counter/args を根拠に調査メモやサポート回答を作りたい

## Inputs

不足している必須情報だけ確認する。

- Trace file: パス、添付、拡張子、サイズ、圧縮有無
- 症状: 何が遅い、止まる、失敗する、順序がおかしいのか
- 再現 window: 問題発生のおおよその時刻、操作開始/終了、スクリーンショットや動画の時刻
- Platform/build: OS、Edge/Chrome、Stable/Beta/Dev/Canary、version、profile 条件
- Capture context: 選択した category、Record mode、duration、buffer full の有無
- Privacy: URL、host、header、account、document title、PII を回答に含めてよいか
- Desired output: 要約、根拠 SQL、イベント一覧、タイムラインの見方、再採取条件

## Bundled References

通常のログ解析では Chromium/Edge/Perfetto の実装ファイルを直接読まない。判断に必要な情報は次の reference から取得する。

| Reference | 内容 | いつ読むか |
|---|---|---|
| [analysis workflow](./references/analysis-workflow.md) | 解析全体の流れ、ファイル形式判定、Perfetto UI/Trace Processor の使い分け、品質チェック | すべての通常利用 |
| [perfetto sql cookbook](./references/perfetto-sql-cookbook.md) | Trace Processor の基本テーブル、Chrome stdlib modules、症状別 SQL snippets | SQL でログを読むとき |
| [scenario playbooks](./references/scenario-playbooks.md) | 症状別に見る track/event/table、原因仮説、再採取時の category 補強 | 症状分類後 |
| [source snapshot notes](./references/source-snapshot-notes.md) | reference 作成時点で要約した実装根拠と更新手順 | reference を更新するときのみ |

Reference にない event/table/format 差分が疑われる場合でも、通常解析では source tree を探索しない。回答では「reference では未確認」と明記し、ユーザーが明示的に reference 更新を依頼した場合だけ source snapshot を更新するための調査を行う。

## Core Rules

- まず trace が開けるか、解析対象 window が含まれているか、buffer が欠けていないかを確認する。
- UI の見た目だけで断定せず、可能なら Trace Processor SQL の `slice`, `track`, `process`, `thread`, `args`, `counter` で根拠を取る。
- Chrome/Edge trace の時刻は Trace Processor では nanoseconds 単位。Chrome JSON の `ts` は microseconds で記録されることが多いが、Trace Processor に入れた後は ns として扱う。
- 解析中は workspace の Chromium/Edge 実装に依存しない。event 名の意味は bundled reference と trace 内の category/name/args/process/thread から判断する。
- URL、host、headers、document title、account、file path、printer name などは privacy-sensitive として扱い、必要最小限だけ引用する。
- trace に必要 category が含まれていない場合は、原因を断定せず「観測不能」と書き、再採取時に追加する category を提案する。必要なら `dbg-tracing-event-selection` skill を使う。

## Workflow

### 1. 解析準備

1. [analysis workflow](./references/analysis-workflow.md) を読み、ファイル形式と解析ツールを決める。
2. trace file の存在、サイズ、圧縮有無、JSON/protobuf の見分けを確認する。`.pftrace.gz` は [analysis workflow](./references/analysis-workflow.md) の展開手順に従い、元ファイルを残して展開する。
3. trace を開けない場合は、破損、圧縮、unsupported JSON feature、巨大ファイル、PII 制約のどれかに分類して次の手を出す。
4. 症状の再現 window が不明なら、trace 全体の期間と major slices/counters から候補 window を探す。

### 2. Broad triage

1. [perfetto sql cookbook](./references/perfetto-sql-cookbook.md) の Basic Inventory を使い、process/thread/track/category/event 分布を確認する。
2. 長い `slice`、密集した `toplevel`/scheduler task、main thread 停止、renderer/browser/GPU process の偏りを見る。
3. `stats`, `metadata`, `trace_bounds`, buffer 由来の欠損兆候を確認する。
4. 症状が category 不足で見えない場合は、ここで解析を止めず、観測できる範囲と不足 category を分けて報告する。

### 3. Scenario analysis

1. [scenario playbooks](./references/scenario-playbooks.md) で primary scenario を選ぶ。
2. scenario に対応する Chrome stdlib module または generic SQL を実行する。
3. root cause 候補を「観測事実」「関連 event」「未確認点」に分ける。
4. 必ず反証も探す。例: CPU hang に見えても main thread が runnable 待ちか、I/O wait か、GPU/renderer 側の待ちかを切り分ける。

### 4. Evidence package

回答には次を含める。

1. Executive summary: 何が起きていたか、確度、影響範囲
2. Evidence: event/table/query、timestamp、duration、process/thread、category/name
3. Timeline: 問題 window の前後関係
4. Interpretation: なぜそれが症状と対応するか
5. Gaps: trace では見えないこと、category 不足、buffer 欠損、PII により伏せた情報
6. Next capture: 再採取するなら追加すべき category、duration、buffer、privacy 注意

## Output Format

通常回答はこの順で簡潔に出す。

1. 結論
2. 根拠イベント/SQL 結果
3. 時系列
4. 未確認点と制約
5. 次のアクションまたは再採取条件

SQL を出す場合は、実行目的を 1 行添えてから fenced `sql` block にする。ユーザーが望まない限り、巨大な raw event dump は貼らず、上位 N 件や要約にする。

## Quality Checks

完了前に確認する。

- 通常解析中に Chromium/Edge/Perfetto の実装ファイルを直接根拠としていない
- trace file が開けたか、開けなかった場合は理由と代替手段を示した
- 症状 window、process/thread、event name、duration を根拠として示した
- category 不足や buffer 欠損がある場合、断定を弱めて明記した
- privacy-sensitive な URL/header/account/file path/printer name を不用意に出していない
- SQL は copy/paste 可能で、Perfetto UI Query tab または `trace_processor` shell で動く形にした
- 次の採取改善案が必要な場合、category 選定は `dbg-tracing-event-selection` skill/reference と整合している

## Example Prompts

- `この edge://tracing の JSON trace から印刷エラーの原因候補を解析して`
- `chrome://tracing で採った protobuf trace を Perfetto SQL で見る手順とクエリを出して`
- `スクロール jank の trace で、どの frame が遅れているか調べて`
- `起動が遅い trace から browser main thread の詰まりを探して`
- `Trace Processor でこのログに含まれる category と長い slice を一覧化して`
