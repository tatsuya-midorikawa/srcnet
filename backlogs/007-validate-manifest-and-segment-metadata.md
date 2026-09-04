# [P2] マニフェストとセグメントメタデータを厳密に検証する

- 種別: 堅牢性 / 破損成果物
- 対象: `src/Srcnet.Storage/Manifest.fs:207-352`, `src/Srcnet.Storage/Reader.fs:51-105`, `src/Srcnet.Storage/Verify.fs:87-259`, `src/Srcnet.Storage/Stats.fs:100-129`
- 信頼度: 100%

## 問題

成果物を破損または外部入力として扱う境界で、JSON の値種別、数値範囲、型固有のセグメント不変条件を十分に検証していない。

例:

- `segments` が配列か確認せず `EnumerateArray` を呼ぶ
- `GetInt64` した件数を未検査で `int` へ変換する
- `PrimaryCount` と record length から求めた必要 payload 長を先に検証しない
- 件数とオフセットの算術 overflow を検査しない
- 不正な件数のまま `Span.Slice` や反復処理へ進む

## 確認結果

正常なマニフェストの `segments` を `{}` に変更して `srcnet verify` を実行すると、期待される形式エラーではなく `InvalidOperationException` が漏れ、終了コード `70` になった。

不正に大きい `.files` の `PrimaryCount` でも、`stats` が `ArgumentOutOfRangeException` になり得る。

## 影響

- 破損成果物で CLI が内部エラー終了する
- 巨大ループ、範囲外アクセス、算術 overflow を誘発する
- 利用者が「非互換・破損」と実装不具合を区別できない

## 対応案

- JSON の各プロパティについて `ValueKind`、必須性、文字列形式、数値範囲を検証する
- すべての narrowing conversion を checked にする
- セグメント kind ごとに record length、count、payload length、オフセット範囲の不変条件を検証してから公開する
- 想定内の破損は `ManifestError.Malformed` または `Reader.InvalidFormat` へ変換する

## 完了条件

- JSON 型違い、負数、`Int32.MaxValue` 超過、算術 overflow、payload 不一致のテストがある
- 破損入力の property/fuzz test で未処理例外、ハング、過剰 allocation が発生しない
- すべての形式不正が終了コード `3` または `4` のドメインエラーになる
