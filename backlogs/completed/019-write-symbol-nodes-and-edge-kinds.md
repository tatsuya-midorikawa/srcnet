# [P0] シンボル ノードと複数エッジ種別を書き出す

- 種別: 実装 / M2
- 対象: `src/Srcnet.Storage/Writer.fs:37-52,162-490`, `src/Srcnet.Storage/Format.fs:17,179-219`, `src/Srcnet.Storage/Reader.fs`, `src/Srcnet.Storage/Verify.fs`, `src/Srcnet.Storage/Stats.fs`, `src/Srcnet.Cli/Commands.fs:144-232`
- 依存: 014, 016, 017
- 参照: [ストレージ](../docs/storage.md) 2, 3, 4, [グラフ モデル](../docs/graph-model.md) 4, 5

## 背景

抽出結果を成果物へ載せる経路がない。現在の書き出しは `Repository` / `Directory` / `File` の 3 種別と、`CONTAINS` の前方・後方 CSR だけである（`Writer.fs:409-460`）。

`NodeRecord` のレイアウトは `qualifiedName`、`ordinal`、行範囲、バイト範囲をすでに持つため、レコード形式の変更は要らない。足りないのは、シンボルを書く経路とエッジ種別ごとの CSR である。

## やること

- `Writer.IndexInput` にシンボルと参照候補を追加する
- ノード表にシンボル レコードを書く。`fileIndex` に所属ファイルの密インデックスを入れ、ファイルに属さないノードは `NodeRecord.NoFile` を使う
- エッジ種別ごとに `<id>.edges.<kind>` と `<id>.redges.<kind>` を書く。M2 で生成されるのは `CONTAINS` / `DECLARES` / `DEFINES` / `GUARDED_BY`
- ノード ID を `NodeIdBuilder` で計算し、`.idmap` を昇順で書く。ID の衝突は握りつぶさず、明示的な失敗にする（[グラフ モデル](../docs/graph-model.md) 4.2）
- 隣接配列は昇順に整列する。同じ始点から同じ終点への重複エッジは 1 本にまとめる
- 形式が変わるため版を上げ、旧版の成果物は読まずに拒否する
  - `Format.FormatVersion` 2 → 3
  - `Manifest.ManifestVersion` 2 → 3
- `Manifest.Counts` にノード種別ごとの件数とエッジ種別ごとの件数を持たせる
- `stats` と `verify` を新しい種別へ対応させる。`index` の出力（テキストと JSON）も、`CONTAINS` 固定の表示をやめる

## 設計上の注意

- 書き出しの順序は密インデックスの昇順に固定する。抽出の完了順に書くと決定性が壊れる
- エッジ種別が増えるとセグメント数も増える。マニフェストのセグメント一覧は名前の序数昇順を保つ
- 大きなセグメントの書き出し中も取り消しを観測する。既存の `CancellationCheckStride` の方式を新しいループにも適用する

## 完了条件

- `verify` が新しい成果物に対して通り、`verify --deterministic` の二度生成がバイト単位で一致する（T-1）
- 版 2 の成果物が `UnsupportedFormatVersion` / `UnsupportedManifestVersion` で拒否される
- CSR と隣接集合の対応が property テストで検証される
- macOS と Windows の成果物がバイト単位で一致する
