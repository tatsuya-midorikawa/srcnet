# [P1] ignore 規則の欠落時にマニフェストを complete にしない

- 種別: 正確性 / 公開判定
- 対象: `src/Srcnet.Discovery/Walk.fs:228-252,424-437`, `src/Srcnet.Discovery/Ignore.fs:261-285`
- 信頼度: 100%

## 問題

ignore 規則が `MaxRulesPerFile` を超えると `IgnoreFileUnreadable` 診断は追加されるが、この種別は `WalkResult.Complete` を false にする `incompleteKinds` に含まれていない。読めなかった ignore ファイルも同じ扱いになる。

その結果、適用できなかった規則があって索引対象を確定できないのに、成果物を `complete: true` として公開する。

## 確認結果

10,000 個の非一致規則の後に `secret.txt` を置いた `.gitignore` で索引した。

- 10,001 個目の規則は適用されず、`secret.txt` が索引された
- `ignore-file-unreadable` 診断は 1 件記録された
- 終了コードは `4` だった
- 公開済みマニフェストは `complete: true` だった

## 影響

- 除外対象の秘密情報や生成物を索引へ含める可能性がある
- 利用側が `complete` を信頼して欠落した ignore 規則を検知できない
- 既定では不完全成果物を上書きしないという `--allow-partial` の契約を迂回する

## 対応案

- ignore ファイルの読取失敗または打切りを、明示的な不完全理由として `WalkResult` へ伝える
- `complete` を false にし、既定では公開を拒否する
- `--allow-partial` 指定時だけ、診断付きの不完全成果物として公開する

## 完了条件

- 規則数超過、権限拒否、走査中の削除、無効なファイル種別で `complete: false` になる
- `--allow-partial` なしでは既存成果物を置き換えない
- `--allow-partial` ありでは不完全理由がマニフェストへ残る
