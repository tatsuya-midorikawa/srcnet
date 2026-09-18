#!/usr/bin/env python3
"""
net-export ログの基本情報を表示するスクリプト。

Usage:
    python3 summary.py <net-export-log.json>
"""

import json
import sys
from datetime import datetime, timezone
from collections import Counter


def ticks_to_utc(ticks, offset):
    """TimeTicks を UTC datetime に変換"""
    utc_ms = offset + int(ticks)
    return datetime.fromtimestamp(utc_ms / 1000, tz=timezone.utc)


def main():
    if len(sys.argv) < 2:
        print("Usage: python3 summary.py <net-export-log.json>", file=sys.stderr)
        sys.exit(1)

    filepath = sys.argv[1]
    with open(filepath, "r", encoding="utf-8") as f:
        data = json.load(f)

    constants = data.get("constants", {})
    events = data.get("events", [])
    polled = data.get("polledData", {})

    # --- ブラウザ情報 ---
    client = constants.get("clientInfo", {})
    print("=" * 60)
    print("Net-Export ログサマリー")
    print("=" * 60)
    print(f"ブラウザ: {client.get('name', 'N/A')} {client.get('version', 'N/A')} ({client.get('version_mod', 'N/A')})")
    print(f"OS: {client.get('os_type', 'N/A')}")
    print(f"コマンドライン: {client.get('command_line', 'N/A')[:120]}...")
    print(f"キャプチャモード: {constants.get('logCaptureMode', 'N/A')}")
    print()

    # --- ログ統計 ---
    offset = constants.get("timeTickOffset", 0)
    print(f"イベント総数: {len(events):,}")
    print(f"イベント型数: {len(constants.get('logEventTypes', {}))}")
    print(f"ソース型数: {len(constants.get('logSourceType', {}))}")

    if events:
        first_time = ticks_to_utc(events[0]["time"], offset)
        last_time = ticks_to_utc(events[-1]["time"], offset)
        duration = last_time - first_time
        print(f"時間範囲: {first_time.isoformat()} 〜 {last_time.isoformat()}")
        print(f"キャプチャ期間: {duration}")
    print()

    # --- ソース型分布 ---
    src_type_names = {v: k for k, v in constants.get("logSourceType", {}).items()}
    src_counter = Counter()
    for e in events:
        src_counter[e["source"]["type"]] += 1

    print("--- ソース型別イベント数 (上位 20) ---")
    for st, cnt in src_counter.most_common(20):
        name = src_type_names.get(st, f"UNKNOWN({st})")
        print(f"  {name}: {cnt:,}")
    print()

    # --- イベント型分布 ---
    evt_type_names = {v: k for k, v in constants.get("logEventTypes", {}).items()}
    evt_counter = Counter()
    for e in events:
        evt_counter[e["type"]] += 1

    print("--- イベント型別 (上位 30) ---")
    for et, cnt in evt_counter.most_common(30):
        name = evt_type_names.get(et, f"UNKNOWN({et})")
        print(f"  {name}: {cnt:,}")
    print()

    # --- エラーイベント ---
    net_errors = {v: k for k, v in constants.get("netError", {}).items()}
    error_events = []
    for e in events:
        if e.get("params", {}).get("net_error", 0) != 0:
            error_events.append(e)

    error_counter = Counter()
    for e in error_events:
        code = e["params"]["net_error"]
        error_counter[code] += 1

    print(f"--- エラーイベント数: {len(error_events):,} ---")
    for code, cnt in error_counter.most_common(20):
        name = net_errors.get(code, f"UNKNOWN({code})")
        print(f"  {name} ({code}): {cnt:,}")
    print()

    # --- polledData サマリー ---
    print("--- polledData セクション ---")
    for k, v in polled.items():
        if isinstance(v, dict):
            print(f"  {k}: dict ({len(v)} keys)")
        elif isinstance(v, list):
            print(f"  {k}: list ({len(v)} items)")
        else:
            print(f"  {k}: {v}")
    print()

    # --- URL リクエスト数 ---
    url_request_type = constants.get("logSourceType", {}).get("URL_REQUEST")
    if url_request_type is not None:
        url_sources = set()
        for e in events:
            if e["source"]["type"] == url_request_type:
                url_sources.add(e["source"]["id"])
        print(f"URL リクエスト数 (ユニークソース): {len(url_sources):,}")

    # --- DNS キャッシュ ---
    resolver_info = polled.get("hostResolverInfo", {})
    cache = resolver_info.get("cache", {})
    if cache:
        entries = cache.get("entries", [])
        print(f"DNS キャッシュエントリ数: {len(entries)}")
        print(f"DNS キャッシュ容量: {cache.get('capacity', 'N/A')}")

    # --- HTTP/2 セッション ---
    spdy_sessions = polled.get("spdySessionInfo", [])
    if spdy_sessions:
        print(f"HTTP/2 セッション数: {len(spdy_sessions)}")

    # --- 拡張機能 ---
    extensions = polled.get("extensionInfo", [])
    if extensions:
        enabled = sum(1 for ext in extensions if ext.get("enabled"))
        print(f"拡張機能: {len(extensions)} (有効: {enabled})")


if __name__ == "__main__":
    main()
