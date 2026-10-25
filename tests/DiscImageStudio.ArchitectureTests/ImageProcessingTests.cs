using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Controls;
using DiscImageStudio.Imaging;

internal static class ImageProcessingTests
{
    private static readonly byte[] Levels = [0, 255];
    private static readonly Type PreparationType = typeof(CalibrationPhotoCanvas).Assembly
        .GetType("DiscImageStudio.Services.RingImagePreparation", throwOnError: true)!;

    internal static void Run()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                using Fixture fixture = new();
                TestDisabledPassThrough(fixture);
                TestGammaAndTransparency(fixture);
                TestRingOrder(fixture);
                TestCleanup(fixture);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("image-processing: passed (switches, gamma coverage, layout order, transparency and cleanup)");
    }

    private static void TestDisabledPassThrough(Fixture fixture)
    {
        string source = fixture.Image("original", 31, 17, (_, _) => (73, 128, 194, 121));
        byte[] before = File.ReadAllBytes(source);
        using (Prepared prepared = fixture.Prepare(source, enabled: false, gamma: true))
        {
            Require(prepared.Path == source, "disabled processing passes the original path through even with gamma selected");
            Require(!fixture.TemporaryFiles().Any(), "disabled processing creates no temporary file");
        }
        Require(File.ReadAllBytes(source).SequenceEqual(before), "disposing a pass-through preserves the original image");
    }

    private static void TestGammaAndTransparency(Fixture fixture)
    {
        string source = fixture.Image("middle-gray", 128, 128, (_, _) => (128, 128, 128, 255));
        byte[] original = File.ReadAllBytes(source);
        double encodedCoverage;
        using (Prepared prepared = fixture.Prepare(source, enabled: true, gamma: false))
        {
            BitmapSource output = GrayscaleDither.LoadImage(prepared.Path);
            Require(output.PixelWidth == 128 && output.PixelHeight == 128,
                "direct image processing preserves source dimensions");
            Require(Math.Abs(output.DpiX - 144) < 1 && Math.Abs(output.DpiY - 72) < 1,
                "direct image processing preserves source DPI");
            encodedCoverage = WhiteCoverage(ReadGray(output));
        }
        using (Prepared prepared = fixture.Prepare(source, enabled: true, gamma: true))
        {
            double linearCoverage = WhiteCoverage(ReadGray(prepared.Path));
            Require(Math.Abs(encodedCoverage - 128.0 / 255) < 0.02,
                "gamma off preserves encoded middle-gray coverage");
            Require(Math.Abs(linearCoverage - Math.Pow((128.0 / 255 + 0.055) / 1.055, 2.4)) < 0.02,
                "gamma on distributes middle gray in linear light");
            Require(encodedCoverage - linearCoverage > 0.25, "the gamma switch changes the actual prepared pixels");
        }
        Require(File.ReadAllBytes(source).SequenceEqual(original), "processing never modifies the source image");
        Require(!fixture.TemporaryFiles().Any(), "disposing processed output cleans up its temporary PNG");

        string transparent = fixture.Image("transparent", 19, 11, (_, _) => (7, 101, 212, 0));
        foreach (bool gamma in new[] { false, true })
        {
            using Prepared prepared = fixture.Prepare(transparent, enabled: true, gamma: gamma);
            Require(ReadGray(prepared.Path).All(value => value == 255), "transparent pixels composite onto white");
        }
    }

    private static void TestRingOrder(Fixture fixture)
    {
        // Tiny black/white source pixels become continuous tones when each copy is
        // reduced; gamma must consume those tones before the one final dither pass.
        string source = fixture.Image("binary-pattern", 1024, 512, (x, y) =>
        {
            byte value = (x + y) % 2 == 0 ? (byte)0 : (byte)255;
            return (value, value, value, 255);
        });
        RingImageLayoutOptions layout = new(58, 26, 56, OutputSize: 256);
        string reference = fixture.PathFor("continuous-ring.png");
        RingImageProcessor.Render(source, reference, layout);
        BitmapSource resampled = GrayscaleDither.LoadImage(reference);
        byte[] expected = ReadGray(GrayscaleDither.Process(resampled, Levels, linearizeSrgb: true));
        using (Prepared prepared = fixture.Prepare(source, enabled: true, gamma: true, layout: layout))
        {
            Require(ReadGray(prepared.Path).SequenceEqual(expected),
                "ring processing applies gamma and dithering to the final resampled image exactly once");
        }

        string legacy = fixture.PathFor("legacy-ring.png");
        RingImageProcessor.Render(source, legacy, layout with { PreserveDitherPalette = Levels });
        byte[] legacyPixels = ReadGray(legacy);
        Require(!expected.SequenceEqual(legacyPixels), "the ring fixture detects lost gamma from premature dithering");
        using (Prepared prepared = fixture.Prepare(source, enabled: false, gamma: true, layout: layout))
        {
            Require(ReadGray(prepared.Path).SequenceEqual(legacyPixels),
                "disabled processing preserves the existing ring palette behavior and ignores gamma");
        }
        Require(!fixture.TemporaryFiles().Any(), "ring output is removed after disposal");
    }

    private static void TestCleanup(Fixture fixture)
    {
        string source = fixture.Image("cleanup", 32, 32, (_, _) => (100, 100, 100, 255));
        RingImageLayoutOptions layout = new(58, 26, 56, OutputSize: 256);
        using CancellationTokenSource alreadyCancelled = new();
        alreadyCancelled.Cancel();
        Throws<OperationCanceledException>(() => fixture.Prepare(source, true, true,
            cancellationToken: alreadyCancelled.Token), "already cancelled processing must stop");
        Require(!fixture.TemporaryFiles().Any(), "cancellation before processing leaves no temporary output");

        using CancellationTokenSource cancelledAfterLayout = new();
        Throws<OperationCanceledException>(() => fixture.Prepare(source, true, true, layout,
            cancelledAfterLayout.Token, _ => cancelledAfterLayout.Cancel()),
            "cancellation after ring layout must stop before returning a result");
        Require(!fixture.TemporaryFiles().Any(), "cancellation after layout removes the intermediate PNG");

        Throws<IOException>(() => fixture.Prepare(source, true, false, layout,
            appendLog: _ => throw new IOException("Injected failure after layout")),
            "failure after writing the layout must propagate");
        Require(!fixture.TemporaryFiles().Any(), "failure after layout removes the intermediate PNG");

        Throws<IOException>(() => fixture.Prepare(source, true, false,
            appendLog: _ => throw new IOException("Injected failure after dithering")),
            "failure after writing the dither result must propagate");
        Require(!fixture.TemporaryFiles().Any(), "failure after dithering removes the final temporary PNG");
        Require(File.Exists(source), "all cleanup paths preserve the source image");
    }

    private static double WhiteCoverage(byte[] pixels)
    {
        Require(pixels.All(value => value is 0 or 255), "processed output uses only black and white");
        return pixels.Count(value => value == 255) / (double)pixels.Length;
    }

    private static byte[] ReadGray(string path) => ReadGray(GrayscaleDither.LoadImage(path));

    private static byte[] ReadGray(BitmapSource source)
    {
        BitmapSource gray = source.Format == PixelFormats.Gray8
            ? source : new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyPixels(pixels, gray.PixelWidth, 0);
        return pixels;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Image processing: " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Image processing: expected " + typeof(T).Name + "; " + message);
    }

    private sealed class Prepared(object value) : IDisposable
    {
        internal string Path => (string)value.GetType().GetProperty("Path", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
        public void Dispose() => ((IDisposable)value).Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _prefix = "processing-test-" + Guid.NewGuid().ToString("N");
        private readonly string _directory;
        private readonly string _temporaryDirectory = (string)PreparationType.GetField("LivePreviewDirectory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

        internal Fixture()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), _prefix);
            Directory.CreateDirectory(_directory);
        }

        internal string PathFor(string name) => System.IO.Path.Combine(_directory, name);

        internal IEnumerable<string> TemporaryFiles() => Directory.Exists(_temporaryDirectory)
            ? Directory.EnumerateFiles(_temporaryDirectory, _prefix + "-*") : [];

        internal Prepared Prepare(string source, bool enabled, bool gamma,
            RingImageLayoutOptions? layout = null, CancellationToken cancellationToken = default,
            Action<string>? appendLog = null)
        {
            MethodInfo method = PreparationType.GetMethod("PrepareImage", BindingFlags.Static | BindingFlags.NonPublic)!;
            try
            {
                return new Prepared(method.Invoke(null,
                    [source, layout, _prefix, enabled, gamma, cancellationToken, true, appendLog])!);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        internal string Image(string name, int width, int height,
            Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
        {
            byte[] bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var value = pixel(x, y);
                int offset = (y * width + x) * 4;
                bgra[offset] = value.B;
                bgra[offset + 1] = value.G;
                bgra[offset + 2] = value.R;
                bgra[offset + 3] = value.A;
            }
            BitmapSource image = BitmapSource.Create(width, height, 144, 72, PixelFormats.Bgra32, null, bgra, width * 4);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(image));
            string path = PathFor(name + ".png");
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }

        public void Dispose()
        {
            foreach (string path in TemporaryFiles()) File.Delete(path);
            foreach (string path in Directory.EnumerateFiles(_directory)) File.Delete(path);
            Directory.Delete(_directory);
        }
    }
}