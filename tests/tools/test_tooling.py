"""Regression tests for the repository's standard-library build tools."""

import importlib.util
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("build_native", ROOT / "tools/build_native.py")
build_native = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build_native)


class NativeBuildTests(unittest.TestCase):
    def archive(self, root, name, kind=tarfile.REGTYPE):
        archive = root / "fixture.tar.gz"
        with tarfile.open(archive, "w:gz") as output:
            member = tarfile.TarInfo(name)
            member.type = kind
            member.size = 1 if kind == tarfile.REGTYPE else 0
            output.addfile(member, io.BytesIO(b"x") if member.size else None)
        return archive

    def test_archive_cannot_escape_to_a_sibling_with_the_same_prefix(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = self.archive(root, "../source-escape/file.c")
            with self.assertRaises(SystemExit):
                build_native.extract(archive, root / "source")
            self.assertFalse((root / "source-escape").exists())

    def test_archive_rejects_non_regular_entries_before_extracting(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for kind in (tarfile.FIFOTYPE, tarfile.CHRTYPE, tarfile.BLKTYPE, tarfile.SYMTYPE):
                with self.subTest(kind=kind):
                    archive = self.archive(root, "source/special", kind)
                    with self.assertRaises(SystemExit):
                        build_native.extract(archive, root / "unpacked")

    def test_regular_source_archive_is_accepted(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = self.archive(root, "source/file.c")
            extracted = build_native.extract(archive, root / "unpacked")
            self.assertEqual(b"x", (extracted / "file.c").read_bytes())

    def test_redirected_help_is_utf8_even_with_a_legacy_console_encoding(self):
        environment = {**os.environ, "PYTHONIOENCODING": "cp1252"}
        result = subprocess.run(
            [sys.executable, str(ROOT / "tools/build_native.py"), "--help"],
            env=environment,
            capture_output=True,
            timeout=10,
            check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("\u69cb\u7bc9", result.stdout.decode("utf-8"))

    def test_required_exports_cover_runtime_and_selected_grammars(self):
        symbols = build_native.required_exports([{"symbol": "tree_sitter_c"}])
        self.assertEqual(sorted(set(symbols)), symbols)
        for name in ("ts_parser_new", "ts_parser_parse_string", "ts_tree_cursor_new", "tree_sitter_c"):
            self.assertIn(name, symbols)
        self.assertNotIn("tree_sitter_cpp", symbols)

    def test_windows_linkers_receive_explicit_runtime_exports(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "with spaces" / "parser.dll"
            for compiler in ("cl", "clang", "gcc"):
                with self.subTest(compiler=compiler):
                    toolchain = object.__new__(build_native.Toolchain)
                    toolchain.compiler = compiler
                    toolchain.is_windows = True
                    toolchain.is_msvc = compiler == "cl"
                    with mock.patch.object(build_native.subprocess, "run") as run:
                        toolchain.link([Path("parser.o")], output, ["ts_parser_new", "tree_sitter_c"])
                        command = run.call_args.args[0]
                    definition = output.with_suffix(".def")
                    expected = f"/DEF:{definition}" if compiler == "cl" else str(definition)
                    self.assertIn(expected, command)
                    self.assertIn("  ts_parser_new\n", definition.read_text(encoding="ascii"))

    def test_msvc_compiles_as_c11(self):
        with tempfile.TemporaryDirectory() as temporary:
            toolchain = object.__new__(build_native.Toolchain)
            toolchain.compiler = "cl"
            toolchain.is_msvc = True
            with mock.patch.object(build_native.subprocess, "run") as run:
                toolchain.compile(Path("parser.c"), Path(temporary) / "parser.o", [])
                self.assertIn("/std:c11", run.call_args.args[0])

class DependencyClosureTests(unittest.TestCase):
    def test_empty_or_incomplete_closure_cannot_pass_the_gate(self):
        spec = importlib.util.spec_from_file_location(
            "dependency_closure", ROOT / ".github/scripts/check_dependency_closure.py"
        )
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "test.deps.json"
            cases = [
                ({}, 1),
                ({"targets": {}}, 1),
                ({"targets": {"net10.0": {"srcnet/0.1.0": {}}}}, 1),
                ({"targets": {"net10.0": {"srcnet/0.1.0": {}, "FSharp.Core/10.0.102": {}}}}, 0),
                ({"targets": {"net10.0": {"srcnet/0.1.0": {}, "FSharp.Core/10.0.102": {}, "Unexpected/1.0": {}}}}, 1),
            ]
            for data, expected in cases:
                with self.subTest(data=data):
                    path.write_text(json.dumps(data), encoding="utf-8")
                    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                        self.assertEqual(expected, module.main(str(path)))


if __name__ == "__main__":
    unittest.main()
