namespace DiscImageStudio.Core;

/// <summary>Shared black/white image quantization, independent of disc encoding.</summary>
public static class BinaryGrayscale
{
    public static ReadOnlySpan<byte> Levels => [0, 255];

    public static int GetLevelIndex(byte luminance) => luminance < 128 ? 0 : 1;

    public static byte GetLuminance(int levelIndex) => levelIndex switch
    {
        0 => 0,
        1 => 255,
        _ => throw new ArgumentOutOfRangeException(nameof(levelIndex), "Grayscale level must be 0..1."),
    };

    public static byte Quantize(byte luminance) => GetLuminance(GetLevelIndex(luminance));
}
