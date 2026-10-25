namespace DiscImageStudio.Core;

/// <summary>Shared image quantization and nominal preview levels, independent of media encoding.</summary>
public static class FourLevelGrayscale
{
    /// <summary>Maps luminance into equal-width bins separated at 64, 128 and 192.</summary>
    public static int GetLevelIndex(byte luminance) => luminance >> 6;

    /// <summary>Returns a nominal display luminance; these values are not measured disc reflectance.</summary>
    public static byte GetLuminance(int levelIndex)
    {
        if ((uint)levelIndex >= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(levelIndex), "Grayscale level must be 0..3.");
        }

        return (byte)(levelIndex * 85);
    }

    public static byte Quantize(byte luminance) => GetLuminance(GetLevelIndex(luminance));
}
