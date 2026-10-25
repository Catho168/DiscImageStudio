"""Validate probe outputs and draw standalone scientific figures (requires matplotlib)."""
import argparse
import csv
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager
import numpy as np


def load(directory, name):
    return json.loads((directory / name).read_text(encoding="utf-8-sig"))


def fine_results(directory, values):
    data = load(directory, "fine-patterns.json")
    cases = data["PhaseSweep"]
    addresses = data["AddressConfirmations"]
    for item in cases + addresses:
        stats = item["Greedy"]
        assert stats["ChannelBits"] == 958272 and item["VerifiedPayloadBytes"] == 65536
        assert sum(stats["RunCounts"]) == stats["Transitions"] - 1
        assert stats["DataRegionTransitions"] + stats["ParityRegionTransitions"] == stats["Transitions"]
        assert not sum(stats["RunCounts"][:2])
    selections = [
        ("稀疏位串：16 bit，DC 段重启", "0000101101110010", "dc-block-reset", 2),
        ("稳健高密度：6 bit，预留 DC 槽", "001111", "physical-17pp-input", 3),
        ("高密度位串：10 bit，DC 段重启", "0111011110", "dc-block-reset", 4),
    ]
    rows = []
    for name, pattern, layout, phase in selections:
        item = next(r for r in cases if (r["Pattern"], r["Layout"], r["Phase"]) == (pattern, layout, phase))
        same = [r for r in addresses if (r["Pattern"], r["Layout"], r["Phase"]) == (pattern, layout, phase)]
        assert len(same) == 4
        metrics = item["Greedy"]
        row = {"name": name, "pattern": pattern, "layout": layout, "phase": phase,
               "greedy_density": metrics["Density"],
               "address_min": min(r["Greedy"]["Density"] for r in same),
               "address_max": max(r["Greedy"]["Density"] for r in same),
               "dc_probe_min": min(item[k] for k in ["ZeroDcDensity", "OneDcDensity", "RandomDcDensity"]),
               "dc_probe_max": max(item[k] for k in ["ZeroDcDensity", "OneDcDensity", "RandomDcDensity"]),
               "data_region_density": metrics["DataRegionDensity"],
               "parity_region_density": metrics["ParityRegionDensity"]}
        rows.append(row)
    with (directory / "recommended-patterns.csv").open("w", newline="", encoding="utf-8-sig") as file:
        writer = csv.DictWriter(file, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)
    with (directory / "fine-patterns.csv").open("w", newline="", encoding="utf-8-sig") as file:
        writer = csv.writer(file)
        writer.writerow(["pattern", "layout", "phase", "PSN", "greedy_density", "zero_dc_density", "one_dc_density", "random_dc_density", "data_region_density", "parity_region_density"])
        for r in cases:
            writer.writerow([r["Pattern"], r["Layout"], r["Phase"], r["Psn"], r["Greedy"]["Density"],
                             r["ZeroDcDensity"], r["OneDcDensity"], r["RandomDcDensity"], r["Greedy"]["DataRegionDensity"], r["Greedy"]["ParityRegionDensity"]])
    chart_rows = [
        ("字节基线：0x09", values[9]["Cluster"]["Density"], min(values[9][k] for k in ["ZeroDcDensity", "OneDcDensity", "RandomDcDensity"]), max(values[9][k] for k in ["ZeroDcDensity", "OneDcDensity", "RandomDcDensity"]), "#94a3b8"),
        ("16 位串 · phase 2\n按 DC 段重启", rows[0]["greedy_density"], rows[0]["dc_probe_min"], rows[0]["dc_probe_max"], "#12827c"),
        ("字节基线：0x9E", values[0x9e]["Cluster"]["Density"], min(values[0x9e][k] for k in ["ZeroDcDensity", "OneDcDensity", "RandomDcDensity"]), max(values[0x9e][k] for k in ["ZeroDcDensity", "OneDcDensity", "RandomDcDensity"]), "#94a3b8"),
        ("6 位串 · phase 3\n预留 DC 槽", rows[1]["greedy_density"], rows[1]["dc_probe_min"], rows[1]["dc_probe_max"], "#12827c"),
        ("10 位串 · phase 4\n按 DC 段重启", rows[2]["greedy_density"], rows[2]["dc_probe_min"], rows[2]["dc_probe_max"], "#12827c"),
    ]
    fig, ax = plt.subplots(figsize=(11, 6.2))
    for i, (name, center, low, high, color) in enumerate(chart_rows):
        ax.barh(i, center * 100, height=.58, color=color)
        low, high = min(low, center) * 100, max(high, center) * 100
        ax.errorbar(center * 100, i, xerr=[[center * 100 - low], [high - center * 100]], color="#253044", capsize=5, linewidth=1.3)
        ax.text(high + .5, i, f"{center*100:.2f}%", va="center", weight="bold", color="#17263b")
    ax.set_yticks(range(len(chart_rows)), [r[0] for r in chart_rows])
    ax.invert_yaxis()
    ax.axhline(1.5, color="#cbd5e1", linewidth=.8)
    ax.set_xlim(0, 50)
    ax.set_xlabel("完整编码簇跳变密度（%）")
    ax.set_title("位级控制扩大密度差：19.41 → 27.42 个百分点", fontsize=16, pad=18)
    ax.grid(axis="x", alpha=.15)
    ax.set_axisbelow(True)
    ax.spines[["top", "right"]].set_visible(False)
    fig.text(.03, .025, "条形：greedy DC 诊断；横线：四种 DC 策略的测试范围，非置信区间。\n"
             "单层 BD-R，PSN 0x100000，包含 EDC / ECC / BIS / DC / 同步；数字密度不等于实盘明暗。", fontsize=10, color="#475569")
    fig.tight_layout(rect=(0, .095, 1, 1))
    fig.savefig(directory / "fine-pattern-comparison.png", dpi=180)
    plt.close(fig)
    (directory / "fine-validation.json").write_text(json.dumps({
        "PhaseCases": len(cases), "AddressConfirmationRowsIncludingBaseline": len(addresses),
        "AllPayloadPositionsVerifiedPerCluster": 65536, "HistogramConservationPassed": True,
        "SelectedPatterns": rows,
    }, ensure_ascii=False, indent=2), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", nargs="?", default="artifacts/bluray-byte-density")
    args = parser.parse_args()
    directory = Path(args.directory)
    font = Path("C:/Windows/Fonts/msyh.ttc")
    if font.exists():
        font_manager.fontManager.addfont(str(font))
        plt.rcParams["font.family"] = font_manager.FontProperties(fname=str(font)).get_name()
    plt.rcParams.update({"axes.unicode_minus": False, "font.size": 11, "savefig.facecolor": "white"})

    values = load(directory, "all-bytes.json")
    assert len(values) == 256 and sorted(x["Byte"] for x in values) == list(range(256))
    for value in values:
        stats = value["Cluster"]
        assert stats["ChannelBits"] == 958272
        assert sum(stats["RunCounts"]) == stats["CompletedRuns"] == stats["Transitions"] - 1
        assert sum(i * n for i, n in enumerate(stats["RunCounts"])) == stats["CompletedRunBits"]
        assert not stats["RunCounts"][1] and not sum(stats["RunCounts"][10:])
        assert 0 < stats["Density"] < .5
    reference_file = directory / "reference/independent-periodic-results.json"
    errors = []
    if reference_file.exists():
        reference = load(directory, "reference/independent-periodic-results.json")
        for value, exact in zip(values, reference["all_bytes"]):
            assert value["Byte"] == exact["q"]
            error = abs(value["Pure"]["Density"] - exact["density"])
            assert error <= 2 / value["Pure"]["ChannelBits"]
            errors.append(error)
    context = load(directory, "context.json")
    for value in context["AllBytes"]:
        assert value["Statistics"]["RunCountOutside2TTo8T"] == 0

    grid = np.array([v["Cluster"]["Density"] * 100 for v in values]).reshape(16, 16)
    fig, ax = plt.subplots(figsize=(11, 10))
    im = ax.imshow(grid, cmap="viridis", vmin=18, vmax=40)
    ax.set_xticks(range(16), [f"{i:X}" for i in range(16)])
    ax.set_yticks(range(16), [f"{i:X}" for i in range(16)])
    ax.set_xlabel("Q 低四位（十六进制）")
    ax.set_ylabel("Q 高四位（十六进制）")
    ax.xaxis.tick_top()
    ax.xaxis.set_label_position("top")
    ax.set_title("单层 BD-R：256 种重复 Q 字节的完整簇跳变密度", pad=20, fontsize=16)
    for row in range(16):
        for column in range(16):
            ax.text(column, row, f"{grid[row, column]:.1f}", ha="center", va="center",
                    color="white" if grid[row, column] < 30 else "#16202a", fontsize=8)
    fig.colorbar(im, ax=ax, shrink=.8, label="跳变数 / channel bits（%）")
    fig.text(.1, .035, "Q 为扰码后的 payload 值；包含 EDC、LDC/BIS、DC、17PP 与同步。\n"
             "PSN 0x100000，greedy DC 诊断模型；数字密度不等于实盘明暗。", fontsize=10, color="#475569")
    fig.subplots_adjust(left=.10, right=.91, top=.88, bottom=.11)
    fig.savefig(directory / "all-bytes-density.png", dpi=160)
    plt.close(fig)

    candidates = [0x09, 0x11, 0x22, 0x3D, 0x7A, 0x9E, 0x00, 0xFF, 0x55, 0xAA]
    fig, ax = plt.subplots(figsize=(11, 5.7))
    y = np.arange(len(candidates))
    pure = [values[q]["Pure"]["Density"] * 100 for q in candidates]
    full = [values[q]["Cluster"]["Density"] * 100 for q in candidates]
    ax.barh(y - .16, pure, height=.29, color="#b7c8e5", label="纯 17PP，未插 DC")
    ax.barh(y + .16, full, height=.29, color="#2762b0", label="完整簇，greedy DC")
    for row, density in enumerate(full):
        ax.text(density + .35, row + .16, f"{density:.2f}%", va="center", fontsize=10)
    ax.set_yticks(y, [f"0x{q:02X}" for q in candidates])
    ax.invert_yaxis()
    ax.set_xlim(0, 47)
    ax.set_xlabel("跳变密度（%）")
    ax.set_title("字节基线：低密度组、高密度组及常见对照", fontsize=15)
    ax.legend(loc="lower right", frameon=False)
    ax.grid(axis="x", alpha=.15)
    ax.set_axisbelow(True)
    ax.spines[["top", "right"]].set_visible(False)
    fig.text(.12, .025, "这些值是数字编码结果；还没有指定哪组对应黑、哪组对应白。", fontsize=10, color="#475569")
    fig.tight_layout(rect=(0, .05, 1, 1))
    fig.savefig(directory / "byte-candidates.png", dpi=170)
    plt.close(fig)

    table = ["# 全部 256 字节的测量结果", "", "Q 指扰码后的目标 payload 字节。完整簇为 958,272 channel bits，PSN 0x100000。", "",
             "| Q | 纯 17PP % | 完整簇 greedy % | DC 全 0 % | DC 全 1 % | DC 随机 % | 平均 run / T |", "|---|---:|---:|---:|---:|---:|---:|"]
    for value in values:
        table.append(f"| {value['Hex']} | {value['Pure']['Density']*100:.5f} | {value['Cluster']['Density']*100:.5f} | "
                     f"{value['ZeroDcDensity']*100:.5f} | {value['OneDcDensity']*100:.5f} | {value['RandomDcDensity']*100:.5f} | {value['Cluster']['MeanRun']:.5f} |")
    (directory / "all-bytes.md").write_text("\n".join(table) + "\n", encoding="utf-8")
    (directory / "validation.json").write_text(json.dumps({
        "DistinctBytes": len(values), "PhysicalChannelBitsPerCluster": 958272,
        "HistogramConservationPassed": True, "No1TOr10TPlusInClusters": True,
        "NoDataRunOutside2TTo8TInSyntheticFrames": True,
        "IndependentPeriodicComparisonCount": len(errors),
        "MaximumPureDensityError": max(errors, default=None),
    }, indent=2), encoding="utf-8")
    print(f"Validated all 256 bytes; reference comparisons: {len(errors)}. Figures written to {directory}.")
    if (directory / "fine-patterns.json").exists():
        fine_results(directory, values)
        print("Validated fine-pattern clusters and generated fine-pattern comparison.")


if __name__ == "__main__":
    main()
