using System.IO;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;

internal static class BluRaySidecarTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestRoundtrip(directory);
            TestRejection(directory);
            TestModuleWritesSidecar(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("blu-ray-sidecar: passed (plan recovery, tone/polarity, drawing bounds, malformed rejection, module output)");
    }

    private static void TestRoundtrip(string directory)
    {
        BluRayDiscParameters disc = new(1024);
        BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc);
        string iso = Path.Combine(directory, "picture.iso");
        BluRayIsoMetadata metadata = BluRayIsoMetadata.FromPlan(
            plan,
            Path.Combine(directory, "source.png"),
            iso,
            invertMapping: true,
            new ToneProfile(0.75, ToneProfile.TwoLevels, -4, SrgbLinearization: true),
            DateTimeOffset.UtcNow);
        metadata.Save(iso);

        BluRayIsoMetadata loaded = BluRayIsoMetadata.TryLoad(iso)
            ?? throw new InvalidOperationException("BD sidecar did not load.");
        Require(loaded.TotalSectors == 1024, "sectors");
        Require(loaded.DrawingStartSector == 0 && loaded.DrawingEndSectorExclusive == 1024, "drawing bounds");
        Require(loaded.InvertMapping, "polarity");
        Require(loaded.Tone.SrgbLinearization && Math.Abs(loaded.Tone.DitherStrength - 0.75) < 1e-12,
            "tone profile");
        Require(loaded.IsoBytes == disc.ContentLength && loaded.ContentLength == disc.ContentLength,
            "content length");
        Require(loaded.VolumeLabel == "BD_IMAGE" && loaded.DataDirectory is null, "volume metadata");
    }

    private static void TestRejection(string directory)
    {
        string iso = Path.Combine(directory, "bad.iso");
        File.WriteAllText(BluRayIsoMetadata.PathFor(iso), "{ \"format\": \"other\", \"totalSectors\": 32 }");
        Require(BluRayIsoMetadata.TryLoad(iso) is null, "wrong format is ignored");

        BluRayIsoMetadata invalid = new(
            "source.png", iso, 0, 1000, 24, 58, false, ToneProfile.LegacyBinary,
            0, 1000, 24, 58, "BD_IMAGE", null, null, DateTimeOffset.UtcNow);
        try
        {
            invalid.Save(iso);
            throw new InvalidOperationException("BD sidecar accepted unaligned sectors.");
        }
        catch (InvalidDataException)
        {
        }

        File.WriteAllText(BluRayIsoMetadata.PathFor(iso), "not json");
        Require(BluRayIsoMetadata.TryLoad(iso) is null, "malformed JSON is ignored");
    }

    private static void TestModuleWritesSidecar(string directory)
    {
        string iso = Path.Combine(directory, "module.iso");
        BluRayDiscModule module = new((_, _) => new BluRayGray8Raster(8, 8, Enumerable.Repeat((byte)128, 64).ToArray()));
        DiscJobResult result = module.ExecuteAsync(new DiscJobRequest("bd-generate",
        [
            "--input", Path.Combine(directory, "source.png"),
            "--output", iso,
            "--total-sectors", "32",
            "--tone-dither", "1",
            "--tone-gamma", "true",
            "--invert", "true",
        ])).GetAwaiter().GetResult();
        Require(result.Succeeded, "module generation");
        Require(File.Exists(BluRayIsoMetadata.PathFor(iso)), "module writes the sidecar");
        BluRayIsoMetadata metadata = BluRayIsoMetadata.TryLoad(iso)
            ?? throw new InvalidOperationException("module sidecar did not load.");
        Require(metadata.InvertMapping && metadata.Tone.SrgbLinearization, "module polarity and gamma");
        Require(metadata.Tone.DitherStrength == 1, "module dither");
        Require(new FileInfo(iso).Length == 32L * 2048, "module ISO length");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Blu-ray sidecar: " + message + ".");
        }
    }
}
