using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.ViewModels;
using DvdImageSolver.Solver;

internal static class ToneTests
{
    internal static void Run()
    {
        TestBinaryMapping();
        TestPhotographicDefault();
        TestDitherReproducesGreyLevels();
        TestFourLevelPalette();
        TestDitherIsDeterministic();
        TestDvdThresholdAndDither();
        TestDvdRejectsFourLevels();
        TestGenerationAndPreviewAgree();
        TestProjectToneRoundtrip();
    }

    /// <summary>The shipped default and the "restore recommended" action have to agree:
    /// 75% dither, two levels, no brightness shift.</summary>
    private static void TestPhotographicDefault()
    {
        True(
            Math.Abs(ToneProfile.Photo.DitherStrength - 0.75) <= 1e-9,
            "photographic tone profile uses 75% dither");
        True(
            ToneProfile.Photo.Levels == ToneProfile.TwoLevels
            && ToneProfile.Photo.Brightness == 0,
            "photographic tone profile keeps two levels and neutral brightness");
        True(
            Math.Abs(new DiscParametersState().ToneDitherStrength - 0.75) <= 1e-9,
            "a new parameter state starts at 75% dither");
    }

    private static void TestBinaryMapping()
    {
        for (int grey = 0; grey <= 255; grey++)
        {
            for (long index = 0; index < 64; index += 7)
            {
                byte expected = grey < 128 ? (byte)0x0D : (byte)0xAA;
                byte actual = CdToneMapping.Map((byte)grey, ToneProfile.LegacyBinary, index);
                True(actual == expected, $"binary mapping at grey {grey}");
            }
        }
    }

    /// <summary>Full-strength dither must integrate back to the source grey at viewing scale;
    /// this is the property that turns the hard stencil into a photograph.</summary>
    private static void TestDitherReproducesGreyLevels()
    {
        // Full strength is the reconstruction property of the dither itself; the shipped
        // default (75%) deliberately keeps a quarter of the grain away and is checked below.
        ToneProfile tone = new(1, ToneProfile.TwoLevels, 0);
        const int samples = 32768;
        foreach (int grey in new[] { 0, 32, 64, 96, 128, 160, 192, 224, 255 })
        {
            int sum = 0;
            for (long index = 0; index < samples; index++)
            {
                sum += CdToneMapping.DisplayGray(
                    CdToneMapping.Map((byte)grey, tone, index));
            }

            double average = sum / (double)samples;
            double expected = Math.Clamp(grey, CdToneMapping.BlackGray, CdToneMapping.WhiteGray);
            True(
                Math.Abs(average - expected) <= 1.5,
                $"2-level dither average at grey {grey}: {average:F2} vs {expected:F2}");
        }

        ToneProfile fourLevels = tone with { Levels = ToneProfile.FourLevels };
        foreach (int grey in new[] { 32, 64, 96, 128, 160, 192, 224 })
        {
            int sum = 0;
            for (long index = 0; index < samples; index++)
            {
                sum += CdToneMapping.DisplayGray(
                    CdToneMapping.Map((byte)grey, fourLevels, index));
            }

            double average = sum / (double)samples;
            True(
                Math.Abs(average - grey) <= 1.5,
                $"4-level dither average at grey {grey}: {average:F2}");
        }
    }

    private static void TestFourLevelPalette()
    {
        ToneProfile tone = ToneProfile.Photo with { Levels = ToneProfile.FourLevels };
        HashSet<byte> seen = [];
        for (long index = 0; index < 4096; index++)
        {
            for (int grey = 0; grey <= 255; grey++)
            {
                seen.Add(CdToneMapping.Map((byte)grey, tone, index));
            }
        }

        True(
            seen.SetEquals([0x0D, 0x10, 0x63, 0xAA]),
            "4-level mapping emits only the four palette bytes");
        True(
            CdToneMapping.DisplayGray(0x21) == 96 && CdToneMapping.DisplayGray(0x28) == 160,
            "the two intermediate palette bytes keep their preview greys");
    }

    private static void TestDitherIsDeterministic()
    {
        ToneProfile tone = new(0.73, 2, 12);
        for (long index = 0; index < 1024; index++)
        {
            byte first = CdToneMapping.Map(140, tone, index);
            byte second = CdToneMapping.Map(140, tone, index);
            True(first == second, "tone mapping is deterministic per logical sample");
        }

        True(
            ToneDither.Phase(0) != ToneDither.Phase(1)
            && ToneDither.Phase(12345) is >= 0 and < 1,
            "dither phase stays in [0, 1) and varies per sample");
    }

    private static void TestDvdThresholdAndDither()
    {
        byte threshold = 128;
        for (int luminance = 0; luminance <= 255; luminance++)
        {
            bool white = DvdToneMapping.UseWhite(
                (byte)luminance,
                threshold,
                ToneProfile.LegacyBinary,
                17);
            True(
                white == (luminance >= threshold),
                $"DVD legacy threshold at luminance {luminance}");
        }

        const int samples = 8192;
        ToneProfile fullDither = new(1, ToneProfile.TwoLevels, 0);
        foreach (int luminance in new[] { 0, 32, 64, 128, 192, 224, 255 })
        {
            int whiteCount = 0;
            for (long index = 0; index < samples; index++)
            {
                if (DvdToneMapping.UseWhite((byte)luminance, threshold, fullDither, index))
                {
                    whiteCount++;
                }
            }

            double fraction = whiteCount / (double)samples;
            double expected = Math.Clamp(luminance / 255.0, 0, 1);
            True(
                Math.Abs(fraction - expected) <= 0.015,
                $"DVD dither white fraction at luminance {luminance}: {fraction:F3} vs {expected:F3}");
        }
    }

    private static void TestDvdRejectsFourLevels()
    {
        DvdStreamingOptions options = new(
            TotalSectors: 16,
            InnerRadiusMm: 24,
            OuterRadiusMm: 58,
            Tone: new ToneProfile(1, ToneProfile.FourLevels, 0));
        Throws<ArgumentOutOfRangeException>(
            options.Validate,
            "DVD dispersion refuses the CD-only 4-level palette");
    }

    /// <summary>Generating a tone-mapped track and projecting the same source with the same
    /// parameters has to produce the same pixels: the projection is only useful if it is the
    /// generation model, not a lookalike.</summary>
    private static void TestGenerationAndPreviewAgree()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"disc-tone-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "gradient.png");
            WriteGradientPng(sourcePath);
            CdDiscParameters parameters = new(
                InnerRadiusMm: 24.5,
                OuterRadiusMm: 56.8,
                Sectors: 8,
                LinearVelocityMmPerSecond: 1200,
                ImageOuterRadiusMm: 58);

            string legacyPath = Path.Combine(directory, "legacy.raw");
            string legacyExplicitPath = Path.Combine(directory, "legacy-explicit.raw");
            CdTrackGenerator.Generate(sourcePath, legacyPath, parameters, interleave: false);
            CdTrackGenerator.Generate(
                sourcePath,
                legacyExplicitPath,
                parameters,
                interleave: false,
                tone: ToneProfile.LegacyBinary);
            True(
                File.ReadAllBytes(legacyPath).SequenceEqual(File.ReadAllBytes(legacyExplicitPath)),
                "omitting the tone option is byte-identical to the legacy binary profile");

            ToneProfile tone = new(1, ToneProfile.FourLevels, 0);
            string tonePath = Path.Combine(directory, "tone.raw");
            CdTrackGenerator.Generate(
                sourcePath,
                tonePath,
                parameters,
                interleave: false,
                tone: tone);
            byte[] bytes = File.ReadAllBytes(tonePath);
            True(
                bytes.All(value => value is 0x0D or 0x10 or 0x63 or 0xAA),
                "generated 4-level track contains only palette bytes");
            True(
                new byte[] { 0x0D, 0x10, 0x63, 0xAA }.All(value => bytes.Contains(value)),
                "a smooth gradient exercises all four palette levels");

            const int previewSize = 400;
            const int samplesPerSector = 16;
            string trackPreview = Path.Combine(directory, "track.png");
            string warpPreview = Path.Combine(directory, "warp.png");
            CdTrackGenerator.PreviewTrack(
                tonePath,
                trackPreview,
                parameters,
                previewSize,
                CdDiscParameters.BytesPerSector / samplesPerSector,
                deinterleave: false);
            CdTrackGenerator.PreviewWarp(
                sourcePath,
                warpPreview,
                parameters,
                parameters,
                previewSize,
                samplesPerSector,
                tone);
            byte[] trackPixels = ReadPngPixels(trackPreview);
            byte[] warpPixels = ReadPngPixels(warpPreview);
            int firstDifference = Enumerable.Range(0, trackPixels.Length)
                .FirstOrDefault(index => trackPixels[index] != warpPixels[index]);
            True(
                trackPixels.SequenceEqual(warpPixels),
                $"tone-mapped read-back and source projection agree pixel for pixel"
                + $" (first difference at {firstDifference}: {trackPixels[firstDifference]} vs {warpPixels[firstDifference]})");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestProjectToneRoundtrip()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"disc-tone-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "tone.discproj");
            ProjectDocument document = new ProjectDocument(
                ProjectJsonStore.CurrentSchemaVersion,
                "影调工程",
                ProjectDocument.CdFamily,
                new ProjectGeometry(24.5, 56.8, 359_849),
                [],
                new ProjectOutputs(Path.Combine(directory, "out.wav"), true, string.Empty, string.Empty, true),
                CalibrationSessionPath: null,
                ThumbnailPngBase64: null,
                CreatedUtc: DateTimeOffset.Now,
                ModifiedUtc: DateTimeOffset.Now,
                Tone: new ToneProfile(0.65, ToneProfile.FourLevels, -12));
            ProjectJsonStore.Save(path, document);
            ProjectDocument loaded = ProjectJsonStore.Load(path);
            True(
                loaded.Tone == document.Tone,
                "project tone settings survive a save/load round trip");

            // A file written before the tone section existed keeps the legacy binary mapping.
            JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            True(json.ContainsKey("tone"), "saved project contains a tone section");
            json.Remove("tone");
            File.WriteAllText(
                path,
                json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            ProjectDocument legacy = ProjectJsonStore.Load(path);
            True(legacy.Tone is null, "absent tone section loads as null (legacy binary)");

            ProjectDocument invalid = document with
            {
                Tone = new ToneProfile(1.5, ToneProfile.TwoLevels, 0),
            };
            Throws<InvalidDataException>(() => ProjectJsonStore.Validate(invalid), "invalid dither strength rejected");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteGradientPng(string path)
    {
        const int size = 256;
        const int stride = size * 4;
        byte[] pixels = new byte[size * stride];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte level = (byte)((x + y) / 2);
                int offset = (y * stride) + (x * 4);
                pixels[offset] = level;
                pixels[offset + 1] = level;
                pixels[offset + 2] = level;
                pixels[offset + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static byte[] ReadPngPixels(string path)
    {
        using FileStream input = File.OpenRead(path);
        BitmapFrame frame = BitmapFrame.Create(
            input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap converted = new(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void True(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("tone: " + name);
        }
    }

    private static void Throws<TException>(Action action, string name)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"tone: {name}: expected {typeof(TException).Name}.");
    }
}
