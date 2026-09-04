#!/usr/bin/env python3
"""許可リストと lean-exec の整合を守る。

モデルの正本は strict-rules であって、この skill ではない。散文にも設定にも
許可リスト外のモデルが残らないことを機械的に確かめる。
"""

from __future__ import annotations

import json
import re
import unittest
from pathlib import Path


SKILL_ROOT = Path(__file__).resolve().parents[1]
STRICT_RULES = SKILL_ROOT.parents[1] / "instructions" / "strict-rules.instructions.md"
MODELS_CONFIG = SKILL_ROOT / "config" / "models.json"
POLICY_CONFIG = SKILL_ROOT / "config" / "policy.json"
MODEL_REFERENCE = re.compile(
    r"\b(?:gpt|claude)-[a-z0-9]+(?:[.-][a-z0-9]+)*\b",
    re.IGNORECASE,
)
ALLOWED_DISPLAY_NAME = re.compile(r"`((?:GPT|Claude)[^`]+)`", re.IGNORECASE)


def model_slug(display_name: str) -> str:
    return re.sub(r"\s+", "-", display_name.strip().lower())


def allowed_models() -> set[str]:
    strict_text = STRICT_RULES.read_text(encoding="utf-8")
    return {model_slug(name) for name in ALLOWED_DISPLAY_NAME.findall(strict_text)}


class ModelPolicyTest(unittest.TestCase):
    def setUp(self):
        self.allowed = allowed_models()
        self.assertTrue(self.allowed, "strict-rules にモデル許可リストがありません")
        self.catalog = json.loads(MODELS_CONFIG.read_text(encoding="utf-8"))
        self.policy = json.loads(POLICY_CONFIG.read_text(encoding="utf-8"))

    def test_documented_models_are_allowed_by_strict_rules(self):
        docs = [SKILL_ROOT / "SKILL.md", *sorted((SKILL_ROOT / "references").glob("*.md"))]
        referenced: set[str] = set()
        for path in docs:
            referenced.update(
                match.lower()
                for match in MODEL_REFERENCE.findall(path.read_text(encoding="utf-8"))
            )

        self.assertTrue(referenced, "lean-exec にモデル指定がありません")
        self.assertEqual(
            referenced - self.allowed,
            set(),
            "lean-exec が許可リスト外のモデルを参照しています",
        )

    def test_catalog_models_are_allowed_by_strict_rules(self):
        """設定は veto を回避する抜け道にならない。"""
        self.assertEqual(
            set(self.catalog["models"]) - self.allowed,
            set(),
            "config/models.json に許可リスト外のモデルがあります",
        )

    def test_catalog_covers_every_allowed_model(self):
        """許可リストに増えたモデルを黙って無視しない。校正前でも enabled: false で載せる。"""
        self.assertEqual(
            self.allowed - set(self.catalog["models"]),
            set(),
            "許可リストにあるのに config/models.json に無いモデルがあります",
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
