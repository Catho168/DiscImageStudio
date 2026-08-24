namespace DiscImageStudio.Dvd;

public sealed record DvdDiscPreset(
    string Id,
    string DisplayName,
    string Description,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double ChannelBitLengthNm,
    double ActualInnerRadiusMm,
    double ActualOuterRadiusMm,
    bool IsCustom = false)
{
    public static IReadOnlyList<DvdDiscPreset> All { get; } =
    [
        new(
            "dvd-5-120mm",
            "DVD 12 cm 单层（4.7 GB，推荐）",
            "标准单层 120 mm DVD 数据区：24–58 mm，共 2,295,104 个扇区。",
            2_295_104,
            24.0,
            58.0,
            133.33,
            24.0,
            58.0),
        new(
            "dvd-5-80mm",
            "Mini DVD 8 cm 单层（1.46 GB）",
            "标准单层 80 mm DVD 数据区：24–38 mm，共 714,544 个扇区。",
            714_544,
            24.0,
            38.0,
            133.33,
            24.0,
            38.0),
        new(
            "custom",
            "自定义参数",
            "参数已手动调整；程序不会覆盖当前数值。",
            2_295_104,
            24.0,
            58.0,
            133.33,
            24.0,
            58.0,
            IsCustom: true),
    ];
}
