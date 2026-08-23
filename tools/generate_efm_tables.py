"""Generate the C# EFMPlus tables from ECMA-267 Annex G.

Usage:
  python tools/generate_efm_tables.py ECMA-267.pdf output.cs
"""

from __future__ import annotations

import sys
from pathlib import Path

import pdfplumber


def extract_page_rows(page) -> list[tuple[int, list[tuple[int, int]]]]:
    tables = page.extract_tables()
    if len(tables) != 1 or len(tables[0]) != 3:
        raise RuntimeError(f"unexpected table layout on page {page.page_number}")
    row = tables[0][2]
    byte_values = [int(value) for value in row[0].splitlines()]
    states: list[list[tuple[int, int]]] = []
    for state_index in range(4):
        words = [int(value, 2) for value in row[1 + state_index * 2].splitlines()]
        next_states = [int(value) for value in row[2 + state_index * 2].splitlines()]
        if len(words) != len(byte_values) or len(next_states) != len(byte_values):
            raise RuntimeError(f"misaligned state {state_index + 1} on page {page.page_number}")
        states.append(list(zip(words, next_states)))
    return [(value, [states[state][index] for state in range(4)]) for index, value in enumerate(byte_values)]


def extract(pdf_path: Path):
    with pdfplumber.open(pdf_path) as pdf:
        main_rows = []
        for page_number in range(67, 73):
            main_rows.extend(extract_page_rows(pdf.pages[page_number - 1]))
        substitution_rows = []
        for page_number in range(73, 75):
            substitution_rows.extend(extract_page_rows(pdf.pages[page_number - 1]))

    if [value for value, _ in main_rows] != list(range(256)):
        raise RuntimeError("main table does not contain bytes 0..255 exactly once")
    if [value for value, _ in substitution_rows] != list(range(88)):
        raise RuntimeError("substitution table does not contain bytes 0..87 exactly once")
    return main_rows, substitution_rows


def emit_array(name: str, rows, item_index: int, formatter) -> str:
    values = []
    for _, states in rows:
        values.extend(formatter(states[state][item_index]) for state in range(4))
    lines = []
    for offset in range(0, len(values), 16):
        lines.append("        " + ", ".join(values[offset : offset + 16]) + ",")
    return f"    internal static readonly {name} =\n    [\n" + "\n".join(lines) + "\n    ];\n"


def generate(pdf_path: Path) -> str:
    main_rows, substitution_rows = extract(pdf_path)
    parts = [
        "// Generated from ECMA-267 Annex G. Do not edit by hand.\n",
        "namespace DvdImageSolver.Encoding;\n\n",
        "internal static partial class EfmPlusTables\n{\n",
        emit_array("ushort[] MainWords", main_rows, 0, lambda value: f"0x{value:04X}"),
        emit_array("byte[] MainNextStates", main_rows, 1, str),
        emit_array("ushort[] SubstitutionWords", substitution_rows, 0, lambda value: f"0x{value:04X}"),
        emit_array("byte[] SubstitutionNextStates", substitution_rows, 1, str),
        "}\n",
    ]
    return "".join(parts)


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit("usage: generate_efm_tables.py ECMA-267.pdf output.cs")
    pdf_path = Path(sys.argv[1])
    output_path = Path(sys.argv[2])
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(generate(pdf_path), encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
