# MEX デバッガ拡張リファレンス

MEX (Mex Debugging Extension) は Microsoft 内部で開発されたWinDbg拡張で、
794 以上のコマンドを提供する。`.load mex` で自動ロードされる。

> **呼び出し規則**: `!mex.<command>` または短縮形 `!mex.<alias>`。
> MEX コマンドは `!mex.` プレフィックスなしでも動作するが、
> 組み込みコマンドと衝突する場合は `!mex.` を付けること。

## ヘルプの使い方

```
!mex.help                    $$ カテゴリ一覧
!mex.help -cat 'General'     $$ カテゴリ内コマンド一覧
!mex.help -cat 'Thread'      $$ スレッド関連コマンド一覧
!mex.help <keyword>          $$ キーワード検索
!mex.help -all               $$ 全コマンド一覧
```

## 頻出コマンド — クイックリファレンス

### 初期トリアージ (最初に実行すべきコマンド)

| コマンド | 短縮 | 説明 | 標準コマンド代替 |
|---------|------|------|----------------|
| `!mex.crash` | | クラッシュダンプの初期解析を一括実行 | `!analyze -v` より高速 |
| `!mex.p` | | プロセス詳細を表示 | `!peb` + `vertarget` |
| `!mex.di` | `!di` | ダンプ情報を表示 | `vertarget` |
| `!mex.cl` | `!cl` | プロセスのコマンドラインを表示 | `!peb` の CommandLine |
| `!mex.us` | `!us` | ユニークスタック (同一スタックをグループ化) | `!uniqstack` |
| `!mex.mods` | `!m` | ロードされたモジュール一覧 (DML 対応) | `lm` |

### スレッド解析

| コマンド | 短縮 | 説明 |
|---------|------|------|
| `!mex.lt` | `!lt` | スレッド一覧 (状態・待機理由付き) |
| `!mex.us` | `!us` | ユニークスタック表示 (スレッドIDとグループ化) |
| `!mex.t` | | スレッド詳細 (`!thread` の改良版) |
| `!mex.f` | `!f` | フレームコンテキストを変更 (`.frame` の改良版) |
| `!mex.dss` | `!dss` | スタック上の文字列をすべて表示 |
| `!mex.ndso` | | ネイティブのスタックオブジェクトをダンプ |
| `!mex.irs` | `!irs` | 生スタックを解釈して表示 |
| `!mex.tp` | `!tp` | スレッドプール情報を表示 |
| `!mex.si` | `!si` | スタック情報を表示 |
| `!mex.runaway2` | | `!runaway` の改良版 (CPU 時間順) |

### メモリ・ヒープ解析

| コマンド | 短縮 | 説明 |
|---------|------|------|
| `!mex.mheap` | | `!heap` の DML 対応版 |
| `!mex.mi` | `!mi` | メモリアドレスの情報を表示 |
| `!mex.nhl` | `!nhl` | ネイティブヒープリーク検出 |
| `!mex.strings` | | アドレス範囲内の可読文字列を表示 |
| `!mex.du` | | Unicode 文字列表示 (改良版) |
| `!mex.da` | | ANSI 文字列表示 (改良版) |

### モジュール・バイナリ解析

| コマンド | 短縮 | 説明 |
|---------|------|------|
| `!mex.mods` | `!m` | モジュール一覧 (フィルタ対応) |
| `!mex.imports` | | モジュールのインポートテーブル表示 |
| `!mex.chkall` | `!chkall` | メモリ上のイメージとシンボルサーバーを比較 |
| `!mex.fem` | `!fem` | 各モジュールに対してコマンドを実行 |

### 例外・エラー解析

| コマンド | 短縮 | 説明 |
|---------|------|------|
| `!mex.crash` | | クラッシュの初期解析 |
| `!mex.mexr` | `!mexr` | 例外レコードの管理・ナビゲーション |
| `!mex.gle` | `!gle` | GetLastError の値を TEB から取得 |
| `!mex.cs` | `!cs` | クリティカルセクションの詳細表示 |

### TTD (Time Travel Debugging) 専用

| コマンド | 短縮 | 説明 |
|---------|------|------|
| `!mex.mtt` | | `!tt` のラッパー (MEX 版タイムトラベル) |
| `!mex.ex` | `!ex` | TTD トレース内の例外を一覧表示 |
| `!mex.idna` | `!i` | TTD ポジションをコンパクトに表示 |
| `!mex.jtl` | `!jtl` | 指定モジュールのロード時点にジャンプ |
| `!mex.feca` | `!feca` | 関数呼び出しの各インスタンスでコマンドを実行 |
| `!mex.tm` | `!tm` | メモリ位置の読み書きを追跡 |
| `!mex.gdp` | `!gdp` | DebugPrint ステートメントを検索・表示 |
| `!mex.gtc` | | TTD トレースから GetTickCount を取得 |

### Edge / Chromium 系ブラウザ専用

| コマンド | 短縮 | 対象プロセス | 説明 |
|---------|------|------------|------|
| `!mex._edgetriage` | | 全般 | Edge プロセスの包括的トリアージ |
| `!mex.edgeurls` | `!edgeurls` | browser / renderer | 全 WebContents / RenderView を表示 |
| `!mex.edgereqs` | `!edgereqs` | 全般 | 処理中の URL リクエスト一覧 |
| `!mex.edgeversion` | `!edgeversion` | 全般 | Edge バージョン情報 |
| `!mex.edgeprofiles` | `!edgeprofiles` | browser | プロファイル一覧 |
| `!mex.edgeextensions` | `!edgeextensions` | browser | 拡張機能一覧 |
| `!mex.edgepolicies` | `!edgepolicies` | browser | ポリシー一覧 |
| `!mex.edgecookies` | `!edgecookies` | browser | Cookie ダンプ |
| `!mex.edgedownloads` | `!edgedownloads` | browser | ダウンロードアイテム一覧 |
| `!mex.edgedualengine` | `!edgedualengine` | browser | デュアルエンジン情報 |
| `!mex.edgeuserpref` | `!edgeuserpref` | browser | ユーザー設定 |
| `!mex.edgesitelistdata` | `!edgesitelistdata` | browser | サイトリストデータ |
| `!mex.edgeshutdown` | `!edgeshutdown` | browser | シャットダウン問題のトリアージ |
| `!mex.edgedoc` | `!edgedoc` | renderer | Blink Document/Node ダンプ |
| `!mex.edgelayout` | `!edgelayout` | renderer | Blink LayoutObject ダンプ |
| `!mex.edgelayers` | `!edgelayers` | renderer | レイヤー一覧 |
| `!mex.edgejss` | `!edgejss` | renderer | V8 スクリプトスタック |
| `!mex.edgefunc` | `!edgefunc` | renderer | V8 JSFunction ダンプ |
| `!mex.edgetype` | `!edgetype` | renderer | V8 オブジェクトのインスタンスタイプ |
| `!mex.edgedecomp` | `!edgedecomp` | renderer | 圧縮ポインタのデコンプレス |

### ユーティリティ (出力加工・制御)

| コマンド | 短縮 | 説明 |
|---------|------|------|
| `!mex.grep` | | コマンド出力を文字列/パターンでフィルタ |
| `!mex.head` | | コマンド出力の先頭 X 行を表示 |
| `!mex.tail` | | コマンド出力の末尾 X 行を表示 |
| `!mex.sort` | | コマンド出力をソート |
| `!mex.count` | | コマンド出力の行数をカウント |
| `!mex.cut` | | 出力の不要部分をカットしてフィルタ |
| `!mex.hl` | `!hl` | 指定テキストをハイライト表示 |
| `!mex.ul` | `!ul` | 重複行をカウントしてユニーク表示 |
| `!mex.time` | | コマンドの実行時間を計測 |
| `!mex.exec` | | 複数コマンドを一括実行 |
| `!mex.lo` | `!lo` | ログファイルを開く |
| `!mex.c` | `!c` | コマンド出力をキャッシュして再利用 |

### イテレーション (ForEach 系)

| コマンド | 短縮 | 説明 |
|---------|------|------|
| `!mex.fet` | `!fet` | 各スレッドに対してコマンドを実行 |
| `!mex.fef` | `!fef` | 各フレームに対してコマンドを実行 |
| `!mex.fem` | `!fem` | 各モジュールに対してコマンドを実行 |
| `!mex.fel` | `!fel` | 出力の各行に対してコマンドを実行 |
| `!mex.fer` | `!fer` | 正規表現マッチごとにコマンドを実行 |
| `!mex.fei` | `!fei` | リストの各アイテムに対してコマンドを実行 |
| `!mex.feai` | `!feai` | 配列/ベクタの各要素に対してコマンドを実行 |
| `!mex.fems` | `!fems` | 同一スタックに対してコマンドを実行 |
| `!mex.feca` | `!feca` | TTD: 関数呼び出しごとにコマンドを実行 |

### カーネルデバッグ専用

| コマンド | 説明 |
|---------|------|
| `!mex.cpu` | CPU 状態情報を表示 |
| `!mex.running` | 現在実行中のスレッドの概要 |
| `!mex.ready` / `!mex.rdy` | Ready 状態のスレッドを表示 |
| `!mex.wq` | ワークキュー情報 |

## コマンド使用例

### 例 1: クラッシュダンプの初期トリアージ
```
!mex.crash          $$ 初期解析を一括実行
!mex.us             $$ ユニークスタックでスレッドを分類
!mex.mods           $$ モジュール一覧
```

### 例 2: スレッドの詳細調査
```
!mex.lt             $$ スレッド一覧
!mex.us             $$ ユニークスタック
~3s                 $$ スレッド 3 に切り替え
!mex.dss            $$ スタック上の文字列を表示
!mex.si             $$ スタック情報
```

### 例 3: TTD トレースでの例外調査
```
!mex.ex             $$ トレース内の全例外を一覧
!mex.idna           $$ 現在位置を記録
!mex.feca msedge!printing::PrintCompositorImpl::CompositePage  $$ 関数呼び出しを追跡
!mex.tm <address>   $$ メモリアクセスを追跡
```

### 例 4: Edge ブラウザのトリアージ
```
!mex._edgetriage    $$ 包括的トリアージレポート
!mex.edgeurls       $$ 開いているURL一覧
!mex.edgeversion    $$ バージョン情報
!mex.edgereqs       $$ 進行中のリクエスト
```

### 例 5: 出力のフィルタリング
```
!mex.grep "Print" !mex.us         $$ ユニークスタックから Print を含む行を検索
!mex.head 20 !mex.lt              $$ スレッド一覧の先頭20行
!mex.count ~*k                    $$ 全スレッドスタックの行数
!mex.grep -r "0x[0-9a-f]+" k     $$ 正規表現でスタック内アドレスを検索
```

## MEX vs 標準コマンド対照表

| 用途 | 標準コマンド | MEX コマンド | MEX の利点 |
|------|------------|------------|-----------|
| クラッシュ解析 | `!analyze -v` | `!mex.crash` | より高速、要点を絞った出力 |
| スレッド一覧 | `~*` | `!mex.lt` | 状態・待機理由を含む詳細表示 |
| ユニークスタック | `!uniqstack` | `!mex.us` | スレッドID関連付け、DML対応 |
| フレーム移動 | `.frame N` | `!mex.f N` | コンテキスト自動設定 |
| モジュール一覧 | `lm` | `!mex.mods` | フィルタ対応、DML リンク |
| PEB 情報 | `!peb` | `!mex.p` | 構造化された出力 |
| コマンドライン | `!peb` → CmdLine | `!mex.cl` | ワンコマンドで取得 |
| ヒープ | `!heap` | `!mex.mheap` | DML 対応、ナビゲーション |
| 例外レコード | `.exr -1` | `!mex.mexr` | 管理・アノテーション |
| 例外一覧 (TTD) | dx @$cursession... | `!mex.ex` | シンプルな構文 |

## カテゴリ一覧

| カテゴリ | コマンド数 | 主な用途 |
|---------|----------|---------|
| General | 97 | 汎用コマンド (crash, cs, dr, dps, envvars 等) |
| Kernel | 92 | カーネルモードデバッグ |
| Utility | 81 | 出力加工 (grep, sort, head, tail, exec 等) |
| Thread | 36 | スレッド解析 (lt, us, tp, ndso 等) |
| Process | 46 | プロセス解析 (p, loader, mheap 等) |
| Edge/Electron | 24 | Chromium 系ブラウザ専用 |
| Binaries | 8 | モジュール・バイナリ解析 |
| TTD | 10 | タイムトラベルデバッグ専用 |
| DotNet | 56 | .NET デバッグ |
| Networking | 38 | ネットワーク解析 |
| Decompile | 15 | 逆コンパイル |
