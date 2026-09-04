#!/usr/bin/env python3
"""route.py の不変条件を検証する。

「安くなったこと」ではなく「安くしても壊れていないこと」を確かめる。
lean-exec が扱ってよい削減は失敗を検出できるものだけなので、検出手段のほうを固定する。
"""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import route as R  # noqa: E402

SKILL_ROOT = Path(__file__).resolve().parents[1]
POLICY_PATH = SKILL_ROOT / "config" / "policy.json"
MODELS_PATH = SKILL_ROOT / "config" / "models.json"
SAMPLES_PATH = SKILL_ROOT / "config" / "sample_prompts.jsonl"
ALLOWLIST_PATH = R.ALLOWLIST
ROUTE_SCRIPT = SKILL_ROOT / "scripts" / "route.py"


def load_all():
    policy = R.load_json(POLICY_PATH, "policy")
    models = R.load_json(MODELS_PATH, "models")
    allowed = R.load_allowlist(ALLOWLIST_PATH)
    pool, vetoed = R.build_pool(models, policy, allowed)
    weights = R.compensated_weights(policy, {})
    return policy, models, allowed, pool, vetoed, weights


def run_cli(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(ROUTE_SCRIPT), *args],
        capture_output=True, text=True, cwd=str(SKILL_ROOT),
    )


def entry(model: str, effort: str, cost: float, base_cost: float | None = None, **caps: float) -> R.PoolEntry:
    return R.PoolEntry(
        model=model, effort=effort, cost=cost,
        base_cost=cost if base_cost is None else base_cost, capability=caps,
        family=model.split("-")[0], context_tiers=("default",), calibrated=True,
    )


class ShortfallTest(unittest.TestCase):
    """Eq. 5 — 次元をまたいだ埋め合わせを許さない。"""

    def test_surplus_does_not_offset_deficit(self):
        weights = {"a": 1.0, "b": 1.0}
        strong_a = entry("m", "none", 1.0, a=1.0, b=0.0)
        balanced = entry("n", "none", 1.0, a=0.5, b=0.5)
        requirements = {"a": 0.5, "b": 0.5}
        # a の余剰 0.5 は b の不足 0.5 を埋めない
        self.assertEqual(R.shortfall(strong_a, requirements, weights), 0.5)
        self.assertEqual(R.shortfall(balanced, requirements, weights), 0.0)

    def test_capability_above_requirement_costs_nothing(self):
        weights = {"a": 2.0}
        self.assertEqual(
            R.shortfall(entry("m", "none", 1.0, a=0.9), {"a": 0.3}, weights), 0.0
        )

    def test_weights_scale_the_gap(self):
        gap = R.shortfall(entry("m", "none", 1.0, a=0.2), {"a": 0.5}, {"a": 3.0})
        self.assertAlmostEqual(gap, 0.9, places=6)


class WeightCompensationTest(unittest.TestCase):
    """Eq. 4 — band 幅の差で操作者の意図がゆがまないこと。"""

    def test_total_weight_is_preserved(self):
        policy, *_ = load_all()
        weights = R.compensated_weights(policy, {})
        expected = sum(policy["weights"][dim] for dim in policy["dimensions"])
        self.assertAlmostEqual(sum(weights.values()), expected, places=6)

    def test_narrow_band_gets_more_weight(self):
        policy = {
            "dimensions": ["wide", "narrow"],
            "bands": {"wide": [0.0, 1.0], "narrow": [0.0, 0.25]},
            "weights": {"wide": 1.0, "narrow": 1.0},
        }
        weights = R.compensated_weights(policy, {})
        self.assertGreater(weights["narrow"], weights["wide"])
        self.assertAlmostEqual(weights["narrow"] / weights["wide"], 4.0, places=6)

    def test_override_is_applied(self):
        policy, *_ = load_all()
        base = R.compensated_weights(policy, {})
        boosted = R.compensated_weights(policy, {"reasoning": 5.0})
        self.assertGreater(boosted["reasoning"] / sum(boosted.values()),
                           base["reasoning"] / sum(base.values()))

    def test_zero_band_is_rejected(self):
        policy = {
            "dimensions": ["a"], "bands": {"a": [0.5, 0.5]}, "weights": {"a": 1.0},
        }
        with self.assertRaises(R.ConfigError):
            R.compensated_weights(policy, {})


class PoolNormalizationTest(unittest.TestCase):
    """Eq. 3 — プール内で最弱を band 下端、最強を band 上端に固定する。"""

    def test_weakest_and_strongest_are_pinned_to_the_band(self):
        policy, _, _, pool, _, _ = load_all()
        for dim in policy["dimensions"]:
            lo, hi = policy["bands"][dim]
            base = [e for e in pool if e.effort in {"none", "medium"}]
            values = [e.capability[dim] for e in base]
            self.assertAlmostEqual(min(values), lo, places=3)
            self.assertAlmostEqual(max(values), hi, places=3)

    def test_removing_a_model_renormalizes_the_rest(self):
        """カタログ変更は設定編集だけで効く。プロファイルは自動で張り直される。"""
        policy, models, allowed, pool, _, _ = load_all()
        reduced = json.loads(json.dumps(models))
        reduced["models"]["claude-haiku-4.5"]["enabled"] = False
        new_pool, _ = R.build_pool(reduced, policy, allowed)
        self.assertNotIn("claude-haiku-4.5", {e.model for e in new_pool})
        for dim in policy["dimensions"]:
            lo, _ = policy["bands"][dim]
            base = [e for e in new_pool if e.effort == "medium"]
            # 最弱が抜けたので、次に弱いモデルが下端へ張り直される
            self.assertAlmostEqual(min(e.capability[dim] for e in base), lo, places=3)

    def test_adding_a_model_needs_no_code_change(self):
        policy, models, allowed, _, _, _ = load_all()
        extended = json.loads(json.dumps(models))
        extended["models"]["gpt-5.6-terra"].update({
            "enabled": True, "cost": 6.0,
            "capability": {"reasoning": 80, "code_gen": 88, "debugging": 70, "tool_use": 84},
        })
        new_pool, _ = R.build_pool(extended, policy, allowed)
        self.assertIn("gpt-5.6-terra", {e.model for e in new_pool})

    def test_effort_variants_expand_the_pool(self):
        _, _, _, pool, _, _ = load_all()
        efforts = {e.effort for e in pool if e.model == "claude-sonnet-5"}
        self.assertEqual(efforts, {"low", "medium", "high", "xhigh", "max"})
        # Haiku は reasoning effort に対応しないので、変種を持たせない
        self.assertEqual({e.effort for e in pool if e.model == "claude-haiku-4.5"}, {"none"})

    def test_higher_effort_costs_more_and_covers_more(self):
        _, _, _, pool, _, _ = load_all()
        variants = sorted(
            (e for e in pool if e.model == "gpt-5.6-sol"), key=lambda e: e.cost,
        )
        for lower, higher in zip(variants, variants[1:]):
            self.assertLess(lower.cost, higher.cost)
            for dim, value in lower.capability.items():
                self.assertLessEqual(value, higher.capability[dim] + 1e-9)


class MatchingTest(unittest.TestCase):
    """Algorithm 2 — 適格集合の最安、空なら最小 shortfall。"""

    def setUp(self):
        self.weights = {"a": 1.0}
        self.pool = [
            entry("cheap", "none", 1.0, a=0.2),
            entry("mid", "none", 5.0, a=0.6),
            entry("top", "none", 20.0, a=0.9),
        ]

    def test_cheapest_eligible_wins(self):
        selected, value, basis, eligible, _ = R.match(self.pool, {"a": 0.5}, self.weights, 0.1)
        self.assertEqual(selected.model, "mid")
        self.assertEqual(basis, "cheapest_eligible")
        self.assertEqual(value, 0.0)
        self.assertEqual(set(eligible), {"mid", "top"})

    def test_fail_open_when_nothing_qualifies(self):
        selected, _, basis, eligible, _ = R.match(self.pool, {"a": 1.0}, self.weights, 0.01)
        self.assertEqual(basis, "fail_open_least_shortfall")
        self.assertEqual(selected.model, "top")
        self.assertEqual(eligible, [])

    def test_never_returns_nothing(self):
        for requirement in (0.0, 0.5, 1.0):
            selected, *_ = R.match(self.pool, {"a": requirement}, self.weights, 0.0)
            self.assertIsNotNone(selected)

    def test_cost_is_monotone_non_increasing_in_tau(self):
        """tau を緩めて選択が高くなることはない。フロンティアが折り返さない条件。"""
        policy, _, _, pool, _, weights = load_all()
        prompts = [json.loads(line)["prompt"] for line in SAMPLES_PATH.read_text(encoding="utf-8").splitlines() if line.strip()]
        for prompt in prompts:
            requirements = R.apply_role(
                policy, R.predict(policy, R.extract_features(policy, prompt, 1)), "analyst",
            )
            previous = None
            for tau in [0.0, 0.01, 0.02, 0.05, 0.1, 0.2, 0.4, 0.8, 1.6]:
                selected, *_ = R.match(pool, requirements, weights, tau)
                if previous is not None:
                    self.assertLessEqual(
                        selected.cost, previous + 1e-9,
                        f"tau={tau} でコストが上がった: {prompt[:30]}",
                    )
                previous = selected.cost


class HardGateTest(unittest.TestCase):
    """ハード ゲートは fail-closed。健全性フィルターの fail-open とは扱いが違う。"""

    def test_allowlist_vetoes_models_outside_the_list(self):
        policy, models, _, _, _, _ = load_all()
        pool, vetoed = R.build_pool(models, policy, {"claude-haiku-4.5"})
        self.assertEqual({e.model for e in pool}, {"claude-haiku-4.5"})
        self.assertIn("gpt-5.6-sol", vetoed)

    def test_empty_allowlist_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "empty.md"
            path.write_text("# no models here\n", encoding="utf-8")
            with self.assertRaises(R.GateError):
                R.load_allowlist(path)

    def test_missing_allowlist_is_rejected(self):
        with self.assertRaises(R.GateError):
            R.load_allowlist(Path("/nonexistent/strict-rules.md"))

    def test_long_context_gate_drops_models_without_it(self):
        _, _, _, pool, _, _ = load_all()
        survivors, _ = R.prefilter(
            pool, long_context=True, exclude_families=set(),
        )
        self.assertNotIn("claude-haiku-4.5", {e.model for e in survivors})
        self.assertTrue(survivors)

    def test_rogue_catalog_cannot_introduce_an_unlisted_model(self):
        """カタログに何を書いても、許可リスト外のモデルは候補に入らない。"""
        policy, models, allowed, _, _, _ = load_all()
        rogue = json.loads(json.dumps(models))
        rogue["models"]["gpt-attacker"] = {
            "enabled": True, "calibrated": True, "cost": 0.001,
            "capability": {dim: 100 for dim in policy["dimensions"]},
            "efforts": ["none"], "context_tiers": ["default"],
        }
        pool, vetoed = R.build_pool(rogue, policy, allowed)
        self.assertIn("gpt-attacker", vetoed)
        self.assertEqual({e.model for e in pool} - allowed, set())

    def test_family_exclusion_keeps_verifier_independent(self):
        _, _, _, pool, _, _ = load_all()
        survivors, _ = R.prefilter(
            pool, long_context=False, exclude_families={"gpt"},
        )
        self.assertTrue(survivors)
        self.assertEqual({e.family for e in survivors}, {"claude"})

    def test_gate_that_empties_the_pool_raises(self):
        """候補が消えたら代替を推測せず落ちる (fail-closed)。"""
        policy, _, _, pool, _, weights = load_all()
        families = {entry.family for entry in pool}
        with self.assertRaises(R.GateError):
            R.decide(
                policy=policy, pool=pool, prompt="何か調べて", role="analyst", turn=1,
                tau=0.1, weights=weights, tier_mode="auto",
                long_context=False, exclude_families=families, current_model=None,
                reroute_triggered=True, override_requirements=None,
            )

    def test_repo_dependent_prompt_is_tier_two(self):
        policy, *_ = load_all()
        prompt = "なんでこのテスト落ちるの?"
        features = R.extract_features(policy, prompt, 1)
        self.assertEqual(R.detect_tier(policy, prompt, features), "T2")

    def test_explicit_file_reference_is_tier_one(self):
        policy, *_ = load_all()
        prompt = "src/lib/time.ts の変換処理を見て"
        features = R.extract_features(policy, prompt, 1)
        self.assertEqual(R.detect_tier(policy, prompt, features), "T1")

    def test_self_contained_prompt_is_tier_three(self):
        policy, *_ = load_all()
        prompt = "ISO 8601 とは何か 1 行で説明して"
        features = R.extract_features(policy, prompt, 1)
        self.assertEqual(R.detect_tier(policy, prompt, features), "T3")

    def test_tier_floor_raises_requirements(self):
        policy, *_ = load_all()
        scores = {dim: 0.05 for dim in policy["dimensions"]}
        floored, floor = R.apply_tier(policy, scores, "T2", measured=False)
        self.assertGreater(floor, 0.0)
        self.assertTrue(all(value >= floor for value in floored.values()))

    def test_measured_context_lowers_the_floor(self):
        policy, *_ = load_all()
        scores = {dim: 0.05 for dim in policy["dimensions"]}
        _, unmeasured = R.apply_tier(policy, scores, "T2", measured=False)
        _, measured = R.apply_tier(policy, scores, "T2", measured=True)
        self.assertLess(measured, unmeasured)

    def test_floor_never_lowers_a_high_requirement(self):
        policy, *_ = load_all()
        scores = {dim: 0.95 for dim in policy["dimensions"]}
        floored, _ = R.apply_tier(policy, scores, "T2", measured=False)
        self.assertTrue(all(value == 0.95 for value in floored.values()))


class RoleTest(unittest.TestCase):
    def test_scale_above_one_is_rejected(self):
        policy, *_ = load_all()
        broken = json.loads(json.dumps(policy))
        broken["roles"]["analyst"]["scale"]["reasoning"] = 1.5
        with self.assertRaises(R.ConfigError):
            R.apply_role(broken, {dim: 0.5 for dim in policy["dimensions"]}, "analyst")

    def test_band_hi_floor_tracks_the_band(self):
        policy, *_ = load_all()
        requirements = R.apply_role(
            policy, {dim: 0.0 for dim in policy["dimensions"]}, "verifier",
        )
        for dim, value in requirements.items():
            self.assertAlmostEqual(value, policy["bands"][dim][1], places=3)

    def test_retriever_is_cheaper_than_verifier_on_the_same_prompt(self):
        policy, _, _, pool, _, weights = load_all()
        prompt = "parse_config の呼び出し元を一覧で出して"
        picks = {}
        for role in ("retriever", "verifier"):
            decision = R.decide(
                policy=policy, pool=pool, prompt=prompt, role=role, turn=1,
                tau=policy["roles"][role]["tau"], weights=weights, tier_mode="auto",
                long_context=False, exclude_families=set(),
                current_model=None, reroute_triggered=True,
                override_requirements=None,
            )
            picks[role] = decision.selected.cost
        self.assertLess(picks["retriever"], picks["verifier"])


class StickyTest(unittest.TestCase):
    """prompt cache を守る。会話の途中でモデルを替えるとキャッシュが丸ごと死ぬ。"""

    RICH = "この設計の妥当性を根拠つきで検証し、矛盾があれば原因を特定して直して"

    def decide(self, **kwargs):
        policy, _, _, pool, _, weights = load_all()
        base = dict(
            policy=policy, pool=pool, prompt="この処理の続きを実装して", role="orchestrator",
            turn=5, tau=0.05, weights=weights, tier_mode="auto",
            long_context=False, exclude_families=set(), current_model="claude-sonnet-5",
            reroute_triggered=False, override_requirements=None,
        )
        base.update(kwargs)
        return R.decide(**base)

    def test_mid_conversation_keeps_the_current_model(self):
        decision = self.decide()
        self.assertEqual(decision.selected.model, "claude-sonnet-5")
        self.assertEqual(decision.basis, "sticky_prompt_cache")

    def test_first_turn_routes_freshly(self):
        decision = self.decide(prompt=self.RICH, turn=1, reroute_triggered=True)
        self.assertIn(decision.basis, {"cheapest_eligible", "fail_open_least_shortfall"})

    def test_after_compaction_routes_freshly(self):
        decision = self.decide(prompt=self.RICH, reroute_triggered=True)
        self.assertIn(decision.basis, {"cheapest_eligible", "fail_open_least_shortfall"})

    def test_unknown_current_model_does_not_block_routing(self):
        decision = self.decide(current_model="claude-opus-5")
        self.assertEqual(decision.selected.model, "claude-opus-5")

    def test_low_confidence_keeps_the_current_model(self):
        decision = self.decide(
            prompt="ok", turn=1, reroute_triggered=True,
            override_requirements={"reasoning": 0.1, "code_gen": 0.1,
                                   "debugging": 0.1, "tool_use": 0.1},
        )
        self.assertEqual(decision.basis, "sticky_low_confidence")

    def test_weak_signal_repo_dependent_prompt_still_counts_as_low_confidence(self):
        """tier の下限は確信度ではない。下限で gamma を押し上げると保持が届かなくなる。"""
        policy, *_ = load_all()
        prompt = "その挙動"
        features = R.extract_features(policy, prompt, 1)
        self.assertEqual(R.detect_tier(policy, prompt, features), "T2")
        decision = self.decide(prompt=prompt, turn=1, reroute_triggered=True)
        self.assertLess(decision.gamma, policy["gamma_sticky"])
        self.assertEqual(decision.basis, "sticky_low_confidence")

    def test_force_route_overrides_the_low_confidence_hold(self):
        decision = self.decide(
            prompt="ok", turn=1, reroute_triggered=True, force=True,
            override_requirements={"reasoning": 0.1, "code_gen": 0.1,
                                   "debugging": 0.1, "tool_use": 0.1},
        )
        self.assertNotEqual(decision.basis, "sticky_low_confidence")

    def test_non_sticky_roles_ignore_the_current_model(self):
        """サブエージェントは毎回新しい文脈で立ち上がる。守るべきキャッシュが無い。"""
        decision = self.decide(role="retriever", tau=0.8)
        self.assertNotIn("sticky", decision.basis)
        self.assertTrue(any("sticky ではない" in note for note in decision.notes))

    def test_sticky_still_repicks_the_effort(self):
        """prompt cache はモデル単位。effort を替えてもキャッシュは切れない。"""
        decision = self.decide(
            prompt="この設計の妥当性を根拠つきで詳細に検証し、矛盾があれば原因を特定して",
        )
        self.assertEqual(decision.selected.model, "claude-sonnet-5")
        self.assertEqual(decision.basis, "sticky_prompt_cache")
        self.assertTrue(decision.table)
        self.assertTrue(all(row["model"].startswith("claude-sonnet-5") for row in decision.table))


class LanguageInvarianceTest(unittest.TestCase):
    """HyDRA §5.1 — 経路は言語ではなくタスクの性質で決まること。"""

    def pairs(self):
        records = [
            json.loads(line)
            for line in SAMPLES_PATH.read_text(encoding="utf-8").splitlines() if line.strip()
        ]
        grouped: dict[str, dict[str, str]] = {}
        for record in records:
            grouped.setdefault(record["pair"], {})[record["lang"]] = record["prompt"]
        return {name: langs for name, langs in grouped.items() if len(langs) == 2}

    def test_translation_pairs_route_consistently(self):
        policy, _, _, pool, _, weights = load_all()
        pairs = self.pairs()
        self.assertGreaterEqual(len(pairs), 15)
        agreements = 0
        checks = 0
        for role in policy["roles"]:
            tau = policy["roles"][role]["tau"]
            for langs in pairs.values():
                picks = {}
                for lang, prompt in langs.items():
                    decision = R.decide(
                        policy=policy, pool=pool, prompt=prompt, role=role, turn=1,
                        tau=tau, weights=weights, tier_mode="auto",
                        long_context=False, exclude_families=set(), current_model=None,
                        reroute_triggered=True,
                        override_requirements=None,
                    )
                    picks[lang] = decision.selected
                checks += 1
                if picks["ja"].key == picks["en"].key:
                    agreements += 1
                ratio = max(picks["ja"].cost, picks["en"].cost) / min(picks["ja"].cost, picks["en"].cost)
                self.assertLessEqual(
                    ratio, 2.0,
                    f"{role}: 日英でコストが {ratio:.1f} 倍ずれた "
                    f"(ja={picks['ja'].key} en={picks['en'].key})",
                )
        self.assertGreaterEqual(
            agreements / checks, 0.85,
            f"日英の経路一致率が低い: {agreements}/{checks}",
        )


class PredictorTest(unittest.TestCase):
    def test_every_head_responds_to_its_own_signal(self):
        """4 本のヘッドが独立に動くこと。1 本でも死んでいると次元を潰したのと同じになる。"""
        policy, *_ = load_all()
        neutral = "これについて一言"
        cases = {
            "reasoning": "なぜこの設計が妥当なのか、トレードオフを比較して根拠つきで説明して",
            "code_gen": "変換用のヘルパー関数を新規に実装して、クラスも追加して",
            "debugging": "例外でクラッシュする不具合を再現して、原因を切り分けて修正して",
            "tool_use": "ログを取得してコマンドを実行し、該当箇所を検索して一覧にして",
        }
        base = R.predict(policy, R.extract_features(policy, neutral, 1))
        for dim, prompt in cases.items():
            scores = R.predict(policy, R.extract_features(policy, prompt, 1))
            self.assertGreater(
                scores[dim], base[dim] + 0.1,
                f"{dim} のヘッドが自分のシグナルに反応していない",
            )

    def test_head_weight_changes_move_the_output(self):
        policy, *_ = load_all()
        prompt = "この不具合を再現して直して"
        features = R.extract_features(policy, prompt, 1)
        muted = json.loads(json.dumps(policy))
        muted["heads"]["debugging"]["weights"]["lex.debugging"] = 0.0
        self.assertGreater(
            R.predict(policy, features)["debugging"],
            R.predict(muted, features)["debugging"],
        )

    def test_tier_floor_does_not_inflate_confidence(self):
        policy, *_ = load_all()
        raw = {dim: 0.1 for dim in policy["dimensions"]}
        floored, floor = R.apply_tier(policy, raw, "T2", measured=False)
        self.assertGreater(max(floored.values()), max(raw.values()))
        self.assertGreater(floor, policy["gamma_sticky"])

    def test_length_is_measured_in_ascii_equivalents(self):
        weight = 2.5
        self.assertEqual(R.effective_length("abc", weight), 3)
        self.assertAlmostEqual(R.effective_length("あいう", weight), 7.5)

    def test_debugging_signal_lifts_the_debugging_head(self):
        policy, *_ = load_all()
        plain = R.predict(policy, R.extract_features(policy, "この関数の説明をして", 1))
        buggy = R.predict(
            policy,
            R.extract_features(policy, "この関数が例外でクラッシュする。再現手順を切り分けて直して", 1),
        )
        self.assertGreater(buggy["debugging"], plain["debugging"])

    def test_lexicon_terms_compile(self):
        policy, *_ = load_all()
        for dim in policy["dimensions"]:
            terms = policy["lexicon"][dim]
            self.assertTrue(terms)
            R.compile_lexicon(terms)

    def test_ascii_terms_do_not_match_inside_words(self):
        rx = R.compile_lexicon(["fix"])
        self.assertTrue(rx.search("please fix it"))
        self.assertFalse(rx.search("prefixes"))


class CostCeilingTest(unittest.TestCase):
    """VS Code は親のコスト階層を超える子を起動しない。起動しない委譲を撃たない。"""

    def test_ceiling_uses_the_model_tier_not_the_effort_price(self):
        """effort 込みの実額で比べると、親より上位のモデルの低 effort 変種が通ってしまう。"""
        _, _, _, pool, _, _ = load_all()
        sonnet_base = next(e.base_cost for e in pool if e.model == "claude-sonnet-5")
        survivors, gates = R.prefilter(
            pool, long_context=False, exclude_families=set(),
            cost_ceiling=sonnet_base,
        )
        models = {entry.model for entry in survivors}
        self.assertIn("claude-sonnet-5", models)
        self.assertNotIn("gpt-5.6-sol", models, "上位階層のモデルが残ってはいけない")
        self.assertNotIn("claude-opus-5", models)
        self.assertTrue(any(gate.startswith("tier<=") for gate in gates))

    def test_ceiling_allows_expensive_efforts_of_an_allowed_model(self):
        _, _, _, pool, _, _ = load_all()
        sonnet_base = next(e.base_cost for e in pool if e.model == "claude-sonnet-5")
        survivors, _ = R.prefilter(
            pool, long_context=False, exclude_families=set(),
            cost_ceiling=sonnet_base,
        )
        efforts = {entry.effort for entry in survivors if entry.model == "claude-sonnet-5"}
        self.assertIn("max", efforts, "同じ階層なら高い effort は使える")

    def test_binding_ceiling_is_reported(self):
        """品質の頭打ちが静かに起きるのを防ぐ。返答からは見えない失敗になるため。"""
        policy, _, _, pool, _, weights = load_all()
        cheap = min(entry.base_cost for entry in pool)
        decision = R.decide(
            policy=policy, pool=pool, prompt="設計の妥当性を厳密に検証して", role="verifier",
            turn=1, tau=policy["roles"]["verifier"]["tau"], weights=weights, tier_mode="auto",
            long_context=False, exclude_families=set(), current_model=None,
            reroute_triggered=True, override_requirements=None, cost_ceiling=cheap,
        )
        self.assertTrue(
            any("頭打ち" in note for note in decision.notes),
            f"上限が効いたのに黙っている: {decision.notes}",
        )

    def test_ceiling_that_does_not_degrade_quality_is_silent(self):
        """上限超の候補が適格でも、選ばれないなら害はない。警告を出すと狼少年になる。"""
        policy, _, _, pool, _, weights = load_all()
        sonnet_base = next(e.base_cost for e in pool if e.model == "claude-sonnet-5")
        decision = R.decide(
            policy=policy, pool=pool, prompt="定数の定義箇所を探して", role="retriever",
            turn=1, tau=policy["roles"]["retriever"]["tau"], weights=weights, tier_mode="auto",
            long_context=False, exclude_families=set(), current_model=None,
            reroute_triggered=True, override_requirements=None, cost_ceiling=sonnet_base,
        )
        # 上限内の最安が選べている以上、上限は選択を変えていない
        self.assertEqual(decision.selected.model, "claude-haiku-4.5")
        self.assertFalse(
            any("頭打ち" in note for note in decision.notes),
            f"害のない上限で警告している: {decision.notes}",
        )

    def test_cli_ignores_the_parent_cost(self):
        cli = json.loads(run_cli(
            "--role", "verifier", "--prompt", "検証して", "--surface", "cli",
            "--parent-model", "claude-haiku-4.5", "--json",
        ).stdout)
        self.assertIsNone(cli["cost_ceiling"])

    def test_vscode_applies_the_parent_cost(self):
        vscode = json.loads(run_cli(
            "--role", "verifier", "--prompt", "検証して", "--surface", "vscode",
            "--parent-model", "claude-sonnet-5", "--json",
        ).stdout)
        self.assertIsNotNone(vscode["cost_ceiling"])
        self.assertLessEqual(vscode["cost"], vscode["cost_ceiling"] * 2.5 + 1e-9)
        self.assertEqual(vscode["model"], "claude-sonnet-5")

    def test_unusable_parent_model_fails_closed(self):
        result = run_cli(
            "--role", "analyst", "--prompt", "x", "--surface", "vscode",
            "--parent-model", "gpt-5.6-terra",
        )
        self.assertEqual(result.returncode, 4)


class DelegationEconomicsTest(unittest.TestCase):
    """委譲の損得は読む量だけでなく単価でも決まる。"""

    def test_scope_price_ratio_lowers_the_delegation_bar(self):
        """高い親から安い子へ移す裁定が、経路判定に現れること。"""
        scope = SKILL_ROOT / "scripts" / "scope.py"
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp)
            for index in range(6):
                (target / f"m{index}.py").write_text(
                    "def f():\n" + "    x = 1  # padding\n" * 120, encoding="utf-8"
                )
            blind = subprocess.run(
                [sys.executable, str(scope), "--turns", "25", "--json", str(target)],
                capture_output=True, text=True,
            )
            aware = subprocess.run(
                [sys.executable, str(scope), "--turns", "25", "--json",
                 "--parent-model", "gpt-5.6-sol:max", "--agent-model", "claude-haiku-4.5",
                 str(target)],
                capture_output=True, text=True,
            )
        self.assertEqual(blind.returncode, 0, blind.stderr)
        self.assertEqual(aware.returncode, 0, aware.stderr)
        blind_data, aware_data = json.loads(blind.stdout), json.loads(aware.stdout)
        self.assertEqual(blind_data["price_ratio"], 1.0)
        self.assertLess(aware_data["price_ratio"], 0.1)
        self.assertEqual(blind_data["route"], "INLINE_NARROW")
        self.assertEqual(
            aware_data["route"], "SUBAGENT",
            "単価差があるのにインラインのままなら、裁定が判定に効いていない",
        )

    def test_scope_rejects_an_unknown_model(self):
        scope = SKILL_ROOT / "scripts" / "scope.py"
        result = subprocess.run(
            [sys.executable, str(scope), "--parent-model", "gpt-nonexistent", str(SAMPLES_PATH)],
            capture_output=True, text=True,
        )
        self.assertNotEqual(result.returncode, 0)

    def test_scope_rejects_a_model_without_cost(self):
        scope = SKILL_ROOT / "scripts" / "scope.py"
        result = subprocess.run(
            [sys.executable, str(scope), "--parent-model", "gpt-5.6-terra", str(SAMPLES_PATH)],
            capture_output=True, text=True,
        )
        self.assertNotEqual(result.returncode, 0)

    def test_scope_rejects_an_effort_the_model_does_not_support(self):
        """haiku は reasoning effort 非対応。黙って倍率を掛けると単価がずれる。"""
        scope = SKILL_ROOT / "scripts" / "scope.py"
        result = subprocess.run(
            [sys.executable, str(scope), "--parent-model", "claude-haiku-4.5:high",
             str(SAMPLES_PATH)],
            capture_output=True, text=True,
        )
        self.assertNotEqual(result.returncode, 0)

    def test_shipped_bands_match_the_sample_set(self):
        """設定の band が予測分布からずれていないこと。ずれると要件と能力が別尺度に乗る。"""
        policy, *_ = load_all()
        prompts = [
            json.loads(line)["prompt"]
            for line in SAMPLES_PATH.read_text(encoding="utf-8").splitlines() if line.strip()
        ]
        fitted = R.fit_bands(policy, prompts, 0.10, 0.90)
        for dim, band in fitted.items():
            self.assertAlmostEqual(policy["bands"][dim][0], band[0], places=2, msg=dim)
            self.assertAlmostEqual(policy["bands"][dim][1], band[1], places=2, msg=dim)

    def test_percentile_interpolates(self):
        self.assertEqual(R.percentile([0.0, 1.0], 0.5), 0.5)
        self.assertEqual(R.percentile([5.0], 0.9), 5.0)


class EvaluationTest(unittest.TestCase):
    """Algorithm 3 — QR / CS / Misroute。削減が壊れたときに気づく手段。"""

    def metrics(self, records):
        policy, _, _, pool, _, weights = load_all()

        def run(prompt, role, turn):
            return R.decide(
                policy=policy, pool=pool, prompt=prompt, role=role, turn=turn,
                tau=policy["roles"][role]["tau"], weights=weights, tier_mode="auto",
                long_context=False, exclude_families=set(),
                current_model=None, reroute_triggered=True,
                override_requirements=None,
            ), pool

        return R.evaluate(records, run)

    def test_perfect_router_scores_full_retention(self):
        records = [{
            "prompt": "parse_config の呼び出し元を一覧で出して",
            "role": "retriever",
            "resolved_by": ["claude-haiku-4.5", "gpt-5.6-sol", "claude-opus-5"],
        }]
        result = self.metrics(records)
        self.assertEqual(result["quality_retention"], 100.0)
        self.assertEqual(result["misroute_rate"], 0.0)
        self.assertGreater(result["cost_savings"], 0.0)

    def test_misroute_counts_overpaying(self):
        records = [{
            "prompt": "認証トークンの更新方式を設計して、トレードオフを根拠つきで説明して",
            "role": "analyst",
            "resolved_by": ["claude-haiku-4.5"],
        }]
        result = self.metrics(records)
        self.assertEqual(result["misroute_rate"], 100.0)

    def test_unresolvable_queries_are_excluded_from_retention(self):
        records = [{"prompt": "何かして", "role": "analyst", "resolved_by": []}]
        result = self.metrics(records)
        self.assertEqual(result["oracle_resolvable"], 0)
        self.assertIsNone(result["quality_retention"])

    def test_misroute_denominator_matches_quality_retention(self):
        """解決不能なクエリを分母に入れると Misroute が過小に出る。"""
        overpaid = {
            "prompt": "認証トークンの更新方式を設計して、トレードオフを根拠つきで説明して",
            "role": "analyst",
            "resolved_by": ["claude-haiku-4.5"],
        }
        alone = self.metrics([overpaid])
        padded = self.metrics([overpaid, {"prompt": "何かして", "role": "analyst", "resolved_by": []}])
        self.assertEqual(alone["misroute_rate"], 100.0)
        self.assertEqual(padded["misroute_rate"], alone["misroute_rate"])
        self.assertEqual(padded["oracle_resolvable"], 1)
        self.assertEqual(padded["queries"], 2)

    def test_misroute_compares_the_cost_actually_paid(self):
        """同じモデルの安い effort で足りたなら、それも払いすぎとして数える。"""
        policy, _, _, pool, _, weights = load_all()
        prompt = "この設計の妥当性を根拠つきで検証して"
        decision = R.decide(
            policy=policy, pool=pool, prompt=prompt, role="verifier", turn=1,
            tau=policy["roles"]["verifier"]["tau"], weights=weights, tier_mode="auto",
            long_context=False, exclude_families=set(),
            current_model=None, reroute_triggered=True, override_requirements=None,
        )
        selected = decision.selected
        cheapest_same_model = min(
            (entry.cost for entry in pool if entry.model == selected.model),
        )
        self.assertLess(
            cheapest_same_model, selected.cost,
            "この検証には、選択より安い effort を持つモデルが選ばれている必要がある",
        )
        result = self.metrics([{
            "prompt": prompt, "role": "verifier", "resolved_by": [selected.model],
        }])
        # 同じモデルなので品質は保てている。それでも安い effort で足りたぶんは払いすぎになる。
        self.assertEqual(result["quality_retention"], 100.0)
        self.assertEqual(result["misroute_rate"], 100.0)


class CliTest(unittest.TestCase):
    def test_text_output_names_a_model(self):
        result = run_cli("--role", "analyst", "--prompt", "この関数の仕様を説明して")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("model=", result.stdout)
        self.assertIn("req:", result.stdout)

    def test_json_output_is_parseable_and_explains_itself(self):
        result = run_cli("--role", "retriever", "--prompt", "定数の定義箇所を探して", "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        for key in ("model", "requirements", "shortfall", "basis", "table", "gates"):
            self.assertIn(key, payload)
        self.assertTrue(payload["table"])

    def test_unknown_role_is_rejected(self):
        result = run_cli("--role", "nope", "--prompt", "x")
        self.assertEqual(result.returncode, 2)

    def test_selected_model_is_always_allowlisted(self):
        allowed = R.load_allowlist(ALLOWLIST_PATH)
        for role in ("retriever", "analyst", "implementer", "verifier", "orchestrator"):
            result = run_cli("--role", role, "--prompt", "落ちる原因を調べて直して", "--json")
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn(json.loads(result.stdout)["model"], allowed)

    def test_allowlist_path_cannot_be_overridden(self):
        """許可リストの参照先を差し替えられると veto が veto でなくなる。"""
        result = run_cli("--role", "analyst", "--prompt", "x", "--allowlist", "/tmp/rogue.md")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--allowlist", result.stderr)

    def test_output_is_reproducible(self):
        args = ("--role", "analyst", "--prompt", "認証まわりの設計を見直したい", "--json")
        self.assertEqual(run_cli(*args).stdout, run_cli(*args).stdout)

    def test_eval_reports_all_three_metrics(self):
        records = [
            {"prompt": "parse_config の呼び出し元を一覧で出して", "role": "retriever",
             "resolved_by": ["claude-haiku-4.5", "gpt-5.6-sol"]},
            {"prompt": "起動時にクラッシュする。原因を特定して直して", "role": "analyst",
             "resolved_by": ["gpt-5.6-sol"]},
        ]
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "history.jsonl"
            path.write_text(
                "\n".join(json.dumps(record, ensure_ascii=False) for record in records),
                encoding="utf-8",
            )
            result = run_cli("--eval", str(path), "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        metrics = json.loads(result.stdout)
        self.assertEqual(metrics["queries"], 2)
        self.assertEqual(metrics["oracle_resolvable"], 2)
        for key in ("quality_retention", "cost_savings", "misroute_rate"):
            self.assertIsNotNone(metrics[key], key)
            self.assertGreaterEqual(metrics[key], 0.0)
            self.assertLessEqual(metrics[key], 100.0)

    def test_raising_tau_never_lowers_cost_savings(self):
        """tau を緩めるとコスト削減は増える方向にしか動かない。"""
        records = [
            {"prompt": "認証方式のトレードオフを整理して方針を示して", "role": "analyst",
             "resolved_by": ["gpt-5.6-sol"]},
            {"prompt": "クラッシュの原因を特定して直して", "role": "analyst",
             "resolved_by": ["gpt-5.6-sol"]},
        ]
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "history.jsonl"
            path.write_text(
                "\n".join(json.dumps(record, ensure_ascii=False) for record in records),
                encoding="utf-8",
            )
            savings = []
            for scale in ("0.25", "1", "4", "16"):
                result = run_cli("--eval", str(path), "--tau-scale", scale, "--json")
                self.assertEqual(result.returncode, 0, result.stderr)
                savings.append(json.loads(result.stdout)["cost_savings"])
        for lower, higher in zip(savings, savings[1:]):
            self.assertGreaterEqual(higher, lower - 1e-9)

    def test_peer_exclusion_changes_the_family(self):
        first = json.loads(run_cli(
            "--role", "verifier", "--prompt", "設計の妥当性を検証して", "--json",
        ).stdout)
        second = json.loads(run_cli(
            "--role", "verifier", "--prompt", "設計の妥当性を検証して",
            "--peer", first["model"], "--json",
        ).stdout)
        self.assertNotEqual(first["model"].split("-")[0], second["model"].split("-")[0])

    def test_scope_json_relaxes_the_tier_floor(self):
        prompt = "なんでこのテスト落ちるの?"
        strict = json.loads(run_cli("--role", "analyst", "--prompt", prompt, "--json").stdout)
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "scope.json"
            path.write_text(json.dumps({"route": "INLINE_NARROW", "expected_agents": 1}),
                            encoding="utf-8")
            measured = json.loads(run_cli(
                "--role", "analyst", "--prompt", prompt, "--scope-json", str(path), "--json",
            ).stdout)
        self.assertLess(measured["tier_floor"], strict["tier_floor"])

    def test_no_match_scope_keeps_the_conservative_floor(self):
        prompt = "なんでこのテスト落ちるの?"
        strict = json.loads(run_cli("--role", "analyst", "--prompt", prompt, "--json").stdout)
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "scope.json"
            path.write_text(json.dumps({"route": "NO_MATCH"}), encoding="utf-8")
            unmeasured = json.loads(run_cli(
                "--role", "analyst", "--prompt", prompt, "--scope-json", str(path), "--json",
            ).stdout)
        self.assertEqual(unmeasured["tier_floor"], strict["tier_floor"])

    def test_requirements_override_bypasses_the_predictor(self):
        result = json.loads(run_cli(
            "--role", "analyst", "--prompt", "x", "--tier", "T3",
            "--requirements", "0.0,0.0,0.0,0.0", "--json",
        ).stdout)
        self.assertEqual(set(result["requirements_raw"].values()), {0.0})

    def test_invalid_requirements_are_rejected(self):
        result = run_cli("--role", "analyst", "--prompt", "x", "--requirements", "2,0,0,0")
        self.assertEqual(result.returncode, 2)

    def test_fit_bands_reports_every_dimension(self):
        result = run_cli("--fit-bands", str(SAMPLES_PATH), "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        policy = R.load_json(POLICY_PATH, "policy")
        self.assertEqual(set(payload["bands"]), set(policy["dimensions"]))


if __name__ == "__main__":
    unittest.main()
