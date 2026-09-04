#!/usr/bin/env python3
"""lean-exec pre-flight sizing.

読む前に「読む量」を測り、インライン実行 / サブエージェント / fleet のどれを選ぶかを返す。
出力は意図的に数行に抑えてある。この道具自体が高コストでは本末転倒なので。

--parent-model と --agent-model を渡すと単価差が投影に入る。同じ量でも誰が読むかで
支払いは変わるため、損益分岐は S >= 3F から S >= 3F x (子の単価 / 親の単価) へ動く。

引数の一覧は --help を見る。終了コードは 0 (計測完了。NO_MATCH / NO_TEXT を含む) /
2 (引数か入力パスが不正) / 3 (一部ファイルを読めず推定が不完全)。

Safety:
  .cases / .scripts / .output / KQL は候補検索の前に除外し、明示指定も拒否する。
  作業ディレクトリ外のホーム配下パスは ~ 表記へ短縮する。
"""

from __future__ import annotations

import argparse
import fnmatch
import glob
import json
import math
import os
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass

SKIP_DIRS = {
    ".git", ".svn", ".hg", "node_modules", "__pycache__", ".venv", "venv",
    "out", "obj", "bin", "dist", "build", "target", ".next", ".cache",
    ".output", ".cases", ".scripts", "KQL",
    ".mypy_cache", ".pytest_cache", ".ruff_cache", ".tox", ".gradle",
    ".terraform", ".idea", ".parcel-cache", ".turbo", ".pnpm-store",
}
BINARY_EXT = {
    ".png", ".jpg", ".jpeg", ".gif", ".ico", ".pdf", ".zip", ".gz", ".7z",
    ".exe", ".dll", ".so", ".dylib", ".node", ".wasm", ".pyc", ".class",
    ".etl", ".pml", ".dmp", ".run", ".db", ".sqlite", ".mp4", ".woff",
    ".woff2", ".ttf", ".bin", ".pack", ".jar", ".pptx", ".xlsx", ".docx",
}

# 経路の境界値。根拠は references/budget.md を参照。
T_INLINE = 2_000
T_NARROW = 15_000
T_SUBAGENT = 60_000
# サブエージェント 1 体の固定費 (system prompt + ツール スキーマ + タスク文)。
# 環境で変わるので budget.md §4 の手順で実測し、LEAN_EXEC_F に入れる。
DEFAULT_F = 6_000
DEFAULT_AGENT_CAP = 180_000
DEFAULT_AGENT_TURNS = 3
DEFAULT_SUMMARY_RATIO = 0.1
MAX_SUMMARY_TOKENS = 2_000
MAX_FILE_BYTES = 32 * 1024 * 1024
ESTIMATE_FACTOR = 1.5
MODELS_CONFIG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "config", "models.json")


def model_cost(name: str) -> float:
    """config/models.json の相対コストを引く。単価の正本は 1 か所にしか置かない。

    トークン量だけで委譲を判断すると、高単価の親から低単価の子へ移す裁定が見えない。
    読む量が同じでも、誰が読むかで支払いは変わる。

    effort を省いた場合は「対応する effort のうち倍率が 1.0 に最も近いもの」として扱う。
    route.py も同じ規約で解決する。ここがずれると、同じ親を 2 つのスクリプトが
    別の単価で扱うことになる。
    """
    model, _, effort = name.partition(":")
    try:
        with open(MODELS_CONFIG, encoding="utf-8") as handle:
            config = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        raise argparse.ArgumentTypeError(
            f"モデル カタログを読めません: {MODELS_CONFIG} ({exc})"
        ) from exc
    spec = config.get("models", {}).get(model)
    if spec is None:
        raise argparse.ArgumentTypeError(f"未知のモデルです: {model}")
    if spec.get("cost") is None:
        raise argparse.ArgumentTypeError(
            f"{model} に cost がありません (enabled: false のまま使おうとしていないか)"
        )
    cost = float(spec["cost"])
    variants = config.get("effort_variants", {})
    supported = spec.get("efforts") or ["none"]
    if effort:
        if effort not in supported:
            raise argparse.ArgumentTypeError(
                f"{model} で使えない effort です: {effort} (使えるのは {', '.join(supported)})"
            )
        variant = variants.get(effort)
        if variant is None:
            raise argparse.ArgumentTypeError(f"未知の effort です: {effort}")
        return cost * float(variant["cost_multiplier"])
    multipliers = {
        name: float(variants[name]["cost_multiplier"])
        for name in supported if name in variants
    }
    if not multipliers:
        raise argparse.ArgumentTypeError(f"{model} の efforts が effort_variants と一致しません")
    nominal = min(multipliers, key=lambda name: (abs(multipliers[name] - 1.0), name))
    return cost * multipliers[nominal]


@dataclass(frozen=True)
class ReadResult:
    text: str | None
    raw_bytes: int
    reason: str | None = None
    detail: str | None = None


def est_tokens(text: str) -> int:
    """BPE を使わない近似。ASCII は 4 文字/トークン、非 ASCII は 1.1 文字/トークン。

    正確なトークナイザではない。桁を外さないことだけを狙った作業用の係数である。
    """
    if text.isascii():
        return int(len(text) / 4.0)
    # encode('ascii', 'ignore') は非 ASCII を落とすので、ASCII 文字数だけは厳密に取れる。
    ascii_n = len(text.encode("ascii", "ignore"))
    other_n = len(text) - ascii_n
    return int(ascii_n / 4.0 + other_n / 1.1)


def human(n: float) -> str:
    for unit in ("", "K", "M", "G"):
        if abs(n) < 1000:
            return f"{n:.0f}{unit}" if unit == "" else f"{n:.1f}{unit}"
        n /= 1000.0
    return f"{n:.1f}T"


def display_path(path: str) -> str:
    absolute = os.path.abspath(path)
    relative = os.path.relpath(absolute)
    if relative != ".." and not relative.startswith(f"..{os.sep}"):
        return relative
    home = os.path.expanduser("~")
    try:
        under_home = os.path.commonpath((absolute, home)) == home
    except ValueError:
        under_home = False
    if under_home:
        return os.path.join("~", os.path.relpath(absolute, home))
    return path


def redact_detail(detail: str) -> str:
    return detail.replace(os.path.expanduser("~"), "~")


def expand_glob(raw: str, hidden: bool) -> list[str]:
    kwargs = {"recursive": True}
    if sys.version_info >= (3, 11):
        kwargs["include_hidden"] = hidden
    return glob.glob(raw, **kwargs)


def iter_files(
    paths: list[str],
    hidden: bool = True,
    walk_errors: list[tuple[str, str]] | None = None,
):
    seen: set[str] = set()

    def record_walk_error(exc: OSError) -> None:
        if walk_errors is not None:
            walk_errors.append((exc.filename or "<unknown>", str(exc)))

    for raw in paths:
        expanded = expand_glob(raw, hidden) or [raw]
        for p in expanded:
            if os.path.isdir(p):
                for root, dirs, files in os.walk(p, onerror=record_walk_error):
                    dirs[:] = [
                        d for d in dirs
                        if d not in SKIP_DIRS and (hidden or not d.startswith("."))
                    ]
                    dirs.sort()
                    for f in sorted(files):
                        fp = os.path.join(root, f)
                        key = os.path.realpath(fp)
                        if key not in seen:
                            seen.add(key)
                            yield fp
            elif os.path.isfile(p):
                key = os.path.realpath(p)
                if key not in seen:
                    seen.add(key)
                    yield p


def path_parts(path: str) -> set[str]:
    return set(path.replace("\\", "/").split("/"))


def is_skipped_path(path: str) -> bool:
    return bool((path_parts(path) | path_parts(os.path.realpath(path))) & SKIP_DIRS)


def keep(path: str, exts: set[str] | None, excludes: list[str]) -> bool:
    if is_skipped_path(path):
        return False
    ext = os.path.splitext(path)[1].lower()
    if exts and ext not in exts:
        return False
    for pat in excludes:
        if fnmatch.fnmatch(path, pat):
            return False
    return True


def read_text(path: str) -> ReadResult:
    try:
        size = os.path.getsize(path)
    except OSError as exc:
        return ReadResult(None, 0, "unreadable", str(exc))
    if size > MAX_FILE_BYTES:
        return ReadResult(None, size, "oversize")
    try:
        with open(path, "rb") as fh:
            data = fh.read()
    except OSError as exc:
        return ReadResult(None, size, "unreadable", str(exc))
    sample = data[:8192]
    control_bytes = sum(byte < 32 and byte not in (9, 10, 13) for byte in sample)
    if b"\0" in sample or (sample and control_bytes / len(sample) > 0.05):
        return ReadResult(None, len(data), "binary")
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        text = data.decode("utf-8", errors="replace")
        if text.count("\ufffd") / max(1, len(text)) > 0.01:
            return ReadResult(
                None,
                len(data),
                "encoding",
                "UTF-8 として解釈できないバイトが 1% を超えています",
            )
    return ReadResult(text, len(data))


def fallback_note(no_ignore: bool) -> str:
    return "" if no_ignore else " この走査では ignore 規則を適用しません"


def run_rg(
    command: list[str],
    no_ignore: bool,
    text: bool = True,
) -> tuple[subprocess.CompletedProcess | None, str | None]:
    try:
        result = subprocess.run(
            command,
            capture_output=True,
            text=text,
            timeout=180,
        )
    except OSError as exc:
        return None, (
            f"rg を起動できませんでした。Python 走査へ切り替えます: {exc}"
            f"{fallback_note(no_ignore)}"
        )
    except subprocess.TimeoutExpired:
        return None, (
            "rg が 180 秒でタイムアウトしました。Python 走査へ切り替えます"
            f"{fallback_note(no_ignore)}"
        )
    if result.returncode not in (0, 1):
        stderr = result.stderr if isinstance(result.stderr, str) else os.fsdecode(result.stderr)
        detail = stderr.strip().splitlines()
        suffix = f": {detail[0]}" if detail else ""
        return None, (
            f"rg が終了コード {result.returncode} を返しました。"
            f"Python 走査へ切り替えます{suffix}{fallback_note(no_ignore)}"
        )
    return result, None


def rg_match_lines(
    pattern: str,
    paths: list[str],
    exts: set[str] | None,
    excludes: list[str],
    hidden: bool = True,
    no_ignore: bool = False,
) -> tuple[dict[str, list[int]] | None, str | None]:
    """rg があれば候補ファイルと 0-based のマッチ行をまとめて取得する。

    SKIP_DIRS は rg の走査前にも除外する。候補抽出後に落とすだけでは、参照禁止の
    ディレクトリを rg が先に読んでしまう。

    --hidden は明示的に渡す。rg は既定で隠しディレクトリを飛ばすため、渡さないと
    .github/ や .claude/ 配下がまるごと計測から抜け落ちて、0 件を静かに返してしまう。
    任意の --exclude は rg glob と fnmatch の意味が一致しないため、Python 走査へ戻し、
    ファイルを開く前に適用する。
    """
    exe = shutil.which("rg")
    if not exe:
        warning = None if no_ignore else "rg がないため ignore 規則を適用せずに走査します"
        return None, warning
    if excludes or any(glob.has_magic(path) for path in paths):
        warning = None if no_ignore else (
            "glob または --exclude を正確に適用するため Python 走査へ切り替えます。"
            "この走査では ignore 規則を適用しません"
        )
        return None, warning
    cmd = [
        exe,
        "--line-number",
        "--with-filename",
        "--null",
        "--only-matching",
        "--replace",
        "",
        "--no-messages",
        "-e",
        pattern,
    ]
    if hidden:
        cmd.append("--hidden")
    if no_ignore:
        cmd.append("--no-ignore")
    for directory in sorted(SKIP_DIRS):
        cmd += ["--glob", f"!**/{directory}/**"]
    for e in sorted(exts or []):
        cmd += ["--glob", f"*{e}"]
    cmd += ["--"] + paths
    out, warning = run_rg(cmd, no_ignore, text=False)
    if out is None:
        return None, warning
    matches: dict[str, set[int]] = {}
    try:
        payload = out.stdout
        position = 0
        while position < len(payload):
            separator = payload.find(b"\0", position)
            colon = payload.find(b":", separator + 1)
            newline = payload.find(b"\n", colon + 1)
            if separator < 0 or colon < 0:
                raise ValueError("NUL 区切りまたは行番号の区切りがありません")
            if newline < 0:
                newline = len(payload)
            path = os.fsdecode(payload[position:separator])
            line_number = int(payload[separator + 1:colon])
            matches.setdefault(path, set()).add(line_number - 1)
            position = newline + 1
    except (TypeError, ValueError) as exc:
        return None, (
            f"rg の行番号出力を解釈できません。Python 走査へ切り替えます: {exc}"
            f"{fallback_note(no_ignore)}"
        )
    return {path: sorted(lines) for path, lines in matches.items()}, None


def rg_files(
    paths: list[str],
    exts: set[str] | None,
    excludes: list[str],
    hidden: bool = True,
    no_ignore: bool = False,
) -> tuple[list[str] | None, str | None]:
    """rg --files で ignore 規則を保ったまま候補を列挙する。"""
    exe = shutil.which("rg")
    if not exe:
        warning = None if no_ignore else "rg がないため ignore 規則を適用せずに走査します"
        return None, warning
    if excludes or any(glob.has_magic(path) for path in paths):
        warning = None if no_ignore else (
            "glob または --exclude を正確に適用するため Python 走査へ切り替えます。"
            "この走査では ignore 規則を適用しません"
        )
        return None, warning
    cmd = [exe, "--files", "--null"]
    if hidden:
        cmd.append("--hidden")
    if no_ignore:
        cmd.append("--no-ignore")
    for directory in sorted(SKIP_DIRS):
        cmd += ["--glob", f"!**/{directory}/**"]
    for extension in sorted(exts or []):
        cmd += ["--glob", f"*{extension}"]
    cmd += ["--"] + paths
    out, warning = run_rg(cmd, no_ignore, text=False)
    if out is None:
        return None, warning
    return list(dict.fromkeys(
        os.fsdecode(path)
        for path in out.stdout.split(b"\0")
        if path
    )), None


def route(
    total_tokens: int,
    capacity_tokens: int,
    largest_file_tokens: int,
    threads: int,
    matched: int,
    agent_cap: int,
    allow_delegation: bool,
    expected_agents: int,
    incomplete: bool = False,
    binary_only: bool = False,
    narrow_threshold: int = T_NARROW,
) -> tuple[str, str]:
    if incomplete:
        return "INCOMPLETE", (
            "読み取り不能、非 UTF-8、または 32 MiB 超のファイルがあり、推定値は下限にすぎない。"
            "対象を分割するか読み取りエラーを解消してから測り直す"
        )
    if binary_only:
        return "NO_TEXT", (
            "対象はバイナリだけだった。テキスト量としては経路判定できないため、"
            "ファイル形式に対応する専用ツールを使う"
        )
    if matched == 0:
        return "NO_MATCH", (
            "計測対象が 0 件。パターン・パス・拡張子・除外条件のどれかが外れている。"
            "0 を「小さい」と読み替えず、ここを直してから測り直す"
        )
    per_thread = max(
        capacity_tokens / max(1, threads),
        largest_file_tokens,
    )
    if per_thread > agent_cap:
        return "NARROW_FIRST", (
            f"上側推定が 1 スレッドあたり ~{human(per_thread)}tok で、"
            f"安全上限 {human(agent_cap)}tok を超える。"
            "問いを絞るか対象を分割し、スレッド数を増やしてから測り直す"
        )
    if total_tokens < T_INLINE:
        return "INLINE", "そのまま自分で読む。サブエージェントの固定費のほうが高くつく"
    if total_tokens < narrow_threshold or not allow_delegation:
        if not allow_delegation:
            return "INLINE_NARROW", "委譲よりインライン実行の推定コストが低い。範囲指定で読む"
        return "INLINE_NARROW", "rg で該当箇所を特定し、view の範囲指定だけ読む。委譲しない"
    if expected_agents >= 3:
        return "FLEET", f"独立スレッド {expected_agents} 本を todos 化し、/fleet で並列ディスパッチ"
    if expected_agents == 2:
        return "SUBAGENT_BG", "background サブエージェント 2 体。親は別作業を進める"
    return "SUBAGENT", "sync サブエージェント 1 体に読ませ、要約だけ受け取る"


def positive_int(raw: str) -> int:
    try:
        value = int(raw)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("正の整数を指定してください") from exc
    if value <= 0:
        raise argparse.ArgumentTypeError("正の整数を指定してください")
    return value


def nonnegative_int(raw: str) -> int:
    try:
        value = int(raw)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("0 以上の整数を指定してください") from exc
    if value < 0:
        raise argparse.ArgumentTypeError("0 以上の整数を指定してください")
    return value


def env_positive_int(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if not raw:
        return default
    try:
        value = int(raw)
    except ValueError:
        print(f"warning: {name}={raw!r} は正の整数ではありません。既定 {default} を使います",
              file=sys.stderr)
        return default
    if value <= 0:
        print(f"warning: {name}={raw!r} は正の整数ではありません。既定 {default} を使います",
              file=sys.stderr)
        return default
    return value


def input_exists(raw: str, hidden: bool) -> bool:
    if not glob.has_magic(raw):
        return os.path.exists(raw)
    return bool(expand_glob(raw, hidden))


def main() -> int:
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("paths", nargs="+", help="計測するファイル、ディレクトリ、glob")
    ap.add_argument("--grep", default=None,
                    help="正規表現に一致する行だけを証拠量として数える")
    ap.add_argument("--ext", default=None,
                    help="対象拡張子をカンマ区切りで絞る")
    ap.add_argument("--exclude", action="append", default=[],
                    help="除外するパス glob。複数回指定できる")
    ap.add_argument("--threads", type=positive_int, default=1,
                    help="独立して進められる調査スレッド数 (既定 1)")
    ap.add_argument("--context", type=nonnegative_int, default=6,
                    help="--grep 時に前後何行を証拠へ含めるか (既定 6)")
    ap.add_argument("--top", type=nonnegative_int, default=5,
                    help="大きいファイルを何件表示するか (既定 5)")
    ap.add_argument("--turns", type=nonnegative_int, default=0,
                    help="親の残りターン数。S×N の投影を出す")
    ap.add_argument("--fixed-cost", type=positive_int, default=None,
                    help="サブエージェント 1 体の固定費 F")
    ap.add_argument("--agent-turns", type=positive_int, default=DEFAULT_AGENT_TURNS,
                    help="サブエージェント内部の推定ターン数 n")
    ap.add_argument("--summary-tokens", type=nonnegative_int, default=None,
                    help="親に返す要約の推定トークン数 R (全エージェント合計)")
    ap.add_argument("--agent-cap", type=positive_int, default=None,
                    help="1 体に渡す証拠量の安全上限")
    ap.add_argument("--optimize", choices=("cost", "latency"), default="cost",
                    help="cost は最小人数、latency は独立スレッドを並列化")
    ap.add_argument("--parent-model", default=None,
                    help="親のモデル (例 gpt-5.6-sol:max)。単価差を投影に反映する")
    ap.add_argument("--agent-model", default=None,
                    help="委譲先のモデル (例 claude-haiku-4.5)。既定は親と同じ単価")
    ap.add_argument("--no-hidden", action="store_true",
                    help="隠しディレクトリを走査から外す")
    ap.add_argument("--no-ignore", action="store_true",
                    help=".gitignore / .ignore などの ignore 規則を無効にする")
    ap.add_argument("--json", action="store_true", help="機械可読出力")
    args = ap.parse_args()

    if args.fixed_cost is None:
        args.fixed_cost = env_positive_int("LEAN_EXEC_F", DEFAULT_F)
    if args.agent_cap is None:
        args.agent_cap = env_positive_int("LEAN_EXEC_AGENT_CAP", DEFAULT_AGENT_CAP)

    hidden = not args.no_hidden
    missing_inputs = [path for path in args.paths if not input_exists(path, hidden)]
    if missing_inputs:
        for path in missing_inputs:
            print(f"error: 入力パスまたは glob に一致する対象がありません: {display_path(path)}",
                  file=sys.stderr)
        return 2
    blocked_inputs = [
        path for path in args.paths
        if not glob.has_magic(path) and is_skipped_path(path)
    ]
    if blocked_inputs:
        for path in blocked_inputs:
            print(f"error: 参照禁止または出力専用のパスは計測できません: {display_path(path)}",
                  file=sys.stderr)
        return 2

    exts = None
    if args.ext:
        values = [e.strip() for e in args.ext.lower().split(",") if e.strip()]
        if not values:
            print("error: --ext に拡張子を 1 つ以上指定してください", file=sys.stderr)
            return 2
        exts = {e if e.startswith(".") else "." + e for e in values}

    try:
        rx = re.compile(args.grep) if args.grep is not None else None
    except re.error as exc:
        print(f"error: --grep のパターンが不正です: {exc}", file=sys.stderr)
        return 2

    per_file: list[tuple[str, int, int]] = []  # path, bytes, tokens
    hit_files = 0
    hit_lines = 0
    scanned = 0
    candidates = 0
    skipped_counts = {"binary": 0, "encoding": 0, "oversize": 0, "unreadable": 0}
    skipped_bytes = {"binary": 0, "encoding": 0, "oversize": 0, "unreadable": 0}
    skipped_examples: list[dict[str, str | int]] = []
    warnings: list[str] = []
    walk_errors: list[tuple[str, str]] = []

    rg_marks: dict[str, list[int]] | None = None
    if rx is not None:
        rg_marks, warning = rg_match_lines(
            args.grep,
            args.paths,
            exts,
            args.exclude,
            hidden,
            args.no_ignore,
        )
        cands = list(rg_marks) if rg_marks is not None else None
    else:
        cands, warning = rg_files(
            args.paths,
            exts,
            args.exclude,
            hidden,
            args.no_ignore,
        )
    if warning:
        warnings.append(redact_detail(warning))
    source = cands if cands is not None else iter_files(args.paths, hidden, walk_errors)

    for path in source:
        candidates += 1
        if not keep(path, exts, args.exclude):
            continue
        if os.path.splitext(path)[1].lower() in BINARY_EXT:
            try:
                size = os.path.getsize(path)
            except OSError:
                size = 0
            skipped_counts["binary"] += 1
            skipped_bytes["binary"] += size
            if len(skipped_examples) < args.top:
                skipped_examples.append({
                    "path": display_path(path),
                    "reason": "binary",
                    "bytes": size,
                })
            continue
        scanned += 1
        result = read_text(path)
        if result.text is None:
            reason = result.reason or "unreadable"
            skipped_counts[reason] += 1
            skipped_bytes[reason] += result.raw_bytes
            if len(skipped_examples) < args.top:
                item: dict[str, str | int] = {
                    "path": display_path(path),
                    "reason": reason,
                    "bytes": result.raw_bytes,
                }
                if result.detail:
                    item["detail"] = redact_detail(result.detail)
                skipped_examples.append(item)
            continue
        text = result.text
        if rx is None:
            per_file.append((path, result.raw_bytes, est_tokens(text)))
            continue

        lines = text.splitlines()
        if rg_marks is not None:
            marks = rg_marks.get(path, [])
        else:
            marks = [i for i, ln in enumerate(lines) if rx.search(ln)]
        if not marks:
            continue
        hit_files += 1
        hit_lines += len(marks)
        wanted: set[int] = set()
        for i in marks:
            wanted.update(range(max(0, i - args.context), min(len(lines), i + args.context + 1)))
        chunk = "\n".join(lines[i] for i in sorted(wanted))
        per_file.append((path, len(chunk.encode("utf-8")), est_tokens(chunk)))

    for path, detail in walk_errors:
        skipped_counts["unreadable"] += 1
        if len(skipped_examples) < args.top:
            skipped_examples.append({
                "path": display_path(path),
                "reason": "unreadable",
                "bytes": 0,
                "detail": redact_detail(detail),
            })

    total_bytes = sum(b for _, b, _ in per_file)
    total_tokens = sum(t for _, _, t in per_file)
    lower_tokens = int(total_tokens / ESTIMATE_FACTOR)
    upper_tokens = math.ceil(total_tokens * ESTIMATE_FACTOR)
    largest_file_tokens = max((tokens for _, _, tokens in per_file), default=0)
    largest_file_lower = int(largest_file_tokens / ESTIMATE_FACTOR)
    largest_file_upper = math.ceil(
        largest_file_tokens * ESTIMATE_FACTOR
    )
    top = sorted(per_file, key=lambda r: (-r[2], r[0]))[: args.top]

    minimum_agents = max(1, math.ceil(upper_tokens / args.agent_cap))
    if args.optimize == "latency" and total_tokens >= T_SUBAGENT:
        expected_agents = args.threads
    else:
        expected_agents = min(minimum_agents, args.threads)
    fixed = args.fixed_cost
    def summary_for(evidence: int) -> int:
        if args.summary_tokens is not None:
            return args.summary_tokens
        return min(MAX_SUMMARY_TOKENS * expected_agents,
                   int(evidence * DEFAULT_SUMMARY_RATIO))

    summary_tokens = summary_for(total_tokens)
    try:
        parent_price = model_cost(args.parent_model) if args.parent_model else 1.0
        agent_price = model_cost(args.agent_model) if args.agent_model else parent_price
    except argparse.ArgumentTypeError as exc:
        print(f"error={exc}", file=sys.stderr)
        return 2
    price_ratio = agent_price / parent_price if parent_price > 0 else 1.0

    def project(evidence: int) -> tuple[int, int]:
        """その証拠量でのインラインと委譲の入力トークン相当。単価で重み付けする。

        推定の上下限でも同じ式を評価したいので、S を引数に取る。
        """
        inline = int(evidence * args.turns * parent_price)
        delegated = int(
            (fixed * args.agent_turns * expected_agents
             + int(evidence * args.agent_turns / 2)) * agent_price
            + summary_for(evidence) * args.turns * parent_price
        )
        return inline, delegated

    def favors_delegation(evidence: int) -> bool:
        if args.turns <= 0:
            # 損益分岐は S·parent >= 3F·agent、すなわち S >= 3F·(agent/parent)。
            # 単価が同じなら元の 3F に戻り、子が 9 倍安ければ閾値も 1/9 になる。
            return evidence >= 3 * fixed * expected_agents * price_ratio
        inline, delegated = project(evidence)
        return delegated < inline

    projected, delegated_projected = project(total_tokens) if args.turns > 0 else (0, 0)
    proxy_favors_delegation = favors_delegation(total_tokens)
    proxy_basis = "projection" if args.turns > 0 else "fixed_gate"
    fixed_gate = total_tokens >= 3 * fixed * expected_agents * price_ratio
    allow_delegation = proxy_favors_delegation or args.optimize == "latency"
    # 単価が下がるぶん、インラインで粘る意味も薄くなる。境界を price_ratio で縮める。
    # ただし allow_delegation (投影か固定費ゲート) も同時に満たす必要があるので、
    # 「安い子がいる」だけでは委譲にならない。要約を戻す往復が割に合うことも要る。
    narrow_threshold = max(T_INLINE, int(T_NARROW * min(1.0, price_ratio)))
    route_thresholds = [
        T_INLINE,
        narrow_threshold,
        args.agent_cap * args.threads,
    ]
    if args.turns <= 0:
        # 固定費ゲートは S の一次式なので、閾値をまたぐかどうかで判定できる。
        route_thresholds.append(int(3 * fixed * expected_agents * price_ratio))
    if args.optimize == "latency":
        route_thresholds.append(T_SUBAGENT)
    estimate_uncertain = any(
        lower_tokens < threshold <= upper_tokens
        for threshold in route_thresholds
    ) or largest_file_lower < args.agent_cap <= largest_file_upper
    if args.turns > 0 and favors_delegation(lower_tokens) != favors_delegation(upper_tokens):
        # 投影の交点は S の一次式ではない (要約が S に依存する)。閾値では表せないので、
        # 推定範囲の両端で判定が割れるかどうかを直接見る。
        estimate_uncertain = True

    incomplete = (
        skipped_counts["encoding"] > 0
        or skipped_counts["oversize"] > 0
        or skipped_counts["unreadable"] > 0
    )
    binary_only = (
        len(per_file) == 0
        and skipped_counts["binary"] > 0
        and not incomplete
        and rx is None
    )
    decision, why = route(
        total_tokens,
        upper_tokens,
        largest_file_upper,
        args.threads,
        len(per_file),
        args.agent_cap,
        allow_delegation,
        expected_agents,
        incomplete,
        binary_only,
        narrow_threshold,
    )

    if args.json:
        print(json.dumps({
            "candidate_files": candidates,
            "scanned": scanned,
            "files": len(per_file),
            "bytes": total_bytes,
            "est_tokens": total_tokens,
            "est_tokens_lower": lower_tokens,
            "est_tokens_upper": upper_tokens,
            "largest_file_est_tokens": largest_file_tokens,
            "largest_file_est_tokens_lower": largest_file_lower,
            "largest_file_est_tokens_upper": largest_file_upper,
            "estimate_factor": ESTIMATE_FACTOR,
            "estimate_crosses_threshold": estimate_uncertain,
            "grep_hit_files": hit_files,
            "grep_hit_lines": hit_lines,
            "threads": args.threads,
            "est_tokens_per_thread": int(total_tokens / max(1, args.threads)),
            "est_tokens_upper_per_thread": int(upper_tokens / max(1, args.threads)),
            "hidden": hidden,
            "ignore_rules": not args.no_ignore,
            "fixed_cost": fixed,
            "agent_cap": args.agent_cap,
            "agent_turns": args.agent_turns,
            "optimize": args.optimize,
            "narrow_threshold": narrow_threshold,
            "parent_model": args.parent_model,
            "agent_model": args.agent_model,
            "parent_price": parent_price,
            "agent_price": agent_price,
            "price_ratio": round(price_ratio, 4),
            "minimum_agents": minimum_agents,
            "expected_agents": expected_agents,
            "summary_tokens": summary_tokens,
            "input_proxy_basis": proxy_basis,
            "projection_scope": "input_proxy_with_relative_pricing_excludes_cache_and_reasoning",
            "input_proxy_favors_delegation": proxy_favors_delegation,
            "fixed_gate_passed": fixed_gate,
            "remaining_turns": args.turns,
            "projected_inline_input_tokens": projected,
            "projected_delegated_input_tokens": delegated_projected,
            "skipped": {
                reason: {
                    "files": skipped_counts[reason],
                    "bytes": skipped_bytes[reason],
                }
                for reason in skipped_counts
            },
            "skipped_examples": skipped_examples,
            "warnings": warnings,
            "route": decision,
            "reason": why,
            "top": [
                {"path": display_path(p), "bytes": b, "est_tokens": t}
                for p, b, t in top
            ],
        }, ensure_ascii=False))
        return 3 if decision == "INCOMPLETE" else 0

    head = (
        f"files={len(per_file)}/{scanned} bytes={human(total_bytes)}"
        f" est_tokens~{human(total_tokens)}"
        f" range~{human(lower_tokens)}-{human(upper_tokens)}"
    )
    if rx is not None:
        head += f" hits={hit_lines}L/{hit_files}F"
    if not hidden:
        head += " hidden=off"
    if args.no_ignore:
        head += " ignore=off"
    print(head)
    for warning in warnings:
        print(f"warning={warning}")
    if estimate_uncertain:
        print("warning=推定範囲が経路の境界をまたぐため、判定の確度は低い")
    for p, b, t in top:
        print(f"  {human(t):>6}tok {human(b):>6}B  {display_path(p)}")
    skipped_total = sum(skipped_counts.values())
    if skipped_total:
        details = " ".join(
            f"{reason}={skipped_counts[reason]}F/{human(skipped_bytes[reason])}B"
            for reason in skipped_counts
            if skipped_counts[reason]
        )
        print(f"skipped={skipped_total} ({details})")
        for item in skipped_examples:
            print(f"  skipped[{item['reason']}] {item['path']}")
    if args.turns > 0:
        print(
            f"input_proxy={'delegate' if proxy_favors_delegation else 'inline'}"
            f" inline~{human(projected)} delegate~{human(delegated_projected)}"
            f" (agents={expected_agents}, n={args.agent_turns}, R={human(summary_tokens)},"
            f" F={human(fixed)})"
        )
    else:
        threshold = 3 * fixed * expected_agents * price_ratio
        print(
            f"fixed_gate={'pass' if fixed_gate else 'fail'}"
            f" (S={human(total_tokens)}, 3F×agents×price={human(threshold)})"
        )
    if price_ratio != 1.0:
        print(
            f"price: parent={args.parent_model}({parent_price:g})"
            f" agent={args.agent_model}({agent_price:g})"
            f" ratio={price_ratio:.3f} → 委譲の損益分岐が {1 / price_ratio:.1f} 倍ゆるむ"
        )
    print(
        f"route={decision} threads={args.threads}"
        f" agents={expected_agents} optimize={args.optimize}"
    )
    print(f"why={why}")
    return 3 if decision == "INCOMPLETE" else 0


if __name__ == "__main__":
    sys.exit(main())
