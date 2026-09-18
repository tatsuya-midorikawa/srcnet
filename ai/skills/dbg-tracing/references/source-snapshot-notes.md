# Source Snapshot Notes

この文書は [analysis-workflow.md](./analysis-workflow.md)、[perfetto-sql-cookbook.md](./perfetto-sql-cookbook.md)、[scenario-playbooks.md](./scenario-playbooks.md) の作成根拠を要約したものです。通常の trace ログ解析では実装ファイルを直接開かず、reference と trace file 自体を根拠にします。

## Snapshot Date

- 2026-05-20

## Summarized Sources

| Source | Summary |
|---|---|
| `content/browser/tracing/tracing_ui.cc` | edge://tracing / chrome://tracing UI は base64 encoded options を受け取り、`stream_format` が `protobuf` の場合は Perfetto tracing session、未指定または JSON の場合は legacy tracing controller path を使う。終了時は compressed endpoint 経由で trace data を返す。 |
| `content/browser/tracing/tracing_ui_unittest.cc` | tracing options は `included_categories`、`excluded_categories`、`record_mode`、`enable_systrace`、`stream_format`、`memory_dump_config` などを含む JSON として parse される。 |
| `third_party/perfetto/docs/getting-started/other-formats.md` | Chrome JSON trace は `pid`、`tid`、`ts`、`ph`、`name`、`cat`、`args` などを持つ event の配列/オブジェクト形式。Perfetto UI と Trace Processor は Chrome JSON を読み込み、`slice`、`track`、`process`、`thread`、`counter`、`args` などへ正規化する。 |
| `third_party/perfetto/docs/analysis/getting-started.md` | Trace Processor は trace format を抽象化し、PerfettoSQL で構造化データを query する分析 workflow を提供する。 |
| `third_party/perfetto/docs/analysis/trace-processor.md` | `trace_processor` shell は trace を読み込んで SQL query できる。基本確認として `slice` や `counter` を query できる。 |
| `third_party/perfetto/docs/analysis/perfetto-sql-getting-started.md` | Trace Processor の基本概念は events、slices、counters、tracks、scheduling、CPU profiling、heap profiling。timestamp は Trace Processor 内では ns 単位。 |
| `third_party/perfetto/src/trace_processor/perfetto_sql/stdlib/chrome/tasks.sql` | Chrome task analysis の stdlib。`chrome_tasks`、`chrome_scheduler_tasks` などで top-level task、scheduler task、mojo/navigation task を扱う。 |
| `third_party/perfetto/src/trace_processor/perfetto_sql/stdlib/chrome/startups.sql` | `chrome_startups` は startup begin、first visible content、launch cause を扱う。 |
| `third_party/perfetto/src/trace_processor/perfetto_sql/stdlib/chrome/page_loads.sql` | `chrome_page_loads` は navigation_id、FCP/LCP/DCL/load/user timing marks を扱う。 |
| `third_party/perfetto/src/trace_processor/perfetto_sql/stdlib/chrome/event_latency.sql` | `chrome_event_latencies` と `chrome_gesture_scroll_updates` は EventLatency、scroll update、presentation/jank 関連情報を扱う。 |
| `third_party/perfetto/src/trace_processor/perfetto_sql/stdlib/chrome/input.sql` | `chrome_inputs` と `chrome_input_pipeline_steps` は input pipeline step、latency id、input type を扱う。 |
| `third_party/perfetto/src/trace_processor/perfetto_sql/stdlib/chrome/graphics_pipeline.sql` | `chrome_graphics_pipeline_surface_frame_steps` と `chrome_graphics_pipeline_display_frame_steps` は surface/display frame pipeline steps を扱う。 |

## Update Policy

- 通常のスキル実行では source を直接探索しない。
- ユーザーが「reference を更新して」「最新実装で確認して」と明示した場合だけ実装確認を行う。
- 更新時は次を行う。
  1. tracing UI の保存/stream format semantics が変わっていないか確認する。
  2. Perfetto docs の Chrome JSON/import/Trace Processor guidance を確認する。
  3. PerfettoSQL stdlib の Chrome modules と table/view names を再抽出する。
  4. scenario playbooks の SQL snippets が現行 stdlib と一致するか確認する。
  5. reference の snapshot date と known gaps を更新する。

## Known Gaps

- この reference は採取済み trace の解析手順をまとめるもので、個々の Edge feature event name の完全 catalog ではない。
- Trace Processor stdlib はバージョン差分があるため、ユーザーの Perfetto UI/trace_processor が古い場合は module/table が存在しない可能性がある。
- platform-specific data source や OS-level scheduler/memory tables は採取設定と platform に依存する。
- 実際の root cause 判定は trace file の内容、再現 window、採取 category、buffer 欠損の有無に依存する。
