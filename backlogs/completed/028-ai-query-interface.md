# [P0] AI が部分グラフを照会できる CLI を実装する

- 種別: 実装 / M4
- 対象: `src/Srcnet.Storage/Reader.fs`, `src/Srcnet.Storage/`, `src/Srcnet.Cli/Args.fs`, `src/Srcnet.Cli/Commands.fs`, `tests/Srcnet.Tests/`, `docs/query-and-cli.md`
- 依存: 019
- 参照: [クエリと CLI](../docs/query-and-cli.md) 1-5, [ストレージ](../docs/storage.md) 4, 7, 8, [ロードマップ](../docs/roadmap.md) M4

## 背景

現在の CLI は `index` / `stats` / `verify` だけである。生成物は memory-mapped 読み取りを前提にした独自バイナリ形式なので、AI エージェントは `.nodes` や CSR セグメントを直接読んでも、必要なノードとエッジを安全かつ小さく取得できない。

実際の Chromium 系コーパスでは 1,152,071 ノード、1,152,070 エッジ、約 300 MiB の成果物になった。全体を JSON 化して AI へ渡す方法は、トークン、メモリ、起動時間のすべてで目的に反する。

## やること

- `Reader` の上に、セグメントの詳細を隠す読み取り API を用意する
  - NodeId または密インデックスからノード属性を読む
  - 文字列テーブルから名前、修飾名、論理パスを復元する
  - エッジ種別、向き、深さを指定して CSR を辿る
  - mmap の view と返したデータの寿命を API 上で明確にする
- AI がシェルから呼べる照会コマンドを実装する
  - `search <text>`: 名前と論理パスの完全一致、前方一致、部分一致
  - `show <node>`: ノード属性と所属ファイル
  - `neighbors <node>`: 種別、向き、深さを制限した近傍
  - `path <from> <to>`: 深さ上限付き最短経路
  - `context <keywords...>`: 複数の検索結果と近傍を予算内へまとめる
- すべての照会に決定的な JSON 出力を用意する
  - `schemaVersion`, `query`, `nodes`, `edges`, `diagnostics`
  - `truncated`, `omittedCount`, `tokenEstimate`
  - 同点時はノード種別、確度、NodeId の順で必ず一意に並べる
- 出力量を必ず有界にする
  - `--limit`, `--depth`, `--budget` に上限を設ける
  - 超過時は黙って捨てず、打ち切りと省略件数を返す
  - ソース本文は既定で含めず、定義位置だけを返す
- 検索索引がまだ存在しない成果物では、全走査へ黙って退行しない。未対応を明示するか、安全な上限内のファイル・ディレクトリ検索だけを提供する
- AI エージェント向けに、CLI を一次界面として使う最小例を `docs/query-and-cli.md` へ追加する。MCP アダプターはこのチケットに含めない

## 設計上の注意

- 成果物全体を managed object へ展開しない。照会に必要なセグメントとページだけを mmap で読む
- マニフェストを読み取る前後で比較し、再索引との競合時に世代を混在させない
- 外部入力の NodeId、エッジ種別、深さ、件数、トークン予算を検証する。無制限 BFS と巨大な JSON 出力を許さない
- CJK の検索キーは Unicode scalar value と正規化済み文字列を使い、UTF-8 バイト境界で分割しない
- CLI があれば Copilot CLI などのエージェントはシェル経由で利用できるため、ネットワーク待ち受けや専用 SDK を先に追加しない

## 完了条件

- 生成済みの Chromium 系成果物に対し、AI が `search` → `show` → `neighbors` / `context` を JSON で実行できる
- すべてのコマンドが指定予算内の出力になり、打ち切り時に省略件数を報告する
- 同じ成果物と引数から、macOS と Windows でバイト単位に同じ JSON が出る
- CJK のファイル名と識別子について、完全一致・前方一致・部分一致のテストが通る
- 破損セグメント、未知の NodeId、過大な深さ、再索引との競合を明示的な診断として返し、クラッシュや無限探索を起こさない
- warm 状態の照会時間とピーク RSS を [性能](../docs/performance.md) の目標に照らして記録する
