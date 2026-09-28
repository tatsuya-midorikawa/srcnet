# 同梱の小さなソースで索引から HTML まで試す

初めて srcnet を使う人向けの手順です。**同梱コーパスを T1 で索引し、返された ID から元ソースを確認します。**
自分のリポジトリを準備する必要はありません。まず自分の OS の手順を上から実行し、その後に「結果の読み方」を確認してください。
保存・共有の方法は [成果物の利用ガイド](artifact-guide.md) にあります。

## 準備するもの

このリポジトリのルートを作業ディレクトリにします。[global.json](../global.json) に対応する .NET 10 SDK が必要です。
SDK はビルドと依存の復元に、.NET 10 runtime は生成した CLI の実行に使います。SDK には runtime も含まれます。
依存を復元済みなら以降はオフラインで実行できます。未復元の場合、最初の publish は NuGet へ接続します。

入力は [tests/corpus/micro](../tests/corpus/micro/)、出力は新しい一時ディレクトリです。
既存の索引や元ソースを削除しません。実行ファイルの配置先 `artifacts/first-success` はこの入門用に空いている場所を使ってください。
次の publish はネイティブ解析器を含めません。T1 の利用に C コンパイラは不要です。

## macOS で実行する

### 1. 解析器なしの CLI と索引を作る

zsh で実行します。`plutil` は macOS 標準の JSON 読み取りにも使えるコマンドです。

```sh
dotnet publish src/Srcnet.Cli/Srcnet.Cli.fsproj -c Release -p:IncludeNativeParser=false -o artifacts/first-success
SRCNET="$PWD/artifacts/first-success/srcnet"
SOURCE_ROOT="$PWD/tests/corpus/micro"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/srcnet-first.XXXXXX")"
INDEX_DIR="$WORK/index"
"$SRCNET" --version
"$SRCNET" index "$SOURCE_ROOT" --out "$INDEX_DIR" --tier 1 --jobs 2 --json > "$WORK/index.json"
INDEX_EXIT=$?
printf 'index exit=%s\n' "$INDEX_EXIT"
plutil -p "$WORK/index.json"
```

検証時は `exitCode: 0`、`hasResult: true`、`complete: true`、`files: 10`、`nodes: 30`、`edges: 43` でした。
`parserAvailable: false` でも T1 では正常です。終了コードが 0 または 4 以外、あるいは `hasResult: false` なら先へ進まず、診断を確認してください。
4 は診断付きの結果です。古い索引が残っているだけの場合と、今回の公開成功を混同しないでください。

### 2. 整合性を検証し、検索結果から ID を取り出す

```sh
"$SRCNET" verify --out "$INDEX_DIR" --json
"$SRCNET" search shapes.c --out "$INDEX_DIR" --json --limit 20 --budget 4000 > "$WORK/search.json"
NODE_ID="$(plutil -extract nodes.0.id raw -o - "$WORK/search.json")"
plutil -extract nodes.0 json -o - "$WORK/search.json"
"$SRCNET" show "$NODE_ID" --out "$INDEX_DIR" --json > "$WORK/show.json"
"$SRCNET" neighbors "$NODE_ID" --out "$INDEX_DIR" --edge CONTAINS --direction both --depth 1 --json > "$WORK/neighbors.json"
```

verify は `valid: true`、`issues: []` を返しました。検索の先頭は `kind: "File"`、`path: "shapes.c"` です。
その場で取得した `NODE_ID` を使うため、説明用の ID を手入力する必要はありません。近傍の `CONTAINS` は包含関係であり、呼び出し関係ではありません。

### 3. 元の行範囲と HTML を確認する

```sh
SOURCE_FILE="$(plutil -extract nodes.0.path raw -o - "$WORK/show.json")"
START_LINE="$(plutil -extract nodes.0.lines.0 raw -o - "$WORK/show.json")"
END_LINE="$(plutil -extract nodes.0.lines.1 raw -o - "$WORK/show.json")"
sed -n "${START_LINE},${END_LINE}p" "$SOURCE_ROOT/$SOURCE_FILE"
"$SRCNET" context shapes.c --out "$INDEX_DIR" --json --budget 2000 > "$WORK/context.json"
"$SRCNET" export html --out "$INDEX_DIR" --node "$NODE_ID" --depth 1 --max-nodes 40 --file "$WORK/graph.html" --json
printf 'HTML: %s\n索引: %s\n' "$WORK/graph.html" "$INDEX_DIR"
```

返却範囲は `shapes.c` の 1〜17 行です。`#include "shapes.h"` や `point_area` の定義を元ソースで確認できます。
最後に表示された HTML のパスをブラウザーで開きます。検証時は `nodes: 3`、`totalNodes: 30` でした。
これは起点周辺の表示です。`truncated: false` でも索引全体を表示した意味にはなりません。

## Windows PowerShell で実行する

以下もリポジトリのルートから、同じ PowerShell セッションで順番に実行します。
実行ファイルの拡張子を検出するため、PowerShell 7 を使う macOS でも同じ構文を試せます。

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
dotnet publish src/Srcnet.Cli/Srcnet.Cli.fsproj -c Release -p:IncludeNativeParser=false -o artifacts/first-success
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$ROOT = (Get-Location).Path
$SRCNET = Join-Path $ROOT "artifacts/first-success/srcnet"
if (Test-Path "$SRCNET.exe") { $SRCNET = "$SRCNET.exe" }
$SOURCE_ROOT = Join-Path $ROOT "tests/corpus/micro"
$WORK = Join-Path ([IO.Path]::GetTempPath()) ("srcnet-first-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $WORK | Out-Null
$INDEX_DIR = Join-Path $WORK "index"
& $SRCNET --version
$raw = & $SRCNET index $SOURCE_ROOT --out $INDEX_DIR --tier 1 --jobs 2 --json
$INDEX_EXIT = $LASTEXITCODE
if ($INDEX_EXIT -notin @(0, 4)) { throw ($raw -join "`n") }
$indexed = $raw | ConvertFrom-Json
if (-not $indexed.hasResult) { throw "index was not published" }
$indexed | Select-Object exitCode, complete, files, nodes, edges, diagnostics
& $SRCNET verify --out $INDEX_DIR --json
if ($LASTEXITCODE -ne 0) { throw "verify failed" }
```

続いて検索の返却 ID を使います。検索や属性取得が失敗した場合は、そのまま次へ進めません。

```powershell
$raw = & $SRCNET search shapes.c --out $INDEX_DIR --json --limit 20 --budget 4000
if ($LASTEXITCODE -ne 0) { throw ($raw -join "`n") }
$search = $raw | ConvertFrom-Json
$NODE_ID = $search.nodes[0].id
$raw = & $SRCNET show $NODE_ID --out $INDEX_DIR --json
if ($LASTEXITCODE -ne 0) { throw ($raw -join "`n") }
$shown = $raw | ConvertFrom-Json
$file = $shown.nodes[0]
Get-Content -LiteralPath (Join-Path $SOURCE_ROOT $file.path) -Encoding utf8 |
  Select-Object -Skip ($file.lines[0] - 1) -First ($file.lines[1] - $file.lines[0] + 1)
& $SRCNET neighbors $NODE_ID --out $INDEX_DIR --edge CONTAINS --direction both --depth 1 --json
if ($LASTEXITCODE -ne 0) { throw "neighbors failed" }
& $SRCNET context shapes.c --out $INDEX_DIR --json --budget 2000
if ($LASTEXITCODE -ne 0) { throw "context failed" }
& $SRCNET export html --out $INDEX_DIR --node $NODE_ID --depth 1 --max-nodes 40 --file (Join-Path $WORK "graph.html") --json
if ($LASTEXITCODE -ne 0) { throw "export failed" }
Write-Output (Join-Path $WORK "graph.html")
```

## 結果の読み方

| 項目 | この例で確認すること |
| --- | --- |
| `id` / `generation` | ID は返却値を使い、別の索引や世代の結果と混ぜない |
| `path` / `lines` | `SOURCE_ROOT` からの相対パスと、両端を含む 1 始まりの行範囲。本文は元ファイルを読む |
| `complete` | 走査の完全性。抽出したシンボルの正確さや鮮度の保証ではない |
| `valid` | 索引内部の整合性。現在のソースとの一致は確認しない |
| `diagnostics` | index では件数。詳細は `diagnosticDetails` と省略情報を見る。照会では診断配列 |
| `truncated` | 指定した照会範囲・予算で省略があるか。全リポジトリを理解した保証ではない |

例えば `search shapes.c --limit 1` は検証時に 7 候補中 1 件を返し、`truncated: true`、`omittedNodeCount: 6` となりました。
返却 ID があることと、全候補を確認したことは別です。

ソースを編集した後は、[鮮度確認](query-and-cli.md#8-指定ファイルの鮮度を確認する) を明示的に実行できます。
この例の `shapes.c` だけを確認しても、残りの 9 ファイルは未確認です。

## 検索が空のときと T2 を使うとき

まず `--out` が今回の `INDEX_DIR` か確認します。T1 は C/C++ の全関数を抽出するものではありません。
実際にこの T1 索引の `context point_area` は終了コード 1 でした。ファイル名で辿って本文を確認するか、C/C++ の T2 を選びます。

T2 は [ネイティブ解析器の構築](../native/README.md) を済ませ、解析器を含む別の CLI 出力先で試してください。
解析器なしで T2 を要求すると T1 へ縮退し、通常は終了コード 4 と理由が返ります。
文法が存在しても、その言語の T2 グラフ抽出が実装済みとは限りません。[言語と段階の対応](extraction.md) と `stats` の `extractionCoverage` を確認します。

別の出力先を使う場合も、以降の全コマンドへ同じ `--out` を渡します。
解決しない場合は、実行したコマンド、終了コード、`diagnosticDetails`、CLI の版を揃えて [CLI リファレンス](query-and-cli.md) と [既知の未達項目](roadmap.md) を確認してください。
共有する診断からは機密パスや識別子を除きます。

## 用語と検証範囲

索引は生成時点のグラフ一式、manifest はその目録、セグメントはグラフの実データです。
NodeId は索引内の識別子、T1 は行指向の抽出、T2 は構文に基づく抽出を指します。

更新日: 2026-09-27。macOS arm64、.NET 10、解析器なしの publish で実行を確認しました。
掲載した PowerShell ブロックも macOS 上で実行し、同じ件数とコピー後の照会を確認しました。Windows 実機での実行は未検証です。
