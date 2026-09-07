#!/usr/bin/env python3
"""tree-sitter のランタイムと文法を取得・検証・構築する。

srcnet は構文解析に tree-sitter を再利用する（docs/decisions.md ADR-3）。
生成済みの文法 `parser.c` は 1 言語で数 MB から 30 MB あり、対応言語を増やすほど
リポジトリへ載せられない大きさになるため、`native/sources.json` で版と SHA-256 を
固定したうえで取得する。取得物は必ずチェックサムで検証し、一致しなければ中断する。

出力は `native/build/` に置かれる単一の共有ライブラリで、Srcnet.Extraction が
`DllImport` で読み込む。**解析対象リポジトリから文法を読み込むことは一切ない**
（docs/security.md C-1）。同梱する文法は sources.json だけが決める。

使い方:
    python3 tools/build_native.py                # 全言語を構築する
    python3 tools/build_native.py --languages c python
    python3 tools/build_native.py --check        # 取得と検証のみ行う
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NATIVE = ROOT / "native"
CACHE = NATIVE / ".cache"
WORK = NATIVE / ".build"
OUTPUT = NATIVE / "build"

LIBRARY_STEM = "srcnet_treesitter"

# ダウンロードの上限。壊れた応答や意図しない巨大な応答で資源を使い切らないため。
MAX_DOWNLOAD_BYTES = 256 * 1024 * 1024


def log(message: str) -> None:
    print(f"[build-native] {message}", flush=True)


def load_manifest() -> dict:
    with open(NATIVE / "sources.json", encoding="utf-8") as handle:
        return json.load(handle)


def archive_url(repository: str, version: str) -> str:
    return f"https://codeload.github.com/{repository}/tar.gz/refs/tags/{version}"


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(repository: str, version: str, expected_sha256: str) -> Path:
    """固定した版を取得し、チェックサムを検証してキャッシュへ置く。"""
    CACHE.mkdir(parents=True, exist_ok=True)
    name = repository.split("/")[-1]
    destination = CACHE / f"{name}-{version}.tar.gz"

    if destination.exists() and sha256_of(destination) == expected_sha256:
        return destination

    url = archive_url(repository, version)
    log(f"取得 {repository} {version}")
    temporary = destination.with_suffix(".partial")

    try:
        with urllib.request.urlopen(url, timeout=120) as response:
            total = 0
            with open(temporary, "wb") as handle:
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > MAX_DOWNLOAD_BYTES:
                        raise SystemExit(f"{repository}: 取得サイズが上限を超えました")
                    handle.write(chunk)

        actual = sha256_of(temporary)
        if actual != expected_sha256:
            raise SystemExit(
                f"{repository} {version}: チェックサムが一致しません\n"
                f"  期待 {expected_sha256}\n  実際 {actual}"
            )

        temporary.replace(destination)
        return destination
    finally:
        # Windows でも書き込みハンドルを閉じてから後片付けする。
        temporary.unlink(missing_ok=True)


def extract(archive: Path, into: Path) -> Path:
    """tar を展開し、単一のトップレベル ディレクトリを返す。"""
    if into.exists():
        shutil.rmtree(into)
    into.mkdir(parents=True)

    with tarfile.open(archive, "r:gz") as tar:
        for member in tar.getmembers():
            # 展開先を抜け出す名前と、リンクによる脱出を拒否する。
            # docs/security.md C-3 と同じ理由で、取得物も信頼しない。
            target = (into / member.name).resolve()
            if not target.is_relative_to(into.resolve()):
                raise SystemExit(f"{archive.name}: 展開先の外を指す項目があります: {member.name}")
            if not (member.isfile() or member.isdir()):
                raise SystemExit(f"{archive.name}: 通常ファイルとディレクトリ以外は受け付けません: {member.name}")

        # `filter` は Python 3.12 以降でしか使えないため、検証は上の走査で行う。
        tar.extractall(into)

    entries = [entry for entry in into.iterdir() if entry.is_dir()]
    if len(entries) != 1:
        raise SystemExit(f"{archive.name}: 想定と異なる構成です")
    return entries[0]

def required_exports(grammars: list[dict]) -> list[str]:
    symbols = set()
    for source in (ROOT / "src/Srcnet.Extraction").glob("*.fs"):
        symbols.update(re.findall(
            r"^\s*extern\s+\S+\s+(ts_[A-Za-z0-9_]+)\s*\(",
            source.read_text(encoding="utf-8"), re.MULTILINE
        ))
    if not symbols:
        raise SystemExit("tree-sitter の相互運用宣言が見つかりません")
    for grammar in grammars:
        symbol = grammar["symbol"]
        if not re.fullmatch(r"tree_sitter_[A-Za-z0-9_]+", symbol):
            raise SystemExit(f"文法の公開シンボル名が不正です: {symbol!r}")
        symbols.add(symbol)
    return sorted(symbols)


def verify_exports(library: Path, symbols: list[str]) -> None:
    loaded = ctypes.CDLL(str(library.resolve()))
    for symbol in symbols:
        if not hasattr(loaded, symbol):
            raise SystemExit(f"{library}: 必須の公開シンボルがありません: {symbol}")


class Toolchain:
    """C コンパイラの起動方法。MSVC と clang / gcc の差だけを吸収する。"""

    def __init__(self) -> None:
        self.is_windows = platform.system() == "Windows"
        self.compiler = self._find_compiler()
        self.is_msvc = Path(self.compiler).stem.lower() == "cl"

    def _find_compiler(self) -> str:
        for candidate in (os.environ.get("CC"), "clang", "cc", "gcc", "cl"):
            if candidate and shutil.which(candidate):
                return shutil.which(candidate)
        raise SystemExit("C コンパイラが見つかりません。CC 環境変数で指定してください")

    def compile(self, source: Path, output: Path, includes: list[Path]) -> None:
        output.parent.mkdir(parents=True, exist_ok=True)

        if self.is_msvc:
            command = [self.compiler, "/nologo", "/c", "/O2", "/utf-8", "/std:c11"]
            command += [f"/I{path}" for path in includes]
            command += [str(source), f"/Fo{output}"]
        else:
            command = [self.compiler, "-c", "-O2", "-fPIC", "-std=c11", "-w"]
            command += [f"-I{path}" for path in includes]
            command += [str(source), "-o", str(output)]

        subprocess.run(command, check=True)

    def link(self, objects: list[Path], output: Path, symbols: list[str]) -> None:
        output.parent.mkdir(parents=True, exist_ok=True)

        if self.is_windows:
            definition = output.with_suffix(".def")
            definition.write_bytes(
                (f"LIBRARY {LIBRARY_STEM}\nEXPORTS\n" + "".join(f"  {name}\n" for name in symbols))
                .encode("ascii")
            )

        if self.is_msvc:
            command = ["link", "/nologo", "/DLL", f"/OUT:{output}", f"/DEF:{definition}"] + [str(o) for o in objects]
        elif self.is_windows:
            command = [self.compiler, "-shared", "-o", str(output)] + [str(o) for o in objects] + [str(definition)]
        elif platform.system() == "Darwin":
            command = [self.compiler, "-dynamiclib", "-o", str(output)] + [str(o) for o in objects]
        else:
            command = [self.compiler, "-shared", "-o", str(output)] + [str(o) for o in objects]

        subprocess.run(command, check=True)


def library_name() -> str:
    system = platform.system()
    if system == "Windows":
        return f"{LIBRARY_STEM}.dll"
    if system == "Darwin":
        return f"lib{LIBRARY_STEM}.dylib"
    return f"lib{LIBRARY_STEM}.so"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--languages", nargs="*", help="構築する言語。既定は全言語")
    parser.add_argument("--check", action="store_true", help="取得と検証のみ行う")
    arguments = parser.parse_args()

    manifest = load_manifest()
    grammars = manifest["grammars"]

    if arguments.languages:
        selected = set(arguments.languages)
        unknown = selected - {grammar["language"] for grammar in grammars}
        if unknown:
            raise SystemExit(f"未知の言語です: {sorted(unknown)}")
        grammars = [grammar for grammar in grammars if grammar["language"] in selected]

    runtime = manifest["runtime"]
    runtime_archive = download(runtime["repository"], runtime["version"], runtime["sha256"])
    archives = [(grammar, download(grammar["repository"], grammar["version"], grammar["sha256"])) for grammar in grammars]

    if arguments.check:
        log(f"検証しました: ランタイム 1 件、文法 {len(archives)} 件")
        return 0

    toolchain = Toolchain()
    log(f"コンパイラ {toolchain.compiler}")

    WORK.mkdir(parents=True, exist_ok=True)
    objects: list[Path] = []

    # 1 つのリポジトリが複数の文法を持つ場合（typescript と tsx など）があるため、
    # 展開は書庫ごとに一度だけ行う。
    extracted: dict[tuple[str, str], Path] = {}

    def source_of(entry: dict, archive: Path) -> Path:
        key = (entry["repository"], entry["version"])
        if key not in extracted:
            name = entry["repository"].replace("/", "-")
            extracted[key] = extract(archive, WORK / f"src-{name}-{entry['version']}")
        return extracted[key]

    runtime_source = extract(runtime_archive, WORK / "tree-sitter")
    runtime_object = WORK / "obj" / "tree-sitter-runtime.o"
    toolchain.compile(
        runtime_source / "lib" / "src" / "lib.c",
        runtime_object,
        [runtime_source / "lib" / "src", runtime_source / "lib" / "include"],
    )
    objects.append(runtime_object)
    log("ランタイムを構築しました")

    built: list[str] = []

    for grammar, archive in archives:
        language = grammar["language"]
        source = source_of(grammar, archive)
        # 1 つのリポジトリが複数の文法を持つ場合は subdirectory がその位置を指す。
        source_directory = source / grammar.get("subdirectory", ".") / "src"

        if not (source_directory / "parser.c").exists():
            raise SystemExit(f"{language}: {source_directory} に parser.c がありません")

        for unit in ("parser.c", "scanner.c"):
            unit_path = source_directory / unit
            # 外部スキャナ（scanner.c）は持たない文法もある。
            if not unit_path.exists():
                continue

            unit_object = WORK / "obj" / f"{language}-{Path(unit).stem}.o"
            toolchain.compile(unit_path, unit_object, [source_directory])
            objects.append(unit_object)

        built.append(language)
        log(f"{language} を構築しました")

    output = OUTPUT / library_name()
    symbols = required_exports(grammars)
    toolchain.link(objects, output, symbols)
    verify_exports(output, symbols)

    (OUTPUT / "languages.json").write_text(
        json.dumps(
            {
                "runtime": runtime["version"],
                "languages": sorted(built),
                "grammars": {grammar["language"]: grammar["version"] for grammar, _ in archives},
            },
            indent=2,
            ensure_ascii=False,
        )
        + "\n",
        encoding="utf-8",
    )

    size = output.stat().st_size
    log(f"完了 {output} ({size // 1024} KiB, {len(built)} 言語)")
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    sys.exit(main())
