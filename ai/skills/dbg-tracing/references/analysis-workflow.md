# Trace Analysis Workflow Reference

Snapshot date: 2026-05-20

この reference は採取済み edge://tracing / chrome://tracing ログを解析するための実務手順です。通常の解析では source tree を開かず、この文書と trace file 自体を根拠にします。

## File Format Triage

| Clue | Likely format | How to analyze |
|---|---|---|
| Starts with `{` or `[` and contains `traceEvents` or Chrome trace event fields | Chrome JSON trace | Perfetto UI に直接 open、または Trace Processor に読み込む |
| Binary/protobuf-looking file, often `.perfetto-trace`, `.pftrace`, `.proto` | Perfetto protobuf trace | Perfetto UI または Trace Processor |
| `.pftrace.gz` | gzip-compressed Perfetto protobuf trace | 元ファイルを残して `.pftrace` へ展開し、展開後に format 判定 |
| `.gz`, `.zip`, `.json.gz` | Compressed trace | 展開してから format 判定 |
| Base64-looking large text from tracing endpoint | Encoded trace payload | base64 decode してから format 判定 |
| HTML or error page content | Wrong saved artifact | trace file ではない。再エクスポートが必要 |

Practical checks:

- `file <trace>` で compressed/binary/text を確認する。
- JSON は先頭数 KB だけ見て、`traceEvents`, `metadata`, `cat`, `name`, `ph`, `pid`, `tid`, `ts`, `args` の有無を確認する。
- protobuf は raw text で読まない。Perfetto UI または Trace Processor に渡す。
- 巨大 JSON はエディタで開かず、Trace Processor へ直接渡す。

## Expanding `.pftrace.gz`

`.pftrace.gz` は通常、gzip 圧縮された Perfetto protobuf trace です。解析前に元ファイルを残したまま展開する。

Recommended commands on macOS/Linux:

```bash
gzip -t trace.pftrace.gz
gzip -dc trace.pftrace.gz > trace.pftrace
file trace.pftrace
```

For an arbitrary path:

```bash
trace="/path/to/log.pftrace.gz"
out="${trace%.gz}"
gzip -t "$trace"
gzip -dc "$trace" > "$out"
file "$out"
```

Recommended commands on Windows PowerShell (`pwsh`):

```powershell
$TracePath = "C:\path\to\log.pftrace.gz"
$OutputPath = $TracePath -replace '\.gz$', ''

$InputStream = [System.IO.File]::OpenRead($TracePath)
try {
	$GzipStream = [System.IO.Compression.GzipStream]::new(
			$InputStream,
			[System.IO.Compression.CompressionMode]::Decompress)
	try {
		$OutputStream = [System.IO.File]::Create($OutputPath)
		try {
			$GzipStream.CopyTo($OutputStream)
		} finally {
			$OutputStream.Dispose()
		}
	} finally {
		$GzipStream.Dispose()
	}
} finally {
	$InputStream.Dispose()
}

Get-Item $OutputPath
```

Notes:

- Prefer `gzip -dc` or `gunzip -c` because they write decompressed bytes to stdout and do not delete the original `.pftrace.gz`.
- On Windows PowerShell, prefer the .NET `System.IO.Compression.GzipStream` command above unless `gzip.exe` is known to be available and binary-safe in the current shell.
- `Expand-Archive` is for zip files, not plain gzip files. `tar -xzf` is for tar archives compressed with gzip and can fail for a single `.pftrace.gz` payload.
- Avoid plain `gunzip trace.pftrace.gz` unless deleting/replacing the compressed original is acceptable.
- After expansion, treat the `.pftrace` output as a Perfetto protobuf trace and open it in Perfetto UI or Trace Processor.
- If `gzip -t` fails, report the file as corrupt or not gzip-compressed before attempting deeper analysis.

## Tool Choice

| Tool | Best for | Notes |
|---|---|---|
| Perfetto UI | タイムライン探索、track/flow/args の視覚確認、Query tab | Chrome JSON と protobuf trace を開ける。UI 上で SQL も実行できる。 |
| `trace_processor` shell | 再現性ある SQL、上位 N 件集計、添付ログの機械確認 | `SELECT ts, dur, name FROM slice LIMIT 10;` で読み込み確認。 |
| Python Trace Processor API | 複数 trace の自動集計、CSV/HTML 生成 | 単発解析では shell/UI が速い。 |
| Raw JSON inspection | format sanity、metadata確認、小さい trace | 大きい trace や protobuf には向かない。 |

## Import Sanity Checks

Trace Processor で開けたら、最初に次を確認する。

```sql
SELECT start_ts, end_ts, dur FROM trace_bounds;
```

```sql
SELECT COUNT(*) AS slices FROM slice;
```

```sql
SELECT COUNT(*) AS counters FROM counter;
```

```sql
SELECT name, idx, severity, source, value
FROM stats
WHERE severity IS NOT NULL OR value != 0
ORDER BY severity DESC, name
LIMIT 50;
```

Look for:

- trace duration が再現時間を含むか
- `slice` が極端に少なくないか
- `stats` に packet loss / parse errors / unknown data がないか
- buffer full が疑われる欠損がないか

## Broad Inventory

1. Processes and threads: browser, renderer, GPU, utility, network service のどれが主役かを確認する。
2. Categories: `slice.category` の分布を見て、採取 category が症状に合っているか確認する。
3. Long slices: 上位 duration の `slice` を process/thread context 付きで見る。
4. Main thread pressure: Browser main thread、CrRendererMain、Compositor、VizCompositorThread、ThreadPool foreground/background を分ける。
5. Counters: memory、CPU、frame、network counters があるか確認する。

## Timeline Method

- まず症状 window を決める。ユーザーの操作時刻がない場合は、長い slice、input event、navigation/startup/page load event、print/PDF event などから候補を作る。
- Window は最低でも問題発生前 1-2 秒、発生後 1-2 秒を含める。
- 関連 process/thread を同じ timestamp range で横断する。
- Async/flow がある場合は始点と終点を両方見る。親 slice だけでなく descendant slice も見る。

## Evidence Strength

| Strength | Criteria |
|---|---|
| High | 症状 window 内で、該当 process/thread/event が明確に遅延または失敗し、args/category が対象機能と一致する |
| Medium | 長い slice や待ちが見えるが、機能固有 event が不足している |
| Low | category 不足、buffer 欠損、window 不明、または generic event しかない |

## Privacy Handling

Do not quote sensitive values unless the user explicitly allows it.

Potentially sensitive:

- URL、host、path、query string、headers、cookies、tokens
- account、email、tenant、profile path、file path
- printer name、document title、PDF/file name
- extension IDs if they reveal internal deployment
- NetLog sensitive fields and request/response details

When needed, redact values while preserving structure, for example `<host>`, `<path>`, `<printer>`, `<account>`.

## If the Trace Is Insufficient

Report what is observable and what is missing. Then propose a focused re-capture:

- Missing category: suggest additional category names and why.
- Missing stack: add `disabled-by-default-cpu_profiler` only if CPU/hang stack attribution matters.
- Missing memory detail: add `disabled-by-default-memory-infra` and memory dump config.
- Missing network detail: add `netlog`; add `disabled-by-default-netlog.sensitive` only with explicit privacy approval.
- Buffer loss: use shorter capture, larger buffer, or ring buffer mode depending on reproduction.

Use the `dbg-tracing-event-selection` skill for category selection details when available.
