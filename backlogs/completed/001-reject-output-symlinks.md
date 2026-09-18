# [P1] 出力先リンク経由の管理外ファイル操作を拒否する

- 種別: セキュリティー / データ損失
- 対象: `src/Srcnet.Cli/Commands.fs:91-107,181-223`, `src/Srcnet.Storage/Manifest.fs:147-200`
- 信頼度: 100%

## 問題

既定の出力先 `_srcnet` は解析対象リポジトリ内にあるため、リポジトリ作成者がシンボリックリンクや Windows の reparse point として配置できる。現在は字句的な `Path.GetFullPath` だけで出力先を決め、リンクを拒否せずに `manifest.json`、`segments`、`.staging`、`.staging.retired` を作成・移動・再帰削除している。

`manifest.json.tmp` などの管理対象ファイル自体がリンクである場合も、リンク先を上書きできる。

## 確認結果

一時リポジトリで `_srcnet` を別ディレクトリへのリンクにし、リンク先の `segments/sentinel.txt` を作成してから `srcnet index` を実行した。

- 終了コードは `0`
- リンク先へ `manifest.json` が作成された
- リンク先の `segments/sentinel.txt` は再帰削除された

## 影響

信頼できないリポジトリを索引するだけで、利用者が書き込み可能な任意の場所にあるファイルを上書きまたは削除される可能性がある。

## 対応案

- 出力ディレクトリから管理対象ファイルまでの各要素について、シンボリックリンクと reparse point を拒否する
- 作成・移動・削除の直前に実体パスを検証し、信頼済み出力ルート配下であることを保証する
- 一時ファイルは既存リンクを追跡せず、排他的に新規作成する
- Windows では junction、mount point、その他の `FileAttributes.ReparsePoint` も同じ方針で扱う

## 完了条件

- `_srcnet`、`segments`、staging、`manifest.json.tmp` の各リンクを使った回帰テストがある
- macOS のシンボリックリンクと Windows の junction/reparse point の双方で、管理外パスが変更されない
- 拒否時は内部エラーではなく、利用者が理解できる診断と非成功終了コードを返す
