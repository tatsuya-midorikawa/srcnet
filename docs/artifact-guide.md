# 索引・JSON・HTML を使い分けて保存する

**照会を続けるには索引一式を残し、AI には必要な JSON と元ソースだけを渡します。**
このガイドは [入門手順](first-success.md) を終えた人向けです。最初の表で用途を選び、保存や共有が必要になったときに該当する節を参照してください。
以下の変数 `SRCNET`、`SOURCE_ROOT`、`INDEX_DIR`、`WORK` は入門手順で設定したものです。

## 用途ごとに必要なファイルを選ぶ

| 生成物 | 主な利用者・用途 | 同伴するもの | 共有前の確認 |
| --- | --- | --- | --- |
| 索引一式 | CLI・エージェントが継続して調査する | manifest と、そこが参照する全セグメント・全断片 | パス、識別子、抽出コメント、条件式などの機密情報 |
| manifest | 人やスキルが件数・生成条件を確認する | 単独では照会不可。対応するセグメントが必要 | リポジトリ名、言語、診断、生成条件 |
| JSON 応答 | AI や自動化が絞った結果を扱う | 元ソースを読むには対応するソース配置 | 結果のパス、名前、診断。打ち切り・世代も保持する |
| HTML | 人が指定範囲の関係を確認する | 閲覧だけなら単一ファイル | 埋め込まれた名前・パス・属性。部分表示であること |
| 元ソース | 人・AI が定義や現在の動作を確認する | JSON の相対パスに対応するソースルート | ソース自体の機密情報。索引より厳しい共有権限の場合がある |
| `agent-context.json` | エージェントがこの端末のパスを再利用する | 選択した索引の隣に置く | 実行ファイル・ソース・索引の絶対パス。通常は共有しない |

バイナリセグメントや HTML 全体を AI に読ませる必要はありません。HTML は索引の代用品ではなく、manifest 単独もグラフの代用品にはなりません。

## AI に調査を依頼する

macOS の例です。PowerShell では同じ引数で `& $SRCNET ...` と呼び、JSON を `ConvertFrom-Json` で解析します。

```sh
"$SRCNET" context shapes.c --out "$INDEX_DIR" --json --limit 20 --budget 2000 > "$WORK/context.json"
plutil -p "$WORK/context.json"
"$SRCNET" freshness shapes.c --root "$SOURCE_ROOT" --out "$INDEX_DIR" --json
```

AI には問いと、この応答から必要なノード・エッジ・診断・省略情報を渡します。
返却された `path` と `lines` はソースの位置であり、本文ではありません。[入門の行範囲確認](first-success.md#3-元の行範囲と-html-を確認する) と同様に、元ファイルへ戻って確かめます。
鮮度確認の検証例は `checkedFiles: 1`、`uncheckedFiles: 9`、`status: "unchanged"` でした。
残りのファイルが最新であるとは判断できません。

エージェントへ manifest の場所だけを渡したい場合は、信頼済みの CLI を明示してローカル設定を保存します。

```sh
"$SRCNET" agent-context --out "$INDEX_DIR" --write --root "$SOURCE_ROOT" --executable "$SRCNET" --json
"$SRCNET" agent-context --out "$INDEX_DIR" --json
```

通常の照会はこの設定も manifest も書き換えません。設定を読めたことは、記載されたプログラムを実行してよい根拠になりません。
明示した索引の場所と信頼済みのユーザー設定を優先します。旧形式の移行は [配布スキル](../ai/skills/README.md) を参照してください。

## 人が HTML で関係を確認する

入門で出した HTML は、全体 30 ノードに対して起点周辺の 3 ノードを表示しました。
`truncated: false` は選択した範囲が上限内だったという意味で、全体表示という意味ではありません。
存在しないように見えるディレクトリが、本当に索引にないかは CLI で確認します。

```sh
"$SRCNET" export html --out "$INDEX_DIR" --max-nodes 1 --file "$WORK/limited.html" --json
```

この上限付き出力の件数・`truncated`・省略情報を、元の索引の件数と比較してください。
HTML の選択範囲を変えても索引の内容は変わりません。HTML に載らないノードも、索引があれば別の照会で参照できます。

## 書き手を止めてから索引一式をコピーする

**コピー中は index と設定更新を実行しないでください。** 元の索引に書き手がいない状態を確保できない場合は、専用の出力先へ生成した索引を使います。
manifest とセグメントを別々の世代からコピーすると欠損します。ロックや staging を手動削除して解決しないでください。

macOS では、まだ存在しない宛先に目録と実データだけをコピーします。

```sh
MOVED_INDEX="$WORK/shared-index"
mkdir "$MOVED_INDEX"
cp "$INDEX_DIR/manifest.json" "$MOVED_INDEX/manifest.json"
cp -R "$INDEX_DIR/segments" "$MOVED_INDEX/segments"
"$SRCNET" verify --out "$MOVED_INDEX" --json
"$SRCNET" search shapes.c --out "$MOVED_INDEX" --json --limit 1 --budget 1000
```

PowerShell では次の形です。`agent-context.json`、ロック、一時ファイルはコピー対象にしていません。

```powershell
$MOVED_INDEX = Join-Path $WORK "shared-index"
New-Item -ItemType Directory -Path $MOVED_INDEX | Out-Null
Copy-Item -LiteralPath (Join-Path $INDEX_DIR "manifest.json") -Destination (Join-Path $MOVED_INDEX "manifest.json")
Copy-Item -LiteralPath (Join-Path $INDEX_DIR "segments") -Destination (Join-Path $MOVED_INDEX "segments") -Recurse
& $SRCNET verify --out $MOVED_INDEX --json
if ($LASTEXITCODE -ne 0) { throw "copied index verification failed" }
& $SRCNET search shapes.c --out $MOVED_INDEX --json --limit 1 --budget 1000
if ($LASTEXITCODE -ne 0) { throw "copied index search failed" }
```

検証時はコピー後も verify と search が終了コード 0 になりました。`--limit 1` の検索には省略があるため、全候補を見たとは扱いません。
セグメントが分割されている場合は `.part-000001` なども必須です。参照される世代を手で選別せず、`segments` 一式を保持する方法なら取りこぼしを避けられます。

同じソースを別の場所に置いても `search --out` は元ソースを開かずに動きます。
本文の確認や freshness には、新しいソースルートを明示してください。再生成時に同じ NodeId を保つには、入力と生成条件に加えて `--repo` の識別子も揃えます。
ローカル設定を使う場合は、新しい場所を確認してから `agent-context --write` で更新します。

## 古い索引や壊れたコピーを扱う

| 状況 | 対応 |
| --- | --- |
| manifest しかない | 照会できない。元の書き手を止めて、対応する全セグメントと取り直す |
| セグメント不足・チェックサム不一致 | verify の終了コード 3 を記録する。ハッシュを書き換えず、正常なコピーを取り直すか別出力先へ再索引する |
| ソースを編集・改名した | 対象ファイルの freshness と元ソースを確認する。変更を反映するには明示的に再索引する |
| 読み取り拒否・確認上限 | 未確認を変更なしと扱わない。権限や対象範囲を見直す |
| 生成が終了コード 4 | `hasResult`、`complete`、理由別の抽出件数を確認し、制約を添えて使う |
| JSON や HTML が打ち切られた | 起点・検索語・深さを絞る。出力を切り取って成功したように扱わない |

古い索引は「その時点の調査用」と明示して残す用途があります。ただし、通常の verify 成功を現在のソースとの一致に読み替えてはいけません。
破損した公開世代をその場で修理するのではなく、別の専用出力先で生成・検証してから利用先を切り替えます。

## 信頼性を示す項目は意味が異なる

`complete` は走査、`valid` は保存データの整合性、`truncated` は選択範囲内の出力省略を示します。
確度 `EXTRACTED` はソースから抽出した根拠であり、ビルド構成を考慮した参照先の確定ではありません。
終了コードはそのコマンドの結果です。どの項目も「現在のソースを完全に理解した」という保証にはなりません。

本文を含まない JSON でも、識別子やパスに機密情報が含まれます。元ソース・索引・JSON・HTML・ローカル設定を別々に確認し、権限のある範囲だけを共有してください。
詳細は [セキュリティ](security.md)、形式は [ストレージ](storage.md)、失敗時の契約は [CLI リファレンス](query-and-cli.md) にあります。

用語: 世代は公開したセグメント集合の識別子、sidecar は manifest と分離したローカル設定ファイル、鮮度は現在の元ソースとの一致です。
不明点の調査には、CLI の版、終了コード、問題のある出力先、秘密情報を除いた診断を用意してください。

更新日: 2026-09-27。macOS でコピー後の verify / search を確認しました。Windows 実機でのコピー・実行は未検証です。
