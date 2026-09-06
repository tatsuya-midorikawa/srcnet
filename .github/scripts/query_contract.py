"""Portable CLI/HTML acceptance artifacts; standard library only."""

import argparse
import json
import subprocess
from pathlib import Path


def estimate(text):
    ranges = (
        (0x2E80, 0x2EFF), (0x3040, 0x30FF), (0x3100, 0x312F),
        (0x3130, 0x318F), (0x3400, 0x4DBF), (0x4E00, 0x9FFF),
        (0xA960, 0xA97F), (0xAC00, 0xD7AF), (0xF900, 0xFAFF),
        (0x20000, 0x3FFFD),
    )
    ascii_count = cjk = other = 0
    for char in text:
        scalar = ord(char)
        if scalar < 128:
            ascii_count += 1
        elif any(low <= scalar <= high for low, high in ranges):
            cjk += 1
        else:
            other += 1
    return (ascii_count + 3) // 4 + cjk + (other + 1) // 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    cli = args.cli.resolve()
    root = args.out.resolve()
    fixture, artifact, portable = (root / name for name in ("fixture", "index", "portable"))
    portable.mkdir(parents=True, exist_ok=True)
    files = {
        "code/main.c": "int alpha(void) { return 1; }\nint beta(void) { return alpha(); }\n",
        "\u65e5\u672c\u8a9e/\u9762\u7a4d\U0001f600.c":
            "int \u9762\u7a4d(void) { return 2; }\n",
        "code/note.c": "// TODO: </script><script>globalThis.__srcnet_injected=true;</script>\n"
                       "int helper(void) { return 3; }\n",
    }
    for name, text in files.items():
        file = fixture / name
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_bytes(text.encode("utf-8"))

    def run(name, command, expected=0, budget=512, limit=10, save=True):
        result = subprocess.run(
            [str(cli), *command, "--json"], capture_output=True, timeout=30, check=False
        )
        if result.returncode != expected:
            raise AssertionError(f"{name}: exit {result.returncode}; "
                                 f"stdout={result.stdout!r}; stderr={result.stderr!r}")
        data = json.loads(result.stdout.decode("utf-8"))
        if "tokenEstimate" in data:
            assert result.stderr == b"", (name, result.stderr)
            assert data["tokenEstimate"] == estimate(result.stdout.decode("utf-8")), name
            assert data["tokenEstimate"] <= budget, name
            assert len(data["nodes"]) <= limit, name
            ids = {node["id"] for node in data["nodes"]}
            assert all(edge["from"] in ids and edge["to"] in ids for edge in data["edges"]), name
        if save:
            (portable / (name + ".json")).write_bytes(result.stdout)
        return data

    run("index", ["index", str(fixture), "--out", str(artifact),
                  "--repo", "query-contract", "--tier", "1", "--jobs", "1"], save=False)
    run("verify", ["verify", "--out", str(artifact)])
    common = ["--out", str(artifact), "--budget", "512", "--limit", "10"]
    found = run("search", ["search", "code/main.c", *common])
    file_id = next(node["id"] for node in found["nodes"] if node["kind"] == "File")
    shown = run("show", ["show", file_id, *common])
    parent_id = next(edge["from"] for edge in shown["edges"] if edge["to"] == file_id)
    incoming = run("neighbors", ["neighbors", file_id, "--edge", "CONTAINS",
                                 "--direction", "in", "--depth", "1", *common])
    assert any(edge["from"] == parent_id and edge["to"] == file_id
               for edge in incoming["edges"])
    run("path", ["path", parent_id, file_id, "--edge", "CONTAINS",
                 "--direction", "out", "--depth", "1", *common])
    run("context", ["context", "main.c", "\u9762\u7a4d", "--depth", "2", *common])
    run("cjk", ["search", "\u9762\u7a4d", *common])
    run("unknown-node", ["show", "f" * 32, *common], expected=1)
    run("invalid-depth", ["neighbors", file_id, "--depth", "17", *common], expected=2)
    run("budget", ["neighbors", parent_id, "--depth", "2", "--out", str(artifact),
                    "--budget", "256", "--limit", "1"], budget=256, limit=1)
    for name, extra in (("overview", []), ("graph", ["--node", parent_id, "--depth", "2", "--max-nodes", "4"])):
        run(name, ["export", "html", "--out", str(artifact), "--file",
                   str(portable / (name + ".html")), *extra], save=False)

    # Public browser behavior fixture uses the real emitted template. Native
    # escaping and data-selection behavior are covered by ExportTests separately.
    payload_name = "\u691c\u8a3c\u304c\U0001f600</script><script>globalThis.__srcnet_injected=true;</script>"
    nodes = []
    for index, (kind, name, qualified, location, distance) in enumerate([
        ("Repository", "fixture", "fixture", "", 1),
        ("Directory", "code", "code", "code", 1),
        ("Function", "\u691c\u8a3c\u304c\U0001f600", payload_name, "\u65e5\u672c\u8a9e/\u9762\u7a4d\U0001f600.c", 0),
        ("File", "helper.c", "code/helper.c", "code/helper.c", 1),
    ]):
        nodes.append(dict(id=f"{index + 1:032x}", kind=kind, name=name, qualifiedName=qualified,
                          path=location, lines=[1, 2], language="c", flags=[], distance=distance, weight=1))
    data = dict(schemaVersion=1, repository="browser-contract", toolVersion="0.1.0",
                query=dict(kind="node", value=nodes[2]["id"], depth=2),
                totalNodes=101, candidateNodes=10, groupedNodeCount=0, aggregated=False,
                truncated=True, omittedCount=6, omittedCountIsLowerBound=False,
                omittedEdgeCount=0, traversalTruncated=False, searchTruncated=False,
                seedIndex=2, nodes=nodes,
                edges=[dict(**{"from": 0, "to": 1}, kind="CONTAINS"),
                       dict(**{"from": 1, "to": 2}, kind="DEFINES"),
                       dict(**{"from": 2, "to": 3}, kind="CALLS")],
                diagnostics=["Bounded browser fixture: six nodes omitted."])
    escaped = json.dumps(data, ensure_ascii=True, separators=(",", ":")).replace("<", "\\u003c")
    html = (portable / "graph.html").read_text(encoding="utf-8")
    marker = '<script id="srcnet-data" type="application/json">'
    start = html.index(marker) + len(marker)
    end = html.index("</script>", start)
    (portable / "browser.html").write_bytes((html[:start] + escaped + html[end:]).encode("utf-8"))
    print(json.dumps({"portable": str(portable), "files": len(list(portable.iterdir()))}))


if __name__ == "__main__":
    main()
