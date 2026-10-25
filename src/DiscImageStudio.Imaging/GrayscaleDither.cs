using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiscImageStudio.Imaging;

/// <summary>
/// Converts an image to an ordered palette of nominal gray levels, which may be unevenly spaced.
/// </summary>
public static class GrayscaleDither
{
    private static readonly double[] LinearSrgbLevels = BuildLinearSrgbLevels();

    public static BitmapSource LoadImage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        BitmapFrame frame = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        frame.Freeze();
        BitmapSource source = ToBgra32(frame);
        source.Freeze();
        return source;
    }

    /// <summary>
    /// Dithers to the supplied encoded gray values, preserving dimensions and DPI.
    /// When <paramref name="linearizeSrgb"/> is true, input and palette bytes are assumed
    /// to be sRGB and diffusion uses linear light. This is not ICC profile conversion.
    /// </summary>
    public static BitmapSource Process(
        BitmapSource source,
        ReadOnlySpan<byte> levels,
        CancellationToken cancellationToken = default,
        bool linearizeSrgb = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = checked(width * 4);
        byte[] bgra = new byte[checked(stride * height)];
        ToBgra32(source).CopyPixels(bgra, stride, 0);
        byte[] gray = ProcessPixels(bgra, width, height, levels, cancellationToken, linearizeSrgb);
        BitmapSource result = BitmapSource.Create(
            width,
            height,
            ValidDpi(source.DpiX),
            ValidDpi(source.DpiY),
            PixelFormats.Gray8,
            null,
            gray,
            width);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Dithers tightly packed, straight-alpha BGRA pixels using serpentine Floyd–Steinberg diffusion.
    /// By default, RGB is weighted with Rec. 709 coefficients in its encoded space, then
    /// composited against white. With <paramref name="linearizeSrgb"/>, input RGB and palette
    /// values are decoded from assumed sRGB; luminance, white compositing, nearest-level
    /// quantization and error diffusion all operate in linear light. No ICC conversion is
    /// performed. Output bytes always come from the original palette; ties go to the lighter level.
    /// </summary>
    public static byte[] ProcessPixels(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        ReadOnlySpan<byte> levels,
        CancellationToken cancellationToken = default,
        bool linearizeSrgb = false)
    {
        ValidateLevels(levels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int pixelCount = checked(width * height);
        int requiredLength = checked(pixelCount * 4);
        if (bgra.Length != requiredLength)
        {
            throw new ArgumentException("The BGRA buffer must contain exactly width × height × 4 bytes.", nameof(bgra));
        }

        cancellationToken.ThrowIfCancellationRequested();
        double[] workingLevels = new double[levels.Length];
        for (int index = 0; index < levels.Length; index++)
        {
            workingLevels[index] = linearizeSrgb ? LinearSrgbLevels[levels[index]] : levels[index];
        }

        byte[] output = new byte[pixelCount];
        double[] currentErrors = new double[checked(width + 2)];
        double[] nextErrors = new double[currentErrors.Length];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int direction = (y & 1) == 0 ? 1 : -1;
            int rowStart = y * width;
            int x = direction > 0 ? 0 : width - 1;
            for (int column = 0; column < width; column++, x += direction)
            {
                int pixelIndex = rowStart + x;
                int offset = pixelIndex * 4;
                double luminance = linearizeSrgb
                    ? (0.2126 * LinearSrgbLevels[bgra[offset + 2]])
                        + (0.7152 * LinearSrgbLevels[bgra[offset + 1]])
                        + (0.0722 * LinearSrgbLevels[bgra[offset]])
                    : ((2126 * bgra[offset + 2])
                        + (7152 * bgra[offset + 1])
                        + (722 * bgra[offset])) / 10000.0;
                double onWhite = 255.0 - ((255.0 - luminance) * bgra[offset + 3] / 255.0);
                int slot = x + 1;
                double adjusted = onWhite + currentErrors[slot];
                int quantizedIndex = FindNearestLevelIndex(adjusted, workingLevels);
                output[pixelIndex] = levels[quantizedIndex];
                double error = adjusted - workingLevels[quantizedIndex];

                // Padding discards diffusion outside the image; mirror all neighbors on odd rows.
                currentErrors[slot + direction] += error * (7.0 / 16.0);
                nextErrors[slot - direction] += error * (3.0 / 16.0);
                nextErrors[slot] += error * (5.0 / 16.0);
                nextErrors[slot + direction] += error * (1.0 / 16.0);
            }

            (currentErrors, nextErrors) = (nextErrors, currentErrors);
            Array.Clear(nextErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    public static void SavePng(BitmapSource output, string path, ReadOnlySpan<byte> levels)
    {
        ValidateLevels(levels);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Dithered images must be saved as lossless PNG files (.png).", nameof(path));
        }

        if (output.Format != PixelFormats.Gray8)
        {
            throw new ArgumentException("The output must be a Gray8 bitmap.", nameof(output));
        }

        byte[] row = new byte[output.PixelWidth];
        for (int y = 0; y < output.PixelHeight; y++)
        {
            output.CopyPixels(new Int32Rect(0, y, output.PixelWidth, 1), row, row.Length, 0);
            foreach (byte gray in row)
            {
                if (!levels.Contains(gray))
                {
                    throw new ArgumentException("The output may contain only the selected gray levels.", nameof(output));
                }
            }
        }

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(output));
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static BitmapSource ToBgra32(BitmapSource source) =>
        source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

    private static double ValidDpi(double dpi) => double.IsFinite(dpi) && dpi > 0 ? dpi : 96.0;

    private static void ValidateLevels(ReadOnlySpan<byte> levels)
    {
        if (levels.Length < 2)
            throw new ArgumentException("At least two gray levels are required.", nameof(levels));
        for (int index = 1; index < levels.Length; index++)
        {
            if (levels[index] <= levels[index - 1])
                throw new ArgumentException("Gray levels must be distinct and sorted from dark to light.", nameof(levels));
        }
    }

    private static double[] BuildLinearSrgbLevels()
    {
        double[] levels = new double[256];
        for (int value = 0; value < levels.Length; value++)
        {
            double encoded = value / 255.0;
            levels[value] = 255.0 * (encoded <= 0.04045
                ? encoded / 12.92
                : Math.Pow((encoded + 0.055) / 1.055, 2.4));
        }

        return levels;
    }

    private static int FindNearestLevelIndex(double luminance, ReadOnlySpan<double> levels)
    {
        for (int index = 0; index < levels.Length - 1; index++)
        {
            if (luminance < (levels[index] + levels[index + 1]) / 2.0)
                return index;
        }
        return levels.Length - 1;
    }
}
