---
name: dbg-tracing-event-selection
description: "Use when: deciding which edge://tracing or chrome://tracing events/categories to select for log collection, Chromium TraceConfig, Perfetto TrackEvent categories, disabled-by-default categories, memory-infra, cpu_profiler, netlog, rendering jank, startup, navigation, Edge feature trace logs. ログ採取時のイベント選択を判定する。"
argument-hint: "症状、再現手順、対象機能、プラットフォーム、取得時間、個人情報制約"
---

# Trace Event Selection

edge://tracing / chrome://tracing でログを採取するときに、症状と対象機能から選ぶべき trace category/event を判定するスキルです。個別の `TRACE_EVENT` 名を直接選ぶのではなく、event を有効化する category を選びます。

## When to Use

- ユーザーが「どのイベントを選べばよいか」「どの category を有効にすべきか」を聞いている
- Edge/Chromium のパフォーマンス、ハング、クラッシュ直前、ナビゲーション、描画、入力、ネットワーク、メモリ、起動、V8/JS、WebUI、Edge 固有機能の trace 採取を設計する
- Perfetto UI / chrome://tracing JSON / protobuf trace のカテゴリ指定を作る
- `disabled-by-default-*`、`memory-infra`、`cpu_profiler`、`netlog.sensitive` を入れるべきか判断する

## Inputs

最初に不足している必須情報だけ確認する。

- 症状: 何が遅い、固まる、欠落する、順序がおかしいのか
- 再現手順: 操作、URL 種別、対象ページ、ユーザー操作、発生までの時間
- 対象: Edge 固有機能名、Chromium コンポーネント、関係するファイルやクラス名
- プラットフォーム: Windows/macOS/Linux/Android/ChromeOS、Edge branded build か
- 採取時間: 短い再現か、長時間待ちの intermittent か
- 取り扱い制約: URL、ヘッダー、Cookie、個人情報、企業情報を含めてよいか
- 出力先: edge://tracing / chrome://tracing UI、Perfetto UI、TraceConfig JSON、コマンドライン

## Bundled References

通常のスキル利用時に Chromium/Edge/Perfetto の実装ファイルを直接読まない。判断に必要な情報は次の reference から取得する。

| Reference | 内容 | いつ読むか |
|---|---|---|
| [trace selection reference](./references/trace-selection-reference.md) | 症状別 category matrix、TraceConfig/Perfetto rules、privacy/cost rules、Edge 固有 category の注意 | すべての通常利用 |
| [selectable categories snapshot](./references/selectable-categories.md) | edge://tracing の `Record Categories` と `Disabled by Default Categories` に出る non-group category の全一覧 | category 名の存在確認、UI 選択名の確認 |
| [source snapshot notes](./references/source-snapshot-notes.md) | reference 作成時点で要約した実装根拠と更新手順 | reference を更新するときのみ |

Reference にない category や最新実装との差分が疑われる場合でも、勝手に実装を探索しない。回答では「reference には未掲載」と明記し、ユーザーが明示的に依頼した場合だけ source snapshot を更新するための調査を行う。

## Core Rules

- chrome://tracing の「イベント選択」は実質的に category 選択である。個別 event 名は trace 内に出る名前で、通常 UI では直接選ばない。
- 登録済み category を優先する。新しい名前を提案しない。登録済みかどうかは [trace selection reference](./references/trace-selection-reference.md) で確認する。
- `disabled-by-default-*` は高コストまたは詳細情報向け。通常の再現では必要最小限だけ明示的に選ぶ。
- category group は UI に出ないことがある。reference に group が載っている場合は、group 名ではなく構成要素 category を選ぶ。
- Edge 固有 category は Edge branded build 限定の可能性がある。reference の Edge category 注意を確認する。
- 直接 Perfetto `TrackEventConfig` を書く場合は、Chromium `TraceConfig` と完全に同じ意味だと思い込まない。特定カテゴリだけ採るなら `disabled_categories: "*"` と `enabled_categories` の組み合わせを基本にする。
- URL、ヘッダー、入力値、アカウント情報を含み得る category は明示的に警告する。特に `disabled-by-default-netlog.sensitive` は許可がない限り提案だけに留める。

## Workflow

### 1. 調査目的を分類する

ユーザーの症状を次のいずれかに分類し、複数にまたがる場合は primary と secondary を分ける。

- 起動、タブ作成、初期化
- ナビゲーション、ページロード、リダイレクト、Service Worker
- ネットワーク、DNS、TLS、proxy、キャッシュ、WebSocket
- 入力、スクロール、クリック、タッチ、IME
- 描画、compositor、GPU、jank、frame drop、レイアウト
- CPU 高負荷、ハング、長い task、thread pool
- メモリ、リーク、OOM、Java heap、partition allocator
- V8/JavaScript、DevTools timeline、WebAssembly
- メディア、音声、WebRTC、capture、DRM
- 印刷、印刷プレビュー、PDF 印刷
- 拡張機能、WebUI、Edge 固有機能

### 2. Reference から category を選ぶ

1. [trace selection reference](./references/trace-selection-reference.md) を読み、症状別 matrix と category catalog から候補を選ぶ。
2. 対象機能名がある場合は、reference 内の Edge/Chromium feature category と照合する。
3. [selectable categories snapshot](./references/selectable-categories.md) で、提案する category が `Record Categories` または `Disabled by Default Categories` のどちらに出るか確認する。
4. category group は構成要素 category に分解して提案する。
5. reference に対象機能の専用 category がない場合は、症状ベースの最小候補を作り、「専用 category は reference 未掲載」と明記する。
6. ユーザーが「最新実装で確認して」「reference を更新して」と明示した場合だけ、[source snapshot notes](./references/source-snapshot-notes.md) の更新手順に従って実装確認を行う。

### 3. 推奨セットを 3 層で作る

必ず次の形で出す。

1. Baseline: 文脈を失わない低コスト category。症状に必要なものだけ。
2. Targeted: 対象機能名や reference の category glossary から選んだ category。
3. Deep/Optional: `disabled-by-default-*`、profiler、memory dump、sensitive netlog など。コストとプライバシー条件を添える。

各 category には「なぜ必要か」「期待できる event の種類」「注意点」を短く付ける。

### 4. 症状別 matrix と config rules を適用する

症状別 matrix、TraceConfig guidance、Perfetto config guidance、privacy/cost rules は [trace selection reference](./references/trace-selection-reference.md) を使う。本文にない細目はすべて reference を優先する。

## Output Format

回答はこの順で簡潔に出す。

1. 推奨カテゴリセット: Baseline / Targeted / Deep optional
2. 選定理由: 症状との対応、reference 上の category 根拠
3. 採取オプション: duration、record mode、buffer、protobuf/json、必要なら memory dump config
4. 注意点: overhead、プライバシー、Edge branded build 限定、OS 限定
5. 確認方法: 採取後に Perfetto/trace JSON で見るべき event name、category、track、プロセス

## Quality Checks

完了前に確認する。

- 通常利用中に実装ファイルへ直接移動していない
- 提案 category が reference に掲載済み、または reference 未掲載であることを明記した
- 提案 category が `Record Categories` か `Disabled by Default Categories` のどちらに出るか確認した
- category group を UI 選択名としてそのまま出していない
- `disabled-by-default-*` を入れる理由が明確で、最小限に抑えている
- `netlog.sensitive` や memory/profiler などの privacy/cost 注意を明記した
- Edge 固有 category は Edge branded build 限定の可能性を明記した
- 最終回答に、実際に選ぶ category 名をコピーしやすい形で含めた

## Example Prompts

- `edge://tracing でスクロール jank を調べるときに選ぶイベントを判定して`
- `chrome://tracing のネットワーク遅延調査用 category セットを作って。sensitive 情報は避けたい`
- `Edge の edge_settings 周りの WebUI フリーズを採る TraceConfig を提案して`
- `memory-infra を含む Perfetto protobuf trace の category と注意点を整理して`