# [P1] レガシー符号化の復号方針を決めて実装する

- 種別: 決定 + 実装 / M2（M1 からの持ち越し）
- 対象: `src/Srcnet.Text/Encodings.fs`, `src/Srcnet.Discovery/Content.fs:145-193`, `src/Srcnet.Extraction/Parsing.fs:75-136`, `docs/decisions.md`
- 依存: 014
- 参照: [ロードマップ](../docs/roadmap.md) M1 未了, [設計判断](../docs/decisions.md) 未決事項, [テスト](../docs/testing.md) T-4

## 背景

M1 は符号化の**判定**までを行い、復号は M2 の要件と併せて決めることになっている。判定結果は `ContentSummary.Encoding` と `EncodingCandidates` に載っているが、復号する経路はまだない。

T2 は UTF-8 のバイト列を要求する（`Parsing.parse` は UTF-8 前提）。Shift_JIS や EUC-JP のファイルを復号しないと、これらのファイルは T1 でも T2 でも識別子を正しく取り出せず、[テスト](../docs/testing.md) T-4 の「文字化けと欠落がない」を満たせない。

## やること

- 方針を決めて [設計判断](../docs/decisions.md) に ADR として記録する。比較する選択肢は次の 3 つ
  - **(a) `System.Text.Encoding.CodePages` を製品依存に追加する**: 実装費用は最小。ただし製品コードへの NuGet 追加は原則禁止であり、依存閉包の検査（T-3）の前提も変わる
  - **(b) 必要な符号化だけ自前で復号する**: Shift_JIS / EUC-JP / ISO-2022-JP / GB18030 / Big5 / EUC-KR。依存は増えないが、変換表の規模と正しさの検証費用が大きい
  - **(c) 非 UTF-8 を T1 のみに縮退させる**: 実装は最小。ただし CJK 圏のリポジトリで T2 が成立しなくなる
- 決定に従って実装する
- 復号は抽出の直前に行い、成果物には NFC 正規化した UTF-8 だけを載せる。原文はファイル側に残す（[ストレージ](../docs/storage.md) 5）
- `AmbiguousEncoding` のファイルでは、記録した符号化を確定値として扱わない。候補の先頭にすぎないという既存の方針（完了済み 010）を守る
- 不正なバイト列は置換して継続する。復号の失敗で走査を止めない
- UTF-16 のファイルは行数計数も保留されている（`Content.fs:154-158`）。復号後に行数を確定させる

## 完了条件

- 選択した方針が ADR として記録され、[ロードマップ](../docs/roadmap.md) の未決事項から外れる
- Shift_JIS / EUC-JP / ISO-2022-JP / GB18030 / Big5 / EUC-KR のコーパスで、識別子とコメントが欠落なく抽出される（T-4）
- 符号化が曖昧なファイルで、単一の符号化を確定値として扱っていないことがテストで確認できる
- 依存閉包の検査（T-3）が通る。(a) を選んだ場合は、その追加が ADR で正当化されている
- UTF-16 のファイルで行数が正しく数えられる
