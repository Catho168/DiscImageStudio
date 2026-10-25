"""Measure actual Q-byte occupancies in the prepared probe's mixture cells.

Run with Python 3 and NumPy:
    python tools/DvdGrayProbe/verify_mixtures.py --prepared ../artifacts/dvd-gray-probe-64-e4

Reads the emitted atlas independently of the C# mask generator. Measurements
describe atlas pixels, not the optical brightness or the ISO's encoded samples.
"""

import argparse
import csv
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np


# Fixed experiment regions, as documented in README.md / probe.json.
MIXTURE_ROWS = (
    ("64_with_E4", 28.5, 30.5, 0x64, 0xE4),
    ("candidate_with_A5", 45.8, 47.5, None, 0xA5),
    ("candidate_with_B9", 47.8, 49.5, None, 0xB9),
    ("candidate_with_92", 49.8, 51.5, None, 0x92),
)


def sha256_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def measure(prepared, chunk_rows, tolerance_pp):
    metadata = json.loads((prepared / "probe.json").read_text(encoding="utf-8-sig"))
    if metadata["schema"] != "dvd-gray-probe-v1":
        raise ValueError("Unsupported prepared probe schema")
    width, height = metadata["atlasWidth"], metadata["atlasHeight"]
    cell_mm, canvas_mm = metadata["cellMm"], metadata["canvasMm"]
    if (width, height, cell_mm, canvas_mm) != (6000, 6000, 0.02, 120):
        raise ValueError("This verifier expects the documented 6000-square 20-um probe")
    panels = sorted(metadata["panelsClockwiseFromTwelve"], key=lambda item: item["panel"])
    if [item["panel"] for item in panels] != list(range(1, 13)):
        raise ValueError("Expected 12 unique numbered panels")
    candidates = [int(item["darkQ"], 16) for item in panels]
    atlas_path = prepared / "atlas.q8"
    if atlas_path.stat().st_size != width * height:
        raise ValueError("Atlas length does not match its declared dimensions")
    atlas_hash = sha256_file(atlas_path)
    if atlas_hash != metadata["atlasSha256"]:
        raise ValueError("Atlas SHA-256 differs from probe.json")

    atlas = np.memmap(atlas_path, dtype=np.uint8, mode="r", shape=(height, width))
    counts = np.zeros((4, 12, 3, 256), dtype=np.int64)
    xx = (np.arange(width, dtype=np.float64)[None, :] + 0.5) * cell_mm - canvas_mm / 2
    for y0 in range(0, height, chunk_rows):
        y1 = min(height, y0 + chunk_rows)
        yy = (np.arange(y0, y1, dtype=np.float64)[:, None] + 0.5) * cell_mm - canvas_mm / 2
        radius = np.sqrt(xx * xx + yy * yy)
        angle = np.arctan2(xx, -yy)
        angle[angle < 0] += 2 * np.pi
        position = angle * 6 / np.pi
        panel = np.minimum(11, np.floor(position).astype(np.int16))
        t = position - panel
        column_position = (t - 0.04) / 0.92 * 3
        column = np.clip(np.floor(column_position).astype(np.int16), 0, 2)
        within = column_position - column
        # Exclude both the panel's white margins and each column's white gutters.
        interior = (t >= 0.04) & (t < 0.96) & (within >= 0.025) & (within < 0.975)
        block = atlas[y0:y1]
        for row_index, (_, inner, outer, _, _) in enumerate(MIXTURE_ROWS):
            region = interior & (radius >= inner) & (radius < outer)
            if not np.any(region):
                continue
            group = (panel[region].astype(np.int64) * 3 + column[region]) * 256
            bins = np.bincount(group + block[region], minlength=12 * 3 * 256)
            counts[row_index] += bins.reshape(12, 3, 256)

    records = []
    for row_index, (row_name, inner, outer, fixed_selected, other) in enumerate(MIXTURE_ROWS):
        for panel in range(12):
            selected = candidates[panel] if fixed_selected is None else fixed_selected
            for column in range(3):
                histogram = counts[row_index, panel, column]
                total = int(histogram.sum())
                if not total:
                    raise ValueError(f"Empty cell: {row_name}, panel {panel + 1}, column {column + 1}")
                selected_count, other_count = int(histogram[selected]), int(histogram[other])
                unexpected = {
                    f"0x{code:02X}": int(count)
                    for code, count in enumerate(histogram)
                    if count and code not in (selected, other)
                }
                target = (column + 1) * 25.0
                measured = selected_count / total * 100
                records.append({
                    "row": row_name,
                    "inner_radius_mm": inner,
                    "outer_radius_mm": outer,
                    "panel": panel + 1,
                    "panel_candidate_q": f"0x{candidates[panel]:02X}",
                    "column_clockwise": column + 1,
                    "selected_q": f"0x{selected:02X}",
                    "other_q": f"0x{other:02X}",
                    "target_selected_percent": target,
                    "total_pixels": total,
                    "selected_pixels": selected_count,
                    "other_pixels": other_count,
                    "unexpected_pixels": total - selected_count - other_count,
                    "unexpected_codes": json.dumps(unexpected, sort_keys=True, separators=(",", ":")),
                    "measured_selected_percent": round(measured, 8),
                    "deviation_percentage_points": round(measured - target, 8),
                    "within_tolerance": abs(measured - target) < tolerance_pp,
                })

    csv_path = prepared / "mixture-measurements.csv"
    with csv_path.open("w", newline="", encoding="utf-8-sig") as output:
        writer = csv.DictWriter(output, fieldnames=list(records[0]))
        writer.writeheader()
        writer.writerows(records)

    row_summaries = []
    for row_name, *_ in MIXTURE_ROWS:
        subset = [record for record in records if record["row"] == row_name]
        worst = max(subset, key=lambda record: abs(record["deviation_percentage_points"]))
        row_summaries.append({
            "row": row_name,
            "cells": len(subset),
            "total_pixels": sum(record["total_pixels"] for record in subset),
            "unexpected_pixels": sum(record["unexpected_pixels"] for record in subset),
            "maximum_absolute_deviation_percentage_points": abs(worst["deviation_percentage_points"]),
            "worst_cell": worst,
            "ranges_by_target_percent": {
                str(target): {
                    "minimum_measured_percent": min(record["measured_selected_percent"] for record in subset if record["target_selected_percent"] == target),
                    "maximum_measured_percent": max(record["measured_selected_percent"] for record in subset if record["target_selected_percent"] == target),
                }
                for target in (25, 50, 75)
            },
        })
    bad_occupancy = [record for record in records if not record["within_tolerance"]]
    code_leaks = [record for record in records if record["unexpected_pixels"]]
    summary = {
        "schema": "dvd-gray-probe-mixture-measurements-v1",
        "measured_utc": datetime.now(timezone.utc).isoformat(),
        "atlas_sha256": atlas_hash,
        "atlas_matches_probe_metadata": True,
        "measurement": "actual atlas pixel occupancy; excludes panel margins and column gutters; not optical brightness or ISO channel-sample occupancy",
        "panel_interior_t": "[0.04, 0.96)",
        "column_interior_fraction": "[0.025, 0.975)",
        "tolerance_percentage_points_exclusive": tolerance_pp,
        "cells_measured": len(records),
        "total_pixels_measured": sum(record["total_pixels"] for record in records),
        "minimum_pixels_per_cell": min(record["total_pixels"] for record in records),
        "maximum_absolute_deviation_percentage_points": max(abs(record["deviation_percentage_points"]) for record in records),
        "unexpected_pixels": sum(record["unexpected_pixels"] for record in records),
        "all_passed": not bad_occupancy and not code_leaks,
        "row_summaries": row_summaries,
        "cells_outside_tolerance": bad_occupancy,
        "cells_with_unexpected_codes": code_leaks,
        "csv": csv_path.name,
    }
    (prepared / "mixture-measurements.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: summary[key] for key in (
        "cells_measured", "total_pixels_measured", "maximum_absolute_deviation_percentage_points",
        "unexpected_pixels", "all_passed",
    )}, indent=2))
    return 0 if summary["all_passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared", required=True, type=Path)
    parser.add_argument("--chunk-rows", default=128, type=int)
    parser.add_argument("--tolerance-pp", default=1.0, type=float)
    args = parser.parse_args()
    if args.chunk_rows <= 0 or args.tolerance_pp <= 0:
        parser.error("chunk rows and tolerance must be positive")
    raise SystemExit(measure(args.prepared, args.chunk_rows, args.tolerance_pp))
