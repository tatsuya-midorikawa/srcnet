#!/usr/bin/env python3
"""lean-exec capability router.

「何を要求するタスクか」を予測し、「それを満たす最安の実行体」を選ぶ。
HyDRA (arXiv:2605.17106) の二部構成をそのまま持ち込んでいる。学習側は決定的な
線形近似に置き換え、選択側 (プロファイル + shortfall matching) は論文どおり設定駆動にした。

  1. 要件予測   r_k in [0,1] (K=4: reasoning / code_gen / debugging / tool_use) + 確信度 gamma
  2. プール構築 config/models.json の生アンカーをプール相対で band へ正規化 (Eq. 3)
  3. ハード ゲート 許可リスト veto (fail-closed) / long context / 系統除外
  4. shortfall    s_m = sum_k w~_k * max(0, r_k - c_mk)   (Eq. 5)
  5. 選択        s_m <= tau の中で最安。空なら最小 shortfall へ fail-open

LLM を一切呼ばない。判定はこのプロセス内で閉じるので、経路選択そのものが課金されない。

prompt cache はモデル単位で持たれる。会話の途中では現在のモデルを保ち、effort だけ選び直す。
選び直すのは turn 1 / --after-compact / --after-summarize / --force-route のときだけ。

設定と許可リストの参照先はすべてコード内に固定してある。差し替えられる余地を残すと
veto が veto でなくなるため、カタログに何を書いても許可リスト外のモデルは除外される。

引数の一覧は --help を見る。終了コードは 0 (判定完了) / 2 (引数か設定が不正) /
4 (ハード ゲートで候補が消えた。fail-closed)。
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

SKILL_ROOT = Path(__file__).resolve().parents[1]
MODELS = SKILL_ROOT / "config" / "models.json"
POLICY = SKILL_ROOT / "config" / "policy.json"
ALLOWLIST = SKILL_ROOT.parents[1] / "instructions" / "strict-rules.instructions.md"

ALLOWED_DISPLAY_NAME = re.compile(r"`((?:GPT|Claude)[^`]+)`", re.IGNORECASE)
ASCII_ONLY = re.compile(r"^[\x00-\x7f]+$")


class ConfigError(Exception):
    """設定が壊れている。推測で埋めずに落とす。"""


class GateError(Exception):
    """ハード ゲートで候補が消えた。fail-closed で落とす。"""


@dataclass(frozen=True)
class PoolEntry:
    model: str
    effort: str
    cost: float
    base_cost: float
    capability: dict[str, float]
    family: str
    context_tiers: tuple[str, ...]
    calibrated: bool

    @property
    def key(self) -> str:
        return self.model if self.effort == "none" else f"{self.model}:{self.effort}"


@dataclass
class Decision:
    role: str
    tau: float
    requirements: dict[str, float]
    raw_requirements: dict[str, float]
    gamma: float
    tier: str
    tier_floor: float
    weights: dict[str, float]
    selected: PoolEntry | None
    shortfall: float
    basis: str
    eligible: list[str]
    table: list[dict] = field(default_factory=list)
    gates: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)


# --------------------------------------------------------------------------
# 設定の読み込み
# --------------------------------------------------------------------------

def load_json(path: Path, what: str) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ConfigError(f"{what} が見つかりません: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ConfigError(f"{what} を JSON として読めません: {path} ({exc})") from exc


def model_slug(display_name: str) -> str:
    return re.sub(r"\s+", "-", display_name.strip().lower())


def load_allowlist(path: Path) -> set[str]:
    """許可リストは veto であって reranker ではない。読めなければ fail-closed。"""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        raise GateError(
            f"許可リストを読めません: {path}。"
            "許可リストを確認できない状態でモデルを選ばない (fail-closed)"
        ) from exc
    allowed = {model_slug(name) for name in ALLOWED_DISPLAY_NAME.findall(text)}
    if not allowed:
        raise GateError(f"許可リストにモデルが 1 つも見つかりません: {path}")
    return allowed


# --------------------------------------------------------------------------
# プール構築 — Eq. 3 / Eq. 4
# --------------------------------------------------------------------------

def band_widths(policy: dict) -> dict[str, float]:
    widths = {}
    for dim in policy["dimensions"]:
        lo, hi = policy["bands"][dim]
        width = hi - lo
        if width <= 0:
            raise ConfigError(f"band が不正です: {dim}={policy['bands'][dim]}")
        widths[dim] = width
    return widths


def compensated_weights(policy: dict, overrides: dict[str, float]) -> dict[str, float]:
    """Eq. 4。band 幅の差で操作者の意図がゆがまないよう、重みを幅の逆数で補正する。"""
    dims = policy["dimensions"]
    widths = band_widths(policy)
    raw = {dim: float(overrides.get(dim, policy["weights"][dim])) for dim in dims}
    if any(value < 0 for value in raw.values()):
        raise ConfigError("次元重みに負の値は使えません")
    total = sum(raw.values())
    if total <= 0:
        raise ConfigError("次元重みの合計が 0 です")
    scaled = {dim: raw[dim] / widths[dim] for dim in dims}
    denom = sum(scaled.values())
    return {dim: scaled[dim] / denom * total for dim in dims}


def build_pool(models_cfg: dict, policy: dict, allowed: set[str]) -> tuple[list[PoolEntry], list[str]]:
    """生アンカーをプール相対で band へ写像し、model x effort の候補集合を作る (Eq. 3)。"""
    dims = policy["dimensions"]
    widths = band_widths(policy)
    variants = models_cfg["effort_variants"]

    usable: dict[str, dict] = {}
    vetoed: list[str] = []
    for name, spec in models_cfg["models"].items():
        if name not in allowed:
            vetoed.append(name)
            continue
        if not spec.get("enabled"):
            continue
        if spec.get("cost") is None or not spec.get("capability"):
            continue
        missing = [dim for dim in dims if dim not in spec["capability"]]
        if missing:
            raise ConfigError(f"{name} の capability に {missing} がありません")
        usable[name] = spec

    pool: list[PoolEntry] = []
    if not usable:
        return pool, vetoed

    for dim in dims:
        lo, hi = policy["bands"][dim]
        raws = [float(spec["capability"][dim]) for spec in usable.values()]
        raw_lo, raw_hi = min(raws), max(raws)
        for spec in usable.values():
            span = raw_hi - raw_lo
            if span <= 0:
                # 全モデルが同点なら能力差はない。最安が勝つよう全員を上端へ置く。
                value = hi
            else:
                value = lo + (float(spec["capability"][dim]) - raw_lo) / span * (hi - lo)
            spec.setdefault("_normalized", {})[dim] = value

    for name, spec in usable.items():
        efforts = spec.get("efforts") or ["none"]
        for effort in efforts:
            if effort not in variants:
                raise ConfigError(f"{name} が未定義の effort を参照しています: {effort}")
            variant = variants[effort]
            capability = {}
            for dim in dims:
                shifted = spec["_normalized"][dim] + variant["band_delta"] * widths[dim]
                capability[dim] = min(1.0, max(0.0, shifted))
            pool.append(PoolEntry(
                model=name,
                effort=effort,
                cost=round(float(spec["cost"]) * float(variant["cost_multiplier"]), 4),
                base_cost=float(spec["cost"]),
                capability=capability,
                family=name.split("-")[0],
                context_tiers=tuple(spec.get("context_tiers") or ["default"]),
                calibrated=bool(spec.get("calibrated")),
            ))
    return pool, vetoed


# --------------------------------------------------------------------------
# 要件予測 — 決定的なシグナル抽出 + K 個の独立ヘッド
# --------------------------------------------------------------------------

def compile_lexicon(terms: list[str]) -> re.Pattern[str]:
    parts = []
    for term in terms:
        parts.append(rf"\b{term}\b" if ASCII_ONLY.match(term) else term)
    return re.compile("|".join(f"(?:{part})" for part in parts), re.IGNORECASE)


def sigmoid(z: float) -> float:
    if z >= 0:
        return 1.0 / (1.0 + math.exp(-z))
    exp_z = math.exp(z)
    return exp_z / (1.0 + exp_z)


def turn_bin(policy: dict, turn: int) -> float:
    for entry in policy["turn_bins"]:
        limit = entry["max_turn"]
        if limit is None or turn <= limit:
            return float(entry["value"])
    return 1.0


def effective_length(text: str, non_ascii_weight: float) -> float:
    """長さを ascii 換算で測る。

    生の文字数で測ると、同じ内容の日本語が英語より短く見え、長さ由来の特徴だけで
    要件スコアがずれる。HyDRA §5.1 が要求する言語不変性はここで壊れる。
    """
    non_ascii = sum(1 for char in text if ord(char) > 127)
    return (len(text) - non_ascii) + non_ascii * non_ascii_weight


def extract_features(policy: dict, prompt: str, turn: int) -> dict[str, float]:
    """HyDRA の 7 フラグ signal prefix に相当する決定的特徴。LLM は呼ばない。"""
    features: dict[str, float] = {}
    for name, pattern in policy["signals"].items():
        features[f"sig.{name}"] = 1.0 if re.search(pattern, prompt, re.IGNORECASE | re.MULTILINE) else 0.0

    length = effective_length(prompt.strip(), float(policy.get("non_ascii_weight", 1.0)))
    short = policy["short_message_chars"]
    long_chars = policy["long_message_chars"]
    features["sig.short"] = 1.0 if length < short else 0.0
    features["sig.turns"] = turn_bin(policy, turn)
    features["len.long"] = min(1.0, max(0.0, (length - short) / max(1, long_chars - short)))

    saturation = max(1, int(policy["lexicon_saturation"]))
    for dim in policy["dimensions"]:
        rx = compile_lexicon(policy["lexicon"][dim])
        hits = len(rx.findall(prompt))
        features[f"lex.{dim}"] = min(hits, saturation) / saturation
    return features


def predict(policy: dict, features: dict[str, float]) -> dict[str, float]:
    scores = {}
    for dim in policy["dimensions"]:
        head = policy["heads"][dim]
        z = float(head["bias"])
        for name, weight in head["weights"].items():
            z += float(weight) * features.get(name, 0.0)
        scores[dim] = round(sigmoid(z), 4)
    return scores


def detect_tier(policy: dict, prompt: str, features: dict[str, float]) -> str:
    """リポジトリ状態への依存度で分ける (HyDRA §4.1)。測れない依存ほど強い側へ寄せる。"""
    if re.search(policy["repo_dependence"], prompt, re.IGNORECASE | re.MULTILINE):
        return "T1" if features.get("sig.file") else "T2"
    if features.get("sig.file"):
        return "T1"
    return "T3"


def apply_tier(
    policy: dict, scores: dict[str, float], tier: str, measured: bool,
) -> tuple[dict[str, float], float]:
    spec = policy["tiers"][tier]
    floor = float(spec["floor_measured"] if measured and "floor_measured" in spec else spec["floor"])
    if floor <= 0:
        return dict(scores), floor
    return {dim: max(value, floor) for dim, value in scores.items()}, floor


def resolve_bound(policy: dict, dim: str, value) -> float:
    """floor / cap に band 端を記号で書けるようにする。band を測り直せば役割も追随する。"""
    if isinstance(value, str):
        lo, hi = policy["bands"][dim]
        if value == "band_hi":
            return float(hi)
        if value == "band_lo":
            return float(lo)
        raise ConfigError(f"未知の境界指定です: {value!r} (band_hi / band_lo / 数値)")
    return float(value)


def apply_role(policy: dict, scores: dict[str, float], role: str) -> dict[str, float]:
    spec = policy["roles"][role]
    out = {}
    for dim, value in scores.items():
        scale = float(spec.get("scale", {}).get(dim, 1.0))
        if scale > 1.0:
            raise ConfigError(
                f"role {role} の scale.{dim}={scale} が 1.0 を超えています。"
                "予測済みの要件を役割都合で水増しすると二重計上になる。下限は floor で表す"
            )
        value *= scale
        value = max(value, resolve_bound(policy, dim, spec.get("floor", {}).get(dim, 0.0)))
        value = min(value, resolve_bound(policy, dim, spec.get("cap", {}).get(dim, 1.0)))
        out[dim] = round(min(1.0, max(0.0, value)), 4)
    return out


# --------------------------------------------------------------------------
# ハード ゲートと shortfall matching — Algorithm 2
# --------------------------------------------------------------------------

def prefilter(
    pool: list[PoolEntry],
    *,
    long_context: bool,
    exclude_families: set[str],
    cost_ceiling: float | None = None,
) -> tuple[list[PoolEntry], list[str]]:
    gates: list[str] = ["allowlist"]
    survivors = pool
    if long_context:
        gates.append("long_context")
        survivors = [entry for entry in survivors if "long_context" in entry.context_tiers]
    if exclude_families:
        gates.append(f"family!={'/'.join(sorted(exclude_families))}")
        survivors = [entry for entry in survivors if entry.family not in exclude_families]
    if cost_ceiling is not None:
        # VS Code の制約は「親のコスト階層を超えられない」であって、effort 込みの実額ではない。
        # 階層はモデル単位なので base_cost で比べる。実額で比べると、親より上位のモデルの
        # 低 effort 変種が通ってしまい、起動しない委譲を撃つことになる。
        gates.append(f"tier<={cost_ceiling:g}")
        survivors = [entry for entry in survivors if entry.base_cost <= cost_ceiling + 1e-9]
    return survivors, gates


def shortfall(entry: PoolEntry, requirements: dict[str, float], weights: dict[str, float]) -> float:
    """Eq. 5。max(0, .) なので、ある次元の余剰は別の次元の不足を埋め合わせない。"""
    total = 0.0
    for dim, need in requirements.items():
        total += weights[dim] * max(0.0, need - entry.capability[dim])
    return round(total, 6)


def match(
    pool: list[PoolEntry],
    requirements: dict[str, float],
    weights: dict[str, float],
    tau: float,
) -> tuple[PoolEntry, float, str, list[str], list[dict]]:
    scored = [(entry, shortfall(entry, requirements, weights)) for entry in pool]
    scored.sort(key=lambda item: (item[0].cost, item[1], item[0].key))
    eligible = [(entry, value) for entry, value in scored if value <= tau]
    if eligible:
        selected, value = min(eligible, key=lambda item: (item[0].cost, item[1], item[0].key))
        basis = "cheapest_eligible"
    else:
        # 適格な候補がないときは最小 shortfall へ fail-open する (Algorithm 2 line 12)。
        # 同点はコストで割る。tau を緩めても選択が高くならないこと (フロンティアの単調性) は
        # この定義に依存している。近傍を許容する緩和を入れると単調性が崩れる。
        selected, value = min(scored, key=lambda item: (item[1], item[0].cost, item[0].key))
        basis = "fail_open_least_shortfall"
    table = [
        {
            "model": entry.key,
            "shortfall": value,
            "cost": entry.cost,
            "eligible": value <= tau,
            "selected": entry.key == selected.key,
        }
        for entry, value in scored
    ]
    return selected, value, basis, [entry.key for entry, _ in eligible], table


# --------------------------------------------------------------------------
# 判定本体
# --------------------------------------------------------------------------

def decide(
    *,
    policy: dict,
    pool: list[PoolEntry],
    prompt: str,
    role: str,
    turn: int,
    tau: float,
    weights: dict[str, float],
    tier_mode: str,
    long_context: bool,
    exclude_families: set[str],
    current_model: str | None,
    reroute_triggered: bool,
    override_requirements: dict[str, float] | None,
    context_measured: bool = False,
    force: bool = False,
    cost_ceiling: float | None = None,
) -> Decision:
    features = extract_features(policy, prompt, turn)
    if override_requirements is not None:
        raw_scores = dict(override_requirements)
    else:
        raw_scores = predict(policy, features)

    tier = tier_mode if tier_mode != "auto" else detect_tier(policy, prompt, features)
    floored, tier_floor = apply_tier(policy, raw_scores, tier, context_measured)
    requirements = apply_role(policy, floored, role)
    # gamma は予測の確信度なので、tier の下限を掛ける前の生スコアから取る。
    # floored から取ると T2 の下限 (0.7) が常に閾値を超え、確信度による保持が届かなくなる。
    gamma = round(max(raw_scores.values()), 4)

    survivors, gates = prefilter(
        pool, long_context=long_context,
        exclude_families=exclude_families, cost_ceiling=cost_ceiling,
    )
    if not survivors:
        raise GateError(
            "ハード ゲート通過後の候補が 0 件。"
            f"gates={'+'.join(gates)}。代替を推測せず、条件を見直す (fail-closed)"
        )

    decision = Decision(
        role=role,
        tau=tau,
        requirements=requirements,
        raw_requirements=raw_scores,
        gamma=gamma,
        tier=tier,
        tier_floor=tier_floor,
        weights=weights,
        selected=None,
        shortfall=0.0,
        basis="",
        eligible=[],
        gates=gates,
    )

    held = None
    if current_model:
        for entry in survivors:
            if entry.model == current_model:
                held = entry
                break

    def hold(basis: str, note: str) -> Decision:
        # prompt cache はモデル単位で持たれる。effort を変えてもキャッシュは切れないので、
        # モデルだけ固定して effort は選び直す。
        same_model = [e for e in survivors if e.model == held.model]
        chosen, value, _, eligible, table = match(same_model, requirements, weights, tau)
        decision.selected = chosen
        decision.shortfall = value
        decision.basis = basis
        decision.eligible = eligible
        decision.table = table
        decision.notes.append(note)
        return decision

    role_sticky = bool(policy["roles"][role].get("sticky"))
    gated_out = current_model is not None and held is None
    if held is not None and not role_sticky:
        decision.notes.append(
            f"role {role} は sticky ではないため --current-model を無視した。"
            "サブエージェントは毎回新しい文脈で立ち上がるので、守るべき prompt cache が無い"
        )
        held = None

    if held is not None and not reroute_triggered:
        return hold(
            "sticky_prompt_cache",
            "prompt cache を守るためモデルは替えない (effort だけ選び直す)。"
            "turn 1 / --after-compact / --after-summarize / --force-route のときだけ選び直す",
        )

    if held is not None and not force and gamma < float(policy["gamma_sticky"]):
        return hold(
            "sticky_low_confidence",
            f"確信度 gamma={gamma} が閾値 {policy['gamma_sticky']} 未満。"
            "根拠の薄い切り替えでキャッシュを捨てない。--force-route で上書きできる",
        )

    selected, value, basis, eligible, table = match(survivors, requirements, weights, tau)
    decision.selected = selected
    decision.shortfall = value
    decision.basis = basis
    decision.eligible = eligible
    decision.table = table
    if cost_ceiling is not None:
        # 上限が「効いた」かどうかは、上限が無かったときの選択と比べて決める。
        # 単に上限超の候補が適格だった、では足りない。それらは高いので元から選ばれない。
        # 品質が実際に頭打ちになったとき (被覆がより良い候補を落としたとき) だけ警告する。
        unrestricted, _ = prefilter(
            pool, long_context=long_context,
            exclude_families=exclude_families, cost_ceiling=None,
        )
        if unrestricted:
            best, best_shortfall, _, _, _ = match(unrestricted, requirements, weights, tau)
            if best_shortfall < value - 1e-9:
                decision.notes.append(
                    f"親のコスト階層 ({cost_ceiling:g}) で品質が頭打ちになっている。"
                    f"上限が無ければ {best.key} (shortfall {best_shortfall:.3f}) を選べたが、"
                    f"上限内では {selected.key} (shortfall {value:.3f}) が限界。"
                    "親を上げるか、この役割だけ親自身で実行する"
                )
    if basis == "fail_open_least_shortfall":
        decision.notes.append(
            f"tau={tau} を満たす候補がない。最小 shortfall へ fail-open した。"
            "要件が高すぎるか、プールに能力が足りない"
        )
    if gated_out and not reroute_triggered:
        decision.notes.append(
            f"現在のモデル {current_model} はゲートを通らないため、キャッシュを捨てて選び直した"
        )
    if not selected.calibrated:
        decision.notes.append(
            "config/models.json の値は未校正の順序事前分布。"
            "--eval で QR / CS / Misroute を測ってから既定にする"
        )
    return decision


# --------------------------------------------------------------------------
# バンド当てはめ — Eq. 3 の beta_lo / beta_hi を実測で決める
# --------------------------------------------------------------------------

def percentile(values: list[float], q: float) -> float:
    if not values:
        raise ConfigError("バンドを当てはめる標本がありません")
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    position = q * (len(ordered) - 1)
    low = math.floor(position)
    high = math.ceil(position)
    if low == high:
        return ordered[low]
    return ordered[low] + (ordered[high] - ordered[low]) * (position - low)


def fit_bands(policy: dict, prompts: list[str], lo_q: float, hi_q: float) -> dict[str, list[float]]:
    """bands は要件予測器そのものの出力分布の百分位でなければならない。

    モデル能力を写像する先が予測分布とずれていると、要件と能力が別の尺度に乗る。
    論文が held-out 集合の百分位を使うのと同じ理由で、ここも実測で決める。
    """
    samples: dict[str, list[float]] = {dim: [] for dim in policy["dimensions"]}
    for prompt in prompts:
        scores = predict(policy, extract_features(policy, prompt, 1))
        for dim, value in scores.items():
            samples[dim].append(value)
    return {
        dim: [round(percentile(values, lo_q), 3), round(percentile(values, hi_q), 3)]
        for dim, values in samples.items()
    }


# --------------------------------------------------------------------------
# 評価 — Algorithm 3 (QR / CS / Misroute)
# --------------------------------------------------------------------------

def evaluate(records: list[dict], run) -> dict:
    resolved_router = 0
    oracle_total = 0
    misroutes = 0
    cost_router = 0.0
    cost_base = 0.0
    skipped = 0

    for record in records:
        prompt = record.get("prompt", "")
        role = record.get("role", "analyst")
        turn = int(record.get("turn", 1))
        resolvers = {name.split(":", 1)[0] for name in record.get("resolved_by", [])}
        try:
            decision, pool = run(prompt, role, turn)
        except GateError:
            skipped += 1
            continue
        selected = decision.selected
        assert selected is not None
        base_cost: dict[str, float] = {}
        for entry in pool:
            base_cost[entry.model] = min(base_cost.get(entry.model, entry.cost), entry.cost)
        cost_router += selected.cost
        cost_base += max(entry.cost for entry in pool)
        if not resolvers:
            continue
        oracle_total += 1
        if selected.model in resolvers:
            resolved_router += 1
        # 実際に払った額と、解決できたモデルの最安変種を比べる。resolved_by はモデル粒度でしか
        # 与えられないので、そのモデルの最安 effort でも解決できたとみなす近似が入る。
        if any(base_cost.get(name, math.inf) < selected.cost for name in resolvers):
            misroutes += 1

    total = len(records) - skipped
    return {
        "queries": total,
        "skipped": skipped,
        "oracle_resolvable": oracle_total,
        "quality_retention": round(resolved_router / oracle_total * 100, 2) if oracle_total else None,
        "cost_savings": round((1 - cost_router / cost_base) * 100, 2) if cost_base else None,
        # Misroute は「解決できたのに払いすぎた」割合なので、分母は QR と同じく
        # 解決可能だったクエリ数にする。解決不能なクエリを分母に入れると過小に出る。
        "misroute_rate": round(misroutes / oracle_total * 100, 2) if oracle_total else None,
        "router_cost": round(cost_router, 3),
        "baseline_cost": round(cost_base, 3),
    }


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def parse_requirements(raw: str, dims: list[str]) -> dict[str, float]:
    parts = [piece.strip() for piece in raw.split(",")]
    if len(parts) != len(dims):
        raise argparse.ArgumentTypeError(f"{len(dims)} 個の値をカンマ区切りで指定してください: {','.join(dims)}")
    values = {}
    for dim, piece in zip(dims, parts):
        try:
            value = float(piece)
        except ValueError as exc:
            raise argparse.ArgumentTypeError(f"{dim} の値が数値ではありません: {piece!r}") from exc
        if not 0.0 <= value <= 1.0:
            raise argparse.ArgumentTypeError(f"{dim} は 0.0-1.0 の範囲で指定してください: {value}")
        values[dim] = value
    return values


def read_jsonl(path: Path) -> list[dict]:
    try:
        raw = path.read_text(encoding="utf-8")
    except OSError as exc:
        raise ConfigError(f"JSONL を読めません: {exc}") from exc
    records = []
    for lineno, line in enumerate(raw.splitlines(), 1):
        line = line.strip()
        if not line:
            continue
        try:
            records.append(json.loads(line))
        except json.JSONDecodeError as exc:
            raise ConfigError(f"{path} の {lineno} 行目が JSON ではありません ({exc})") from exc
    return records


def read_prompt(args: argparse.ArgumentParser, parsed: argparse.Namespace) -> str:
    if parsed.prompt is not None:
        return parsed.prompt
    if parsed.prompt_file:
        try:
            return Path(parsed.prompt_file).read_text(encoding="utf-8")
        except OSError as exc:
            args.error(f"--prompt-file を読めません: {exc}")
    if not sys.stdin.isatty():
        return sys.stdin.read()
    args.error("--prompt / --prompt-file / stdin のいずれかでプロンプトを渡してください")
    return ""


def format_text(decision: Decision, scope: dict | None, pool_size: int, surface: str) -> list[str]:
    selected = decision.selected
    assert selected is not None
    dims = list(decision.requirements)
    req = " ".join(f"{dim}={decision.requirements[dim]:.2f}" for dim in dims)
    lines = [
        f"req: {req} gamma={decision.gamma:.2f} tier={decision.tier}"
        + (f" floor={decision.tier_floor:.2f}" if decision.tier_floor > 0 else ""),
        f"role={decision.role} tau={decision.tau:.3f} pool={pool_size}"
        f" surface={surface} gates={'+'.join(decision.gates)}",
    ]
    for row in decision.table:
        mark = "SELECTED" if row["selected"] else ("eligible" if row["eligible"] else "-")
        lines.append(
            f"  {row['model']:<26} s={row['shortfall']:.3f} cost={row['cost']:<7.2f} {mark}"
        )
    effort = "" if selected.effort == "none" else f" effort={selected.effort}"
    lines.append(
        f"model={selected.model}{effort} cost={selected.cost:.2f}"
        f" shortfall={decision.shortfall:.3f} basis={decision.basis}"
    )
    if scope is not None:
        lines.append(f"route={scope.get('route')} agents={scope.get('expected_agents')}")
    for note in decision.notes:
        lines.append(f"note={note}")
    return lines


def main() -> int:
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--role", default="analyst",
                    help="orchestrator / retriever / analyst / implementer / verifier")
    ap.add_argument("--prompt", default=None,
                    help="判定対象のプロンプト。省略時は stdin")
    ap.add_argument("--prompt-file", default=None,
                    help="プロンプトをファイルから読む")
    ap.add_argument("--tau", type=float, default=None,
                    help="role 既定の tau を上書きする")
    ap.add_argument("--tau-scale", type=float, default=1.0,
                    help="すべての tau を X 倍する。コスト-品質フロンティアの掃引に使う")
    ap.add_argument("--weight", action="append", default=[], metavar="DIM=X",
                    help="次元重みを上書きする (複数回指定可)")
    ap.add_argument("--requirements", default=None,
                    help="予測器を使わず要件ベクトルを直接与える (a,b,c,d)")
    ap.add_argument("--turn", type=int, default=1,
                    help="会話のターン番号。sticky 判定に使う")
    ap.add_argument("--current-model", default=None,
                    help="現在使っているモデル。sticky 判定に使う (`:effort` は無視する)")
    ap.add_argument("--after-compact", action="store_true",
                    help="/compact 直後。prompt cache は切れているので再ルーティングする")
    ap.add_argument("--after-summarize", action="store_true",
                    help="背景要約の直後。同上")
    ap.add_argument("--force-route", action="store_true",
                    help="sticky を無視して必ず再ルーティングする")
    ap.add_argument("--tier", default="auto", choices=["auto", "T1", "T2", "T3"],
                    help="コンテキスト tier を固定する")
    ap.add_argument("--long-context", action="store_true",
                    help="long context tier が要る")
    ap.add_argument("--peer", default=None,
                    help="そのモデルと同じ系統を候補から外す (verifier の独立性確保)")
    ap.add_argument("--exclude-family", action="append", default=[],
                    help="系統を直接除外する (複数回指定可)")
    ap.add_argument("--surface", default="cli", choices=["cli", "vscode"],
                    help="vscode は親のコスト階層が上限になる")
    ap.add_argument("--parent-model", default=None,
                    help="親セッションのモデル。--surface vscode でコスト上限になる (`:effort` は無視する)")
    ap.add_argument("--scope-json", default=None,
                    help="scope.py --json の出力を読む。実測済みなら T2 の floor を外す ('-' で stdin)")
    ap.add_argument("--eval", default=None,
                    help="JSONL の過去タスクで QR / CS / Misroute を測る")
    ap.add_argument("--fit-bands", default=None,
                    help="JSONL のプロンプト集合で予測分布の百分位を測り、bands を出す")
    ap.add_argument("--fit-quantiles", default="0.10,0.90",
                    help="--fit-bands で使う下側/上側の百分位")
    ap.add_argument("--json", action="store_true",
                    help="機械可読出力")
    args = ap.parse_args()

    try:
        policy = load_json(POLICY, "ポリシー")
        models_cfg = load_json(MODELS, "モデル カタログ")
        # 許可リストの参照先は差し替えられない。差し替えられると veto が veto でなくなる。
        allowed = load_allowlist(ALLOWLIST)
        pool, vetoed = build_pool(models_cfg, policy, allowed)
    except ConfigError as exc:
        print(f"error={exc}", file=sys.stderr)
        return 2
    except GateError as exc:
        print(f"error={exc}", file=sys.stderr)
        return 4

    if args.role not in policy["roles"]:
        print(f"error=未定義の role: {args.role} (定義済み: {', '.join(policy['roles'])})", file=sys.stderr)
        return 2

    if args.fit_bands:
        try:
            records = read_jsonl(Path(args.fit_bands))
            quantiles = [float(piece) for piece in args.fit_quantiles.split(",")]
            if len(quantiles) != 2 or not 0.0 <= quantiles[0] < quantiles[1] <= 1.0:
                raise ConfigError("--fit-quantiles は 0.0 <= lo < hi <= 1.0 の 2 値です")
            bands = fit_bands(
                policy,
                [record.get("prompt", "") for record in records],
                quantiles[0],
                quantiles[1],
            )
        except (ConfigError, ValueError) as exc:
            print(f"error={exc}", file=sys.stderr)
            return 2
        if args.json:
            print(json.dumps({"samples": len(records), "bands": bands}, ensure_ascii=False))
        else:
            print(f"samples={len(records)} quantiles={quantiles[0]}/{quantiles[1]}")
            for dim, band in bands.items():
                print(f"  {dim:<12} [{band[0]:.3f}, {band[1]:.3f}]  width={band[1] - band[0]:.3f}")
        return 0

    if not pool:
        print(
            "error=許可リストを通るモデルが 0 件。config/models.json と strict-rules を突き合わせる (fail-closed)",
            file=sys.stderr,
        )
        return 4
    if args.tau_scale <= 0:
        print("error=--tau-scale は正の数を指定してください", file=sys.stderr)
        return 2

    overrides: dict[str, float] = {}
    for item in args.weight:
        if "=" not in item:
            print(f"error=--weight は DIM=X 形式です: {item!r}", file=sys.stderr)
            return 2
        dim, _, value = item.partition("=")
        dim = dim.strip()
        if dim not in policy["dimensions"]:
            print(f"error=未定義の次元: {dim}", file=sys.stderr)
            return 2
        try:
            overrides[dim] = float(value)
        except ValueError:
            print(f"error=--weight の値が数値ではありません: {value!r}", file=sys.stderr)
            return 2

    try:
        weights = compensated_weights(policy, overrides)
    except ConfigError as exc:
        print(f"error={exc}", file=sys.stderr)
        return 2

    exclude_families = {family.strip() for family in args.exclude_family if family.strip()}
    if args.peer:
        peer = args.peer.split(":", 1)[0]
        if peer not in models_cfg["models"]:
            print(f"error=--peer が未知のモデルです: {peer}", file=sys.stderr)
            return 2
        exclude_families.add(peer.split("-")[0])

    override_requirements = None
    if args.requirements:
        try:
            override_requirements = parse_requirements(args.requirements, policy["dimensions"])
        except argparse.ArgumentTypeError as exc:
            print(f"error={exc}", file=sys.stderr)
            return 2

    # sticky はモデル単位なので、`:effort` が付いていても落とす。
    current_model = args.current_model.split(":", 1)[0] if args.current_model else None
    if current_model and current_model not in models_cfg["models"]:
        print(f"error=--current-model が未知のモデルです: {current_model}", file=sys.stderr)
        return 2

    surface = args.surface
    # VS Code は親のコスト階層を超える子を起動しない。CLI にこの制約は無い。
    # 階層はモデル単位なので effort 変種では変わらない。`:effort` が付いていても落とす。
    cost_ceiling = None
    if args.parent_model:
        parent_name = args.parent_model.split(":", 1)[0]
        if parent_name not in models_cfg["models"]:
            print(f"error=--parent-model が未知のモデルです: {parent_name}", file=sys.stderr)
            return 2
        parent = next((e for e in pool if e.model == parent_name), None)
        if parent is None:
            print(
                f"error=--parent-model {parent_name} は許可リストを通らないか無効です。"
                "親が使えないモデルを基準にしない (fail-closed)",
                file=sys.stderr,
            )
            return 4
        if surface == "vscode":
            cost_ceiling = parent.base_cost

    scope = None
    if args.scope_json:
        try:
            raw = sys.stdin.read() if args.scope_json == "-" else Path(args.scope_json).read_text(encoding="utf-8")
            scope = json.loads(raw)
        except (OSError, json.JSONDecodeError) as exc:
            print(f"error=--scope-json を読めません: {exc}", file=sys.stderr)
            return 2
    # 事前計測が成立したときだけ「リポジトリ状態を確認済み」と見なす。
    # NO_MATCH / INCOMPLETE は測れていないので、保守的な floor を維持する。
    context_measured = bool(
        scope and scope.get("route") not in {None, "NO_MATCH", "NO_TEXT", "INCOMPLETE"}
    )

    def run(prompt: str, role: str, turn: int) -> tuple[Decision, list[PoolEntry]]:
        tau = args.tau if args.tau is not None else float(policy["roles"][role]["tau"])
        return decide(
            policy=policy,
            pool=pool,
            prompt=prompt,
            role=role,
            turn=turn,
            tau=tau * args.tau_scale,
            weights=weights,
            tier_mode=args.tier,
            long_context=args.long_context,
            exclude_families=exclude_families,
            current_model=current_model,
            reroute_triggered=(
                turn <= 1 or args.after_compact or args.after_summarize or args.force_route
            ),
            override_requirements=override_requirements,
            context_measured=context_measured,
            force=args.force_route,
            cost_ceiling=cost_ceiling,
        ), pool

    if args.eval:
        try:
            records = read_jsonl(Path(args.eval))
        except ConfigError as exc:
            print(f"error={exc}", file=sys.stderr)
            return 2
        metrics = evaluate(records, lambda p, r, t: run(p, r, t))
        if args.json:
            print(json.dumps(metrics, ensure_ascii=False))
        else:
            print(
                f"queries={metrics['queries']} oracle={metrics['oracle_resolvable']}"
                f" QR={metrics['quality_retention']}% CS={metrics['cost_savings']}%"
                f" Mis={metrics['misroute_rate']}%"
            )
            if metrics["skipped"]:
                print(f"skipped={metrics['skipped']} (ハード ゲートで候補が消えた)")
        return 0

    prompt = read_prompt(ap, args)

    try:
        decision, _ = run(prompt, args.role, args.turn)
    except GateError as exc:
        print(f"error={exc}", file=sys.stderr)
        return 4

    selected = decision.selected
    assert selected is not None
    if args.json:
        print(json.dumps({
            "role": decision.role,
            "surface": surface,
            "tau": round(decision.tau, 6),
            "tier": decision.tier,
            "tier_floor": decision.tier_floor,
            "gamma": decision.gamma,
            "requirements_raw": decision.raw_requirements,
            "requirements": decision.requirements,
            "weights": {dim: round(value, 4) for dim, value in decision.weights.items()},
            "model": selected.model,
            "effort": None if selected.effort == "none" else selected.effort,
            "cost": selected.cost,
            "cost_ceiling": cost_ceiling,
            "shortfall": decision.shortfall,
            "basis": decision.basis,
            "eligible": decision.eligible,
            "gates": decision.gates,
            "vetoed_by_allowlist": vetoed,
            "calibrated": selected.calibrated,
            "table": decision.table,
            "scope_route": scope.get("route") if scope else None,
            "notes": decision.notes,
        }, ensure_ascii=False))
        return 0

    for line in format_text(decision, scope, len(pool), surface):
        print(line)
    if vetoed:
        print(f"vetoed={','.join(vetoed)} (許可リスト外。候補から除外した)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
