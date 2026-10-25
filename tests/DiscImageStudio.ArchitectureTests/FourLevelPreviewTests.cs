using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DvdImageSolver;

internal static class FourLevelPreviewTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"binary-dvd-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            TestDvdCalibrationBoundaries(directory);
            TestDvdPaletteMetadata(directory);
        }
        finally
        {
            foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }

        Console.WriteLine("binary-dvd-preview: passed (black/white DVD calibration boundaries and generated palette metadata)");
    }

    private static void TestDvdCalibrationBoundaries(string directory)
    {
        byte[] inputs = [0, 43, 85, 127, 128, 169, 170, 255];
        byte[] expectedLevels = [0, 0, 0, 0, 255, 255, 255, 255];
        for (int index = 0; index < inputs.Length; index++)
        {
            string source = Path.Combine(directory, $"source-{inputs[index]}.png");
            string output = Path.Combine(directory, $"preview-{inputs[index]}.png");
            WriteSolidPng(source, inputs[index]);
            Require(DvdCommandRunner.Run([
                "calibrate", "--image", source, "--output", output,
                "--total-sectors", "16", "--fill-sectors", "16",
                "--generated-inner-radius-mm", "24", "--generated-outer-radius-mm", "58",
                "--actual-inner-radius-mm", "24", "--actual-outer-radius-mm", "58",
                "--preview-size", "64", "--samples-per-sector", "512",
            ]) == 0, $"DVD calibration accepts source luminance {inputs[index]}");

            byte[] pixels = ReadBgraPixels(output);
            int opaque = 0;
            int visiblePattern = 0;
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset + 3] == 0) continue;
                opaque++;
                Require(pixels[offset] == pixels[offset + 1] && pixels[offset] == pixels[offset + 2],
                    "DVD calibration produces neutral grayscale pixels");
                // Unsampled portions of the annulus are white. Every sampled pixel
                // of a constant source must retain its one nominal palette level.
                if (pixels[offset] == 255) continue;
                visiblePattern++;
                Require(pixels[offset] == expectedLevels[index],
                    $"DVD calibration maps input {inputs[index]} to {expectedLevels[index]}, observed {pixels[offset]}");
            }

            Require(opaque > 0, "DVD calibration renders the annulus");
            Require(expectedLevels[index] == 255 ? visiblePattern == 0 : visiblePattern > 0,
                $"DVD calibration includes the expected content for input {inputs[index]}");
        }
    }

    private static void TestDvdPaletteMetadata(string directory)
    {
        string source = Path.Combine(directory, "metadata-source.png");
        string iso = Path.Combine(directory, "metadata.iso");
        WriteSolidPng(source, 85);
        Require(DvdCommandRunner.Run([
            "solve", "--image", source, "--iso-output", iso,
            "--total-sectors", "16", "--fill-sectors", "16",
            "--inner-radius-mm", "24", "--outer-radius-mm", "58",
            "--algorithm", "dispersion", "--fast-output", "true", "--fast-parallelism", "1",
        ]) == 0, "DVD fast generation succeeds with an intermediate gray source");
        Require(new FileInfo(iso).Length == 16 * 2048, "DVD test generation writes one complete payload block");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(iso + ".json"));
        JsonElement pool = document.RootElement.GetProperty("dispersionPool");
        Require(pool.GetProperty("blackByte").GetString() == "0xA5"
            && pool.GetProperty("whiteByte").GetString() == "0x92",
            "DVD palette metadata retains the requested endpoints");
        Require(pool.GetProperty("blackTransitionsPerCodeword").GetInt32() == 5
            && pool.GetProperty("whiteTransitionsPerCodeword").GetInt32() == 2,
            "DVD palette metadata reports the measured endpoint transition polarity");

        JsonElement levels = pool.GetProperty("levels");
        Require(levels.GetArrayLength() == 2, "DVD palette metadata describes only black and white");
        string[] scrambledBytes = ["0xA5", "0x92"];
        int[] previewLevels = [0, 255];
        int[] minimums = [0, 128];
        int[] maximums = [127, 255];
        int[] transitions = [5, 2];
        for (int index = 0; index < 2; index++)
        {
            JsonElement level = levels[index];
            Require(level.GetProperty("inputMinimum").GetInt32() == minimums[index]
                && level.GetProperty("inputMaximum").GetInt32() == maximums[index]
                && level.GetProperty("previewLuminance").GetInt32() == previewLevels[index]
                && level.GetProperty("scrambledByte").GetString() == scrambledBytes[index]
                && level.GetProperty("transitionsPerCodeword").GetInt32() == transitions[index],
                $"DVD palette metadata records the complete mapping for level {index}");
        }
    }

    private static void WriteSolidPng(string path, byte luminance)
    {
        const int size = 65;
        byte[] pixels = Enumerable.Repeat(luminance, size * size).ToArray();
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static byte[] ReadBgraPixels(string path)
    {
        using FileStream input = File.OpenRead(path);
        BitmapSource frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        FormatConvertedBitmap bitmap = new(frame, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
