"""Pack, install from a local-only feed, and exercise the installed .NET tool."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[2]


def run(arguments, environment=None, expected=0):
    result = subprocess.run(
        [str(argument) for argument in arguments], capture_output=True,
        text=True, encoding="utf-8", timeout=180, env=environment, check=False,
    )
    if result.returncode != expected:
        raise AssertionError(
            f"{arguments}: exit {result.returncode}\n{result.stdout}\n{result.stderr}"
        )
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--require-parser", action="store_true")
    mode.add_argument("--without-parser", action="store_true")
    mode.add_argument("--partial-parser", action="store_true")
    arguments = parser.parse_args()
    output = arguments.out.resolve()
    output.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="tool-contract-", dir=output) as temporary:
        work = Path(temporary)
        packages = work / "packages"
        settings = ["-p:IncludeNativeParser=false"] if arguments.without_parser else []
        run(["dotnet", "pack", ROOT / "src/Srcnet.Cli/Srcnet.Cli.fsproj",
             "-c", "Release", "--nologo", "-o", packages, *settings])
        package_files = list(packages.glob("*.nupkg"))
        assert len(package_files) == 1, package_files
        with zipfile.ZipFile(package_files[0]) as package:
            names = package.namelist()
            specification = ET.fromstring(package.read(next(name for name in names if name.endswith(".nuspec"))))
            package_id = specification.findtext(".//{*}id")
            version = specification.findtext(".//{*}version")
            native = [name for name in names if name.endswith(
                ("srcnet_treesitter.dylib", "srcnet_treesitter.dll", "srcnet_treesitter.so"))]
            assert not native or any(name.endswith("srcnet_treesitter.NOTICES.txt") for name in names)
            assert not arguments.without_parser or not native
            assert "README.md" in names and "LICENSE" in names
            assert not any("BenchmarkDotNet" in name or "xunit" in name.lower() for name in names)

        environment = {
            **os.environ, "NUGET_PACKAGES": str(work / "nuget"),
            "DOTNET_CLI_HOME": str(work / "dotnet-home"),
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1",
        }
        tools = work / "tools"
        run(["dotnet", "tool", "install", package_id, "--version", version,
             "--source", packages, "--tool-path", tools], environment)
        cli = tools / ("srcnet.exe" if os.name == "nt" else "srcnet")
        assert "--budget" in run([cli, "--help"], environment)
        assert "--max-file-size" in run([cli, "index", "--help"], environment)
        version_text = run([cli, "--version"], environment)
        source = work / "source"
        (source / "\u65e5\u672c\u8a9e").mkdir(parents=True)
        (source / "\u65e5\u672c\u8a9e/main.c").write_bytes(b"int answer(void) { return 42; }\n")
        (source / "main.cpp").write_bytes(b"int other() { return 1; }\n")
        index = work / "index"
        tier = "2" if arguments.require_parser or arguments.without_parser or arguments.partial_parser else "1"
        indexed = json.loads(run(
            [cli, "index", source, "--out", index, "--tier", tier, "--json"],
            environment, expected=4 if arguments.without_parser or arguments.partial_parser else 0))
        assert indexed["complete"] and indexed["files"] == 2
        if arguments.require_parser:
            assert native and indexed["parserAvailable"] and indexed["extractedFiles"]["tier2"] == 2
        if arguments.without_parser:
            assert not indexed["parserAvailable"] and indexed["tier"] == 1 and indexed["requestedTier"] == 2
        if arguments.partial_parser:
            assert indexed["parserAvailable"] and indexed["extractedFiles"]["tier2"] == 1
            assert indexed["extractedFiles"]["tier1"] == 1 and indexed["diagnostics"] > 0
        verified = json.loads(run([cli, "verify", source, "--out", index, "--deterministic", "--json"], environment))
        assert verified["valid"]
        found = json.loads(run([cli, "search", "main.c", "--out", index, "--json", "--budget", "512"], environment))
        assert any(node["path"] == "\u65e5\u672c\u8a9e/main.c" for node in found["nodes"])
        report = dict(package=package_id, version=version, native=bool(native),
                      parserRequired=arguments.require_parser, withoutParser=arguments.without_parser,
                      partialParser=arguments.partial_parser,
                      versionOutput=version_text,
                      files=indexed["files"], valid=verified["valid"])

    (output / "tool-contract.json").write_text(json.dumps(report, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True))


if __name__ == "__main__":
    main()
