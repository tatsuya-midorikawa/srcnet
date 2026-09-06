# クエリと CLI

## 1. 方針

srcnet は自然言語を理解しない。クエリは **字句一致 + 構造探索 + 決定的ランキング** で解く。埋め込みも LLM も使わないため、「意味的に近い」ではなく「名前が一致し、グラフ上で近く、中心性が高い」で答える。

この割り切りを利用者に隠さない。曖昧な入力に対しては、推測した単一の答えではなく候補と根拠を返す。

## 2. サブコマンド

| コマンド | 用途 |
| --- | --- |
| `srcnet index <path>` | インデックス生成（完全 / 増分） |
| `srcnet search <text>` | シンボル・パスの検索 |
| `srcnet show <node>` | ノードの属性と定義位置 |
| `srcnet neighbors <node>` | 近傍探索（種別・向き・深さを指定） |
| `srcnet path <a> <b>` | 2 ノード間の最短経路 |
| `srcnet impact <files...>` | 変更の影響範囲 |
| `srcnet explain <node>` | 構造的事実の定型出力 |
| `srcnet context <keywords...>` | 予算内に収めた文脈パック |
| `srcnet stats` | グラフ統計 |
| `srcnet verify` | 整合性・決定性の検証 |

### 2.1 主なオプション

```text
srcnet index <path>
  --out <dir>            出力先（既定 <repo>/.srcnet）
  --incremental          変更ファイルのみ再抽出
  --jobs <n>             並列度（結果には影響しない）
  --memory-limit <size>  ピーク メモリ上限
  --tier <0|1|2|3>       抽出段階（3 は未実装）
  --assume-encoding <名> 符号化が曖昧なファイルへ適用する符号化
  --no-gitignore         .gitignore を無視
  --allow-partial        不完全な走査結果での上書きを許可

srcnet search <text>
  --root <path>          解析ルート（生成物の位置を決めるために使う）
  --ignore-case          大文字小文字を畳んで検索する

srcnet neighbors <node>
  --edge <kind,...>      エッジ種別（既定は成果物が持つすべて）
  --direction in|out|both
  --depth <n>
  --include-ambiguous    AMBIGUOUS なエッジも含める

照会（search / show / neighbors / path / context）の共通:
  --root <path>          解析ルート
  --limit <n>            返すノード数の上限（既定 50、上限 10000）
  --depth <n>            探索の深さ（既定 1、上限 16）

共通:
  --json                 機械可読出力
  --budget <tokens>      出力トークン予算（既定 8000、上限 1000000）
  --repo <id>            複数リポジトリ時のスコープ
```

`<node>` にはノード ID（16 進 32 桁）か、名前・修飾名の**完全一致**を渡す。一致が複数あるときは
推測で 1 つに絞らず、候補を `stderr` へ並べて終了コード 1 で終わる。所属パスの一致では解決しない。
ファイル名を渡したときに、そのファイルの全シンボルが候補になるのを避けるためである。

### 2.2 終了コード

| コード | 意味 |
| --- | --- |
| 0 | 成功 |
| 1 | 該当なし（検索結果が空など、正常な結果） |
| 2 | 利用者入力の誤り |
| 3 | 生成物が存在しない、または形式版が非互換 |
| 4 | 処理は完了したが診断あり（部分的失敗） |
| 5 | 中断された |
| 70 | 内部エラー |

`stdout` には結果のみを出力し、進捗・警告・診断は `stderr` に出す。これによりパイプ処理と機械利用が安全になる。

## 3. ランキング

検索結果の順位は次の辞書式で決定する。すべて決定的である。

1. 一致の強さ（完全一致 > 前方一致 > 部分一致）
2. 一致した対象（シンボル名 > 修飾名 > パス）
3. ノード種別の優先度（定義 > 宣言）
4. フラグによる減点（`vendored`、`generated`、テストは順位を下げる）
5. 中心性スコア
6. 正規化パスの辞書順（最終的な同点解消）

6 段目を必ず置くことで、同点時も順序が一意に定まる。

## 4. トークン予算

`--budget` は出力の上限トークン数を指定する。AI エージェントのコスト削減が目的であるため、予算制御は中核機能であり任意機能ではない。

- 予算超過時は、ランキング下位から切り詰める
- 切り詰めた場合、`truncated` フラグと省略件数を必ず含める。黙って捨てない
- トークン数は決定的な近似計数で見積もる。特定のモデルのトークナイザには依存しない。見積り方法と誤差は出力に明示する
- ソース抜粋は既定で行範囲のみを返し、`--snippet` を指定した場合のみ本文を含める

## 5. 出力形式

### 5.1 JSON

```jsonc
{
  "schemaVersion": 1,
  "query": { "kind": "neighbors", "node": "...", "depth": 2 },
  "nodes": [
    {
      "id": "…",
      "kind": "Function",
      "name": "…",
      "qualifiedName": "…",
      "path": "…",
      "lines": [120, 168],
      "flags": ["definition"],
      "community": 12
    }
  ],
  "edges": [
    { "from": "…", "to": "…", "kind": "CALLS", "confidence": "RESOLVED", "evidence": "same-translation-unit" }
  ],
  "diagnostics": [],
  "truncated": false,
  "omittedCount": 0,
  "tokenEstimate": 1820
}
```

`schemaVersion` は独立して版管理し、破壊的変更時に増やす。

`--json` を指定した場合、診断も封筒の中の `diagnostics` に入れる。機械が読む先を 1 つに保ち、
`stdout` を JSON だけに保つためである。テキスト出力では結果を `stdout`、診断を `stderr` へ分ける。

`tokenEstimate` は決定的な近似で、`tokenEstimateMethod` にその方法を明示する。現在の方法は
`ascii/4 + cjk*1 + other/2` で、実際のトークナイザとは 2 割程度ずれ得る。

### 5.2 テキスト

人間と AI の両方が読める簡潔な形式で出力する。端末幅に依存する整形は、東アジア文字幅を考慮する（[移植性と国際化](platform-and-i18n.md)）。

## 6. `explain` の内容

LLM を使わないため、`explain` は散文を生成せず、構造的事実を定型で並べる。

- 定義位置、種別、可視性、所属モジュール、所属コミュニティ
- 呼び出し元・呼び出し先の上位（確度付き）
- 継承・実装関係
- 対応するテスト
- ソースに書かれているドキュメント コメントの**原文抜粋**
- 関連する根拠コメント（`TODO:` / `NOTE:` など）の原文
- ビルド ターゲットと構成ガード

「この関数の役割は〜です」という要約は生成しない。事実の提示に留め、解釈は呼び出し側の AI に委ねる。これが AI コストを移さずに削減する唯一の一貫した方法である。

## 7. エージェント連携

### 7.1 AI エージェントからの最小の使い方

CLI をシェルから呼ぶだけでよい。専用の SDK もネットワーク待ち受けも要らない。

```sh
# 1. 一度だけ索引を作る
srcnet index /path/to/repo --tier 2

# 2. 名前で当たりを付ける（既定で 50 件・8000 トークンに収まる）
srcnet search Area --root /path/to/repo --json --limit 10

# 3. 気になったノードの属性と定義位置を見る（ID か完全一致の名前で指定）
srcnet show 8ac06571c1dd246a51a3bc985dbe8500 --root /path/to/repo --json

# 4. 周囲の構造を辿る
srcnet neighbors 8ac06571c1dd246a51a3bc985dbe8500 --root /path/to/repo \
  --edge CONTAINS,DEFINES --direction out --depth 2 --json

# 5. 複数の語から、予算に収めた文脈をまとめて取る
srcnet context Area Point --root /path/to/repo --json --budget 4000
```

守るべき点は 3 つである。

1. **出力は必ず有界である。** `--limit` と `--budget` に既定値があり、超えたぶんは
   `truncated` と `omittedCount` で報告される。黙って捨てない
2. **ソース本文は返らない。** 既定では定義位置（パスと行範囲）だけを返す。本文が要る場合は
   その位置を使って通常のファイル読み取りを行う
3. **推測しない。** 名前が一意でなければ候補を返す。`confidence` は解決の根拠を表し、
   `AMBIGUOUS` を確定として扱わない

MCP アダプターは同じ照会サービスの上の薄い層として後続で追加する。二重実装は行わない。

### 7.2 実装状況

CLI と JSON 出力を一次界面とする。MCP サーバー モードは、同じ照会サービスの上の薄いアダプターとして後続マイルストーンで追加する（[ロードマップ](roadmap.md)）。二重実装は行わない。

MCP を追加する場合も、既定は標準入出力での接続とし、ネットワーク待ち受けは行わない（[セキュリティ](security.md)）。
