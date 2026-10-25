using DiscImageStudio.Core;

namespace DiscImageStudio.DitherTool;

public enum DitherMode
{
    Dvd,
    Cd,
}

internal static class DitherModes
{
    internal static ReadOnlySpan<byte> Levels(DitherMode mode) => mode switch
    {
        DitherMode.Dvd or DitherMode.Cd => BinaryGrayscale.Levels,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static string Label(DitherMode mode) => mode == DitherMode.Dvd ? "DVD 二阶" : "CD 二阶";
    internal static string Suffix(DitherMode mode) => "黑白抖动";
    internal static string PaletteLabel(DitherMode mode) => "0 / 255";

    internal static DitherMode Parse(string value) => value.ToLowerInvariant() switch
    {
        "dvd" => DitherMode.Dvd,
        "cd" => DitherMode.Cd,
        _ => throw new ArgumentException("--mode 请选择 dvd 或 cd，均输出黑白二阶。"),
    };
}
