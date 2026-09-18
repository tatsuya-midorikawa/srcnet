#!/usr/bin/env python3
"""
net-export ログから DNS 情報を分析するスクリプト。

Usage:
    python3 dns_analysis.py <net-export-log.json> [--filter <hostname>]
"""

import json
import sys
import argparse
from datetime import datetime, timezone
from collections import defaultdict, Counter


def ticks_to_utc(ticks, offset):
    """TimeTicks を UTC datetime に変換"""
    utc_ms = offset + int(ticks)
    return datetime.fromtimestamp(utc_ms / 1000, tz=timezone.utc)


def main():
    parser = argparse.ArgumentParser(description="net-export DNS 分析")
    parser.add_argument("logfile", help="net-export ログ JSON ファイル")
    parser.add_argument("--filter", "-f", help="ホスト名でフィルタ")
    args = parser.parse_args()

    with open(args.logfile, "r", encoding="utf-8") as f:
        data = json.load(f)

    constants = data["constants"]
    events = data["events"]
    polled = data.get("polledData", {})
    offset = constants.get("timeTickOffset", 0)

    evt_type_names = {v: k for k, v in constants["logEventTypes"].items()}
    src_type_names = {v: k for k, v in constants["logSourceType"].items()}
    net_errors = {v: k for k, v in constants["netError"].items()}

    # === DNS キャッシュ ===
    resolver_info = polled.get("hostResolverInfo", {})
    cache = resolver_info.get("cache", {})
    dns_config = resolver_info.get("dns_config", {})

    print("=" * 60)
    print("DNS 分析")
    print("=" * 60)

    if dns_config:
        print("\n--- DNS 設定 ---")
        nameservers = dns_config.get("nameservers", [])
        print(f"ネームサーバー: {json.dumps(nameservers, indent=2)}")
        print(f"安全な DNS (DoH): {dns_config.get('can_use_secure_dns_transactions', 'N/A')}")
        print(f"通常の DNS: {dns_config.get('can_use_insecure_dns_transactions', 'N/A')}")
        secure_mode = dns_config.get("secure_dns_mode")
        if secure_mode is not None:
            print(f"Secure DNS モード: {secure_mode}")

    if cache:
        entries = cache.get("entries", [])
        print(f"\n--- DNS キャッシュ ---")
        print(f"容量: {cache.get('capacity', 'N/A')}")
        print(f"エントリ数: {len(entries)}")
        print(f"ネットワーク変更回数: {cache.get('network_changes', 'N/A')}")

        if entries:
            filter_keyword = args.filter.lower() if args.filter else None
            print(f"\n{'ホスト名':<50} {'アドレス':<40} {'TTL':<8} {'有効期限'}")
            print("-" * 130)
            for e in entries:
                hostname = e.get("hostname", "N/A")
                if filter_keyword and filter_keyword not in hostname.lower():
                    continue

                addresses = []
                if "addresses" in e:
                    addresses.extend(e["addresses"])
                if "ip_endpoints" in e:
                    addresses.extend([json.dumps(ep) for ep in e["ip_endpoints"]])

                error = e.get("error")
                if error is not None:
                    addr_str = f"ERROR: {net_errors.get(error, error)}"
                else:
                    addr_str = ", ".join(addresses[:3])
                    if len(addresses) > 3:
                        addr_str += f" (+{len(addresses)-3})"

                ttl = e.get("ttl", "N/A")
                expiration = e.get("expiration")
                exp_str = ""
                if expiration:
                    try:
                        exp_str = ticks_to_utc(expiration, offset).strftime("%H:%M:%S")
                    except (ValueError, OSError):
                        exp_str = str(expiration)

                print(f"{hostname:<50} {addr_str:<40} {str(ttl):<8} {exp_str}")

    # === DNS 解決イベント ===
    host_resolver_type = constants["logSourceType"].get("HOST_RESOLVER_IMPL_JOB")
    dns_tx_type = constants["logSourceType"].get("DNS_TRANSACTION")
    doh_type = constants["logSourceType"].get("DNS_OVER_HTTPS")

    # HOST_RESOLVER ジョブをグループ化
    resolver_jobs = defaultdict(list)
    for e in events:
        src_type = e["source"]["type"]
        if src_type in (host_resolver_type, dns_tx_type, doh_type):
            resolver_jobs[e["source"]["id"]].append(e)

    print(f"\n--- DNS 解決イベント ---")
    print(f"HOST_RESOLVER ジョブ数: {len([sid for sid, evts in resolver_jobs.items() if evts[0]['source']['type'] == host_resolver_type])}")

    # DNS キャッシュヒット統計
    cache_hit_type = constants["logEventTypes"].get("HOST_RESOLVER_MANAGER_CACHE_HIT")
    cache_hits = sum(1 for e in events if e["type"] == cache_hit_type) if cache_hit_type else 0
    host_req_type = constants["logEventTypes"].get("HOST_RESOLVER_MANAGER_REQUEST")
    host_requests = sum(1 for e in events if e["type"] == host_req_type) if host_req_type else 0
    print(f"DNS リクエスト数: {host_requests:,}")
    print(f"DNS キャッシュヒット数: {cache_hits:,}")
    if host_requests > 0:
        print(f"キャッシュヒット率: {cache_hits/host_requests*100:.1f}%")

    # DNS エラー集計
    dns_errors = []
    for sid, evts in resolver_jobs.items():
        for e in evts:
            net_error = e.get("params", {}).get("net_error", 0)
            if net_error != 0:
                host = None
                for ev in evts:
                    host = ev.get("params", {}).get("host") or ev.get("params", {}).get("hostname")
                    if host:
                        break
                dns_errors.append({
                    "host": host or "N/A",
                    "error": net_errors.get(net_error, f"UNKNOWN({net_error})"),
                    "code": net_error,
                })
                break

    if dns_errors:
        print(f"\n--- DNS エラー ({len(dns_errors)} 件) ---")
        error_counter = Counter((e["host"], e["error"]) for e in dns_errors)
        for (host, error), cnt in error_counter.most_common(20):
            print(f"  {host}: {error} x{cnt}")

    # === DoH 情報 ===
    doh_disabled = polled.get("dohProvidersDisabledDueToFeature", [])
    if doh_disabled:
        print(f"\n--- 無効化された DoH プロバイダー ---")
        for provider in doh_disabled:
            print(f"  {provider}")


if __name__ == "__main__":
    main()
