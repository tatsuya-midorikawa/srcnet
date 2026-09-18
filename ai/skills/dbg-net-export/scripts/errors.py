#!/usr/bin/env python3
"""
net-export ログからエラーイベントを抽出・分析するスクリプト。

Usage:
    python3 errors.py <net-export-log.json> [--source-type <type>] [--error-code <code>]
"""

import json
import sys
import argparse
from datetime import datetime, timezone
from collections import Counter, defaultdict


def ticks_to_utc(ticks, offset):
    """TimeTicks を UTC datetime に変換"""
    utc_ms = offset + int(ticks)
    return datetime.fromtimestamp(utc_ms / 1000, tz=timezone.utc)


def main():
    parser = argparse.ArgumentParser(description="net-export エラー分析")
    parser.add_argument("logfile", help="net-export ログ JSON ファイル")
    parser.add_argument("--source-type", "-s", help="ソース型名でフィルタ (例: URL_REQUEST)")
    parser.add_argument("--error-code", "-c", help="エラーコード名でフィルタ (例: ERR_CONNECTION_REFUSED)")
    parser.add_argument("--top", "-t", type=int, default=30, help="表示件数")
    args = parser.parse_args()

    with open(args.logfile, "r", encoding="utf-8") as f:
        data = json.load(f)

    constants = data["constants"]
    events = data["events"]
    offset = constants.get("timeTickOffset", 0)

    src_type_names = {v: k for k, v in constants["logSourceType"].items()}
    evt_type_names = {v: k for k, v in constants["logEventTypes"].items()}
    net_errors = {v: k for k, v in constants["netError"].items()}
    quic_errors = {v: k for k, v in constants.get("quicError", {}).items()}
    quic_rst_errors = {v: k for k, v in constants.get("quicRstStreamError", {}).items()}

    # エラーイベントの抽出
    error_events = []
    for e in events:
        params = e.get("params", {})
        net_error = params.get("net_error", 0)
        quic_error = params.get("quic_error")
        quic_rst_error = params.get("quic_rst_stream_error")

        if net_error != 0 or quic_error is not None or quic_rst_error is not None:
            src_type_name = src_type_names.get(e["source"]["type"], f"UNKNOWN({e['source']['type']})")
            evt_type_name = evt_type_names.get(e["type"], f"UNKNOWN({e['type']})")

            error_info = {
                "time": e["time"],
                "source_id": e["source"]["id"],
                "source_type": src_type_name,
                "event_type": evt_type_name,
                "params": params,
            }

            if net_error != 0:
                error_info["error_type"] = "net_error"
                error_info["error_code"] = net_error
                error_info["error_name"] = net_errors.get(net_error, f"UNKNOWN({net_error})")
            elif quic_error is not None:
                error_info["error_type"] = "quic_error"
                error_info["error_code"] = quic_error
                error_info["error_name"] = quic_errors.get(quic_error, f"UNKNOWN({quic_error})")
            elif quic_rst_error is not None:
                error_info["error_type"] = "quic_rst_error"
                error_info["error_code"] = quic_rst_error
                error_info["error_name"] = quic_rst_errors.get(quic_rst_error, f"UNKNOWN({quic_rst_error})")

            error_events.append(error_info)

    # フィルタリング
    if args.source_type:
        error_events = [e for e in error_events if args.source_type.upper() in e["source_type"].upper()]
    if args.error_code:
        error_events = [e for e in error_events if args.error_code.upper() in e["error_name"].upper()]

    print("=" * 60)
    print("エラー分析")
    print("=" * 60)
    print(f"エラーイベント総数: {len(error_events):,}")
    print()

    # エラーコード別集計
    error_counter = Counter(e["error_name"] for e in error_events)
    print("--- エラーコード別集計 ---")
    for name, cnt in error_counter.most_common(args.top):
        print(f"  {name}: {cnt:,}")
    print()

    # ソース型別エラー集計
    src_error_counter = Counter(e["source_type"] for e in error_events)
    print("--- ソース型別エラー数 ---")
    for name, cnt in src_error_counter.most_common(20):
        print(f"  {name}: {cnt:,}")
    print()

    # エラーイベント詳細 (上位N件)
    print(f"--- エラーイベント詳細 (最新 {min(args.top, len(error_events))} 件) ---")
    for e in error_events[-args.top:]:
        try:
            time_str = ticks_to_utc(e["time"], offset).strftime("%H:%M:%S.%f")[:-3]
        except (ValueError, OSError):
            time_str = str(e["time"])

        print(f"[{time_str}] [{e['source_type']}:{e['source_id']}] {e['event_type']}")
        print(f"  {e['error_type']}: {e['error_name']} ({e['error_code']})")

        # 関連パラメータを表示
        relevant_keys = {"url", "host", "hostname", "address", "group_id", "description"}
        for k in relevant_keys:
            if k in e["params"]:
                val = e["params"][k]
                if isinstance(val, str) and len(val) > 120:
                    val = val[:120] + "..."
                print(f"  {k}: {val}")
        print()


if __name__ == "__main__":
    main()
