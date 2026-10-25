using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Imaging;

internal static class RingDitherTests
{
    private static readonly byte[] Levels = [0, 255];

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ring-dither-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            TestPaletteAndMixturePreservation(directory);
            TestOrdinaryImageFallback(directory);
        }
        finally
        {
            foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
        Console.WriteLine("ring-dither: passed (binary palette, transformed mixture brightness, default render and ordinary-image fallback)");
    }

    private static void TestPaletteAndMixturePreservation(string directory)
    {
        string source = Path.Combine(directory, "dither-source.png");
        // The source is much larger than a ring copy. High-quality resampling
        // intentionally averages these fine pixels into roughly 63.75 and 191.25.
        // Requantizing those regions would destroy their intended mixed tones.
        WritePng(source, 1024, 512, (x, y) =>
        {
            byte gray = x < 512
                ? ((x + y) % 4 == 0 ? (byte)255 : (byte)0)
                : ((x + y) % 4 == 0 ? (byte)0 : (byte)255);
            return (gray, gray, gray, (byte)255);
        });
        RingImageLayoutOptions standard = new(58, 26, 56, OutputSize: 512);
        RingImageLayoutOptions preserved = standard with { PreserveDitherPalette = Levels };
        string referencePath = Path.Combine(directory, "continuous-reference.png");
        string defaultAgainPath = Path.Combine(directory, "unpreserved-default.png");
        string preservedPath = Path.Combine(directory, "binary-ring.png");
        RingImageLayoutSummary referenceSummary = RingImageProcessor.Render(source, referencePath, standard);
        RingImageLayoutSummary preservedSummary = RingImageProcessor.Render(source, preservedPath, preserved);
        RingImageProcessor.Render(source, defaultAgainPath, standard with { PreserveDitherPalette = ReadOnlyMemory<byte>.Empty });

        byte[] reference = ReadGray(referencePath);
        byte[] actual = ReadGray(preservedPath);
        Require(reference.SequenceEqual(ReadGray(defaultAgainPath)), "the unconfigured imaging API keeps its original rendered pixels");
        Require(reference.Any(value => value is not (0 or 255)),
            "the high-quality reference actually exercises intermediate grays");
        Require(actual.Length == 512 * 512 && actual.All(value => value is 0 or 255)
            && actual.Distinct().Count() == 2,
            "preserved CD/DVD ring output contains only the two intended palette tones");
        Require(referenceSummary.CopyCount == preservedSummary.CopyCount
            && referenceSummary.CopyWidthPixels == preservedSummary.CopyWidthPixels
            && referenceSummary.CopyHeightPixels == preservedSummary.CopyHeightPixels,
            "palette preservation keeps the same ring geometry");
        Require(Math.Abs(reference.Average(value => (double)value) - actual.Average(value => (double)value)) < 0.3,
            "redithering conserves the whole transformed image's average brightness");

        TestRegionMixture(reference, actual, referenceSummary, standard.CanvasOuterRadiusMm, rightHalf: false, dark: 0, light: 255);
        TestRegionMixture(reference, actual, referenceSummary, standard.CanvasOuterRadiusMm, rightHalf: true, dark: 0, light: 255);
    }

    private static void TestRegionMixture(byte[] reference, byte[] actual, RingImageLayoutSummary summary,
        double canvasOuterRadiusMm, bool rightHalf, byte dark, byte light)
    {
        // Select contiguous interior patches by geometry, never by the reference
        // pixel's brightness: that would select particular phases of the source
        // texture and discard neighboring pixels receiving its diffusion error.
        int size = summary.OutputSize;
        double pixelsPerMm = size / (2 * canvasOuterRadiusMm);
        double width = summary.CopyWidthMm * pixelsPerMm;
        double height = summary.CopyHeightMm * pixelsPerMm;
        double radius = summary.CopyCentreRadiusMm * pixelsPerMm;
        List<int> selected = [];
        for (int copy = 0; copy < summary.CopyCount; copy++)
        {
            double angle = copy * 2 * Math.PI / summary.CopyCount;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            double centreX = size / 2.0 + radius * sine;
            double centreY = size / 2.0 - radius * cosine;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - centreX, dy = y + 0.5 - centreY;
                double localX = (cosine * dx + sine * dy) / width;
                double localY = (-sine * dx + cosine * dy) / height;
                if ((rightHalf ? localX is >= 0.1 and <= 0.4 : localX is >= -0.4 and <= -0.1)
                    && Math.Abs(localY) <= 0.35)
                    selected.Add(y * size + x);
            }
        }
        int[] region = selected.ToArray();
        Require(region.Length > 1000, "the fixture has a substantial resampled mixture region");
        double expectedMean = region.Average(index => (double)reference[index]);
        double actualMean = region.Average(index => (double)actual[index]);
        double expectedLightFraction = (expectedMean - dark) / (light - dark);
        double actualLightFraction = region.Count(index => actual[index] == light) / (double)region.Length;
        Console.WriteLine($"ring-dither {dark}/{light}: mean {expectedMean:F2} → {actualMean:F2}, light coverage {expectedLightFraction:P2} → {actualLightFraction:P2}");
        Require(Math.Abs(actualMean - expectedMean) < 3.0,
            $"the {dark}/{light} region preserves its continuous-reference brightness ({expectedMean:F2} vs {actualMean:F2})");
        Require(Math.Abs(actualLightFraction - expectedLightFraction) < 0.025,
            $"the {dark}/{light} spatial mixture retains its transformed coverage ({expectedLightFraction:P2} vs {actualLightFraction:P2})");
    }

    private static void TestOrdinaryImageFallback(string directory)
    {
        RingImageLayoutOptions standard = new(58, 26, 56, OutputSize: 256);
        RingImageLayoutOptions preserved = standard with { PreserveDitherPalette = Levels };
        foreach (string kind in new[] { "continuous", "colored", "alpha", "previous-dark-gray", "previous-light-gray" })
        {
            string source = Path.Combine(directory, $"{kind}-source.png");
            WritePng(source, 37, 23, (x, y) => kind switch
            {
                "continuous" => ((byte)128, (byte)128, (byte)128, (byte)255),
                "colored" => ((byte)85, (byte)86, (byte)85, (byte)255),
                "alpha" => ((byte)0, (byte)0, (byte)0, (byte)127),
                "previous-dark-gray" => ((byte)85, (byte)85, (byte)85, (byte)255),
                _ => ((byte)170, (byte)170, (byte)170, (byte)255),
            });
            string reference = Path.Combine(directory, $"{kind}-reference.png");
            string actual = Path.Combine(directory, $"{kind}-preserved.png");
            RingImageProcessor.Render(source, reference, standard);
            RingImageProcessor.Render(source, actual, preserved);
            Require(ReadBgra(reference).SequenceEqual(ReadBgra(actual)),
                $"{kind} input follows the unchanged high-quality path, even when binary preservation is requested");
        }
    }

    private static void WritePng(string path, int width, int height,
        Func<int, int, (byte Blue, byte Green, byte Red, byte Alpha)> pixel)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            (byte blue, byte green, byte red, byte alpha) = pixel(x, y);
            int offset = (y * width + x) * 4;
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static byte[] ReadGray(string path)
    {
        byte[] pixels = ReadBgra(path);
        byte[] gray = new byte[pixels.Length / 4];
        for (int index = 0; index < gray.Length; index++)
        {
            int offset = index * 4;
            Require(pixels[offset] == pixels[offset + 1] && pixels[offset] == pixels[offset + 2]
                && pixels[offset + 3] == 255, "the grayscale ring is fully opaque and neutral");
            gray[index] = pixels[offset];
        }
        return gray;
    }

    private static byte[] ReadBgra(string path)
    {
        using FileStream input = File.OpenRead(path);
        BitmapFrame frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap bitmap = new(frame, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Ring dither: {message}.");
    }
}
