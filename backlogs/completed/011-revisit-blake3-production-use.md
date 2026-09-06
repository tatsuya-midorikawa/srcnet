# [P2] 採用条件を満たすまで scalar BLAKE3 の本番利用を見直す

- 種別: 性能 / 設計判断
- 対象: `src/Srcnet.Core/Blake3.fs:223-408`, `src/Srcnet.Discovery/Content.fs:49-52,93-112`, `src/Srcnet.Storage/Writer.fs:80-122`, `src/Srcnet.Storage/Verify.fs:50-76`, `docs/decisions.md:138-157`, `docs/performance.md:81-103`
- 信頼度: 100%

## 問題

独自 BLAKE3 実装を、全入力ファイルの内容ハッシュ、全セグメントの書込時チェックサム、検証時の再ハッシュ、ノード ID に採用している。一方、リポジトリ内の実測では BCL `SHA256` より一貫して約 5 倍遅い。

| 入力 | SHA256 | BLAKE3 | 比 |
| --- | ---: | ---: | ---: |
| 4 KiB | 1.85 µs | 9.33 µs | 5.03x |
| 16 MiB | 6,884 µs | 38,654 µs | 5.62x |

ADR-8 は「実測で採用条件を検証済み」としているが、開発規約の「BCL より優位でない自前実装は採用しない」という条件と、記録された測定結果が矛盾している。

## 影響

リポジトリの全バイトを複数回通る処理で CPU 時間が増え、大規模リポジトリの index / verify 時間と消費電力を大きく悪化させる。現在の scalar 実装を前提にフォーマットと ID 契約を固定すると、後からの移行コストも増える。

## 対応案

次のいずれかを、全体 index benchmark と互換性方針を含めて決定する。

1. SIMD BLAKE3 を完成させ、macOS/Windows の対象 CPU で BCL baseline を上回ってから採用する
2. 当面は BCL ハッシュを使い、ID/format version を明示的に更新する
3. BLAKE3 が必須なら、性能要件の例外理由と許容 budget を ADR に明記する

アルゴリズム変更時は既存 ID と成果物形式が変わるため、単純置換ではなく versioning と migration 方針が必要になる。

## 完了条件

- macOS と Windows で代表サイズと大容量入力を同条件比較する
- primitive 単体だけでなく index / verify 全体の throughput、p95、CPU、allocation を測定する
- 採用する実装が文書化した性能 budget と ADR の採用条件を満たす
- ID とストレージ形式の互換性方針が確定している
