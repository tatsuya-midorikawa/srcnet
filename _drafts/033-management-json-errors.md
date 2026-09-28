# [P1] index・stats・verify の失敗時も JSON で結果を受け取れるようにする

- 状態: 対応中
- 種別: 機能 / CLI の自動化
- 起票日: 2026-09-27
- 依存: なし。

## 確認した事実

2026-09-27、macOS で現行ソースを Release ビルドし、存在しないパスを指定して確認した。

| 実行条件 | 終了コード | 標準出力 | 標準エラー |
| --- | --- | --- | --- |
| `index <存在しないルート> --json` | 2 | 空 | 診断あり |
| `stats --out <存在しない索引> --json` | 3 | 空 | 診断あり |
| `verify --out <存在しない索引> --json` | 3 | 空 | 診断あり |
| `search Area --out <存在しない索引> --json` | 3 | JSON | 空 |
| `export html --out <存在しない索引> --json` | 3 | JSON | 空 |

- [Commands.fs](../src/Srcnet.Cli/Commands.fs) の `index`、`reportIndex`、`statsWithCancellation`、`verify` に、JSON を出さず診断を返す失敗経路がある。
- [QueryCommands.fs](../src/Srcnet.Cli/QueryCommands.fs) は JSON のエラー封筒を共有している。
- [照会スキル](../ai/skills/srcnet/SKILL.md) も、管理系の失敗で JSON がない場合を扱うよう利用者に求めている。
- 現行リファレンスが失敗時の封筒を保証するのは JSON 照会である。本件は既存契約の違反ではなく、管理系にも契約を揃える改善である。

## 利用者の困りごと

自動化側がコマンドと成功・失敗によって JSON とテキストを読み分ける必要があり、診断や終了状態の取りこぼしにつながる。

## 改善内容

- `--json` 指定時は、入力誤り、索引欠損・破損、部分的失敗、中断について、単一の機械可読な結果を返す契約を定める。
- 既存の出力処理を再利用し、コマンド、終了コード、診断、結果の有無を明示する。既存成功フィールドの互換性を確認する。
- index の `complete` と処理の成功・失敗を混同しない。診断数だけでなく、上限付きの診断情報を取得できるようにする。
- テキスト出力の使い勝手と終了コードを維持し、進捗を JSON の標準出力へ混ぜない。

## 対象外

プロセス強制終了や OS による起動失敗でも JSON を保証すること。すでに封筒を返す search / export の不要な再設計。

## 受け入れ条件

- [x] 上表の index / stats / verify のケースで、標準出力全体を 1 個の JSON として解析できる。
- [x] JSON の終了コードとプロセス終了コードが一致し、診断は上限付きで収まる。
- [x] 成功、欠損、破損、不正引数、キャンセル、診断付き完了を既存 CLI テストで検証する。
- [x] search / export の既存エラー契約と、管理系の既存成功結果を壊さない。
- [ ] macOS / Windows で確認し、スキルと CLI リファレンスの分岐説明を更新する。

## 実装と検証（2026-09-27）

- 管理出力へ exitCode / hasResult と最大 32 件の diagnosticDetails、文字列長上限、省略件数を追加した。既存の成功フィールドは維持した。
- [QueryCliTests.fs](../tests/Srcnet.Tests/QueryCliTests.fs) で欠損・不正引数・成功・破損・診断付き公開を実プロセスで確認。100 件の診断でも詳細 32 件・省略 68 件を返す。
- macOS の SIGINT 実行で終了コード 5 と単一 JSON を確認。旧 manifest は不変、staging は除去された。search/export を含む全 478 テストも成功した。
- CLI リファレンスと配布スキルの分岐説明を更新した。環境は macOS 27.0 arm64 / SDK 10.0.102。Windows は未検証、完了日なし。

## 完了時の扱い

検証結果と完了日を追記し、[運用ルール](README.md) に従ってこのファイルを `_drafts/_completed/` へ移動する。
