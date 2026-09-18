# Trace Analysis Scenario Playbooks

Snapshot date: 2026-05-20

Use these playbooks after import sanity and broad inventory. They tell the analysis agent what to inspect in the trace, how to interpret findings, and when to request a re-capture.

## Startup / First Visible Content

Inspect:

- `chrome_startups` from `chrome.startups`
- Browser main thread long tasks near `startup_begin_ts`
- GPU/Viz initialization events if first visible content is delayed
- Disk/file/network events during profile initialization

Evidence to report:

- Startup begin timestamp, first visible content timestamp, launch cause
- Longest blocking slices on browser main thread or critical renderer/GPU threads
- Whether delay is CPU work, I/O wait, GPU/compositor, or missing data

Re-capture if missing:

- Add `startup`, `browser`, `loading`, `navigation`, `ui`, `gpu`, and CPU profiler only if stack attribution is needed.

## Navigation / Page Load

Inspect:

- `chrome_page_loads` from `chrome.page_loads`
- Navigation/loading/browser/renderer slices in the page load window
- Network and cache events if FCP/LCP or load event is delayed
- ServiceWorker events for offline/PWA or intercepted requests

Evidence to report:

- navigation_id, FCP/LCP/DCL/load timings, URL redacted as needed
- Renderer main thread long tasks around FCP/LCP
- Network or cache stalls visible in trace

Re-capture if missing:

- Add `navigation`, `loading`, `blink`, `blink.net`, `blink.resource`, `net`, `netlog`, and ServiceWorker-related categories.

## Input Latency / Scroll Jank

Inspect:

- `chrome_event_latencies`, `chrome_gesture_scroll_updates`
- `chrome_input_pipeline_steps`
- `chrome_scroll_jank_v4_results` and `chrome_scroll_jank_v4_reasons` when present
- Main thread, compositor, Viz, GPU tracks around delayed frames

Evidence to report:

- Event type, latency duration, janky/presented status, vsync interval
- Pipeline stage where delay accumulates
- Long task or compositor/GPU step that overlaps the delayed input

Re-capture if missing:

- Add `input`, `input.scrolling`, `latency`, `cc`, `viz`, `gpu`, `disabled-by-default-devtools.timeline.inputs`, and frame/jank categories as needed.

## Rendering / GPU / Frame Drop

Inspect:

- `chrome_graphics_pipeline_surface_frame_steps`
- `chrome_graphics_pipeline_display_frame_steps`
- Viz/CC/GPU slices and counters
- Swap, presentation, surface aggregation, compositor frame submission

Evidence to report:

- Slow pipeline step and process/thread context
- Whether delay is pre-aggregation, display aggregation, GPU service, or presentation
- Any repeated did-not-produce-frame or discarded frame pattern

Re-capture if missing:

- Add `cc`, `viz`, `gpu`, `compositor`, `disabled-by-default-viz.quads`, `disabled-by-default-cc.debug`, and GPU debug categories only when needed.

## CPU Hang / Long Task

Inspect:

- `chrome_tasks` and top long `thread_slice`
- `thread_state` to distinguish running vs runnable vs blocked/I/O wait
- CPU profiling summary if captured
- IPC/mojo descendants and ancestors of the long task

Evidence to report:

- Longest task duration, process/thread, category/name
- Whether the thread was executing, waiting on CPU, blocked, or waiting for IPC/GPU/network
- Stack/profile summary if available

Re-capture if missing:

- Add `toplevel`, `scheduler`, `sequence_manager`, `ipc`, `mojom`, and `disabled-by-default-cpu_profiler` for stack attribution.

## Memory / OOM / Leak Suspicion

Inspect:

- Memory counters and memory-infra events
- Process memory tracks, heap/profile tables if available
- Growth over time by process and relevant counters
- Large allocation/free patterns if heap profiling exists

Evidence to report:

- Which process grows, by how much, over what interval
- Whether memory dump or heap sample data exists
- Whether trace supports leak attribution or only aggregate memory observation

Re-capture if missing:

- Add `memory`, `partition_alloc`, `disabled-by-default-memory-infra`, and memory dump config. Use heap profiler only when platform/build supports it and overhead is acceptable.

## Network / Request Delay

Inspect:

- `net`, `loading`, `blink.net`, `netlog` slices
- URLRequest/DNS/TLS/HTTP related event names
- Page load correlation if this is navigation-facing
- Request args, redacted unless explicitly allowed

Evidence to report:

- Request phase or network stack operation with high duration
- Whether delay is DNS, connect/TLS, proxy, cache, request scheduling, response wait, or renderer-side loading
- Privacy redaction applied

Re-capture if missing:

- Add `netlog`; add `disabled-by-default-netlog.sensitive` only with explicit approval because it may include URLs, headers, hostnames, and other sensitive values.

## Print / PDF / Print Preview

Inspect:

- `print`, `print_preview`, `pdf_plugin` categories and names
- Browser/UI/View events around opening preview or printing
- Renderer/PDF plugin events for PDF render/print path
- IPC/mojo slices around print dialog, preview generation, or error display

Evidence to report:

- First print/PDF event, failing/long print step, dialog or preview timeline
- Whether delay/error occurs in UI, renderer, PDF plugin, printer integration, or IPC boundary
- Printer/document names redacted unless allowed

Re-capture if missing:

- Add `print`, `print_preview`, `pdf_plugin`, `browser`, `renderer`, `ui`, `views`, `ipc`, `mojom`, plus CPU profiler only for hangs.

## WebRTC / Camera / Audio

Inspect:

- `webrtc`, `webrtc_stats`, `mediastream`, `audio`, `camera`, `media` slices
- Audio latency counters/events if captured
- GPU/media process activity for capture/render issues

Evidence to report:

- Capture start/stop, device pipeline delays, audio/video processing spans
- Whether problem is device/capture, WebRTC stack, renderer, GPU/media, or network-related

Re-capture if missing:

- Add `webrtc`, `webrtc_stats`, `mediastream`, `audio`, `media`, `camera`, `disabled-by-default-webrtc`, and `disabled-by-default-audio.latency` only when needed.

## Edge Feature / WebUI / Settings / Security

Inspect:

- Edge-specific category/name search: `edge_*`, `SmartScreen`, `wdag`, `pdf_plugin`, `print_preview`
- Browser/UI/View/WebUI thread slices around user action
- Mojo/IPCs if feature crosses browser/renderer/service boundary
- Network or file events if feature fetches policy/content/configuration

Evidence to report:

- Feature category present or absent
- Operation start/end event names and the slow/failing boundary
- Whether generic browser/UI/renderer data is enough or a re-capture is needed

Re-capture if missing:

- Add the matching Edge feature category and supporting `browser`, `content`, `ui`, `views`, `ipc`, `mojom`; add network/memory/profiler categories only for the observed failure mode.

## Extension / Content Verification

Inspect:

- `extensions`, `extensions.content_verifier.debug`
- Browser/renderer IPC around extension load or API call
- File/network events for extension resource loading or update

Evidence to report:

- Extension operation timeline and process/thread context
- Verification, file read, IPC, or renderer-side blockage
- Extension IDs redacted if they reveal internal deployment

Re-capture if missing:

- Add `extensions`, `extensions.content_verifier.debug`, `browser`, `renderer`, `ipc`, `mojom`, and `disabled-by-default-file` for file I/O detail.
