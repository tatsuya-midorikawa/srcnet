# Perfetto SQL Cookbook for Chrome/Edge Traces

Snapshot date: 2026-05-20

These snippets are intended for Perfetto UI Query tab or the `trace_processor` shell. Trace Processor normalizes Chrome JSON and Perfetto protobuf traces into common tables such as `slice`, `track`, `process`, `thread`, `counter`, and `args`.

## Basic Inventory

Trace bounds:

```sql
SELECT
  start_ts / 1e9 AS start_s,
  end_ts / 1e9 AS end_s,
  dur / 1e9 AS dur_s
FROM trace_bounds;
```

Processes:

```sql
SELECT upid, pid, name, cmdline
FROM process
ORDER BY name, pid;
```

Threads:

```sql
SELECT t.utid, t.tid, t.name AS thread_name, p.name AS process_name, p.pid
FROM thread AS t
LEFT JOIN process AS p USING (upid)
ORDER BY process_name, thread_name;
```

Category distribution:

```sql
SELECT category, COUNT(*) AS events, SUM(dur) / 1e6 AS total_ms
FROM slice
GROUP BY category
ORDER BY events DESC
LIMIT 50;
```

Top long slices with context:

```sql
INCLUDE PERFETTO MODULE slices.with_context;

SELECT
  ts / 1e9 AS ts_s,
  dur / 1e6 AS dur_ms,
  process_name,
  thread_name,
  category,
  name
FROM thread_slice
ORDER BY dur DESC
LIMIT 50;
```

Search for an event name or category:

```sql
SELECT id, ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, category, name
FROM slice
WHERE name GLOB '*Print*' OR category GLOB '*print*'
ORDER BY ts
LIMIT 100;
```

Arguments for a slice:

```sql
SELECT a.flat_key, a.string_value, a.int_value, a.real_value
FROM slice AS s
JOIN args AS a ON s.arg_set_id = a.arg_set_id
WHERE s.id = $slice_id
ORDER BY a.flat_key;
```

Ancestor context for a slice:

```sql
SELECT id, ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, category, name
FROM ancestor_slice($slice_id)
ORDER BY depth DESC;
```

Descendant work under a slice:

```sql
SELECT id, ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, category, name
FROM descendant_slice($slice_id)
ORDER BY dur DESC
LIMIT 50;
```

## Chrome Stdlib Modules

Use these when the trace contains the corresponding Chrome events.

| Scenario | Include | Useful tables/views |
|---|---|---|
| Tasks and main-thread work | `INCLUDE PERFETTO MODULE chrome.tasks;` | `chrome_tasks`, `chrome_scheduler_tasks` |
| Startup | `INCLUDE PERFETTO MODULE chrome.startups;` | `chrome_startups` |
| Page load | `INCLUDE PERFETTO MODULE chrome.page_loads;` | `chrome_page_loads` |
| Input pipeline | `INCLUDE PERFETTO MODULE chrome.input;` | `chrome_inputs`, `chrome_input_pipeline_steps` |
| Event latency / scroll | `INCLUDE PERFETTO MODULE chrome.event_latency;` | `chrome_event_latencies`, `chrome_gesture_scroll_updates` |
| Graphics pipeline | `INCLUDE PERFETTO MODULE chrome.graphics_pipeline;` | `chrome_graphics_pipeline_surface_frame_steps`, `chrome_graphics_pipeline_display_frame_steps` |
| Scroll jank | `INCLUDE PERFETTO MODULE chrome.scroll_jank_v4;` | `chrome_scroll_jank_v4_results`, `chrome_scroll_jank_v4_reasons` |
| Interactions | `INCLUDE PERFETTO MODULE chrome.interactions;` | `chrome_interactions` |
| Histograms | `INCLUDE PERFETTO MODULE chrome.histograms;` | `chrome_histograms` |
| CPU profiling | `INCLUDE PERFETTO MODULE stacks.cpu_profiling;` | `cpu_profiling_samples`, `cpu_profiling_summary_tree` |
| Scheduling context | `INCLUDE PERFETTO MODULE sched.with_context;` | `sched_with_thread_process` |

## Long Tasks and Hangs

Chrome task summary:

```sql
INCLUDE PERFETTO MODULE chrome.tasks;
INCLUDE PERFETTO MODULE slices.with_context;

SELECT
  s.ts / 1e9 AS ts_s,
  s.dur / 1e6 AS dur_ms,
  ts.thread_name,
  ts.process_name,
  c.task_type,
  c.task_name
FROM chrome_tasks AS c
JOIN slice AS s USING (id)
JOIN thread_slice AS ts USING (id)
ORDER BY s.dur DESC
LIMIT 50;
```

Runnable waiting vs running:

```sql
INCLUDE PERFETTO MODULE sched.with_context;

SELECT
  st.ts / 1e9 AS ts_s,
  st.dur / 1e6 AS dur_ms,
  p.name AS process_name,
  t.name AS thread_name,
  st.state,
  st.io_wait
FROM thread_state AS st
JOIN thread AS t USING (utid)
LEFT JOIN process AS p USING (upid)
WHERE st.dur > 50 * 1000 * 1000
ORDER BY st.dur DESC
LIMIT 50;
```

CPU profile hot functions:

```sql
INCLUDE PERFETTO MODULE stacks.cpu_profiling;

SELECT name, mapping_name, self_count, cumulative_count
FROM cpu_profiling_summary_tree
ORDER BY cumulative_count DESC
LIMIT 50;
```

## Startup

```sql
INCLUDE PERFETTO MODULE chrome.startups;

SELECT
  id,
  startup_begin_ts / 1e9 AS begin_s,
  (first_visible_content_ts - startup_begin_ts) / 1e6 AS first_visible_ms,
  launch_cause,
  browser_upid
FROM chrome_startups
ORDER BY startup_begin_ts;
```

Look around startup window:

```sql
INCLUDE PERFETTO MODULE slices.with_context;

SELECT ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, process_name, thread_name, category, name
FROM thread_slice
WHERE ts BETWEEN $begin_ts AND $end_ts
ORDER BY dur DESC
LIMIT 100;
```

## Page Load / Navigation

```sql
INCLUDE PERFETTO MODULE chrome.page_loads;

SELECT
  navigation_id,
  navigation_start_ts / 1e9 AS start_s,
  fcp / 1e6 AS fcp_ms,
  lcp / 1e6 AS lcp_ms,
  (dom_content_loaded_event_ts - navigation_start_ts) / 1e6 AS dcl_ms,
  (load_event_ts - navigation_start_ts) / 1e6 AS load_ms,
  url
FROM chrome_page_loads
ORDER BY navigation_start_ts;
```

Generic navigation/page-load event search:

```sql
SELECT ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, category, name
FROM slice
WHERE category GLOB '*navigation*'
   OR category GLOB '*loading*'
   OR name GLOB '*PageLoad*'
   OR name GLOB '*Navigation*'
ORDER BY ts
LIMIT 200;
```

## Input Latency and Scroll Jank

Event latency:

```sql
INCLUDE PERFETTO MODULE chrome.event_latency;

SELECT
  ts / 1e9 AS ts_s,
  dur / 1e6 AS dur_ms,
  event_type,
  is_presented,
  is_janky_scrolled_frame,
  vsync_interval_ms,
  scroll_update_id
FROM chrome_event_latencies
ORDER BY dur DESC
LIMIT 100;
```

Input pipeline steps:

```sql
INCLUDE PERFETTO MODULE chrome.input;

SELECT
  latency_id,
  ts / 1e9 AS ts_s,
  dur / 1e6 AS dur_ms,
  input_type,
  step,
  utid
FROM chrome_input_pipeline_steps
ORDER BY latency_id, ts
LIMIT 200;
```

Scroll jank v4 summary when available:

```sql
INCLUDE PERFETTO MODULE chrome.scroll_jank_v4;

SELECT *
FROM chrome_scroll_jank_v4_results
LIMIT 100;
```

## Rendering and GPU

Graphics pipeline steps:

```sql
INCLUDE PERFETTO MODULE chrome.graphics_pipeline;

SELECT
  ts / 1e9 AS ts_s,
  dur / 1e6 AS dur_ms,
  step,
  surface_frame_trace_id,
  display_trace_id,
  utid
FROM (
  SELECT ts, dur, step, surface_frame_trace_id, NULL AS display_trace_id, utid
  FROM chrome_graphics_pipeline_surface_frame_steps
  UNION ALL
  SELECT ts, dur, step, NULL AS surface_frame_trace_id, display_trace_id, utid
  FROM chrome_graphics_pipeline_display_frame_steps
)
ORDER BY dur DESC
LIMIT 100;
```

Generic Viz/CC/GPU search:

```sql
SELECT ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, category, name
FROM slice
WHERE category GLOB '*viz*'
   OR category GLOB '*cc*'
   OR category GLOB '*gpu*'
ORDER BY dur DESC
LIMIT 100;
```

## Memory

Memory counters:

```sql
SELECT
  c.ts / 1e9 AS ts_s,
  c.value,
  t.name AS track_name
FROM counter AS c
JOIN counter_track AS t ON c.track_id = t.id
WHERE t.name GLOB '*Memory*'
   OR t.name GLOB '*RSS*'
   OR t.name GLOB '*malloc*'
ORDER BY c.ts
LIMIT 200;
```

Memory-infra slices/events:

```sql
SELECT ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, category, name
FROM slice
WHERE category GLOB '*memory*' OR name GLOB '*Memory*'
ORDER BY ts
LIMIT 200;
```

## Network

Generic network event search:

```sql
SELECT ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, category, name
FROM slice
WHERE category GLOB '*net*'
   OR category GLOB '*loading*'
   OR name GLOB '*URLRequest*'
   OR name GLOB '*Http*'
   OR name GLOB '*DNS*'
ORDER BY ts
LIMIT 200;
```

Redact URL/host/header args unless the user permits disclosure.

## Print / PDF

```sql
SELECT ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, category, name
FROM slice
WHERE category GLOB '*print*'
   OR category GLOB '*pdf*'
   OR name GLOB '*Print*'
   OR name GLOB '*PDF*'
ORDER BY ts
LIMIT 200;
```

Context around a print/PDF event:

```sql
INCLUDE PERFETTO MODULE slices.with_context;

SELECT ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, process_name, thread_name, category, name
FROM thread_slice
WHERE ts BETWEEN $event_ts - 1000000000 AND $event_ts + 3000000000
ORDER BY ts;
```

## Edge Feature Search

Use category/name search first, then inspect args and surrounding tasks.

```sql
SELECT ts / 1e9 AS ts_s, dur / 1e6 AS dur_ms, category, name
FROM slice
WHERE category GLOB '*edge*'
   OR category GLOB '*SmartScreen*'
   OR category GLOB '*wdag*'
   OR category GLOB '*pdf_plugin*'
   OR category GLOB '*print_preview*'
ORDER BY ts
LIMIT 300;
```
