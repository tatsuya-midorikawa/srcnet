# ネイティブ依存（tree-sitter）

srcnet は構文解析に [tree-sitter](https://tree-sitter.github.io/) を再利用する。
判断の根拠は [docs/decisions.md](../docs/decisions.md) の ADR-3 に、統制は
[docs/security.md](../docs/security.md) 3.1 に記録している。

## なぜ同梱しないのか

tree-sitter の文法は生成済みの `parser.c` として配布される。1 言語あたりのサイズは
C で約 3.9 MB、C++ で約 26 MB、C# で約 32 MB あり、対応予定の言語をすべて同梱すると
100 MB を超える。版管理へ載せる大きさではないため、**取得元・版・SHA-256 を
`sources.json` で固定したうえで構築時に取得する**方式を採る。

固定と検証は供給網に対する防御でもある。チェックサムが一致しない取得物は使わない。

## 構築

```console
$ python3 tools/build_native.py                       # 全言語
$ python3 tools/build_native.py --languages c python  # 一部の言語
$ python3 tools/build_native.py --check               # 取得と検証のみ
```

必要なもの:

- Python 3.9 以降
- C11 コンパイラ（`clang` / `gcc` / `cl`。`CC` 環境変数で指定できる。`cl` は Developer Command Prompt から実行する）
- 初回のみ、GitHub への到達性

生成物:

| パス | 内容 |
| --- | --- |
| `build/libsrcnet_treesitter.dylib` (`.so` / `.dll`) | ランタイムと文法を含む単一の共有ライブラリ |
| `build/languages.json` | 実際に構築した言語と、それぞれの文法の版 |
| `.cache/` | 検証済みの取得物 |
| `.build/` | 展開したソースと中間生成物 |

`build/`、`.cache/`、`.build/` はいずれも版管理の対象外である。
`Srcnet.Extraction` は `build/` の共有ライブラリを出力ディレクトリへ複製する。
Windows では F# の相互運用宣言と選択した文法から `.def` を生成し、必要な関数を DLL へ
明示的に公開する。構築後は各 OS で実際にライブラリを開き、必須の公開シンボルが揃っていることを確認する。
ツールの標準出力・標準エラーは UTF-8 に固定し、リダイレクト時も日本語を出力できる。

## 構築しない場合

共有ライブラリが無くてもビルドとテストは成功する。その場合 `Srcnet.Extraction` は
構文解析を「利用不可」として報告し、走査・構造グラフ・照会（T0 / T1 の範囲）は
そのまま動作する。構文解析を要する抽出だけが行われない。

## 同梱している文法

C, C++, Python, Rust, Go, Java, JavaScript, C#, TypeScript, TSX, F#。

1 つのリポジトリが複数の文法を持つ場合があるため、`subdirectory` で位置を指定できる。
TypeScript（`typescript` と `tsx`）と F#（`ionide/tree-sitter-fsharp`）がこれに当たる。

`.tsx` は JSX を含み TypeScript 文法では解析できないため専用の文法を使う。
`.fsi` は本体と同じ `fsharp` 文法で解析する。ionide の `fsharp_signature` 文法は
`module` 宣言を含む現実的なシグネチャ ファイルを解析できず、通常の文法が同じ入力を
誤りなく解析したため同梱しない。

## ABI の版

文法は `LANGUAGE_VERSION` として ABI の版を宣言し、ランタイムが対応する範囲に
入っていなければ読み込めない。現在のランタイム v0.25.10 は 13〜15 に対応する。
F# の文法は 15 を要求するため、これより古いランタイムでは読み込めない。

v0.26 以降のランタイムは `ts_parser_set_timeout_micros` と
`ts_parser_set_cancellation_flag` を削除しているため、更新するときは
進捗コールバック API への移行が必要になる。

## 文法を追加する

1. `sources.json` の `grammars` に項目を追加する。`version` はタグ、`sha256` は
   `codeload.github.com` の tar.gz に対するもの
2. 1 つのリポジトリが複数の文法を持つ場合は `subdirectory` を指定する
3. `symbol` に文法の入口関数名を書く（`tree_sitter_<name>`）
4. `src/Srcnet.Extraction/Grammars.fs` に入口関数の宣言と対応付けを加える
5. 文法が宣言する `LANGUAGE_VERSION` がランタイムの対応範囲にあることを確かめる
6. `python3 tools/build_native.py` で構築し直す

チェックサムは次の方法で求められる。

```console
$ curl -sSL -o g.tar.gz https://codeload.github.com/tree-sitter/tree-sitter-<name>/tar.gz/refs/tags/<tag>
$ shasum -a 256 g.tar.gz
```

## ライセンス

tree-sitter のランタイムと本書が参照する文法は、いずれも MIT ライセンスで配布されている。
取得物のライセンス表記は展開したソース内に含まれる。
