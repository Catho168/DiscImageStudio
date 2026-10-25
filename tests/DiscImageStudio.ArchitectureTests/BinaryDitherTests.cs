using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;
using DiscImageStudio.Imaging;

internal static class BinaryDitherTests
{
    private static readonly byte[] Levels = [0, 255];

    internal static void Run()
    {
        TestBinaryBoundaries();
        TestBlackWhiteMixtures();
        TestPaletteAndCancellation();
        TestPngRoundTrip();
        Console.WriteLine("binary-dither: passed (0/255 boundaries, black/white spatial mixtures, idempotence, cancellation and strict PNG output)");
    }

    private static void TestBinaryBoundaries()
    {
        Require(BinaryGrayscale.Levels.SequenceEqual(Levels), "the active palette is black and white only");
        for (int value = 0; value <= 255; value++)
        {
            byte input = (byte)value;
            byte expected = value < 128 ? (byte)0 : (byte)255;
            byte[] actual = GrayscaleDither.ProcessPixels([input, input, input, 255], 1, 1, Levels);
            Require(actual[0] == expected, $"single-pixel input {value} chooses the closest black/white endpoint");
            Require(BinaryGrayscale.GetLuminance(BinaryGrayscale.GetLevelIndex(input)) == expected,
                $"the encoder's binary mapping agrees at input {value}");
        }
    }

    private static void TestBlackWhiteMixtures()
    {
        const int width = 257;
        const int height = 251;
        foreach (byte tone in new byte[] { 32, 64, 128, 192, 224 })
        {
            byte[] input = Enumerable.Repeat(tone, width * height).ToArray();
            byte[] actual = GrayscaleDither.ProcessPixels(ToBgra(input), width, height, Levels);
            Require(actual.All(value => value is 0 or 255), "spatial mixtures never introduce gray pixels");
            double expectedWhiteFraction = tone / 255.0;
            double actualWhiteFraction = actual.Count(value => value == 255) / (double)actual.Length;
            Require(Math.Abs(actualWhiteFraction - expectedWhiteFraction) < 0.005,
                $"tone {tone} preserves white coverage {expectedWhiteFraction:P2}, observed {actualWhiteFraction:P2}");
            Require(Math.Abs(actual.Average(value => (double)value) - tone) < 1.0,
                $"tone {tone} preserves its average luminance through black/white mixing");
        }
    }

    private static void TestPaletteAndCancellation()
    {
        byte[] input = Enumerable.Range(0, 35).Select(index => Levels[index % 2]).ToArray();
        byte[] actual = GrayscaleDither.ProcessPixels(ToBgra(input), 7, 5, Levels);
        Require(actual.SequenceEqual(input), "existing binary pixels are unchanged by redithering");
        Require(GrayscaleDither.ProcessPixels([0, 0, 0, 0], 1, 1, Levels)[0] == 255,
            "transparent input composites against white");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        bool cancelled = false;
        try { GrayscaleDither.ProcessPixels([0, 0, 0, 255], 1, 1, Levels, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "cancelled binary processing returns no image");
    }

    private static void TestPngRoundTrip()
    {
        const int width = 19;
        const int height = 11;
        byte[] input = ToBgra(Enumerable.Range(0, width * height).Select(index => (byte)(index * 43 % 256)).ToArray());
        BitmapSource source = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, input, width * 4);
        BitmapSource result = GrayscaleDither.Process(source, Levels);
        byte[] expected = GrayscaleDither.ProcessPixels(input, width, height, Levels);
        Require(result.PixelWidth == width && result.PixelHeight == height
            && Math.Abs(result.DpiX - 144) < 0.01 && Math.Abs(result.DpiY - 144) < 0.01,
            "binary processing keeps image dimensions and DPI");
        Require(result.Format == PixelFormats.Gray8 && result.IsFrozen,
            "the binary result is a frozen Gray8 bitmap");
        Require(ReadGray(result).SequenceEqual(expected), "bitmap processing matches raw pixel processing");

        string directory = Path.Combine(Path.GetTempPath(), $"binary-dither-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "binary.png");
            GrayscaleDither.SavePng(result, path, Levels);
            BitmapSource loaded = GrayscaleDither.LoadImage(path);
            byte[] actual = ReadGray(loaded);
            Require(actual.SequenceEqual(expected) && actual.All(value => value is 0 or 255),
                "the saved PNG retains exact binary pixels");
            Require(ReadGray(GrayscaleDither.Process(loaded, Levels)).SequenceEqual(expected),
                "reopening binary output preserves every pixel");

            string invalidPath = Path.Combine(directory, "invalid-gray.png");
            BitmapSource invalid = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new byte[] { 85 }, 1);
            bool rejected = false;
            try { GrayscaleDither.SavePng(invalid, invalidPath, Levels); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected && !File.Exists(invalidPath), "binary PNG saving rejects an intermediate gray");
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
        if (!condition) throw new InvalidOperationException($"Binary dither: {message}.");
    }
}
