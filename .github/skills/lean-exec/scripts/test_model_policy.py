#!/usr/bin/env python3
"""モデル カタログと lean-exec の整合を守る。

モデルの正本は config/models.json。散文にカタログ外のモデルが残らず、
有効なモデルに選択用の情報が揃っていることを機械的に確かめる。
"""

from __future__ import annotations

import json
import re
import unittest
from pathlib import Path


SKILL_ROOT = Path(__file__).resolve().parents[1]
MODELS_CONFIG = SKILL_ROOT / "config" / "models.json"
POLICY_CONFIG = SKILL_ROOT / "config" / "policy.json"
MODEL_REFERENCE = re.compile(
    r"\b(?:gpt|claude)-[a-z0-9]+(?:[.-][a-z0-9]+)*\b",
    re.IGNORECASE,
)
class ModelPolicyTest(unittest.TestCase):
    def setUp(self):
        self.catalog = json.loads(MODELS_CONFIG.read_text(encoding="utf-8"))
        self.policy = json.loads(POLICY_CONFIG.read_text(encoding="utf-8"))
        self.assertTrue(self.catalog["models"], "モデル カタログが空です")

    def test_documented_models_are_in_catalog(self):
        docs = [SKILL_ROOT / "SKILL.md", *sorted((SKILL_ROOT / "references").glob("*.md"))]
        referenced: set[str] = set()
        for path in docs:
            referenced.update(
                match.lower()
                for match in MODEL_REFERENCE.findall(path.read_text(encoding="utf-8"))
            )

        self.assertTrue(referenced, "lean-exec にモデル指定がありません")
        self.assertEqual(
            referenced - set(self.catalog["models"]),
            set(),
            "lean-exec がモデル カタログ外のモデルを参照しています",
        )

    def test_enabled_models_carry_cost_and_capability(self):
        dimensions = self.policy["dimensions"]
        for name, spec in self.catalog["models"].items():
            if not spec.get("enabled"):
                continue
            self.assertIsNotNone(spec.get("cost"), f"{name} に cost がありません")
            self.assertGreater(spec["cost"], 0, f"{name} の cost が正ではありません")
            self.assertIsNotNone(spec.get("capability"), f"{name} に capability がありません")
            self.assertEqual(
                set(spec["capability"]), set(dimensions),
                f"{name} の capability が次元と一致しません",
            )

    def test_effort_variants_are_defined(self):
        variants = set(self.catalog["effort_variants"])
        for name, spec in self.catalog["models"].items():
            self.assertTrue(spec.get("efforts"), f"{name} に efforts がありません")
            self.assertEqual(
                set(spec["efforts"]) - variants, set(),
                f"{name} が未定義の effort を参照しています",
            )

    def test_policy_dimensions_are_fully_specified(self):
        dimensions = self.policy["dimensions"]
        for section in ("weights", "bands", "heads", "lexicon"):
            keys = set(self.policy[section])
            self.assertEqual(
                keys, set(dimensions),
                f"policy.{section} が次元と一致しません",
            )

    def test_roles_only_attenuate(self):
        """role の scale は 1.0 以下。要件の水増しは floor で表す。"""
        for role, spec in self.policy["roles"].items():
            for dim, value in spec.get("scale", {}).items():
                self.assertLessEqual(value, 1.0, f"{role}.scale.{dim} が 1.0 を超えています")
            self.assertIn("tau", spec, f"{role} に tau がありません")
            self.assertGreaterEqual(spec["tau"], 0.0)


if __name__ == "__main__":
    unittest.main()
