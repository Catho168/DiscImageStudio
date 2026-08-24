namespace DiscImageStudio.Cd;

public sealed record CdDiscPreset(
    string Id,
    string DisplayName,
    string Description,
    long Sectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double LinearVelocityMmPerSecond,
    double ImageOuterRadiusMm,
    double ActualInnerRadiusMm,
    double ActualOuterRadiusMm,
    double ActualLinearVelocityMmPerSecond,
    bool IsCustom = false)
{
    public static IReadOnlyList<CdDiscPreset> All { get; } =
    [
        new(
            "cd-80",
            "CD-R/RW 80 分钟（700 MB，推荐）",
            "采用 1200 mm/s 标准扫描速度，适合常见 120 mm、约 80 分钟的空白 CD。",
            359_849,
            24.5,
            56.8,
            1_200,
            57.5,
            24.3,
            56.6,
            1_200),
        new(
            "ritek-medical-aqua",
            "铼德医疗水蓝盘（实测）",
            "实测参数：内半径 24.911275 mm、外半径 57.931155 mm、359,845 扇区；扫描速度采用 1200 mm/s。",
            359_845,
            24.911275,
            57.931155,
            1_200,
            58.0,
            24.911275,
            57.931155,
            1_200),
        new(
            "cd-74",
            "CD-R/RW 74 分钟（650 MB）",
            "适合标称 74 分钟的 120 mm 空白 CD；采用 1200 mm/s 标准扫描速度。",
            333_000,
            24.5,
            56.8,
            1_200,
            57.5,
            24.3,
            56.6,
            1_200),
        new(
            "custom",
            "自定义参数",
            "参数已手动调整；程序不会覆盖当前数值。",
            359_849,
            24.5,
            56.8,
            1_200,
            57.5,
            24.3,
            56.6,
            1_200,
            IsCustom: true),
    ];
}
