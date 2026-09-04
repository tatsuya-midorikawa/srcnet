---
name: lean-exec
description: '精度を保ちながら GitHub Copilot のトークンと AI クレジットを抑える実行プロトコル。要件予測と shortfall matching による能力ベースのモデル選択、事前計測、3 段絞り込み、単価差を織り込んだ委譲判断、CLI の /fleet と VS Code の /subAgent の使い分け、prompt cache を守る sticky ルーティング、QR / CS / Misroute での検証を扱う。Use when: トークン削減, コスト削減, コンテキスト節約, 効率的な調査, /fleet, /subAgent, subagent, parallel subagents, model routing, capability routing, shortfall matching, token budget, prompt cache, long session optimization.'
argument-hint: '実行したいプロンプト、または最適化したいタスクの内容'
user-invocable: true
---

# lean-exec — 最小トークンで最大精度の実行プロトコル

与えられたプロンプトを、**正確さを一切落とさずに**、消費トークンを可能な限り抑えて完遂させる。

削るのは重複・未使用・冗長な言い回しだけで、検証の手数は削らない。安くするために結論を弱めるのは失敗とみなす。

## 二部構成で決める

しきい値を 1 本引いて「大きいか小さいか」で決めない。**問いは 2 つある。**

| 問い | 決めるもの | 道具 |
| --- | --- | --- |
| どれだけ読むか | 経路 (INLINE / SUBAGENT / FLEET) | `scripts/scope.py` |
| どんな能力が要るか | モデルと reasoning effort | `scripts/route.py` |

この 2 つは独立している。資料が 200 KB でも中身が定型の抽出なら軽量モデルで足り、数行でも仕様解釈なら高推論モデルが要る。**量の軸に能力の判断を畳み込むと、どちらの判断も鈍る。**

能力の側はさらに 2 段に割れる。要件を予測するところだけが判断で、どのモデルが何をできるかは `config/models.json` にある。だからモデルの追加・削除・値付け替えは設定編集だけで済む。

どちらのスクリプトも LLM を呼ばない。**判定そのものは課金されない。**

## 常に効く 6 つ

1. **文脈は「残りターン数ぶん」課金される。** 効くのは資料サイズではなく `資料サイズ × 残りターン数`。
2. **ターン数が乗数。** 独立したツール呼び出しは 1 メッセージにまとめる。**最も手軽で効果が大きい。**
3. **読む前に測る。** 量を知らずに読み始めない。
4. **委譲は文脈を隔離し、同時に単価を下げる。** 量だけで判断すると後者の裁定が見えない。
5. **並列化は総額削減を保証しない。** 固定費がエージェント数ぶん乗る。総額は実測する。
6. **モデルは前もって選ぶ。** 安く試して駄目なら上げる、をしない。読み込みぶんは戻ってこない。

## Phase 0 — 分類と要件予測

プロンプトを Lookup / Investigate / Change / Author のどれかに置く。Lookup を委譲するのが最頻の無駄。Investigate をインラインで押し切ると文脈が破裂する。

分類と同時に要件を測る。

```bash
python3 .github/skills/lean-exec/scripts/route.py --role analyst --prompt "$PROMPT"
```

```
req: reasoning=0.90 code_gen=0.19 debugging=0.45 tool_use=0.25 gamma=0.90 tier=T3
role=analyst tau=0.060 pool=16 surface=cli gates=config
model=gpt-5.6-sol effort=medium cost=9.00 shortfall=0.028 basis=cheapest_eligible
```

4 次元 (`reasoning` / `code_gen` / `debugging` / `tool_use`) を独立に見るのが要点になる。**ある次元の余剰は別の次元の不足を埋めない。** 1 本のスコアに潰すと、片方の次元だけ強い中位モデルを使えなくなる。

予測を信用できないときは `--requirements 0.8,0.2,0.6,0.1` で見立てを直接渡す。判定の中身は `references/models.md`。

## Phase 1 — 実測

読む前に、取り込む必要のある資料の量を測る。**本文は出さない。**

```bash
python3 .github/skills/lean-exec/scripts/scope.py \
  --grep 'PATTERN' --ext .cc,.h --threads 3 --turns 20 \
  --parent-model gpt-5.6-sol:max --agent-model claude-haiku-4.5 src/
```

`--parent-model` と `--agent-model` を渡すと単価差が投影に入る。**損益分岐が `S ≥ 3F` から `S ≥ 3F × (子の単価 ÷ 親の単価)` に変わり、`15k` の境界も同じ比率で縮む。** 親が高いほど、小さい資料でも委譲が正当化される。式の導出は `references/budget.md` §2.5。

**`files=0` を「小さい」と読み替えない。** `NO_MATCH` / `NO_TEXT` / `INCOMPLETE` は測れていないという意味で、量は未知のままである。

参照禁止・出力専用の `.cases`、`.scripts`、`.output`、`KQL` は自動で除外する。既定では `.gitignore` を尊重する。

計測結果を `route.py --scope-json` へ渡すと、リポジトリ依存で上がっていた要件の下限が外れる。

## Phase 2 — 経路選択

| 条件 | 経路 | 中身 |
| --- | --- | --- |
| `< 2k` | `INLINE` | 自分で読む |
| `2k – 15k` | `INLINE_NARROW` | 3 段絞り込みで自分で読む |
| `≥ 15k` かつ 1 体の安全上限内 | `SUBAGENT` | コスト優先の既定。sync 1 体 |
| 1 体では上限超、2 体なら収まる | `SUBAGENT_BG` | background 2 体 |
| 2 体でも上限超、3 体以上なら収まる | `FLEET` | todos 化して並列委譲 |
| 全スレッドで割っても上限超、または最大ファイル単体が上限超 | `NARROW_FIRST` | **経路選択より先に問いを絞るか対象を割る** |

既定は `--optimize cost` で、入る限り 1 体にまとめる。待ち時間を優先するときだけ `--optimize latency`。

最後の行だけは性質が違う。容量を超えた委譲は**サブエージェント側で静かに溢れる**。返ってくる要約からは分からないので、ここではコストより安全上限を優先する。

**単価が下がらないなら、委譲しても得はしない。** 同じモデルへの委譲は文脈の隔離にしかならないので、`S × 残りターン数` が大きいときだけ選ぶ。

## Phase 3 — 3 段絞り込み

どの経路でも資料の触り方は同じ。段を飛ばさない。**ビルトイン ツールを先に使う** — シェル経由の `rg` は往復が増え、出力が大きいと一時ファイルに退避されて読み直しが要る。

1. **数える** — `rg` (`output_mode: "count"` / `"files_with_matches"`)
2. **位置を出す** — `rg` (`output_mode: "content"`, `-n`, `head_limit: 5`)
3. **範囲を読む** — `view` に `view_range`。ファイルの丸読みはしない

シンボルの参照元は `usages` (LSP) が最短。ツール出力が一時ファイルへ退避されたら、そのファイルも `view` + `view_range` で狭く読む。

## Phase 4 — 実行

- 独立した呼び出しは 1 メッセージにまとめる
- すべてのコマンド出力に上限をかける (`| head -N`、`-m N`、`--oneline -n 20`)
- 進捗メモは散文で書かず `sql` ツールの `todos` に置く
- **委譲するたびに `route.py --role` を引き、返ったモデルと effort を明示する。** 省略すると親のモデルが継承され、探索まで高い単価で回る
- 同じ資料への追加質問は `write_agent` で追撃する。範囲が変わるなら立て直す
- background に投げたら完了通知を待つ。`read_agent` でポーリングしない
- 手がかりが 3 手増えなければ手法を変える
- 失敗した試行で汚れたら `/rewind` (`esc esc`) で巻き戻す

## Phase 5 — 検証と報告

ここは削らない。

- 結論を左右する箇所は、要約ではなく一次情報 (`file:line`、コマンド出力) を自分で確認する
- ビルド・テスト・lint・再現確認を実際に走らせ、結果を見る
- 報告は結論から書く。前置きと実況を書かない。コードと差分は貼らず `file:line` で指す
- 日本語の文章品質は `natural-japanese` スキルの規範に従う

## 委譲するときの必須ルール

判定は面に依存しない。違うのは起動方法と制約だけである。

| | GitHub Copilot CLI | VS Code の GitHub Copilot |
| --- | --- | --- |
| 並列 | `/fleet` | `/subAgent` |
| 単発 | `task` ツール | `runSubagent` ツール |
| コスト上限 | なし | **子は親のコスト階層を超えられない** |
| 消費の確認 | `/usage`、`/context` | サブエージェント欄のホバー |

- **VS Code では高い方向への逃げ道がない。** `--surface vscode --parent-model <親>` を渡す。上限のせいで品質が落ちたときだけ警告が出るので、出たら親を上げるかその役割だけ親自身で実行する
- **並列委譲は先に todos を作ってから撃つ。** 空のまま撃つと分解の往復が丸損になる
- **todo ごとに `route.py --role` を引く。** 全部を同じモデルで回すのは、いちばん重い todo に全体を合わせること
- **サブエージェントには行数上限つきの返答フォーマットを渡す。** 上限がないと長い引用が親へ戻り、委譲の意味が消える
- **撃たない条件**: 独立スレッドが 2 本以下 / 資料が小さく単価も変わらない / 依存が強く結局直列になる / 結果を見ないと次が決まらない作業

手順、指示テンプレート、面ごとの起動は `references/routing.md` §3.5–5。

## セッションの途中でモデルを替えない

**モデルを替えると prompt cache は丸ごと無効になる。** 差額よりキャッシュを捨てた損のほうが大きくなりやすい。選び直すのは会話の最初のターン、`/compact` の直後、背景要約の直後の 3 つだけ。

```bash
route.py --role orchestrator --prompt "$PROMPT" --turn 6 --current-model claude-sonnet-5 --reroute
```

`route.py` が既定でこう振る舞う。キャッシュはモデル単位なので**保持中も effort は選び直す**。損益の式は `references/budget.md` §1。

## カタログが変わったら設定を直す。散文は直さない

**モデルの正本は `config/models.json` だけである。** `enabled: true` で、`cost` と `capability` が定義されたモデルだけを候補にする。

モデルの増減・値付け替えは同ファイルの編集だけで完了する。スクリプトと散文には触らない。校正実績がないモデルは `enabled: false` のまま置く。`scripts/test_model_policy.py` が設定と散文の整合を検査する。

有効なモデルが 0 件、または有効なモデルの設定が欠けている場合は設定エラーで止める。能力が足りずに適格な候補がない場合だけ、最小 shortfall のモデルへ fail-open する。

## 検証コストは削らない

- 根拠のない主張を結論に採用しない。サブエージェントには根拠の所在を必ず書かせる
- 「証拠がない」を「存在しない」と混同しない。`NO_MATCH` も同じ
- 打ち切り条件を着手前に決める
- 最終検証で effort を下げない。`verifier` の `tau` を緩めない
- 容量を超える委譲を撃たない。溢れたことは返答からは分からない
- 自信が持てないなら `recheck` スキルで否定的に洗い直す

削ってよいのは**失敗を検出できる**削減だけである。軽量モデルへの切り替えは `file:line` を突き合わせれば外したと分かる。一方、最終検証の effort を落とす・容量超過の委譲を撃つ・要約を裏取りせずに採用する、はいずれも**返ってきたものが正しく見えてしまう**。この 3 つには手を付けない。

`route.py --eval` に過去タスクを渡すと、**QR** (安くしすぎ) と **Misroute** (高く買いすぎ) が逆方向から見張る。定義は `references/budget.md` §6.5。

CLI レバー (`/usage`、`/mcp`、`/rewind`、`/compact` など) の使い分けは `references/budget.md` §5.5。VS Code では並列委譲が `/subAgent`、消費の確認はホバーになる。

## 短い作業では、この skill 自体が赤字になる

スクリプトは LLM を呼ばないので判定はタダだが、**この SKILL.md を読むことには数千トークン × 残りターンがかかる。** 参照を 1 つ開けばさらに乗る。

したがって **1〜2 ターンで終わる作業や、資料が数 KB しかない作業では開かない。** 原則の 2 つ — 独立した呼び出しは 1 メッセージにまとめる、読む前に量を測る — だけ守れば足りる。

参照はさらに opt-in で、次の場合だけ開く。

| 参照 | 開くとき |
| --- | --- |
| `references/budget.md` | しきい値の根拠 / 固定費 `F` の校正 / 単価差の式 / 浪費の特定 |
| `references/routing.md` | サブエージェント指示を書く / 並列委譲の todo 設計 / 面ごとの起動方法 |
| `references/models.md` | 要件予測と shortfall matching の中身 / 設定の校正 / 役割ごとの `tau` の根拠 |

## 出典

モデル選択の設計は HyDRA (arXiv:2605.17106) に基づく。採用と逸脱は `references/models.md` §8。

## 関連スキル

- `compact-plus` — `/compact` の直前に作業状態を退避する
- `recheck` — 出した結論を否定的に再確認する
- `natural-japanese` — 日本語の出力品質
