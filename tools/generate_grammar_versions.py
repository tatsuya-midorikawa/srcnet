#!/usr/bin/env python3
"""同梱する文法の版表を F# のソースとして生成する。

`native/sources.json` を唯一の出典とし、実行時に `native/` を読む経路を作らない
（docs/security.md C-1、backlog 020）。生成結果はリポジトリへコミットするため、
Python が無い環境でもビルドできる。

使い方:

    python3 tools/generate_grammar_versions.py           # 生成して書き出す
    python3 tools/generate_grammar_versions.py --check   # 差分があれば終了コード 1
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCES = ROOT / "native" / "sources.json"
OUTPUT = ROOT / "src" / "Srcnet.Extraction" / "GrammarVersions.fs"

# 生成物へ埋め込む値は、識別子・版・16 進のいずれかに限る。
# 出典が改変されても、F# のソースへ任意の文字列を注入できないようにする。
LANGUAGE_PATTERN = re.compile(r"\A[a-z0-9_]{1,32}\Z")
VERSION_PATTERN = re.compile(r"\A[A-Za-z0-9._-]{1,32}\Z")
SHA256_PATTERN = re.compile(r"\A[0-9a-f]{64}\Z")

HEADER = '''/// 同梱している文法の版。
///
/// **このファイルは `tools/generate_grammar_versions.py` が生成する。手で編集しない。**
///
/// 版は成果物へ記録する決定性の前提条件である（docs/extraction.md 3.2、backlog 020）。
/// 出典は `native/sources.json` のみで、実行時に読み込む経路は作らない
/// （docs/security.md C-1）。
module Srcnet.Extraction.GrammarVersions

/// 文法 1 つ分の記録。
[<Struct>]
type GrammarVersion =
  { /// `native/sources.json` の `language`。
    Language: string
    Version: string
    /// 取得元アーカイブの SHA-256（16 進小文字）。
    Sha256: string }

/// 同梱対象の文法の版。言語名の序数昇順。
///
/// ここに載るのは「同梱対象として構成された文法」であり、実際に構築されたかどうかとは
/// 独立である。実際に利用できた文法との差は `Grammars.available` が示す。
let all: GrammarVersion[] =
  [|'''

FOOTER = '''

/// 構文解析ランタイムの版。文法と同じく決定性の前提条件になる。
[<Literal>]
let RuntimeVersion = "{runtime_version}"

/// 言語名から版を引く。同梱対象でなければ `ValueNone`。
let tryFind (language: string) =
  let mutable found = ValueNone

  for entry in all do
    if found.IsNone && System.String.Equals(entry.Language, language, System.StringComparison.Ordinal) then
      found <- ValueSome entry

  found
'''


def render(manifest: dict) -> str:
    grammars = sorted(manifest["grammars"], key=lambda item: item["language"])
    lines = [HEADER]

    for index, grammar in enumerate(grammars):
        language = grammar["language"]
        version = grammar["version"]
        sha256 = grammar["sha256"]

        if not LANGUAGE_PATTERN.match(language):
            raise ValueError(f"language が識別子として不正です: {language!r}")
        if not VERSION_PATTERN.match(version):
            raise ValueError(f"version が不正です: {version!r}")
        if not SHA256_PATTERN.match(sha256):
            raise ValueError(f"sha256 が 16 進小文字 64 桁ではありません: {sha256!r}")

        prefix = "     " if index else "     "
        lines.append(
            f'{prefix}{{ Language = "{language}"\n'
            f'       Version = "{version}"\n'
            f'       Sha256 = "{sha256}" }}'
        )

    body = "\n".join(lines)
    runtime_version = manifest["runtime"]["version"]

    if not VERSION_PATTERN.match(runtime_version):
        raise ValueError(f"runtime.version が不正です: {runtime_version!r}")

    return body + " |]" + FOOTER.format(runtime_version=runtime_version)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="差分があれば失敗する")
    arguments = parser.parse_args()

    with open(SOURCES, encoding="utf-8") as handle:
        manifest = json.load(handle)

    rendered = render(manifest)

    if arguments.check:
        current = OUTPUT.read_text(encoding="utf-8") if OUTPUT.exists() else ""

        if current != rendered:
            print(f"{OUTPUT} が sources.json と一致しません。再生成してください。", file=sys.stderr)
            return 1

        return 0

    # 改行は LF に固定する。OS 既定の改行を混ぜると成果物の差分が全件になる（ADR-7）。
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(rendered)

    print(f"[grammar-versions] {OUTPUT} を生成しました")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
