#!/usr/bin/env python3

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).with_name("scope.py")
sys.path.insert(0, str(SCRIPT.parent))
import scope


class ScopeCliTest(unittest.TestCase):
    def run_scope(self, *args: str, env: dict[str, str] | None = None):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), *args],
            capture_output=True,
            text=True,
            env=env,
            timeout=30,
        )
        payload = json.loads(result.stdout) if result.stdout.startswith("{") else None
        return result, payload

    def test_token_estimate_uses_separate_ascii_and_non_ascii_rates(self):
        self.assertEqual(scope.est_tokens("abcdefgh"), 2)
        self.assertEqual(scope.est_tokens("日本"), 1)
        self.assertEqual(scope.est_tokens("ab日本"), 2)

    def test_display_path_redacts_home_prefix_outside_working_directory(self):
        path = str(Path.home() / "outside-workspace" / "file.txt")

        with mock.patch.object(scope.os, "getcwd", return_value="/tmp"):
            displayed = scope.display_path(path)

        self.assertNotIn(str(Path.home()), displayed)
        self.assertTrue(displayed.startswith("~"))

    def test_hidden_directories_are_included_by_default(self):
        with tempfile.TemporaryDirectory() as tmp:
            hidden = Path(tmp, ".github")
            hidden.mkdir()
            Path(hidden, "skill.md").write_text("needle\n", encoding="utf-8")

            result, payload = self.run_scope("--grep", "needle", "--json", tmp)

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(payload["files"], 1)
            self.assertTrue(payload["hidden"])

    @unittest.skipUnless(shutil.which("rg"), "ripgrep required")
    def test_ripgrep_reports_match_lines_without_python_rescan(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "a.txt")
            path.write_text("a\nneedle\nb\nneedle\nc\n", encoding="utf-8")

            result, payload = self.run_scope(
                "--grep", "needle", "--context", "1", "--json", str(path)
            )

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["grep_hit_lines"], 2)
            self.assertEqual(payload["grep_hit_files"], 1)
            self.assertEqual(payload["warnings"], [])

    def test_empty_regex_is_not_confused_with_missing_grep_option(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "a.txt")
            path.write_text("a\nb\n", encoding="utf-8")

            result, payload = self.run_scope(
                "--grep", "", "--context", "0", "--json", str(path)
            )

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["grep_hit_lines"], 2)

    @unittest.skipIf(os.name == "nt", "POSIX filename semantics required")
    @unittest.skipUnless(shutil.which("rg"), "ripgrep required")
    def test_ripgrep_output_handles_colon_and_newline_in_filename(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "a:x\nname.txt")
            path.write_text("needle\n", encoding="utf-8")

            result, payload = self.run_scope(
                "--grep", "needle", "--json", str(path)
            )

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["files"], 1)
            self.assertEqual(payload["grep_hit_lines"], 1)

    @unittest.skipUnless(shutil.which("rg"), "ripgrep required")
    def test_unsupported_ripgrep_pattern_falls_back_with_warning(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "a.txt")
            path.write_text("needleX\n", encoding="utf-8")

            result, payload = self.run_scope(
                "--grep", "(?<=needle)X", "--json", str(path)
            )

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["grep_hit_lines"], 1)
            self.assertTrue(payload["warnings"])

    def test_prohibited_paths_are_classified_without_opening_them(self):
        for name in (".cases", ".scripts", ".output", "KQL"):
            with self.subTest(name=name):
                self.assertTrue(scope.is_skipped_path(f"/workspace/{name}/file.txt"))

    def test_ripgrep_excludes_prohibited_directories_before_search(self):
        completed = subprocess.CompletedProcess([], 1, "", "")
        with (
            mock.patch.object(scope.shutil, "which", return_value="/usr/bin/rg"),
            mock.patch.object(scope.subprocess, "run", return_value=completed) as run,
        ):
            scope.rg_match_lines("needle", ["/workspace"], None, [])

        command = run.call_args.args[0]
        for name in (".cases", ".scripts", ".output", "KQL"):
            with self.subTest(name=name):
                self.assertIn(f"!**/{name}/**", command)

    def test_nonpositive_numeric_options_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "a.txt")
            path.write_text("a\n", encoding="utf-8")
            for option, value in (
                ("--threads", "0"),
                ("--context", "-1"),
                ("--top", "-1"),
                ("--turns", "-1"),
                ("--fixed-cost", "0"),
                ("--agent-turns", "0"),
                ("--agent-cap", "0"),
            ):
                with self.subTest(option=option):
                    result, _ = self.run_scope(option, value, str(path))
                    self.assertEqual(result.returncode, 2)

    def test_missing_input_is_an_error(self):
        result, _ = self.run_scope("/path/that/does/not/exist")

        self.assertEqual(result.returncode, 2)
        self.assertIn("入力パス", result.stderr)

    def test_oversize_file_is_incomplete_not_no_match(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "large.txt")
            with path.open("wb") as fh:
                fh.truncate(33 * 1024 * 1024)

            result, payload = self.run_scope("--json", str(path))

            self.assertEqual(result.returncode, 3)
            self.assertEqual(payload["route"], "INCOMPLETE")
            self.assertEqual(payload["skipped"]["oversize"]["files"], 1)

    def test_extensionless_binary_is_not_counted_as_text(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "blob")
            path.write_bytes(b"\0alpha\0beta")

            result, payload = self.run_scope("--json", str(path))

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["route"], "NO_TEXT")
            self.assertEqual(payload["skipped"]["binary"]["files"], 1)

    def test_binary_outside_extension_filter_is_not_reported_as_input(self):
        with tempfile.TemporaryDirectory() as tmp:
            Path(tmp, "blob.bin").write_bytes(b"\0alpha")

            result, payload = self.run_scope("--ext", ".txt", "--json", tmp)

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["route"], "NO_MATCH")
            self.assertEqual(payload["skipped"]["binary"]["files"], 0)

    def test_non_utf8_text_is_reported_as_incomplete(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "legacy.txt")
            path.write_bytes(b"\x82\xa0" * 200)

            result, payload = self.run_scope("--json", str(path))

            self.assertEqual(result.returncode, 3)
            self.assertEqual(payload["route"], "INCOMPLETE")
            self.assertEqual(payload["skipped"]["encoding"]["files"], 1)

    @unittest.skipIf(os.name == "nt", "POSIX permission semantics required")
    def test_unreadable_file_is_reported_as_incomplete(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "private.txt")
            path.write_text("secret\n", encoding="utf-8")
            path.chmod(0)
            try:
                result, payload = self.run_scope("--json", str(path))
            finally:
                path.chmod(0o600)

            self.assertEqual(result.returncode, 3)
            self.assertEqual(payload["route"], "INCOMPLETE")
            self.assertEqual(payload["skipped"]["unreadable"]["files"], 1)

    def test_route_and_cost_gate_do_not_contradict(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "mid.txt")
            path.write_text("x " * 34_000, encoding="utf-8")

            result, payload = self.run_scope("--json", str(path))
            projected_result, projected = self.run_scope(
                "--turns", "20", "--json", str(path)
            )

            self.assertEqual(result.returncode, 0)
            self.assertFalse(payload["input_proxy_favors_delegation"])
            self.assertEqual(payload["route"], "INLINE_NARROW")
            self.assertTrue(payload["estimate_crosses_threshold"])
            self.assertEqual(projected_result.returncode, 0)
            self.assertTrue(projected["input_proxy_favors_delegation"])
            self.assertEqual(projected["route"], "SUBAGENT")

    def test_overlapping_inputs_are_counted_once(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "a.txt")
            path.write_text("alpha\n", encoding="utf-8")

            result, payload = self.run_scope("--json", tmp, str(path))

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["files"], 1)

    @unittest.skipUnless(shutil.which("rg"), "ripgrep required")
    def test_ignore_rules_are_on_by_default_and_can_be_disabled(self):
        with tempfile.TemporaryDirectory() as tmp:
            Path(tmp, ".ignore").write_text("ignored.txt\n", encoding="utf-8")
            Path(tmp, "ignored.txt").write_text("ignored\n", encoding="utf-8")
            Path(tmp, "kept.txt").write_text("kept\n", encoding="utf-8")

            result, payload = self.run_scope("--json", tmp)
            all_result, all_payload = self.run_scope("--no-ignore", "--json", tmp)

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["files"], 2)
            self.assertTrue(payload["ignore_rules"])
            self.assertEqual(all_result.returncode, 0)
            self.assertEqual(all_payload["files"], 3)
            self.assertFalse(all_payload["ignore_rules"])

    def test_custom_agent_cap_changes_route(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "a.txt")
            path.write_text("x " * 20_000, encoding="utf-8")

            result, payload = self.run_scope(
                "--agent-cap", "1000", "--fixed-cost", "1", "--json", str(path)
            )

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["route"], "NARROW_FIRST")

    def test_large_single_file_cannot_be_hidden_by_thread_average(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "large.txt")
            path.write_text("x " * 260_000, encoding="utf-8")

            result, payload = self.run_scope(
                "--threads", "2", "--fixed-cost", "1", "--json", str(path)
            )

            self.assertEqual(result.returncode, 0)
            self.assertLess(
                payload["est_tokens_upper_per_thread"],
                payload["agent_cap"],
            )
            self.assertGreater(
                payload["largest_file_est_tokens_upper"],
                payload["agent_cap"],
            )
            self.assertTrue(payload["estimate_crosses_threshold"])
            self.assertEqual(payload["route"], "NARROW_FIRST")

    def test_cost_and_latency_choose_different_agent_counts(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "large.txt")
            path.write_text("x " * 140_000, encoding="utf-8")

            cost_result, cost = self.run_scope(
                "--threads", "3", "--fixed-cost", "1", "--json", str(path)
            )
            latency_result, latency = self.run_scope(
                "--threads", "3", "--fixed-cost", "1",
                "--optimize", "latency", "--json", str(path)
            )

            self.assertEqual(cost_result.returncode, 0)
            self.assertEqual(cost["route"], "SUBAGENT")
            self.assertEqual(cost["expected_agents"], 1)
            self.assertEqual(latency_result.returncode, 0)
            self.assertEqual(latency["route"], "FLEET")
            self.assertEqual(latency["expected_agents"], 3)

    def test_invalid_environment_value_is_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "a.txt")
            path.write_text("alpha\n", encoding="utf-8")
            env = os.environ.copy()
            env["LEAN_EXEC_F"] = "invalid"

            result, payload = self.run_scope("--json", str(path), env=env)

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["fixed_cost"], 6000)
            self.assertIn("warning: LEAN_EXEC_F", result.stderr)

    def test_explicit_value_overrides_invalid_environment_without_warning(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp, "a.txt")
            path.write_text("alpha\n", encoding="utf-8")
            env = os.environ.copy()
            env["LEAN_EXEC_F"] = "invalid"

            result, payload = self.run_scope(
                "--fixed-cost", "10", "--json", str(path), env=env
            )

            self.assertEqual(result.returncode, 0)
            self.assertEqual(payload["fixed_cost"], 10)
            self.assertNotIn("LEAN_EXEC_F", result.stderr)


if __name__ == "__main__":
    unittest.main()
