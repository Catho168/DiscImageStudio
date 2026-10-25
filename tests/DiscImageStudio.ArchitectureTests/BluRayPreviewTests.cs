using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Services;

internal static class BluRayPreviewTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-preview-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestProjection(directory);
            TestReadback(directory);
            TestHighResolution(directory);
            TestValidationAndCancellation(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("blu-ray-preview: passed (geometry projection, polarity, drawing bounds, batched ISO readback, exact high-resolution output, cancellation)");
    }

    private static void TestProjection(string directory)
    {
        string source = Path.Combine(directory, "source.png");
        byte[] pixels = new byte[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++) pixels[y * 64 + x] = x < 32 ? (byte)0 : (byte)255;
        SaveGray(source, pixels);
        string normal = Path.Combine(directory, "normal.png");
        BluRayDiscParameters disc = new();
        BluRayPreviewRenderer.RenderCalibration(source, normal, disc, disc, 0, disc.TotalSectors, 64, false);
        byte[] original = ReadBgra(normal);
        Require(Alpha(original, 32, 32) == 0 && Alpha(original, 0, 0) == 0,
            "the hub and outside-disc pixels remain transparent");
        Require(Level(original, 10, 32) == 0 && Level(original, 53, 32) == 255,
            "matching geometries preserve left/right source orientation");

        string inverted = Path.Combine(directory, "inverted.png");
        BluRayPreviewRenderer.RenderCalibration(source, inverted, disc, disc, 0, disc.TotalSectors, 64, true);
        byte[] inverse = ReadBgra(inverted);
        for (int i = 0; i < original.Length; i += 4)
            if (original[i + 3] != 0) Require(original[i] + inverse[i] == 255, "mapping inversion swaps every payload class");

        string rotated = Path.Combine(directory, "rotated.png");
        BluRayPreviewRenderer.RenderCalibration(source, rotated, disc, disc with { StartAngleDegrees = 90 },
            0, disc.TotalSectors, 64, false);
        byte[] rotation = ReadBgra(rotated);
        Require(Level(rotation, 32, 10) == 255 && Level(rotation, 32, 53) == 0,
            "actual start-angle adjustment rotates sector positions in the readback geometry");

        string limited = Path.Combine(directory, "limited.png");
        BluRayPreviewRenderer.RenderCalibration(source, limited, disc, disc,
            disc.TotalSectors / 2, disc.TotalSectors * 3 / 4, 64, false);
        byte[] bounded = ReadBgra(limited);
        Require(Level(bounded, 45, 32) == 128 && Alpha(bounded, 45, 32) == 255,
            "reserved inner sectors are neutral instead of source image content");
        Require(Level(bounded, 63, 32) == 128, "reserved outer sectors are neutral");
    }

    private static void TestReadback(string directory)
    {
        // A small image exercises many cluster seeds without creating a full 25 GB ISO.
        BluRayDiscParameters disc = new(TotalSectors: 1024);
        string iso = Path.Combine(directory, "readback.iso");
        string preview = Path.Combine(directory, "readback.png");
        foreach ((byte payload, byte expected) in new[]
        {
            (BluRayRunLengthMapping.ThreeTByte, (byte)0),
            (BluRayRunLengthMapping.SixTByte, (byte)255),
            ((byte)0x27, (byte)128),
        })
        {
            BluRayStreamingGenerator.GenerateFile(iso, new(disc, 0, disc.TotalSectors), new BluRayConstantByteMapping(payload));
            BluRayPreviewRenderer.RenderReadback(iso, preview, disc, 64);
            byte[] pixels = ReadBgra(preview);
            int filled = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0) continue;
                filled++;
                Require(pixels[i] == expected && pixels[i + 1] == expected && pixels[i + 2] == expected,
                    "ISO samples reapply each cluster's correct scrambler mask and recognize only AA/44");
            }
            Require(filled > 1500, "the sparse readback covers the disc annulus");
        }

        // Spatially varying payload verifies the inverse LDC/BIS lookup, which a
        // constant-byte fixture alone could not detect. Ignore the source boundary:
        // nearest-payload approximation spans parity/run-out intervals there.
        BluRayImagePayloadMapping split = new(disc, static (x, _) => x < 0 ? (byte)0 : (byte)255,
            new BluRayRunLengthMapping());
        BluRayStreamingGenerator.GenerateFile(iso, new(disc, 0, disc.TotalSectors), split);
        BluRayPreviewRenderer.RenderReadback(iso, preview, disc, 64);
        byte[] spatial = ReadBgra(preview);
        int compared = 0, matching = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                if (Alpha(spatial, x, y) == 0 || Math.Abs(x - 31.5) < 6) continue;
                compared++;
                if (Level(spatial, x, y) == (x < 32 ? 0 : 255)) matching++;
            }
        Require(compared > 1000 && matching >= compared * 0.98,
            "readback of a spatial payload retains source orientation through LDC/BIS addressing");
    }

    private static void TestValidationAndCancellation(string directory)
    {
        string output = Path.Combine(directory, "cancelled.png");
        BluRayDiscParameters disc = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => BluRayPreviewRenderer.RenderCalibration(
            "missing.png", output, disc, disc, 0, disc.TotalSectors, 64, false, cancellation.Token));
        Throws<OperationCanceledException>(() => BluRayPreviewRenderer.RenderReadback(
            "missing.iso", output, disc, 64, cancellation.Token));
        // Validate the upper bound without allocating or rendering an 8192-pixel image.
        Throws<OperationCanceledException>(() => BluRayPreviewRenderer.RenderCalibration(
            "missing.png", output, disc, disc, 0, disc.TotalSectors, 8192, false, cancellation.Token));
        Throws<OperationCanceledException>(() => BluRayPreviewRenderer.RenderReadback(
            "missing.iso", output, disc, 8192, cancellation.Token));
        Require(!File.Exists(output), "pre-cancelled renders create no preview file");
        Throws<ArgumentOutOfRangeException>(() => BluRayPreviewRenderer.RenderCalibration(
            "missing.png", output, disc, disc, 32, 16, 64, false));
        Throws<ArgumentOutOfRangeException>(() => BluRayPreviewRenderer.RenderReadback("missing.iso", output, disc, 1));
        foreach (int invalidSize in new[] { 63, 8193 })
        {
            Throws<ArgumentOutOfRangeException>(() => BluRayPreviewRenderer.RenderCalibration(
                "missing.png", output, disc, disc, 0, disc.TotalSectors, invalidSize, false));
            Throws<ArgumentOutOfRangeException>(() => BluRayPreviewRenderer.RenderReadback(
                "missing.iso", output, disc, invalidSize));
        }
        string shortIso = Path.Combine(directory, "short.iso");
        File.WriteAllBytes(shortIso, new byte[2048]);
        Throws<ArgumentException>(() => BluRayPreviewRenderer.RenderReadback(shortIso, output, disc, 64));
        Require(!File.Exists(output), "wrong-sized images fail before publishing a preview");
    }

    private static void TestHighResolution(string directory)
    {
        // An odd size above the previous 2048 cap detects rounding/clamping. This
        // also produces several read batches, including a partially filled final one.
        const int size = 2049;
        string source = Path.Combine(directory, "high-source.png");
        byte[] gray = new byte[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++) gray[y * 64 + x] = x < 32 ? (byte)0 : (byte)255;
        SaveGray(source, gray);
        BluRayDiscParameters disc = new();
        string projected = Path.Combine(directory, "high-projection.png");
        BluRayPreviewRenderer.RenderCalibration(source, projected, disc, disc, 0, disc.TotalSectors, size, false);
        byte[] projection = ReadBgra(projected, size);
        int left = size / 5, right = size * 4 / 5, center = size / 2;
        Require(Alpha(projection, left, center, size) == 255 && Level(projection, left, center, size) == 0
            && Alpha(projection, right, center, size) == 255 && Level(projection, right, center, size) == 255,
            "high-resolution projection keeps the requested pixel dimensions and source orientation");

        string iso = Path.Combine(directory, "high-readback.iso");
        string output = Path.Combine(directory, "high-readback.png");
        BluRayDiscParameters smallDisc = new(TotalSectors: 1024);
        BluRayStreamingGenerator.GenerateFile(iso, new(smallDisc, 0, smallDisc.TotalSectors),
            new BluRayConstantByteMapping(BluRayRunLengthMapping.ThreeTByte));
        BluRayPreviewRenderer.RenderReadback(iso, output, smallDisc, size);
        byte[] readback = ReadBgra(output, size);
        int painted = 0;
        for (int offset = 0; offset < readback.Length; offset += 4)
        {
            if (readback[offset + 3] == 0) continue;
            painted++;
            Require(readback[offset] == 0 && readback[offset + 1] == 0 && readback[offset + 2] == 0,
                "every completed high-resolution read batch preserves the decoded payload class");
        }
        Require(painted > size * size / 2, "all high-resolution read batches populate the annulus");
        Require(Alpha(readback, center, center, size) == 0 && Alpha(readback, 0, 0, size) == 0,
            "high-resolution readback preserves the transparent hub and disc exterior");
    }

    private static void SaveGray(string path, byte[] pixels)
    {
        BitmapSource bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Gray8, null, pixels, 64);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static byte[] ReadBgra(string path, int size = 64)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapSource source = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Require(source.PixelWidth == size && source.PixelHeight == size,
            $"preview output must be exactly {size} × {size}, not silently clamped or rounded");
        BitmapSource bitmap = source.Format == PixelFormats.Bgra32
            ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        return pixels;
    }

    private static byte Level(byte[] pixels, int x, int y, int size = 64) => pixels[(y * size + x) * 4];
    private static byte Alpha(byte[] pixels, int x, int y, int size = 64) => pixels[(y * size + x) * 4 + 3];
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blu-ray preview: " + message);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Blu-ray preview expected " + typeof(T).Name);
    }
}
