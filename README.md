# srcnet

`srcnet` は、ソースコードから **AI を一切使わずに** ナレッジ グラフを生成し、AI コーディング エージェントが「リポジトリ全体を読む」代わりに参照できるようにするツールです。

Chromium や Linux カーネルのような数千万行規模のリポジトリを、実用的な時間とメモリで扱いきることを最優先の設計目標としています。

> **状態: 開発初期。** [マイルストーン](docs/roadmap.md) M0（基盤）と M1（構造グラフ）の実装に加え、
> M2 の構文解析とシンボルのグラフ化、M4 の有界 JSON 照会と対話的 HTML 出力を実装しています。
> 名前の完全一致・前方一致用の字句索引も実装済みです。シンボル間の参照解決、増分更新、
> n-gram 転置索引、コミュニティ解析は未実装です。4 GiB のメモリ上限と大規模コーパスの
> 両 OS での受け入れ条件は未達であり、実装済みとマイルストーン完了を区別します。実測記録は
> [性能](docs/performance.md) 5.1 に、それ以外は目標として本文に記載しています。

## 解決する問題

AI コーディング エージェントは、質問のたびにリポジトリを grep して大量のファイルを読み込みます。リポジトリが大きいほどトークン消費が増え、コストと待ち時間が跳ね上がります。

srcnet はリポジトリの構造を事前に一度だけ解析してグラフ化し、質問に必要な部分だけを切り出して返します。**グラフ生成の全工程で LLM を使わない**ため、グラフを作ること自体には AI コストが一切かかりません。

## 設計上の立場

| 項目 | srcnet の選択 |
| --- | --- |
| 実装 | F# 10 / .NET 10 |
| 構文解析 | 固定した tree-sitter 文法。T2 のグラフ抽出は現在 C / C++ |
| 保存形式 | 固定長レコード、memory-mapped セグメント、CSR 隣接表 |
| 規模目標 | 数十万〜百万ファイル / 数千万行。T2 全体のメモリ有界化は未実装 |
| LLM | 生成・照会ともに不使用 |
| 検索 | 名前・修飾名・パスの字句一致 + 構造探索 |

他ツールや保存形式に対する一般的な性能優位は主張しません。srcnet の設計は
**LLM を呼ばず、必要な部分だけ読む**ことを目的とし、規模上限と性能は実測で判断します。

## 何をするか

以下はプロジェクト全体の目標です。現行の対応範囲と優先順位は [ロードマップ](docs/roadmap.md) にあります。

1. リポジトリを走査し、ファイル・シンボル・ビルド ターゲット・所有者などをノード化する
2. include / import、呼び出し、継承、テスト、ビルド依存、共変更などをエッジとして解決する
3. 中心性とコミュニティを決定的なアルゴリズムで計算する
4. 質問に対して、トークン予算内に収まる部分グラフを返す

## 何をしないか

意図的に範囲外とするものです。詳細は [docs/requirements.md](docs/requirements.md) を参照してください。

- LLM 推論、埋め込みベクトル生成、意味的類似検索
- 自然言語の意味理解（クエリは字句一致と構造探索で解決する）
- 対象リポジトリのビルド実行、スクリプト実行、コード生成
- ネットワーク通信とテレメトリ

## 設計原則

- **AI 不使用**: 生成・照会のどちらも、モデル推論とネットワーク通信を行わない
- **決定的**: 同一入力に対してバイト単位で同一の成果物を生成する
- **有界を目指す**: 照会・単一ファイル処理には上限がある。索引生成全体のメモリ有界化は未完了
- **移植性**: macOS と Windows を対等に扱い、CJK を含む入力で破綻しない

## 使い方

.NET 10 SDK が必要です（[global.json](global.json) で版を固定しています）。

```console
$ dotnet build srcnet.slnx -c Release
$ dotnet test srcnet.slnx -c Release
$ dotnet run --project src/Srcnet.Cli -c Release -- --help
```

以下の `srcnet` はビルドした実行ファイルを指します。PATH へ配置していない場合は
`dotnet run --project src/Srcnet.Cli -c Release --` に置き換えて実行できます。

### コマンドとして導入する

ソースから .NET ツールを作り、専用フォルダーへ導入できます。ツールの導入は
生成したローカルパッケージだけを使い、グローバル設定や PATH を変更しません。
ビルド時の NuGet 復元は、依存がキャッシュされていなければネットワークを使います。

```console
$ dotnet pack src/Srcnet.Cli -c Release -o artifacts/packages -p:IncludeNativeParser=false
$ dotnet tool install Srcnet.Cli --version 0.1.0 --source ./artifacts/packages --tool-path ./artifacts/bin
```

macOS では `./artifacts/bin/srcnet --help`、Windows の PowerShell では
`.\artifacts\bin\srcnet.exe --help` で起動します。以降はこの実行ファイルを
任意の作業ディレクトリから呼べます。導入には .NET 10 SDK、実行には .NET 10 runtime が必要です。
同じフォルダーのツールを入れ直す場合は、`dotnet tool uninstall Srcnet.Cli --tool-path ./artifacts/bin`
でそのツールだけを外してから導入し直します。

この例は解析器を含めない移植可能なパッケージを作ります。T1 で索引する場合は `--tier 1` を指定します。
C / C++ の T2 が必要なら、先に `python3 tools/build_native.py --languages c cpp` を実行し、
`-p:IncludeNativeParser=false` を外してパッケージ化してください。Windows では `python` を使えます。
ネイティブ解析器を含むパッケージは、構築した OS・CPU architecture 用です。

ツール形式が不要なら、`dotnet publish src/Srcnet.Cli -c Release -o artifacts/cli` で
実行用フォルダーを作れます。実行ファイルだけでなく、生成された DLL・設定・ライセンス表記も
一緒に配置してください。こちらも .NET 10 runtime を使います。

### 索引と照会

索引・整合性検査に加えて、部分グラフを予算内で照会できます。

```console
$ srcnet index <path> [--out <dir>] [--jobs <n>] [--no-gitignore] [--json]
$ srcnet stats [<path>] [--out <dir>] [--json]
$ srcnet verify [<path>] [--out <dir>] [--deterministic] [--json]
$ srcnet search <text> --root <path> --json --limit 20 --budget 4000
$ srcnet show <node-id> --root <path> --json
$ srcnet neighbors <node-id> --root <path> --direction in --depth 2 --json
$ srcnet path <from-id> <to-id> --root <path> --depth 16 --json
$ srcnet context <keywords...> --root <path> --budget 4000 --json
$ srcnet export html --root <path> --node <node-id> --depth 2 --max-nodes 2000
```

JSON の予算は封筒・エッジ・診断を含む出力全体へ適用し、省略件数を明示します。
検索索引のない旧成果物は再索引が必要です。HTML は単一ファイルで、外部通信なしに
ズーム・パン・検索・向き／種別フィルターを使えます。詳細は [クエリと CLI](docs/query-and-cli.md) にあります。

`index` は構造ノードに加え、指定した段階で抽出したシンボル・参照候補と字句索引を
`<repo>/.srcnet/` へ書き出します。T0 は構造のみ、T1 は行指向、T2 は C / C++ の構文抽出です。
`verify --deterministic` は同じ入力から二度生成し、`manifest.json`
を含む全ファイルがバイト単位で一致することを確認します。オプションの一覧は `srcnet --help` にあります。

セグメントは `segments/<generation>/` へ世代ごとに書かれ、`manifest.json` の原子的な置換だけが
公開の切替点になります。再索引中に `stats` や `verify` を実行しても、欠損や世代の混在は起きません。
同じ出力先への同時書き込みは排他ロックで拒否します。既存世代の破損を検出した場合は、
その不変パスを上書きせず、別の出力先への再索引を求めます。

`stdout` には結果のみを出力し、進捗・警告・診断は `stderr` へ出します。終了コードは
[クエリと CLI](docs/query-and-cli.md) 2.2 に従います。
各コマンドの `--help` / `-h` でも説明を表示できます。`--` 以降は位置引数として扱うため、
`srcnet search --json -- --option-name` のようにオプションに見える名前も検索できます。

エージェント向けには、索引を作る `srcnet-index` と既存索引を照会する `srcnet` の
[配布用スキル](ai/skills/README.md) があります。導入先と、ソース・実行ファイル・索引の
パスの渡し方は同文書を参照してください。

## 実装の構成

| プロジェクト | 内容 |
| --- | --- |
| `src/Srcnet.Text` | Unicode 正規化、東アジア文字幅、符号化判定、出力の無害化、語分割 |
| `src/Srcnet.Core` | 内容ハッシュ、ノード ID、論理パス、グラフの領域モデル、診断 |
| `src/Srcnet.Discovery` | 走査、`.gitignore` 照合、言語分類、読取と内容ハッシュ |
| `src/Srcnet.Extraction` | tree-sitter による構文解析（相互運用の境界検証を含む） |
| `src/Srcnet.Storage` | 固定長セグメント、CSR、字句索引、マニフェスト、mmap 読み取り、検証 |
| `src/Srcnet.Cli` | 引数解析、端末出力、サブコマンド |
| `tests/Srcnet.Tests` | 単体・性質・通しテスト |
| `bench/Srcnet.Benchmarks` | ベンチマーク（開発専用依存） |

製品コードの managed 依存は `FSharp.Core` のみで、CI が依存閉包を機械的に検査します。
唯一のネイティブ依存が構文解析器の tree-sitter で、版とチェックサムを
[native/sources.json](native/sources.json) で固定しています。

### 構文解析器の構築

構文解析は任意の構成要素です。構築しなくてもビルド・テスト・`index` は動作し、
その場合も T1 の行指向抽出は動作し、T2 を要求すると縮退の診断を出します。
診断付きの生成は終了コード 4 になるため、自動化では終了コードと `complete` を区別して扱ってください。

```console
$ python3 tools/build_native.py                       # 全言語
$ python3 tools/build_native.py --languages c python  # 一部の言語
```

同梱できる文法は C, C++, Python, Rust, Go, Java, JavaScript, C#, TypeScript, TSX, F# です。
C コンパイラと、初回のみ GitHub への到達性が必要です。詳細は
[native/README.md](native/README.md) を参照してください。

## ドキュメント

| 文書 | 内容 |
| --- | --- |
| [要件](docs/requirements.md) | 機能要件、非機能要件、スコープ境界、受け入れ基準 |
| [アーキテクチャ](docs/architecture.md) | パイプライン、並列化、決定性、モジュール境界 |
| [グラフ モデル](docs/graph-model.md) | ノード種別、エッジ種別、ID 体系、確度 |
| [抽出](docs/extraction.md) | 言語対応、段階的抽出、シンボル解決 |
| [ストレージ](docs/storage.md) | オンディスク形式、mmap、増分更新、索引 |
| [クエリと CLI](docs/query-and-cli.md) | サブコマンド、出力形式、トークン予算 |
| [移植性と国際化](docs/platform-and-i18n.md) | macOS / Windows 差異、CJK、文字エンコーディング |
| [セキュリティ](docs/security.md) | 脅威モデル、信頼境界、緩和策 |
| [性能](docs/performance.md) | 目標値、計測方法、ベンチマーク条件 |
| [テスト](docs/testing.md) | テスト戦略、コーパス、CI マトリクス |
| [ロードマップ](docs/roadmap.md) | マイルストーン、未決事項 |
| [設計判断の記録](docs/decisions.md) | 主要な決定と根拠、却下した代替案 |

開発規約は [.github/instructions/fsharp-dotnet.instructions.md](.github/instructions/fsharp-dotnet.instructions.md) にあります。

## ライセンス

Apache License 2.0。[LICENSE](LICENSE) を参照してください。
