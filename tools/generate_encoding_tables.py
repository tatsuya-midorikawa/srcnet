#!/usr/bin/env python3
"""レガシー符号化の復号表を生成する。

`docs/decisions.md` ADR-9 の決定に従い、必要な符号化だけを自前で復号する。製品コードへ
NuGet を追加しないという原則（`.github/instructions/fsharp-dotnet.instructions.md`）を
守るためであり、`System.Text.Encoding.CodePages` は採用しない。

出典は Python 標準ライブラリの CJK codec で、生成結果はリポジトリへコミットする。
これにより、ビルドと実行のどちらでも外部データを取得しない。

表は「先頭バイト × 後続バイト」で引ける密な `uint16` 配列とし、Deflate で圧縮して置く。
未対応の位置は 0 とし、復号時は置換文字にする。

使い方:

    python3 tools/generate_encoding_tables.py           # 生成して書き出す
    python3 tools/generate_encoding_tables.py --check   # 差分があれば終了コード 1
"""

from __future__ import annotations

import argparse
import struct
import sys
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "src" / "Srcnet.Text" / "tables"

# 2 バイト表を作る符号化。名前は `Encodings.DetectedEncoding` の綴りに合わせる。
DOUBLE_BYTE = {
    "shift_jis": "cp932",
    "euc_jp": "euc_jp",
    "gb18030": "gb18030",
    "big5": "cp950",
    "euc_kr": "cp949",
}

# GB18030 の 4 バイト列のうち、基本多言語面へ写る範囲。
GB18030_SUPPLEMENTARY_LINEAR = 189000


def double_byte_table(codec: str) -> bytearray:
    """先頭バイト × 後続バイトで引く `uint16` の表を作る。未対応は 0。"""
    table = bytearray(256 * 256 * 2)

    for lead in range(0x81, 0x100):
        for trail in range(0x21, 0x100):
            raw = bytes((lead, trail))

            try:
                decoded = raw.decode(codec)
            except UnicodeDecodeError:
                continue

            # 1 スカラー値へ写らないものは扱わない。合字や複数文字は復号の対象外。
            if len(decoded) != 1:
                continue

            scalar = ord(decoded)

            # 基本多言語面の外は `uint16` で表せない。該当は実質存在しないが、
            # 静かに切り捨てず表から外す。
            if scalar > 0xFFFF:
                continue

            offset = (lead * 256 + trail) * 2
            struct.pack_into("<H", table, offset, scalar)

    return table


def gb18030_four_byte_ranges() -> bytearray:
    """GB18030 の 4 バイト列から基本多言語面への写像を、連続する区間の列にする。"""
    runs: list[tuple[int, int, int]] = []
    start_linear = -1
    start_scalar = -1
    previous_linear = -2
    previous_scalar = -2

    for linear in range(GB18030_SUPPLEMENTARY_LINEAR):
        first = linear // 12600
        rest = linear % 12600
        second = rest // 1260
        rest %= 1260
        third = rest // 10
        fourth = rest % 10
        raw = bytes((0x81 + first, 0x30 + second, 0x81 + third, 0x30 + fourth))

        try:
            decoded = raw.decode("gb18030")
        except UnicodeDecodeError:
            scalar = -1
        else:
            scalar = ord(decoded) if len(decoded) == 1 and ord(decoded) <= 0xFFFF else -1

        if scalar < 0:
            if start_linear >= 0:
                runs.append((start_linear, start_scalar, previous_linear - start_linear + 1))
                start_linear = -1
            previous_linear = linear
            previous_scalar = -2
            continue

        if start_linear >= 0 and linear == previous_linear + 1 and scalar == previous_scalar + 1:
            previous_linear = linear
            previous_scalar = scalar
            continue

        if start_linear >= 0:
            runs.append((start_linear, start_scalar, previous_linear - start_linear + 1))

        start_linear = linear
        start_scalar = scalar
        previous_linear = linear
        previous_scalar = scalar

    if start_linear >= 0:
        runs.append((start_linear, start_scalar, previous_linear - start_linear + 1))

    payload = bytearray()
    payload += struct.pack("<I", len(runs))

    for linear, scalar, length in runs:
        payload += struct.pack("<III", linear, scalar, length)

    return payload


def render() -> dict[str, bytes]:
    files: dict[str, bytes] = {}

    for name, codec in DOUBLE_BYTE.items():
        table = double_byte_table(codec)
        # 圧縮レベルを固定しないと、zlib の版で出力が変わり決定性を損なう。
        files[f"{name}.bin"] = zlib.compress(bytes(table), 9)

    files["gb18030_4byte.bin"] = zlib.compress(bytes(gb18030_four_byte_ranges()), 9)
    return files


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="差分があれば失敗する")
    arguments = parser.parse_args()

    files = render()

    if arguments.check:
        for name, payload in files.items():
            path = OUTPUT / name

            if not path.exists() or path.read_bytes() != payload:
                print(f"{path} が生成結果と一致しません。再生成してください。", file=sys.stderr)
                return 1

        return 0

    OUTPUT.mkdir(parents=True, exist_ok=True)
    total = 0

    for name, payload in files.items():
        (OUTPUT / name).write_bytes(payload)
        total += len(payload)
        print(f"[encoding-tables] {name}: {len(payload)} バイト")

    print(f"[encoding-tables] 合計 {total} バイト")
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
