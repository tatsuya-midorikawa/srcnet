#!/usr/bin/env python3
"""
net-export ログから URL リクエストを分析するスクリプト。

Usage:
    python3 url_requests.py <net-export-log.json> [--filter <keyword>] [--errors-only] [--top <N>]
"""

import json
import sys
import argparse
from datetime import datetime, timezone
from collections import defaultdict


def ticks_to_utc(ticks, offset):
    """TimeTicks を UTC datetime に変換"""
    utc_ms = offset + int(ticks)
    return datetime.fromtimestamp(utc_ms / 1000, tz=timezone.utc)


def main():
    parser = argparse.ArgumentParser(description="net-export URL リクエスト分析")
    parser.add_argument("logfile", help="net-export ログ JSON ファイル")
    parser.add_argument("--filter", "-f", help="URL に含むキーワードでフィルタ")
    parser.add_argument("--errors-only", "-e", action="store_true", help="エラーリクエストのみ表示")
    parser.add_argument("--top", "-t", type=int, default=50, help="表示件数 (デフォルト: 50)")
    parser.add_argument("--slow", "-s", type=int, help="指定ミリ秒以上かかったリクエストのみ表示")
    args = parser.parse_args()

    with open(args.logfile, "r", encoding="utf-8") as f:
        data = json.load(f)

    constants = data["constants"]
    events = data["events"]
    offset = constants.get("timeTickOffset", 0)

    # 逆引きマップ
    src_type_names = {v: k for k, v in constants["logSourceType"].items()}
    evt_type_names = {v: k for k, v in constants["logEventTypes"].items()}
    net_errors = {v: k for k, v in constants["netError"].items()}

    url_request_type = constants["logSourceType"].get("URL_REQUEST")
    if url_request_type is None:
        print("URL_REQUEST ソース型が見つかりません", file=sys.stderr)
        sys.exit(1)

    # URL_REQUEST イベントをソースIDでグループ化
    sources = defaultdict(list)
    for e in events:
        if e["source"]["type"] == url_request_type:
            sources[e["source"]["id"]].append(e)

    # 各リクエストの情報を抽出
    requests = []
    for src_id, src_events in sources.items():
        url = None
        method = None
        net_error = None
        status_code = None
        start_time = None
        end_time = None

        for e in src_events:
            evt_name = evt_type_names.get(e["type"], "")
            params = e.get("params", {})

            # URL は複数のイベント型に含まれうる (URL_REQUEST_START_JOB, REQUEST_ALIVE, CORS_REQUEST)
            if url is None and "url" in params:
                url = params["url"]
            if method is None and "method" in params:
                method = params["method"]

            if "net_error" in params and params["net_error"] != 0:
                net_error = params["net_error"]

            if evt_name == "NETWORK_DELEGATE_HEADERS_RECEIVED":
                status_code = params.get("response_code", status_code)

            if start_time is None:
                start_time = e["time"]
            end_time = e["time"]

        if url is None:
            continue

        duration = int(end_time) - int(start_time) if start_time and end_time else None

        requests.append({
            "source_id": src_id,
            "url": url,
            "method": method or "GET",
            "status_code": status_code,
            "net_error": net_error,
            "error_name": net_errors.get(net_error, "") if net_error else None,
            "start_time": ticks_to_utc(start_time, offset) if start_time else None,
            "duration_ms": duration,
        })

    # フィルタリング
    if args.filter:
        keyword = args.filter.lower()
        requests = [r for r in requests if keyword in (r["url"] or "").lower()]

    if args.errors_only:
        requests = [r for r in requests if r["net_error"] is not None]

    if args.slow:
        requests = [r for r in requests if r["duration_ms"] is not None and r["duration_ms"] >= args.slow]

    # ソート (開始時刻順)
    requests.sort(key=lambda r: r["start_time"] or datetime.min.replace(tzinfo=timezone.utc))

    # 表示
    total = len(requests)
    requests = requests[:args.top]

    print(f"URL リクエスト: {total} 件 (表示: {len(requests)} 件)")
    print()

    for r in requests:
        error_str = f" ERROR: {r['error_name']} ({r['net_error']})" if r["net_error"] else ""
        status_str = f" [{r['status_code']}]" if r["status_code"] else ""
        duration_str = f" {r['duration_ms']}ms" if r["duration_ms"] is not None else ""
        time_str = r["start_time"].strftime("%H:%M:%S.%f")[:-3] if r["start_time"] else "??:??:??"

        print(f"[{time_str}] {r['method']}{status_str}{duration_str}{error_str}")
        print(f"  {r['url']}")
        print()


if __name__ == "__main__":
    main()
