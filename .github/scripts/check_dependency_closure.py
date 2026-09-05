#!/usr/bin/env python3
"""製品成果物の依存閉包を検査する。

docs/testing.md T-3 に対応する。ネットワーク クライアントとモデル ランタイムが
製品バイナリの依存閉包に含まれないことを、許可リスト方式で機械的に確認する。
新しい依存を意図して追加した場合は ALLOWED を更新すること。

唯一のネイティブ依存である tree-sitter は managed の依存閉包には現れない。
その供給網上の統制は native/sources.json での版とチェックサムの固定が担う
（docs/decisions.md ADR-3、docs/security.md 3.1）。
"""

import json
import sys

# 製品成果物に現れてよいアセンブリ。FSharp.Core は言語ランタイムであり、
# それ以外はすべて本リポジトリのプロジェクトである。
ALLOWED = {
    "srcnet",
    "Srcnet.Core",
    "Srcnet.Text",
    "Srcnet.Discovery",
    "Srcnet.Extraction",
    "Srcnet.Storage",
    "FSharp.Core",
}


def main(path: str) -> int:
    with open(path, encoding="utf-8") as handle:
        deps = json.load(handle)

    found = set()
    for target in deps.get("targets", {}).values():
        for name in target:
            found.add(name.split("/")[0])

    unexpected = sorted(found - ALLOWED)
    if unexpected:
        print("製品成果物に想定外の依存が含まれています:", unexpected, file=sys.stderr)
        return 1

    print("依存閉包:", sorted(found))
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("使い方: check_dependency_closure.py <deps.json>", file=sys.stderr)
        raise SystemExit(2)
    raise SystemExit(main(sys.argv[1]))
