using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Imaging;

internal static class GammaDitherTests
{
    private static readonly byte[] Binary = [0, 255];
    private const int FlatWidth = 512;
    private const int FlatHeight = 511;

    internal static void Run()
    {
        TestDisabledRetainsLegacyPixels();
        TestLinearGrayCoverage();
        TestDecodeChannelsBeforeLuminance();
        TestLinearAlpha();
        TestEncodedPalettes();
        TestBitmapAndCancellation();
        Console.WriteLine("gamma-dither: passed (default pixel compatibility, sRGB light coverage, per-channel decoding, linear alpha, encoded palettes, dimensions and cancellation)");
    }

    private static void TestDisabledRetainsLegacyPixels()
    {
        byte[] input =
        [
            20, 20, 20, 255, 50, 50, 50, 255, 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255,
            30, 30, 30, 255, 80, 80, 80, 255, 130, 130, 130, 255, 180, 180, 180, 255, 230, 230, 230, 255,
            0, 0, 0, 0, 0, 0, 0, 127, 0, 0, 0, 128, 0, 0, 255, 128, 255, 0, 0, 127,
            255, 255, 255, 255, 60, 60, 60, 255, 110, 110, 110, 255, 160, 160, 160, 255, 210, 210, 210, 255,
        ];
        // Independent exact-rational reference: encoded RGB weights 2126/7152/722
        // over 10000, white compositing, and mirrored Floyd-Steinberg fractions.
        byte[] expected =
        [
            0, 0, 0, 255, 0,
            0, 255, 0, 255, 255,
            255, 0, 255, 0, 255,
            255, 0, 0, 255, 255,
        ];
        byte[] omitted = GrayscaleDither.ProcessPixels(input, 5, 4, Binary);
        byte[] disabled = GrayscaleDither.ProcessPixels(input, 5, 4, Binary, linearizeSrgb: false);
        Require(omitted.SequenceEqual(expected) && disabled.SequenceEqual(expected),
            "omitting gamma and explicitly disabling it both preserve the legacy encoded-space pixels");
    }

    private static void TestLinearGrayCoverage()
    {
        // Independent sRGB reference values, normalized to white=1. They are
        // light fractions, not encoded bytes or a simple power-2.2 approximation.
        foreach ((byte gray, double expected) in new (byte, double)[]
        {
            (32, 0.014443843596092545),
            (64, 0.05126945837404324),
            (128, 0.21586050011389926),
            (192, 0.5271151257058131),
        })
        {
            byte[] result = ProcessFlat(gray, gray, gray, 255, Binary);
            Require(result.All(value => value is 0 or 255), "gamma changes dot density, never the output palette");
            double coverage = result.Count(value => value == 255) / (double)result.Length;
            Near(expected, coverage, 0.002,
                $"gamma-enabled flat {gray} conserves its sRGB linear light as white-dot coverage");
        }

        Require(GrayscaleDither.ProcessPixels([128, 128, 128, 255], 1, 1, Binary)[0] == 255
            && GrayscaleDither.ProcessPixels([128, 128, 128, 255], 1, 1, Binary, linearizeSrgb: true)[0] == 0,
            "a single encoded middle-gray pixel is below half light only with gamma enabled");
        Require(GrayscaleDither.ProcessPixels([187, 187, 187, 255], 1, 1, Binary, linearizeSrgb: true)[0] == 0
            && GrayscaleDither.ProcessPixels([188, 188, 188, 255], 1, 1, Binary, linearizeSrgb: true)[0] == 255,
            "the gamma-enabled one-pixel boundary is between encoded 187 and 188");
    }

    private static void TestDecodeChannelsBeforeLuminance()
    {
        foreach ((byte blue, byte green, byte red, double expected) in new (byte, byte, byte, double)[]
        {
            (0, 0, 255, 0.2126),
            (0, 255, 0, 0.7152),
            (255, 0, 0, 0.0722),
            (0, 128, 0, 0.15438342968146074),
        })
        {
            byte[] result = ProcessFlat(blue, green, red, 255, Binary);
            double coverage = result.Count(value => value == 255) / (double)result.Length;
            Near(expected, coverage, 0.002,
                $"BGRA({blue},{green},{red},255) decodes each color channel before luminance weighting");
        }
        // In encoded values green128 is brighter than red255; in linear light
        // their order reverses. Decoding the already weighted gray misses this.
    }

    private static void TestLinearAlpha()
    {
        foreach ((byte blue, byte green, byte red, byte alpha, double expected) in new (byte, byte, byte, byte, double)[]
        {
            (0, 0, 0, 128, 127.0 / 255),
            (0, 0, 255, 128, 0.6047560784313726),
        })
        {
            byte[] result = ProcessFlat(blue, green, red, alpha, Binary);
            Near(expected, result.Count(value => value == 255) / (double)result.Length, 0.002,
                "alpha composites against white in linear light rather than decoding an encoded-space composite");
        }
        Require(GrayscaleDither.ProcessPixels([255, 18, 73, 0], 1, 1, Binary, linearizeSrgb: true)[0] == 255,
            "fully transparent colored input is white");
    }

    private static void TestEncodedPalettes()
    {
        foreach (byte[] palette in new byte[][] { Binary, [0, 85, 255], [32, 128, 224] })
        {
            byte[] input = Enumerable.Range(0, 63).Select(index => palette[index % palette.Length]).ToArray();
            byte[] result = GrayscaleDither.ProcessPixels(ToBgra(input), 9, 7, palette, linearizeSrgb: true);
            Require(result.SequenceEqual(input), "gamma-enabled processing keeps existing encoded palette pixels unchanged");
        }

        byte[] threeLevels = [0, 85, 255];
        byte[] three = ProcessFlat(128, 128, 128, 255, threeLevels);
        Require(three.All(value => value is 0 or 85 or 255), "custom palette output uses the supplied encoded bytes");
        double threeMean = three.Average(value => value switch { 0 => 0.0, 85 => 0.09084171118340768, _ => 1.0 });
        Near(0.21586050011389926, threeMean, 0.002, "nonuniform encoded palette diffuses errors in linear light");

        byte[] limitedLevels = [32, 128, 224];
        byte[] limited = ProcessFlat(100, 100, 100, 255, limitedLevels);
        Require(limited.All(value => value is 32 or 128 or 224), "custom palettes need not contain black or white");
        double limitedMean = limited.Average(value => value switch
        {
            32 => 0.014443843596092545,
            128 => 0.21586050011389926,
            _ => 0.7454042095403874,
        });
        Near(0.12743768043564743, limitedMean, 0.002, "a limited palette preserves achievable linear light");
        Require(GrayscaleDither.ProcessPixels([95, 95, 95, 255], 1, 1, limitedLevels, linearizeSrgb: true)[0] == 32
            && GrayscaleDither.ProcessPixels([96, 96, 96, 255], 1, 1, limitedLevels, linearizeSrgb: true)[0] == 128,
            "nearest selection also decodes the output palette rather than comparing with its encoded byte values");

        byte[] toe = ProcessFlat(5, 5, 5, 255, [0, 10, 255]);
        Require(toe.All(value => value is 0 or 10), "the sRGB linear segment remains in its neighboring dark tones");
        Near(0.5, toe.Count(value => value == 10) / (double)toe.Length, 0.005,
            "sRGB values 0..10 use the linear segment, making encoded5 half the light of encoded10");
    }

    private static void TestBitmapAndCancellation()
    {
        const int width = 19, height = 11;
        byte[] pixels = ToBgra(Enumerable.Range(0, width * height).Select(index => (byte)(index * 43 % 256)).ToArray());
        BitmapSource source = BitmapSource.Create(width, height, 144, 120, PixelFormats.Bgra32, null, pixels, width * 4);
        BitmapSource result = GrayscaleDither.Process(source, Binary, linearizeSrgb: true);
        Require(result.PixelWidth == width && result.PixelHeight == height && result.Format == PixelFormats.Gray8 && result.IsFrozen,
            "gamma processing preserves dimensions and returns a frozen Gray8 bitmap");
        Near(144, result.DpiX, 0.01, "horizontal DPI is retained");
        Near(120, result.DpiY, 0.01, "vertical DPI is retained");
        byte[] actual = new byte[width * height];
        result.CopyPixels(actual, width, 0);
        Require(actual.SequenceEqual(GrayscaleDither.ProcessPixels(pixels, width, height, Binary, linearizeSrgb: true)),
            "bitmap processing forwards the enabled gamma option to the pixel path");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ThrowsCancelled(() => GrayscaleDither.ProcessPixels(pixels, width, height, Binary, cancellation.Token, linearizeSrgb: true));
        ThrowsCancelled(() => GrayscaleDither.Process(source, Binary, cancellation.Token, linearizeSrgb: true));
    }

    private static byte[] ProcessFlat(byte blue, byte green, byte red, byte alpha, byte[] palette)
    {
        byte[] pixels = new byte[FlatWidth * FlatHeight * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }
        return GrayscaleDither.ProcessPixels(pixels, FlatWidth, FlatHeight, palette, linearizeSrgb: true);
    }

    private static byte[] ToBgra(byte[] gray)
    {
        byte[] result = new byte[gray.Length * 4];
        for (int index = 0; index < gray.Length; index++)
        {
            result[index * 4] = result[index * 4 + 1] = result[index * 4 + 2] = gray[index];
            result[index * 4 + 3] = 255;
        }
        return result;
    }

    private static void ThrowsCancelled(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Gamma dither: a cancelled request returned an image.");
    }

    private static void Near(double expected, double actual, double tolerance, string message)
        => Require(Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected:R}, observed {actual:R}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Gamma dither: {message}.");
    }
}
