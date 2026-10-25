using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;
using DiscImageStudio.Imaging;

internal static class ThreeLevelDitherTests
{
    private static readonly byte[] Levels = [0, 85, 255];

    internal static void Run()
    {
        TestNonuniformBoundaries();
        TestSerpentineFixture();
        TestUnequalToneMixtures();
        TestPaletteAndCancellation();
        TestBitmapAndPngRoundTrip();
        Console.WriteLine("three-level-dither: passed (0/85/255 nearest boundaries, nonuniform error diffusion, mixtures, palette preservation, cancellation and PNG)");
    }

    private static void TestNonuniformBoundaries()
    {
        Require(ThreeLevelGrayscale.MiddleLuminance == 85 && ThreeLevelGrayscale.Levels.SequenceEqual(Levels),
            "the nominal dark gray is 85, not an evenly spaced midpoint");
        for (int value = 0; value <= 255; value++)
        {
            // Select by distance to the independent expected palette; brighter
            // wins ties. This also exercises every encoder quantizer input.
            byte expected = Levels.OrderBy(level => Math.Abs(value - level)).ThenByDescending(level => level).First();
            byte input = (byte)value;
            byte[] result = GrayscaleDither.ProcessPixels([input, input, input, 255], 1, 1, Levels);
            Require(result[0] == expected, $"a one-pixel source {value} selects the nearest actual palette tone");
            Require(ThreeLevelGrayscale.Quantize(input) == expected,
                $"encoding and dithering share the nonuniform nearest-level boundaries at input {value}");
            Require(ThreeLevelGrayscale.GetLuminance(ThreeLevelGrayscale.GetLevelIndex(input)) == expected,
                "the public palette index and luminance agree");
        }

        Require(ThreeLevelGrayscale.Quantize(42) == 0 && ThreeLevelGrayscale.Quantize(43) == 85
            && ThreeLevelGrayscale.Quantize(169) == 85 && ThreeLevelGrayscale.Quantize(170) == 255,
            "the nearest bins are 0..42, 43..169 and 170..255");
    }

    private static void TestSerpentineFixture()
    {
        byte[] input =
        [
            20, 50, 100, 150, 220,
            30, 80, 130, 180, 230,
            40, 90, 140, 190, 240,
            10, 60, 110, 160, 210,
        ];
        // Computed independently using exact rational error fractions and the
        // 7/16, 3/16, 5/16, 1/16 stencil, reflected on alternate rows.
        byte[] expected =
        [
            0, 85, 85, 85, 255,
            0, 85, 85, 255, 255,
            85, 85, 85, 255, 255,
            0, 85, 85, 85, 255,
        ];
        byte[] result = GrayscaleDither.ProcessPixels(ToBgra(input), 5, 4, Levels);
        Require(result.SequenceEqual(expected), "diffusion uses errors from the actual unequal palette spacing");
    }

    private static void TestUnequalToneMixtures()
    {
        const int width = 257;
        const int height = 251;
        foreach ((byte input, byte dark, byte light) in new (byte, byte, byte)[] { (42, 0, 85), (128, 85, 255), (170, 85, 255) })
        {
            byte[] result = GrayscaleDither.ProcessPixels(ToBgra(Enumerable.Repeat(input, width * height).ToArray()),
                width, height, Levels);
            Require(result.All(value => value == dark || value == light), "a flat tone mixes its neighboring palette levels");
            double expectedLightFraction = (input - dark) / (double)(light - dark);
            double actualLightFraction = result.Count(value => value == light) / (double)result.Length;
            Require(Math.Abs(actualLightFraction - expectedLightFraction) < 0.005,
                $"flat {input} retains the expected {expectedLightFraction:P2} light pixels, observed {actualLightFraction:P2}");
            Require(Math.Abs(result.Average(value => (double)value) - input) < 1.0,
                $"flat {input} retains average brightness with nonuniform levels");
        }

        // In particular 128 is about 75% B9 and 25% white; treating B9 as
        // middle gray would incorrectly produce a nearly solid B9 image.
    }

    private static void TestPaletteAndCancellation()
    {
        byte[] source = Enumerable.Range(0, 35).Select(index => Levels[index % Levels.Length]).ToArray();
        byte[] result = GrayscaleDither.ProcessPixels(ToBgra(source), 7, 5, Levels);
        Require(result.SequenceEqual(source), "already dithered 0/85/255 pixels remain unchanged");
        Require(GrayscaleDither.ProcessPixels([0, 0, 0, 0], 1, 1, Levels)[0] == 255,
            "transparent pixels composite onto white in three-level mode");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        bool cancelled = false;
        try { GrayscaleDither.ProcessPixels([0, 0, 0, 255], 1, 1, Levels, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "pre-cancelled three-level processing returns no image");
    }

    private static void TestBitmapAndPngRoundTrip()
    {
        const int width = 19;
        const int height = 11;
        byte[] input = ToBgra(Enumerable.Range(0, width * height).Select(index => (byte)(index * 43 % 256)).ToArray());
        BitmapSource source = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, input, width * 4);
        BitmapSource result = GrayscaleDither.Process(source, Levels);
        Require(result.PixelWidth == width && result.PixelHeight == height
            && Math.Abs(result.DpiX - 144) < 0.01 && Math.Abs(result.DpiY - 144) < 0.01,
            "processing retains original dimensions and DPI");
        Require(result.IsFrozen && result.Format == PixelFormats.Gray8, "the result is a frozen Gray8 bitmap");
        byte[] expected = GrayscaleDither.ProcessPixels(input, width, height, Levels);
        Require(ReadGray(result).SequenceEqual(expected), "bitmap and raw pixel processing agree");
        Require(ReadGray(GrayscaleDither.Process(result, Levels)).SequenceEqual(expected),
            "reopening a three-level bitmap does not redither its existing pixels");

        string directory = Path.Combine(Path.GetTempPath(), $"three-level-dither-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "three-level.png");
            GrayscaleDither.SavePng(result, path, Levels);
            BitmapSource loaded = GrayscaleDither.LoadImage(path);
            byte[] actual = ReadGray(loaded);
            Require(actual.All(value => value is 0 or 85 or 255) && actual.SequenceEqual(expected),
                "the saved PNG contains exactly the processed three-level pixels");
            using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(exclusive.Length > 0, "loading leaves the PNG unlocked");
        }
        finally
        {
            foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static byte[] ToBgra(byte[] gray)
    {
        byte[] pixels = new byte[gray.Length * 4];
        for (int index = 0; index < gray.Length; index++)
        {
            pixels[index * 4] = pixels[index * 4 + 1] = pixels[index * 4 + 2] = gray[index];
            pixels[index * 4 + 3] = 255;
        }
        return pixels;
    }

    private static byte[] ReadGray(BitmapSource source)
    {
        BitmapSource gray = source.Format == PixelFormats.Gray8 ? source : new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        byte[] result = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyPixels(result, gray.PixelWidth, 0);
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Three-level dither: {message}.");
    }
}
