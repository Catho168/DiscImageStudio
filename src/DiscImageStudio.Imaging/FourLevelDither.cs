using System.Windows.Media.Imaging;

namespace DiscImageStudio.Imaging;

/// <summary>Compatibility entry points for the CD four-level palette.</summary>
public static class FourLevelDither
{
    public static ReadOnlySpan<byte> Levels => [0, 85, 170, 255];

    public static BitmapSource LoadImage(string path) => GrayscaleDither.LoadImage(path);

    public static BitmapSource Process(BitmapSource source, CancellationToken cancellationToken = default)
        => GrayscaleDither.Process(source, Levels, cancellationToken);

    public static byte[] ProcessPixels(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        CancellationToken cancellationToken = default)
        => GrayscaleDither.ProcessPixels(bgra, width, height, Levels, cancellationToken);

    public static void SavePng(BitmapSource output, string path) => GrayscaleDither.SavePng(output, path, Levels);
}
