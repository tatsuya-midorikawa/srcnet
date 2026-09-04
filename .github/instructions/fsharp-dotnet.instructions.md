---
description: "Use when implementing, reviewing, refactoring, testing, optimizing, or configuring ultra-fast and lightweight F# 10 applications and libraries on .NET 10. Enforces macOS and Windows compatibility, defect-free and secure operation, CJK support, crash and freeze prevention, large-file and search performance, resource efficiency, SIMD, testing, and tooling practices."
name: "F# 10 and .NET 10 Engineering"
applyTo: "**/*.fs, **/*.fsi, **/*.fsx, **/*.fsproj, **/global.json, **/Directory.Build.props, **/Directory.Build.targets, **/Directory.Packages.props, **/*.sln, **/*.slnx, .github/workflows/*.yml, .github/workflows/*.yaml"
---
# F# 10 / .NET 10 開発規約

この文書の各箇条書きは、ユーザーが明示的に例外を指示しない限り必須とする。「原則」は記載された例外だけを許し、「検討」「比較」は評価の実施自体を必須とする。規約に反する依頼を黙って実装せず、衝突点と準拠する代案を示す。

## 基準と進め方

- 新規コードは F# 10 と .NET 10 を基準にし、新規プロジェクトは SDK-style と `net10.0` を使用する。明示的な依頼なしに preview 機能、古いターゲット、互換レイヤーを追加してはならない。
- 編集前に対象コード、呼び出し元、関連テスト、プロジェクト設定を確認し、振る舞いを決める最小の箇所を特定する。
- 根本原因を直す最小の変更を選び、無関係なリファクタリング、将来用の抽象化、不要な設定や依存関係を混ぜてはならない。
- リポジトリ固有の命名、構成、公開 API、テスト、フォーマット規約を尊重する。ただし本規約の必須条件と衝突する場合は明示する。
- 実装後は対象範囲に最も近いテストを先に実行し、その後に必要なビルド、全テスト、フォーマット、静的解析を実行する。未実行または失敗した検証を成功と報告してはならない。

## 必須の非機能要件

- 実装前に、対象 workload、データ規模、実行環境、測定方法、性能・resource budget、合否閾値を定義する。ユーザー指定がない場合は、代表値と worst-case を用いて安定版または変更前 baseline から非劣化を最低条件とし、OS、CPU、メモリ、データセット、cold / warm 条件を記録する。証跡なしに要件達成と判定してはならない。
- **macOS と Windows の両環境で動作すること:** 対象 architecture の両 OS で build、test、実行、配布を検証し、同じ外部仕様を保証する。path separator、case sensitivity、改行、file locking、permission、shell、native API、文字 encoding へ暗黙依存しない。OS 固有実装が必要なら明示的に分離し、両方の実装と fallback をテストする。
- **不具合がないこと:** 既知または再現可能な不具合、失敗するテスト、未処理の境界条件をゼロにしてから完了とする。unit、integration、regression、property / fuzz test と静的解析を変更リスクに応じて実施し、未検証の状態を「不具合なし」と報告してはならない。
- **セキュリティー リスクがないこと:** threat model に基づき、入力検証、認可、secret、injection、path traversal、unsafe / native memory、memory mapping、serialization、依存関係を確認し、既知の脆弱性と未対処の security finding をゼロにする。セキュリティーは性能より優先し、検証を省いてはならない。
- **CJK フォントでも動作すること:** 日本語、簡体字・繁体字中国語、韓国語を content、file path、検索、入出力、ログ、UI がある場合の表示・入力で検証する。UTF-8、Unicode scalar value、grapheme cluster、normalization、font fallback、文字幅、折り返しを正しく扱い、特定 font の metrics、ASCII、1 code unit = 1文字という仮定を置かない。文字化け、欠落、豆腐、切れ、重なりを許容しない。
- **クラッシュやフリーズがないこと:** 未処理例外、process abort、deadlock、livelock、無限 loop、無限待機、UI / request thread の長時間 block、resource 枯渇による停止を防ぐ。すべての長時間処理に cancellation、必要な timeout、bounded concurrency、失敗時の cleanup を設け、stress、concurrency、長時間、異常系テストで確認する。
- **大容量ファイルの処理が高速であること:** 最大対応サイズと処理時間 budget を定義し、全体 load や全量 copy を避け、streaming、chunking、memory mapping、buffer pooling、並列化を実測比較する。両 OS で cold / warm cache、代表値、上限値を benchmark し、メモリ使用量を入力サイズに対して bounded に保つ。
- **システムリソースの使用が最適化されていること:** CPU、working set、allocation、GC、thread、handle、mapped view、disk / network I/O を計測して budget 内に保つ。resource は決定的に解放し、leak、無制限 queue / cache / concurrency、過剰 polling、不要な wake-up を残さない。
- **レスポンスが迅速であること:** 起動時間、time to first result、操作・request ごとの p50 / p95 / p99 latency budget を定義し、両 OS で計測する。interactive path を同期 I/O や重い計算で block せず、長時間処理は cancellation と、利用者接点がある場合は progress または incremental result を提供する。
- **検索処理が高速であること:** exact / partial、case、culture、Unicode normalization など検索 semantics を明示し、適切な index、algorithm、data layout、prefilter、SIMD、並列化を実測比較する。CJK を含む代表データと最大規模データについて cold / warm の throughput と p95 / p99 latency を両 OS で benchmark し、病的な worst-case と不要な全件走査を避ける。

## 性能最優先の原則

- 性能に関する選択で他節の一般則と衝突する場合は本節を優先する。ただし、正しさ、メモリ安全性、セキュリティ、外部契約を犠牲にしてはならない。
- 正しさ、メモリ安全性、セキュリティ、外部契約を満たした上で、実行速度、throughput、tail latency、使用メモリ、allocation、起動時間、成果物サイズを最優先の設計指標とする。可読性や抽象化より性能・軽量性を優先してよい。
- 設計開始時に対象 workload、入力サイズ、実行環境、性能指標、baseline、許容 regression を明示する。性能要件が未指定なら、代表的な実データと worst-case を選び、変更前の baseline を計測する。
- hot path は profiler、hardware counter、allocation 計測で特定する。変更後は同一条件の benchmark で比較し、統計的なばらつきを考慮して速度と memory の改善を確認する。計測なしに「高速」「軽量」と断定してはならない。
- SIMD 化できる数値計算、走査、変換、検索、比較では、scalar 実装だけで終えず、`System.Numerics.Vector<'T>`、`Vector128<'T>`、`Vector256<'T>`、`Vector512<'T>`、`System.Runtime.Intrinsics` を優先的に検討して benchmark する。
- hardware intrinsic を使う実装は `IsSupported` で ISA を判定し、AVX2、AVX-512、AdvSimd など対象 CPU ごとの高速 path と、正しさを保つ portable fallback を用意する。境界、端数、alignment、整数 overflow、浮動小数点の精度差をテストする。
- hot path では boxing、closure、tuple、intermediate collection、LINQ、reflection、不要な delegate / interface dispatch、例外生成を避ける。必要に応じて `Span<'T>`、`ReadOnlySpan<'T>`、`Memory<'T>`、`stackalloc`、`ArrayPool<'T>`、`struct`、byref、連続した `Array`、明示的 loop を使用する。
- データ構造は cache locality、分岐予測、vectorization、アクセス頻度に合わせて選ぶ。性能上有利なら hot path 内に限って事前確保した可変 buffer と imperative loop を純粋な外部 API の内側で使用する。
- 一回の走査で済む処理を複数 pipeline に分けず、列挙の重複、不要な copy、materialization、境界をまたぐ変換を排除する。I/O は batching、buffering、streaming を比較し、過剰な syscall と小さな非同期処理を避ける。
- 大容量ファイル、ランダムアクセス、プロセス間共有では memory-mapped I/O を必ず比較対象にする。`System.IO.MemoryMappedFiles.MemoryMappedFile`、OS の `mmap` / `CreateFileMapping`、構成上 Rust native component を許容できる場合の `memmap2` などを、通常の buffered / streaming I/O と同じ workload で benchmark する。採用時は view と handle の寿命、範囲・alignment、file truncation、page fault、flush、32/64-bit address space、対象 OS の fallback を検証する。
- 外部ライブラリを採用する前に、目的へ特化した自前実装を最優先候補として作成・比較する。汎用性を捨てたアルゴリズム、SIMD、memory layout、allocation 制御で外部ライブラリより高速かつ軽量にできる場合は自前実装を選ぶ。
- 自前実装は、代表 workload の benchmark で候補ライブラリと BCL 実装を上回り、correctness test と edge-case test に加えて、適用可能な fuzz test または property test を通ることを採用条件とする。優位性が確認できない自前実装は採用しない。
- 暗号 primitive、認証 protocol、乱数生成など、独自実装が security risk になる領域は速度だけを理由に自作せず、検証済みの platform API を使用する。
- benchmark 専用依存は benchmark project に隔離し、production の参照、依存 closure、publish artifact に含めない。benchmark は Release、最適化有効、debugger 非接続、warmup 実施、同一マシン・同一 runtime 条件で行い、平均値だけでなく分布、allocation、GC、成果物サイズも記録する。
- Native AOT、trimming、ReadyToRun、tiered compilation、PGO、server / workstation GC は配布形態と workload に応じて比較する。機能を有効化しただけで改善とみなさず、publish artifact と実環境相当の benchmark で選択し、reflection、serialization、dynamic code、外部契約との互換性を publish 結果で検証する。
- 性能上の意図が通常の F# idiom から分かりにくい箇所には、維持すべき不変条件と benchmark の場所を短く記す。保守時に高速 path を通常実装へ戻してはならない。

## 関数型プログラミング

- 値とデータ構造は不変を既定とする。`mutable`、参照セル、可変コレクション、グローバル状態は、局所性または計測済みの性能要件がある場合だけ使用する。
- ビジネスルールを純粋関数として構成し、I/O、時刻、乱数、環境変数、ネットワーク、データベースなどの副作用は境界へ隔離する。
- 副作用を隠れたグローバル依存にせず、必要な関数や値を引数として渡す。時刻に依存する処理では、直接の現在時刻取得より `TimeProvider` など注入可能な境界を優先する。
- ドメインは record、discriminated union、必要に応じた single-case union で表現し、不正な状態を可能な限り型で表現不能にする。検証が必要な型は private case と smart constructor を使用する。
- 値が存在しない可能性には `option`、想定内の失敗には `Result` を使用する。通常の制御フローに例外、`null`、sentinel 値を使用してはならない。hot path で allocation が問題になる場合は、意味を保ったまま `voption`、struct tuple、`[<Struct>]` discriminated union などの値型表現を使用する。
- `Option.get`、`List.head`、`List.tail`、`Seq.head`、`Array.find` など失敗し得る部分関数は、不変条件が直前で証明されていない限り避け、`tryHead`、`tryFind`、pattern matching など安全な形を使う。
- pattern matching は意味のある case を明示し、discriminated union の将来の変更を隠す `_` を安易に使用しない。公開境界から届く値には網羅性を持たせる。
- hot path、大きなデータ、反復走査には、連続配置され SIMD 化しやすい `Array`、`Span<'T>`、`ReadOnlySpan<'T>` を基本とする。`List` は小さく、先頭追加や再帰的分解が中心の不変データに限定する。`seq` は遅延評価または streaming が必要な場合に限定し、hot path で使う場合は列挙 overhead と再評価を benchmark する。
- 変換は `map`、`choose`、`filter`、`fold`、`collect`、`traverse` 相当の既存関数を優先する。ただし複雑な一行や不透明な point-free style より、名前付きの中間値と読みやすい制御フローを選ぶ。
- pipeline と function composition はデータの流れが明確になる場合に使う。型やエラーの意味を隠すほど長い pipeline は分割する。
- 再帰は問題の構造に自然な場合だけ使い、深さが増える処理は tail-recursive にするか標準コレクション関数へ置き換える。入力依存で stack overflow し得る再帰を残してはならない。
- computation expression は効果の種類を明確にするために使い、独自 builder は繰り返し現れる実需要がない限り追加しない。
- 型推論は関数内部で活用し、公開 API、モジュール境界、曖昧になりやすい値、数値・単位を扱う境界には型注釈を付ける。
- 単位が重要な数値には units of measure を検討し、異なる単位を同じ裸の数値で扱わない。

## エラー、非同期、相互運用

- 期待される検証エラーや業務エラーは、意味のある独自 error type と `Result` で返す。文字列だけのエラーを多層に伝播させない。
- 例外は予期しない障害または .NET API 境界に限定し、握りつぶしてはならない。捕捉する場合は文脈を保持し、復旧、変換、記録のいずれかを行う。`raise ex` で stack trace を失ってはならない。
- .NET 相互運用から来る `null`、例外、可変オブジェクトは境界で検証・変換し、純粋なドメイン層へ漏らさない。nullness annotation が有効なプロジェクトでは警告を抑制せず解消する。
- .NET API と連携する非同期処理は `task {}` と `Task` を既定とし、既存 API が `Async` を要求する場合だけ変換する。`.Result`、`.Wait()`、`Async.RunSynchronously` で非同期処理を同期ブロックしてはならない。
- `CancellationToken` は公開非同期境界で受け取り、下位 API へ伝播する。fire-and-forget は避け、必要な場合は所有者、例外処理、終了方法を明示する。
- タイムアウト、再試行、並行数制限は境界で明示する。再試行は一時的かつ冪等な操作に限定し、指数 backoff など既存の標準機構を優先する。
- `IDisposable` / `IAsyncDisposable` は `use` / `use!` で確実に解放する。共有可変状態を避け、必要な同期は最小範囲に限定する。

## 一般的な設計とコーディング

- KISS と YAGNI を守り、性能要件に寄与しない抽象化を避ける。標準ライブラリは baseline として測定し、より高速・軽量な特化実装を作れる場合は自前実装を優先する。重複が偶然一度現れただけで抽象化せず、実際に同じ理由で変わる知識だけを共通化する。
- 名前はドメインの意図を表し、略語、型名の反復、`Utils`、`Helpers`、`Manager` のような責務不明の名前を避ける。
- 関数とモジュールは一つの明確な責務に保ち、ネストや分岐が増えたら型、pattern matching、早期の検証、純粋な小関数で単純化する。
- public を既定にせず、必要最小限の可視性にする。公開 API は互換性、入力条件、失敗、キャンセル、所有権を明確にし、必要な XML documentation を付ける。
- 信頼境界では入力の形式、範囲、長さ、権限を検証する。SQL、コマンド、HTML などは構造化 API と parameterization / encoding を使用し、文字列連結で組み立てない。
- secret、token、個人情報をソース、テストデータ、ログへ書かない。ログは構造化し、必要な相関情報を含め、機密値を redaction する。
- 機械可読な日時は `DateTimeOffset` と UTC を基本とし、テキスト・数値の保存や通信には明示的な culture と format を使用する。ローカル時刻や既定 culture に暗黙依存しない。
- リソース上限を考慮し、外部入力に対する無制限の読み込み、再帰、並行処理、再試行を作らない。
- cold path も無駄な allocation と依存を避ける。hot path では明快さを理由に遅い実装を残さず、profile と benchmark に基づいて最速の検証済み実装を選ぶ。
- コメントはコードから分からない理由、制約、trade-off を説明する。処理内容の言い換え、古いコードのコメントアウト、将来の憶測を残さない。
- compiler warning と analyzer warning を新たに増やさない。警告の全体抑制は避け、不可避なら最小範囲で理由を示す。

## プロジェクトと依存関係

- `.fsproj` の `<Compile Include>` 順序は F# の依存順序として扱い、ファイル追加・移動時に定義が利用箇所より前になることを確認する。
- production code の新しい NuGet package は原則として追加しない。BCL、runtime intrinsic、または目的特化の自前実装を先に比較し、それらでは達成不能で、候補 package が benchmark で明確に優位な場合だけ、保守状況、license、脆弱性、transitive dependency、binary size を評価して追加する。
- test、benchmark、analyzer、formatter など、製品の source、runtime dependency、依存 closure、publish artifact に残らない開発専用 NuGet package は許可する。専用 project または local tool manifest に隔離し、適用可能な package 参照には `PrivateAssets="all"` を設定して、production artifact に含まれないことを検証する。
- SDK と package version はリポジトリの `global.json`、Central Package Management、lock file 方針に従う。安易な一括更新や floating version を使用しない。
- `FSharp.Core` の SDK 既定参照を尊重し、明確な互換性要件がない限り個別に version を固定しない。
- serialization、database、HTTP などの外部契約は domain type と分離し、境界用 DTO で明示的に変換する。契約変更には後方互換性と migration を検討する。

## テスト

- 新しい振る舞いには成功、境界値、想定内の失敗を検証するテストを追加する。bug fix には修正前に失敗する最小の regression test を追加する。
- 純粋な domain logic は高速で決定的な unit test にし、I/O 境界は integration test で契約を確認する。純粋関数を mock してはならない。
- テストは公開された振る舞いを検証し、private な実装手順に固定しない。一つのテストが失敗した理由を特定できる粒度にする。
- 時刻、乱数、環境、実行順、network に暗黙依存させない。固定の待機時間で非同期完了を待たず、制御可能な clock、signal、fake を使う。
- 性質を表す方が具体例の列挙より明快な場合は property test を優先する。必要な test package は開発専用依存として隔離し、production artifact に含めない。

## 完了条件

- リポジトリ指定の formatter を使用する。F# formatter が構成済みなら Fantomas の設定を尊重し、手作業の独自整形を混ぜない。
- 対象 project または solution に対して、原則として `dotnet build` と `dotnet test` を実行する。通常は build の暗黙 restore を使用し、locked mode や restore 単体の検証が必要な場合だけ `dotnet restore` を別途実行する。リポジトリ固有コマンドがある場合はそちらを優先する。
- macOS と Windows のそれぞれで Release build、test、該当する publish artifact の起動を実機または CI runner で検証する。一方でも未検証または失敗なら完了扱いにしてはならない。
- CJK content・file path・font、security、異常系、stress、concurrency、長時間実行を対象とするテストを通し、既知の不具合、security finding、クラッシュ、フリーズ、resource leak がないことを確認する。
- 性能に影響する変更では、変更前 baseline、BCL / 外部ライブラリ候補、自前実装を同じ benchmark で比較し、大容量ファイル、応答、検索を含む該当 workload の実行時間、throughput、p50 / p95 / p99 latency、allocated bytes、working set、CPU、GC、I/O、成果物サイズを記録する。performance または resource regression がある変更を完了扱いにしてはならない。
- 変更箇所に最も近いテスト、全体テスト、formatter / analyzer の順で結果を確認し、新しい警告、意図しない公開 API や package 変更がないことを確認する。
- 最終報告では変更内容、実行した検証と結果、未検証事項または既知の制約を簡潔に示す。
