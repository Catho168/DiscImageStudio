namespace DiscImageStudio.Core;

/// <summary>
/// Nonuniform nominal image/preview tones. The middle value is a starting point
/// for the visually dark DVD gray, not a measured optical reflectance.
/// </summary>
public static class ThreeLevelGrayscale
{
    public const byte MiddleLuminance = 85;

    public static ReadOnlySpan<byte> Levels => [0, MiddleLuminance, 255];

    /// <summary>Nearest nominal level; midpoint ties select the lighter level.</summary>
    public static int GetLevelIndex(byte luminance)
        => luminance * 2 < MiddleLuminance ? 0
            : luminance * 2 < MiddleLuminance + 255 ? 1 : 2;

    public static byte GetLuminance(int levelIndex) => levelIndex switch
    {
        0 => 0,
        1 => MiddleLuminance,
        2 => 255,
        _ => throw new ArgumentOutOfRangeException(nameof(levelIndex), "Grayscale level must be 0..2."),
    };

    public static byte Quantize(byte luminance) => GetLuminance(GetLevelIndex(luminance));
}
