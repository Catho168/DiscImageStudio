using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DiscImageStudio.Core;
using DvdImageSolver.Encoding;
using DvdImageSolver.Solver;

namespace DvdGrayProbe;

internal static class Program
{
    private const uint Sectors = 2297888;
    private const double Inner = 23.991460627731737, Outer = 57.964893657925366, BitNm = 133.3;
    private const double Linear = -3.9256854319227684E-05, Quadratic = 6.964795915429256E-05, Cubic = -4.670918950137523E-05;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Console.WriteLine("DvdGrayProbe prepare --source FOUR_LEVEL.png --output-dir DIR\n"
                    + "DvdGrayProbe generate --prepared DIR --output TEST.iso [--parallelism 8]\n"
                    + "DvdGrayProbe verify --prepared DIR --iso TEST.iso\n"
                    + "Verbatim 43533 calibrated geometry; complete raw DVD sector image; no burn command.");
                return 0;
            }
            Dictionary<string, string> options = new(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
                if (i + 1 == args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException("Expected unique --name value options.");
            string Need(string name) => options.TryGetValue(name, out string? value) ? Path.GetFullPath(value) : throw new ArgumentException("Missing " + name);
            switch (args[0])
            {
                case "prepare": Prepare(Need("--source"), Need("--output-dir")); break;
                case "generate": Generate(Need("--prepared"), Need("--output"), options.TryGetValue("--parallelism", out string? p) ? int.Parse(p) : 8); break;
                case "verify": Verify(Load(Need("--prepared")), Need("--iso")); break;
                default: throw new ArgumentException("Unknown command.");
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static MultiBlockSolveOptions Options(uint start, uint fill, int parallelism) => new(
        start, fill, 0x30000, 0, 1, 1, 0, 0, true,
        new ImageMappingOptions(Sectors, Inner, Outer, BitNm, 0, true, 128, 1,
            PitchLinear: Linear, PitchQuadratic: Quadratic, PitchCubic: Cubic),
        Algorithm: SolverAlgorithm.Dispersion, FastParallelism: parallelism);

    private static void Prepare(string source, string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Preparation directory must be empty to avoid replacing an existing experiment.");
        Directory.CreateDirectory(directory);
        ProbePattern pattern = new(source);
        string atlasPath = Path.Combine(directory, "atlas.q8");
        File.WriteAllBytes(atlasPath, pattern.Atlas);
        pattern.SavePreview(Path.Combine(directory, "code-map.png"), true);
        pattern.SavePreview(Path.Combine(directory, "nominal-preview.png"), false);
        WriteNew(Path.Combine(directory, "probe.json"), new
        {
            schema = "dvd-gray-probe-v1", createdUtc = DateTimeOffset.UtcNow,
            source, sourceSha256 = HashFile(source), sourceKind = "already-dithered four-level Gray8 PNG",
            crop = new { x = 2880, y = 1920, width = 640, height = 640, resize = false, redither = false },
            atlasSha256 = HashFile(atlasPath), atlasWidth = ProbePattern.Size, atlasHeight = ProbePattern.Size,
            cellMm = ProbePattern.CellMm, canvasMm = 120, coordinateSystem = "x right, y down; centre at 0,0",
            sampling = "nearest immutable direct-Q atlas cell; no bilinear luminance mixing",
            paletteCodes = ProbePattern.Codes.Select(q => $"{q:X2}"),
            panelsClockwiseFromTwelve = ProbePattern.PanelCandidates.Select((q, i) => new { panel = i + 1, darkQ = $"{q:X2}", centreDegrees = (i + .5) * 30 }),
            layout = new[]
            {
                "r24.5..25.6: six pure Q codes repeated four times",
                "r26.0..28.1: shared serpentine-FS 0/85/170/255 logical gradient",
                "r28.5..30.5: 64/E4 direct binary FS mixtures, clockwise columns 25/50/75 percent 64",
                "12.8mm square image at radius37.5: identical supplied-image crop, only dark Q changes",
                "r45.8..47.5: 25/50/75 percent candidate mixed with A5",
                "r47.8..49.5: 25/50/75 percent candidate mixed with B9",
                "r49.8..51.5: 25/50/75 percent candidate mixed with 92",
                "r52.2..57.3: A5/66/64/E4/B9/92 pure code patches repeated four times; labels at r56.2",
            },
            geometry = new { preset = "Verbatim DVD-R AZO 43533 calibrated", totalSectors = Sectors,
                innerRadiusMm = Inner, outerRadiusMm = Outer, channelBitLengthNm = BitNm,
                pitchLinear = Linear, pitchQuadratic = Quadratic, pitchCubic = Cubic, clockwise = true },
            fullDiscBytes = (long)Sectors * 2048,
            previewMeaning = "code-map is a false-color code legend; nominal-preview assumes 0/85/170/255 and is not measured optical brightness",
            mixtureMeaning = "requested spatial pixel occupancies, not calibrated optical luminance; binary FS masks shared across candidates",
            opticalValidationPerformed = false,
        });
        VerifyFragments(pattern, null);
        Console.WriteLine("Prepared: " + directory);
    }

    private static ProbePattern Load(string directory)
    {
        using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "probe.json")));
        JsonElement root = metadata.RootElement;
        if (root.GetProperty("schema").GetString() != "dvd-gray-probe-v1") throw new IOException("Unknown experiment schema.");
        string atlas = Path.Combine(directory, "atlas.q8");
        if (HashFile(atlas) != root.GetProperty("atlasSha256").GetString()) throw new IOException("Atlas hash mismatch.");
        return ProbePattern.Load(atlas);
    }

    private static void Generate(string prepared, string output, int parallelism)
    {
        if (parallelism is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(parallelism));
        ProbePattern pattern = Load(prepared);
        string partial = output + ".partial";
        foreach (string path in new[] { output, partial, output + ".json" }) RefuseExisting(path);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        Stopwatch elapsed = Stopwatch.StartNew();
        double lastUpdate = -10;
        try
        {
            using (FileStream stream = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                FastDispersionImageWriter.WriteScrambledCodewordPatternStream(
                    sample => pattern.Sample(sample.Xmm, sample.Ymm), stream, Options(0, Sectors, parallelism), progress =>
                    {
                        if (progress.Elapsed.TotalSeconds - lastUpdate < 10 && progress.CompletedBlocks != progress.TotalBlocks) return;
                        lastUpdate = progress.Elapsed.TotalSeconds;
                        Console.WriteLine($"{progress.Fraction:P1} | {progress.BytesWritten:N0} bytes | {progress.Elapsed.TotalSeconds:F1}s");
                    }, cancellation.Token);
                stream.Flush(true);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            Verify(pattern, partial);
            string hash = HashFile(partial);
            File.Move(partial, output);
            WriteNew(output + ".json", new
            {
                schema = "dvd-gray-probe-image-v1", status = "generated-and-verified", completeDiscImage = true,
                format = "raw 2048-byte-sector DVD image without filesystem", totalSectors = Sectors,
                bytes = new FileInfo(output).Length, sha256 = hash, preparedDirectory = prepared,
                atlasSha256 = HashFile(Path.Combine(prepared, "atlas.q8")),
                preset = "Verbatim DVD-R AZO 43533 calibrated", seconds = elapsed.Elapsed.TotalSeconds,
                actualBurnPerformed = false, opticalValidationPerformed = false,
                validation = "exact length; all bytes in 9 radial slices x 128 sectors checked against target Q via independent descrambler; SHA256",
            });
            Console.WriteLine($"Complete: {output}\nSHA256: {hash}");
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static void Verify(ProbePattern pattern, string iso)
    {
        using FileStream stream = File.OpenRead(iso);
        if (stream.Length != (long)Sectors * 2048) throw new IOException("Incorrect full image length.");
        VerifyFragments(pattern, stream);
    }

    private static void VerifyFragments(ProbePattern pattern, FileStream? actual)
    {
        DvdTrackGeometry track = DvdTrackGeometry.Create(Inner, Outer, Sectors * 38688d * BitNm * 1e-6, Linear, Quadratic, Cubic);
        long[] counts = new long[256];
        foreach (double radius in new[] { 25.0, 27.0, 29.4, 37.5, 46.6, 48.6, 50.6, 54.0, 56.2 })
        {
            uint start = (uint)(track.ArcLengthAtRadius(radius) / (38688 * BitNm * 1e-6)) / 16 * 16;
            List<byte> expected = new(128 * 2048);
            using MemoryStream fragment = new();
            FastDispersionImageWriter.WriteScrambledCodewordPatternStream(sample =>
            {
                byte q = pattern.Sample(sample.Xmm, sample.Ymm);
                expected.Add(q);
                return q;
            }, fragment, Options(start, 128, 1));
            byte[] data = fragment.ToArray();
            if (actual is not null)
            {
                actual.Position = (long)start * 2048;
                actual.ReadExactly(data);
            }
            ushort[] presets = [1, 0x5500, 2, 0x2A00, 4, 0x5400, 8, 0x2800, 0x10, 0x5000, 0x20, 0x2001, 0x40, 0x4002, 0x80, 5];
            for (int sector = 0; sector < 128; sector++)
            {
                int state = presets[((0x30000 + start + sector) >> 4) & 15];
                for (int i = 0; i < 2048; i++)
                {
                    int index = sector * 2048 + i;
                    byte q = (byte)(data[index] ^ (state & 255));
                    if (q != expected[index]) throw new IOException($"Wrong Q at LBA {start + sector}, byte {i}.");
                    counts[q]++;
                    for (int bit = 0; bit < 8; bit++) state = ((state << 1) | (((state >> 14) ^ (state >> 10)) & 1)) & 0x7FFF;
                }
            }
        }
        if (ProbePattern.Codes.Any(code => counts[code] == 0)) throw new IOException("Sample coverage missed a code.");
        Console.WriteLine("Verified 2,359,296 target Q bytes across all test rows: "
            + string.Join(", ", ProbePattern.Codes.Select(q => $"{q:X2}={counts[q]}")));
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void RefuseExisting(string path) { if (File.Exists(path)) throw new IOException("Already exists: " + path); }
    private static void WriteNew(string path, object value)
    {
        using FileStream stream = new(path, FileMode.CreateNew);
        JsonSerializer.Serialize(stream, value, Json);
    }
}
