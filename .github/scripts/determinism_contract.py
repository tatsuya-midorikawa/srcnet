"""Compare complete index artifacts across worker counts, on either supported OS."""

import argparse
import filecmp
import json
from pathlib import Path
import subprocess


def run(cli, command, allowed):
    result = subprocess.run(
        [str(cli), *command, "--json"], capture_output=True, timeout=300, check=False
    )
    if result.returncode not in allowed:
        raise AssertionError(
            f"{command[0]} exited {result.returncode}: "
            f"stdout={result.stdout!r}; stderr={result.stderr!r}"
        )
    return json.loads(result.stdout.decode("utf-8"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    cli, root, output = args.cli.resolve(), args.root.resolve(), args.out.resolve()
    first, second = output / "index-a", output / "index-b"

    for directory, jobs in ((first, 1), (second, 8)):
        result = run(
            cli, ["index", str(root), "--out", str(directory), "--jobs", str(jobs)], {0, 4}
        )
        if result["complete"] is not True:
            raise AssertionError("A partial scan cannot establish determinism")

    left = {path.relative_to(first) for path in first.rglob("*") if path.is_file()}
    right = {path.relative_to(second) for path in second.rglob("*") if path.is_file()}
    if left != right:
        raise AssertionError(f"Artifact paths differ: {left ^ right}")
    for name in sorted(left):
        if not filecmp.cmp(first / name, second / name, shallow=False):
            raise AssertionError(f"Artifact bytes differ: {name}")

    verified = run(cli, ["verify", str(root), "--out", str(first), "--deterministic"], {0})
    if verified["valid"] is not True:
        raise AssertionError(verified)
    print(f"Identical artifacts: {len(left)} files; jobs=1 and jobs=8")


if __name__ == "__main__":
    main()
