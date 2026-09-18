# Trace Selection Reference

この reference は edge://tracing / chrome://tracing / Perfetto の category 選定に必要な要約情報です。通常のスキル利用では Chromium/Edge/Perfetto の実装ファイルを直接開かず、この文書を根拠に判断します。

## Selection Model

- UI で選ぶ「event」は実質的に trace category。個別 event name は trace 内に現れる名前であり、通常は UI の選択単位ではない。
- edge://tracing / chrome://tracing UI の全選択肢は [selectable-categories.md](./selectable-categories.md) を参照する。
- 空の category filter は既定 category を有効にする。`disabled-by-default-*` は既定では無効。
- `disabled-by-default-*`、`debug`、`slow` 系 category は高コストまたは詳細診断向け。必要最小限にする。
- category group は UI に直接出ないことがある。group を見つけた場合は構成要素 category を選ぶ。
- Edge 固有 category は Edge branded build 限定の可能性がある。

## UI Category Inventory

The current snapshot contains these selectable non-group category counts:

| UI section | Count | Full list |
|---|---:|---|
| Record Categories | 241 | [Record Categories](./selectable-categories.md#record-categories) |
| Disabled by Default Categories | 116 | [Disabled by Default Categories](./selectable-categories.md#disabled-by-default-categories) |

Use the full list when checking whether a category is actually selectable in the UI. The shorter catalogs below are for faster scenario selection only.

## Symptom Matrix

| 症状 | Baseline | Deep/Optional |
|---|---|---|
| 起動、初期化 | `startup`, `browser`, `content`, `loading`, `navigation`, `interactions`, `gpu`, `ui`, Edge なら `startupboost` | `disabled-by-default-cpu_profiler`, `disabled-by-default-thread_pool_diagnostics`, 必要なら startup tracing 設定 |
| ナビゲーション、ページロード | `browser`, `content`, `navigation`, `loading`, `renderer_host`, `renderer`, `blink`, `blink.net`, `blink.resource`, `net` | `devtools.timeline`, `disabled-by-default-devtools.timeline`, `disabled-by-default-devtools.timeline.frame`, `disabled-by-default-net`, `netlog`, `disabled-by-default-netlog.sensitive` |
| ネットワーク | `net`, `netlog`, `network.scheduler`, `loading`, `navigation`, `blink.net`, `browser` | `disabled-by-default-net`, `disabled-by-default-network`, `disabled-by-default-netlog.sensitive` |
| 入力、スクロール | `input`, `input.scrolling`, `latency`, `ui`, `views`, `cc`, `viz`, `interactions`, `scheduler` | `disabled-by-default-devtools.timeline.inputs`, `disabled-by-default-cc.debug.scheduler`, `disabled-by-default-viz.hit_testing_flow` |
| 描画、jank、frame drop | `cc`, `viz`, `gpu`, `compositor`, `ui`, `views`, `input`, `latency`, `toplevel`, `scheduler`, `sequence_manager` | `disabled-by-default-cc`, `disabled-by-default-cc.debug`, `disabled-by-default-viz.quads`, `disabled-by-default-viz.overdraw`, `disabled-by-default-viz.surface_lifetime`, `disabled-by-default-gpu.debug`, `disabled-by-default-gpu.service`, `disabled-by-default-devtools.timeline.frame` |
| CPU 高負荷、ハング | `toplevel`, `scheduler`, `sequence_manager`, `base`, `ipc`, `mojom`, `browser`, `content` | `disabled-by-default-cpu_profiler`, `disabled-by-default-thread_pool_diagnostics`, `disabled-by-default-base`, `disabled-by-default-toplevel.ipc` |
| メモリ、OOM、リーク | `memory`, `partition_alloc`, `browser`, `renderer`, `gpu` | `disabled-by-default-memory-infra` と `memory_dump_config`、`disabled-by-default-java-heap-profiler`, `disabled-by-default-memory-infra.v8.code_stats` |
| V8/JavaScript | `v8`, `v8.execute`, `blink`, `devtools.timeline`, `renderer`, `toplevel` | `disabled-by-default-v8.compile`, `disabled-by-default-v8.runtime`, `disabled-by-default-v8.runtime_stats`, `disabled-by-default-v8.runtime_stats_sampling`, `disabled-by-default-devtools.timeline.stack`, `disabled-by-default-v8.inspector` |
| メディア、音声、WebRTC | `media`, `audio`, `webaudio`, `mediastream`, `webrtc`, `gpu`, `compositor` | `disabled-by-default-audio`, `disabled-by-default-audio.latency`, `disabled-by-default-webrtc`, `disabled-by-default-video_and_image_capture`, DRM 系 Edge category が該当すれば追加 |
| 印刷、印刷プレビュー、PDF 印刷 | `print`, `browser`, `content`, `renderer`, `renderer_host`, `ui`, `views`, `ipc`, `mojom`, Edge なら `print_preview`、PDF 関連なら `pdf_plugin` | `devtools.timeline`, `disabled-by-default-devtools.timeline`, `disabled-by-default-devtools.timeline.stack`, 固まる/高負荷なら `disabled-by-default-cpu_profiler` |
| 拡張機能 | `extensions`, `browser`, `renderer`, `ipc`, `mojom`, `navigation`, `loading` | `extensions.content_verifier.debug`, `devtools`, 対象 API の専用 category |
| WebUI、Edge 固有機能 | 対象に近い Edge category、`browser`, `content`, `ui`, `views`, `ipc`, `mojom` | 対象機能の `.debug` または `disabled-by-default-*` category。掲載済みのものだけ |

## Scenario Playbooks

| シナリオ | Targeted categories | Deep/Optional |
|---|---|---|
| 印刷失敗、印刷ダイアログ、プリンタ選択 | `print`, `print_preview`, `browser`, `ui`, `views`, `ipc`, `mojom` | 固まる場合は `disabled-by-default-cpu_profiler`、JS 起点なら `disabled-by-default-devtools.timeline.stack` |
| PDF 表示、PDF 印刷、PDF プラグイン | `pdf_plugin`, `print`, `print_preview`, `renderer`, `browser`, `ui`, `viz` | 描画問題なら `disabled-by-default-devtools.timeline`, `disabled-by-default-viz.quads` |
| ダウンロード、保存、ファイル生成 | `download`, `download_service`, `FileSystem`, `disk_cache`, `browser`, `net` | ファイル I/O 深掘りなら `disabled-by-default-file` |
| 設定画面、WebUI、フライアウト | `edge_settings`, `edge_base_flyout`, `browser`, `content`, `ui`, `views`, `mojom` | JS/UI 詳細なら `devtools.timeline`, `disabled-by-default-devtools.timeline` |
| サインイン、ID、パスワード、自動入力、Wallet | `identity`, `passwords`, `password_manager`, `edge_autofill`, `edge_settings_autofill`, `edge_wallet`, `browser` | 高負荷/ハングなら `disabled-by-default-cpu_profiler` |
| SmartScreen、安全性警告、セキュリティ UI | `SmartScreen`, `safe_browsing`, `navigation`, `browser`, `net`, `ui` | ネットワーク詳細は `netlog`; sensitive 情報が必要な場合のみ `disabled-by-default-netlog.sensitive` |
| 拡張機能、content verifier | `extensions`, `extensions.content_verifier.debug`, `browser`, `renderer`, `ipc`, `mojom` | 読み込み/検証が遅い場合は `disabled-by-default-file`, `disabled-by-default-cpu_profiler` |
| Omnibox、検索、ナビゲーション候補 | `omnibox`, `navigation`, `browser`, `ui`, `loading`, `net` | ネットワーク候補調査なら `netlog` |
| メディア再生、DRM、ハードウェアデコード | `media`, `gpu`, `drm`, `DXVA_Decoding`, `MFCdmTrace`, `MFCdmWarning`, `MFCdmError` | GPU/decoder 詳細は `disabled-by-default-gpu.decoder`, `disabled-by-default-gpu.service` |
| WebRTC、カメラ、マイク | `webrtc`, `webrtc_stats`, `camera`, `mediastream`, `audio`, `media` | `disabled-by-default-webrtc`, `disabled-by-default-webrtc_stats`, `disabled-by-default-audio.latency` |
| 音声認識、翻訳、AI/オンデバイスモデル | `edge_network_speech_recognition`, `edge_on_device_speech_recognition`, `edge_on_device_model`, `edge_video_translate`, `entity_extraction`, `page_understanding` | `disabled-by-default-edge_video_translate.debug`, CPU 問題なら `disabled-by-default-cpu_profiler` |
| Edge WebView / embedded browser | `edge_webview`, `browser`, `content`, `renderer`, `navigation`, `ipc`, `mojom` | 描画なら `disabled-by-default-devtools.timeline.frame` |
| 起動、Startup Boost、初期ページ表示 | `startup`, `startupboost`, `browser`, `loading`, `navigation`, `ui`, `gpu` | 早期起動やハングは `disabled-by-default-cpu_profiler`, `disabled-by-default-thread_pool_diagnostics` |
| 同期、履歴、ブラウジングデータ | `sync`, `history`, `edge_history`, `browsing_data`, `browser`, `sql`, `leveldb` | DB/IO 詳細は `disabled-by-default-file` |
| キャッシュ、ストレージ、IndexedDB | `disk_cache`, `CacheStorage`, `IndexedDB`, `FileSystem`, `sql`, `leveldb` | 深掘りは `disabled-by-default-file` と `disabled-by-default-memory-infra` を必要時のみ |
| IME、手書き、入力候補 | `ime`, `edge_handwriting`, `input`, `ui`, `views`, `latency` | 入力遅延は `disabled-by-default-devtools.timeline.inputs` |
| アクセシビリティ、読み上げ、支援技術連携 | `accessibility`, `ui`, `views`, `browser`, `content`, `renderer` | UI ハングなら `disabled-by-default-cpu_profiler` |
| Component update、バックグラウンド処理 | `update_client`, `browser`, `network.scheduler`, `download_service`, `diagnostic_event` | ネットワーク詳細は `netlog`; 高負荷なら `disabled-by-default-cpu_profiler` |
| GPU、表示、Compositor、フレーム落ち | `gpu`, `viz`, `cc`, `compositor`, `ui`, `views`, `latency` | `disabled-by-default-gpu.debug`, `disabled-by-default-gpu.service`, `disabled-by-default-viz.quads`, `disabled-by-default-cc.debug` |
| メモリ増加、OOM、リーク疑い | `memory`, `partition_alloc`, `performance_manager.graph`, `browser`, `renderer`, `gpu` | `disabled-by-default-memory-infra`, `disabled-by-default-memory-infra.v8.code_stats`, `disabled-by-default-java-heap-profiler` |

## Common Category Catalog

| 領域 | Category |
|---|---|
| Browser/content/navigation | `browser`, `content`, `navigation`, `loading`, `renderer`, `renderer_host`, `resources`, `ServiceWorker` |
| Blink/web platform | `blink`, `blink.animations`, `blink.bindings`, `blink.console`, `blink.net`, `blink.resource`, `blink.user_timing`, `blink.worker`, `WebCore` |
| UI/input/rendering | `ui`, `views`, `views.frame`, `input`, `input.scrolling`, `interactions`, `latency`, `latencyInfo`, `cc`, `viz`, `compositor`, `gpu` |
| Scheduling/process | `toplevel`, `base`, `scheduler`, `scheduler.flow`, `sequence_manager`, `ipc`, `mojom`, `mojom.flow`, `wakeup.flow` |
| Storage/data | `sql`, `leveldb`, `IndexedDB`, `CacheStorage`, `FileSystem`, `disk_cache`, `partition_alloc`, `memory` |
| Network | `net`, `net.stream`, `netlog`, `network.scheduler`, `download`, `download_service` |
| JavaScript/V8 | `v8`, `v8.execute`, `v8.wasm`, `devtools`, `devtools.timeline` |
| Media | `media`, `audio`, `webaudio`, `mediastream`, `webrtc`, `webrtc_stats`, `camera`, `midi`, `drm` |
| Print/PDF | `print`, Edge: `print_preview`, `pdf_plugin` |
| Extensions/security | `extensions`, `safe_browsing`, `passwords`, `identity`, `SiteEngagement` |
| Startup/shutdown | `startup`, `shutdown`, `login` |

## Edge Category Notes

Edge branded builds may expose additional categories. Use them only when the requested feature clearly maps to the category name.

| 領域 | Category |
|---|---|
| Edge UI/features | `edge_settings`, `edge_settings_autofill`, `edge_find_in_page`, `edge_frame`, `edge_hub_apps`, `edge_spaceworks`, `edge_quick_search`, `edge_history`, `edge_wallet` |
| Edge web/platform | `edge_webview`, `edge_prism`, `edge_handwriting`, `edge_network_speech_recognition`, `edge_on_device_speech_recognition`, `edge_on_device_model`, `edge_video_translate`, `edge_video_translate.debug` |
| Shopping/content | `edge_shopping`, `price_comparison`, `Collections`, `Hub`, `page_understanding`, `entity_extraction` |
| Security/enterprise | `SmartScreen`, `wdag`, `TyposquattingChecker`, `diagnostic_event`, `etw_log`, `perftrack`, `resource_management` |
| Media/DRM/PDF/print | `DXVA_Decoding`, `MFCdmError`, `MFCdmTrace`, `MFCdmWarning`, `pdf_plugin`, `print_preview` |
| Startup | `startupboost` |

## Deep Categories

| Category | 用途 | 注意 |
|---|---|---|
| `disabled-by-default-cpu_profiler` | CPU 高負荷、ハング、long task のスタック調査 | overhead と trace サイズが増える |
| `disabled-by-default-memory-infra` | memory dump、OOM、リーク、process memory breakdown | `memory_dump_config` を併用する |
| `disabled-by-default-java-heap-profiler` | Android/Java heap 調査 | 対象 platform を確認する |
| `disabled-by-default-thread_pool_diagnostics` | ThreadPool 詰まり、post task 遅延 | 長時間採取ではサイズに注意 |
| `disabled-by-default-devtools.timeline` | DevTools timeline 相当の詳細 | 描画/JS/入力で有用だが noisy |
| `disabled-by-default-devtools.timeline.frame` | frame/jank 詳細 | frame drop 調査向け |
| `disabled-by-default-devtools.timeline.inputs` | 入力イベント詳細 | 入力遅延調査向け |
| `disabled-by-default-devtools.timeline.stack` | JS/DevTools stack 詳細 | privacy とサイズに注意 |
| `disabled-by-default-net` | net stack 詳細 | URL/接続情報の扱いに注意 |
| `disabled-by-default-netlog.sensitive` | sensitive NetLog 詳細 | URL、host、header、認証関連値などを含み得る。明示許可が必要 |
| `disabled-by-default-cc`, `disabled-by-default-cc.debug` | compositor/cc 詳細 | 描画 jank 用。大量になる |
| `disabled-by-default-viz.quads`, `disabled-by-default-viz.overdraw`, `disabled-by-default-viz.surface_lifetime` | Viz surface/quads/overdraw 詳細 | 描画・合成の深掘り用 |
| `disabled-by-default-gpu.debug`, `disabled-by-default-gpu.service` | GPU process/service 詳細 | GPU/driver 近辺の問題向け |
| `disabled-by-default-v8.compile`, `disabled-by-default-v8.runtime`, `disabled-by-default-v8.runtime_stats` | V8 compile/runtime 詳細 | JS 性能調査向け |
| `disabled-by-default-audio`, `disabled-by-default-audio.latency`, `disabled-by-default-webrtc` | audio/WebRTC 詳細 | media 再現時だけ |

## TraceConfig Rules

- `included_categories` が空なら既定 category が有効。`disabled-by-default-*` は含まれない。
- 通常 category を `included_categories` に入れると、基本的にはその category セットへ絞られる。
- `disabled-by-default-*` を `included_categories` に入れると、その detailed category が有効になる。
- `disabled-by-default-*` だけを指定する運用では、既定 category も維持される挙動がある。文脈も採りたい場合に有用。
- `excluded_categories` はノイズ削減に使う。sensitive category は広域指定で巻き込まない。
- metadata は通常 trace に含まれる前提でよい。
- 長時間再現は continuous/ring buffer、短時間再現は until full または as much as possible を検討する。

## Perfetto TrackEvent Rules

- `enabled_categories` / `disabled_categories` は glob。
- 直接 Perfetto config で特定カテゴリだけ採るなら、`disabled_categories: "*"` と必要な `enabled_categories` の組み合わせを使う。
- Perfetto C++ TrackEvent は match しなかった category を既定で有効扱いにする。
- Perfetto C SDK は match しなかった category を既定で無効扱いにする。
- `slow` と `debug` tag は既定で無効扱いになる実装がある。必要な detailed category は明示する。
- `enabled_tags` / `disabled_tags` は tag ベースの絞り込みに使えるが、Edge/Chromium の通常 UI 操作では category 名指定を優先する。

## Privacy and Cost Rules

- `disabled-by-default-netlog.sensitive`: 明示許可なしに最終セットへ入れない。
- `netlog`: ネットワーク診断には有用だが URL や接続情報を含む可能性がある。
- `disabled-by-default-memory-infra`: dump サイズが大きくなる。interval、level、buffer を明示する。
- `disabled-by-default-cpu_profiler`: overhead とサイズが増える。CPU/ハング調査など目的が明確な場合だけ使う。
- `debug`/`slow` category: 初回採取では抑え、再現の焦点が絞れたら追加する。

## Output Checklist

- Baseline / Targeted / Deep optional の 3 層で出す。
- category ごとに「なぜ必要か」「期待できる event」「注意点」を短く付ける。
- `disabled-by-default-*` は目的とコストを明記する。
- sensitive category は許可条件を書く。
- reference 未掲載の category は推測で断定しない。