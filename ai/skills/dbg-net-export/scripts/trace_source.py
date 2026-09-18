#!/usr/bin/env python3
"""
net-export ログから特定ソースIDのイベントをトレースするスクリプト。
netlog_viewer の Events タブ相当の詳細表示を行う。

Usage:
    python3 trace_source.py <net-export-log.json> --id <source_id>
    python3 trace_source.py <net-export-log.json> --url <keyword>
    python3 trace_source.py <net-export-log.json> --host <hostname>
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


def format_params(params, indent=4):
    """パラメータを読みやすい形式で表示"""
    if not params:
        return ""
    lines = []
    prefix = " " * indent
    for k, v in params.items():
        if isinstance(v, (dict, list)):
            v_str = json.dumps(v, ensure_ascii=False)
            if len(v_str) > 100:
                v_str = json.dumps(v, indent=2, ensure_ascii=False)
                v_lines = v_str.split("\n")
                lines.append(f"{prefix}--> {k} = {v_lines[0]}")
                for vl in v_lines[1:]:
                    lines.append(f"{prefix}    {vl}")
                continue
        elif isinstance(v, str) and len(v) > 120:
            v = v[:120] + "..."
        lines.append(f"{prefix}--> {k} = {v}")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description="net-export ソーストレース")
    parser.add_argument("logfile", help="net-export ログ JSON ファイル")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--id", "-i", type=int, help="ソース ID")
    group.add_argument("--url", "-u", help="URL キーワードで検索")
    group.add_argument("--host", help="ホスト名で検索")
    parser.add_argument("--follow", "-F", action="store_true", help="依存ソースも追跡")
    args = parser.parse_args()

    with open(args.logfile, "r", encoding="utf-8") as f:
        data = json.load(f)

    constants = data["constants"]
    events = data["events"]
    offset = constants.get("timeTickOffset", 0)

    src_type_names = {v: k for k, v in constants["logSourceType"].items()}
    evt_type_names = {v: k for k, v in constants["logEventTypes"].items()}
    phase_names = {v: k for k, v in constants["logEventPhase"].items()}
    net_errors = {v: k for k, v in constants["netError"].items()}

    # 全イベントをソースIDでグループ化
    all_sources = defaultdict(list)
    for e in events:
        all_sources[e["source"]["id"]].append(e)

    # ソースIDの特定
    target_ids = set()

    if args.id is not None:
        target_ids.add(args.id)
    elif args.url:
        keyword = args.url.lower()
        url_request_type = constants["logSourceType"].get("URL_REQUEST")
        for sid, evts in all_sources.items():
            if evts[0]["source"]["type"] != url_request_type:
                continue
            for e in evts:
                url = e.get("params", {}).get("url", "")
                if keyword in url.lower():
                    target_ids.add(sid)
                    break
    elif args.host:
        keyword = args.host.lower()
        for sid, evts in all_sources.items():
            for e in evts:
                params = e.get("params", {})
                host = params.get("host", "") or params.get("hostname", "")
                url = params.get("url", "")
                if keyword in host.lower() or keyword in url.lower():
                    target_ids.add(sid)
                    break

    if not target_ids:
        print("該当するソースが見つかりませんでした。", file=sys.stderr)
        sys.exit(1)

    print(f"対象ソース数: {len(target_ids)}")

    # 依存ソースの追跡
    if args.follow:
        additional_ids = set()
        for sid in target_ids:
            for e in all_sources.get(sid, []):
                dep = e.get("params", {}).get("source_dependency", {})
                if "id" in dep:
                    additional_ids.add(dep["id"])
        target_ids.update(additional_ids)
        if additional_ids:
            print(f"依存ソース追加: {len(additional_ids)} 件")

    print()

    # 各ソースのイベントを表示
    for sid in sorted(target_ids):
        src_events = all_sources.get(sid, [])
        if not src_events:
            continue

        src_type = src_type_names.get(src_events[0]["source"]["type"], "UNKNOWN")
        start_time = src_events[0]["source"].get("start_time")

        # Description の決定
        description = ""
        for e in src_events:
            params = e.get("params", {})
            if "url" in params:
                description = params["url"]
                break
            if "host" in params:
                description = params["host"]
                break
            if "hostname" in params:
                description = params["hostname"]
                break
            if "group_id" in params:
                description = params["group_id"]
                break
            if "key" in params:
                description = params["key"]
                break

        print("=" * 80)
        print(f"Source {sid}: {src_type}")
        if description:
            desc_display = description if len(description) <= 100 else description[:100] + "..."
            print(f"Description: {desc_display}")
        if start_time:
            try:
                print(f"Start: {ticks_to_utc(start_time, offset).isoformat()}")
            except (ValueError, OSError):
                pass
        print("-" * 80)

        # イベント表示
        base_time = int(src_events[0]["time"]) if src_events else 0
        depth = 0

        for e in src_events:
            evt_name = evt_type_names.get(e["type"], f"UNKNOWN({e['type']})")
            phase = phase_names.get(e["phase"], "?")
            rel_time = int(e["time"]) - base_time

            # インデント調整
            if phase == "PHASE_END":
                depth = max(0, depth - 1)

            indent = "  " * depth
            phase_marker = ""
            if phase == "PHASE_BEGIN":
                phase_marker = " (BEGIN)"
            elif phase == "PHASE_END":
                phase_marker = " (END)"

            # エラー表示
            net_error = e.get("params", {}).get("net_error", 0)
            error_str = ""
            if net_error != 0:
                error_str = f" *** {net_errors.get(net_error, f'error={net_error}')} ***"

            print(f"t={rel_time:>8} {indent}{evt_name}{phase_marker}{error_str}")

            # パラメータ表示
            if e.get("params"):
                params_str = format_params(e["params"], indent=len(indent) + 12)
                if params_str:
                    print(params_str)

            if phase == "PHASE_BEGIN":
                depth += 1

        print()


if __name__ == "__main__":
    main()
