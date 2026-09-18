# Source Snapshot Notes

この文書は [trace-selection-reference.md](./trace-selection-reference.md) の作成根拠を要約したものです。通常の trace category 選定では実装ファイルを直接開かず、この snapshot と selection reference を使います。

## Snapshot Date

- 2026-05-20

## Selectable Category Inventory

- Record Categories: 241
- Disabled by Default Categories: 116
- Total non-group categories: 357
- Full snapshot: [selectable-categories.md](./selectable-categories.md)

## Summarized Sources

以下は snapshot 作成時に参照した代表的な実装です。通常利用時に開く必要はありません。

| Source | Summary |
|---|---|
| `base/trace_event/builtin_categories.h` | Built-in Chromium/Edge categories、Edge branded build category、`debug`/`slow` tags、category groups を定義する。新規 category はここに登録されることが多い。 |
| `v8/src/tracing/trace-categories.h` | V8 TrackEvent category registry を定義する。base registry と重複する `v8` 系 category もあるため、snapshot では dedupe する。 |
| `third_party/webrtc/rtc_base/trace_categories.h` | WebRTC TrackEvent category registry を定義する。base registry と重複する `webrtc` 系 category もあるため、snapshot では dedupe する。 |
| `base/trace_event/trace_config_category_filter.cc` | `included_categories`、`excluded_categories`、`disabled-by-default-*`、category group の有効化規則を持つ。 |
| `base/trace_event/trace_config.cc` | Chromium `TraceConfig` を JSON / Perfetto `TrackEventConfig` に変換する。`__metadata` は有効化される。 |
| `content/browser/tracing/tracing_controller_impl.cc` | UI 向け category set は base/V8/WebRTC の registry から集約され、group category は除外される。 |
| `content/browser/tracing/tracing_ui.cc` | edge://tracing / chrome://tracing の recording 開始、JSON/protobuf 分岐、Perfetto config setup を扱う。 |
| `third_party/catapult/tracing/tracing/ui/extras/about_tracing/record_selection_dialog.html` | UI は `disabled-by-default-` prefix の有無で `Record Categories` と `Disabled by Default Categories` を分ける。 |
| `services/tracing/public/cpp/perfetto/perfetto_config.cc` | `memory-infra`、`histogram_samples`、`cpu_profiler`、`system_metrics`、Java heap などの追加 data source 起動条件を持つ。 |
| `third_party/perfetto/protos/perfetto/config/track_event/track_event_config.proto` | Perfetto `TrackEventConfig` の enabled/disabled categories/tags と既定挙動を定義する。 |
| `third_party/perfetto/src/tracing/internal/track_event_internal.cc` | Perfetto C++ SDK の category/tag matching、category group の判定、未 match category の既定有効挙動を実装する。 |

## Update Policy

- 通常のスキル実行では source を直接探索しない。
- ユーザーが「最新実装で確認して」「reference を更新して」と明示した場合だけ実装確認を行う。
- 更新時は次を行う。
  1. `builtin_categories.h`、`v8/src/tracing/trace-categories.h`、`third_party/webrtc/rtc_base/trace_categories.h` から non-group category を抽出し、重複を除外する。
  2. `trace_config_category_filter.cc` と `trace_config.cc` で category filter semantics の変更を確認する。
  3. `perfetto_config.cc` で追加 data source の起動条件を確認する。
  4. Perfetto `track_event_config.proto` と `track_event_internal.cc` で matching order と default behavior の変更を確認する。
  5. [selectable-categories.md](./selectable-categories.md) を更新し、Record/Disabled counts を再計算する。
  6. [trace-selection-reference.md](./trace-selection-reference.md) を更新し、この snapshot date と summaries を更新する。

## Known Gaps

- V8/WebRTC の独自 registry は今回 snapshot に含めているが、build flag により実行時の表示が変わる可能性がある。
- Edge feature-specific category は category 名から判断できる範囲を catalog 化している。詳細 event name までは保持していない。
- OS-specific data source は platform 条件が変わる可能性があるため、必要時は reference 更新として確認する。