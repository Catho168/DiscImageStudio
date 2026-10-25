using System.IO;
using DiscImageStudio.BluRay;

internal static class BluRayParallelGenerationTests
{
    internal static void Run()
    {
        TestExactBytesAndOrderedProgress();
        TestRasterGeometryAgainstScalar();
        TestAllThresholdValues();
        TestIndependentScramble();
        TestCustomMappingsStaySequential();
        TestCancellationAndOutputFailure();
        Console.WriteLine("blu-ray-parallel-generation: passed (exact bytes, ordered output, custom mapping isolation, cancellation)");
    }

    private static void TestRasterGeometryAgainstScalar()
    {
        // Pixel-sized high-frequency detail makes small coordinate errors visible;
        // wide/short images additionally exercise the fitted-image white margins.
        foreach ((int width, int height) in new[] { (1025, 1025), (4097, 17) })
        {
            byte[] pixels = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = (byte)(((x + y) & 1) == 0 ? 127 : 128);
            BluRayGray8Raster raster = new(width, height, pixels);
            foreach (BluRayDiscParameters disc in new[]
            {
                new BluRayDiscParameters(StartAngleDegrees: 89.999999),
                new BluRayDiscParameters(TotalSectors: 32, StartAngleDegrees: -27),
                new BluRayDiscParameters(InnerRadiusMm: 0.5, StartAngleDegrees: 173),
            })
            {
                foreach (bool invert in new[] { false, true })
                {
                    BluRayImagePayloadMapping scalar = new(disc, raster, new ScalarRunLength(invert));
                    BluRayImagePayloadMapping batched = new(disc, raster, new BluRayRunLengthMapping(invert));
                    byte[] expected = new byte[2048], actual = new byte[2048];
                    // FillScrambledPayload accepts individual sectors, including
                    // the last sector and sectors not aligned to a 32-sector cluster.
                    foreach (uint sector in new[] { 0U, 1U, 31U, disc.TotalSectors / 2, disc.TotalSectors - 1 }.Distinct())
                    {
                        BluRaySectorContext context = new(sector, disc.GetPhysicalSectorNumber(sector), disc);
                        scalar.FillScrambledPayload(context, expected);
                        batched.FillScrambledPayload(context, actual);
                        Require(actual.SequenceEqual(expected),
                            $"batched geometry equals scalar at {width}x{height}, capacity={disc.TotalSectors}, sector={sector}, invert={invert}");
                    }
                }
            }
        }
    }

    private static void TestAllThresholdValues()
    {
        BluRayDiscParameters disc = new();
        foreach (bool invert in new[] { false, true })
        {
            int scalarSample = 0, vectorSample = 0;
            BluRayImagePayloadMapping scalar = new(disc, (_, _) => (byte)scalarSample++, new ScalarRunLength(invert));
            BluRayImagePayloadMapping vector = new(disc, (_, _) => (byte)vectorSample++, new BluRayRunLengthMapping(invert));
            byte[] expected = new byte[2048], actual = new byte[2048];
            BluRaySectorContext context = new(0, disc.GetPhysicalSectorNumber(0), disc);
            scalar.FillScrambledPayload(context, expected);
            vector.FillScrambledPayload(context, actual);
            Require(expected.SequenceEqual(actual) && scalarSample == 2048 && vectorSample == 2048,
                "all 256 luminances, including 127/128, match scalar threshold with either polarity");
        }
    }

    private static void TestIndependentScramble()
    {
        BluRayDiscParameters disc = new();
        using MemoryStream output = new();
        BluRayStreamingGenerator.GenerateToStream(output, new(disc, 0, 64), new BluRayConstantByteMapping(0x5a));
        byte[] bytes = output.ToArray();
        for (uint sector = 0; sector < 64; sector++)
        {
            uint state = 0x8000 | (((BluRayDiscParameters.FirstLayer0PhysicalSector + sector) >> 5) & 0x7fff);
            for (int offset = 0; offset < 2048; offset++)
            {
                Require(bytes[(int)sector * 2048 + offset] == (byte)(0x5a ^ state),
                    "SIMD XOR matches independent byte/bit scrambler across two cluster seeds");
                for (int bit = 0; bit < 8; bit++)
                {
                    uint feedback = ((state >> 15) ^ (state >> 14) ^ (state >> 12) ^ (state >> 3)) & 1;
                    state = ((state << 1) | feedback) & 0xffff;
                }
            }
        }
    }

    private static void TestExactBytesAndOrderedProgress()
    {
        byte[] pixels = new byte[257 * 113];
        new Random(2097).NextBytes(pixels);
        BluRayGray8Raster raster = new(257, 113, pixels);
        // Thirty-five clusters cross the largest parallel batch and leave a short
        // final batch. Full-disc and small custom geometries exercise both inverses.
        foreach ((BluRayDiscParameters disc, uint start, bool invert) in new[]
        {
            (new BluRayDiscParameters(), 0U, false),
            (new BluRayDiscParameters(StartAngleDegrees: 31.5), 6_119_680U, true),
            (new BluRayDiscParameters(InnerRadiusMm: 25, OuterRadiusMm: 57),
                BluRayDiscParameters.SectorsPerLayer - 1120, false),
            (new BluRayDiscParameters(TotalSectors: 1184, StartAngleDegrees: -83), 32U, true),
        })
        {
            BluRayImagePayloadMapping image = new(disc, raster, new BluRayRunLengthMapping(invert));
            BluRayStreamingOptions options = new(disc, start, 1120);
            using MemoryStream expected = new();
            using MemoryStream actual = new();
            // An injected scalar strategy retains sequential calls and the
            // public per-byte mapping, independent of the new SIMD threshold.
            BluRayStreamingGenerator.GenerateToStream(expected, options,
                new BluRayImagePayloadMapping(disc, raster, new ScalarRunLength(invert)));
            int caller = Environment.CurrentManagedThreadId;
            List<long> completed = [];
            BluRayStreamingSummary result = BluRayStreamingGenerator.GenerateToStream(actual, options, image,
                new InlineProgress(value =>
                {
                    Require(Environment.CurrentManagedThreadId == caller, "progress stays on the caller thread");
                    Require(value.CompletedBytes == actual.Length, "progress follows emitted output");
                    completed.Add(value.CompletedBytes);
                }));
            Require(actual.ToArray().SequenceEqual(expected.ToArray()),
                $"parallel output exactly matches sequential payload at {start}, invert={invert}");
            Require(result.BytesWritten == options.ContentLength && actual.Length == options.ContentLength,
                "all clusters are written exactly once");
            Require(completed.Count == 36 && completed[0] == 0, "one initial update and one per cluster");
            Require(completed.Zip(completed.Skip(1), (a, b) => b - a).All(delta => delta == 65536),
                "progress is sequential across batch boundaries");
        }
    }

    private static void TestCustomMappingsStaySequential()
    {
        BluRayDiscParameters disc = new();
        BluRayStreamingOptions options = new(disc, 0, 64);
        int caller = Environment.CurrentManagedThreadId;
        int samples = 0;
        BluRayImagePayloadMapping procedural = new(disc, (_, _) =>
        {
            Require(Environment.CurrentManagedThreadId == caller, "custom procedural sampler stays on its caller thread");
            samples++;
            return 128;
        }, new BluRayRunLengthMapping());
        BluRayStreamingGenerator.GenerateToStream(Stream.Null, options, procedural);
        Require(samples == 64 * 2048, "each procedural sample is invoked once");

        TrackingLuminance custom = new(caller);
        BluRayImagePayloadMapping injected = new(disc, new BluRayGray8Raster(1, 1, [128]), custom);
        BluRayStreamingGenerator.GenerateToStream(Stream.Null, options, injected);
        Require(custom.Count == 64 * 2048, "injected luminance mapping is invoked sequentially for each byte");
        TrackingLuminance optedInCustom = new(caller);
        BluRayImagePayloadMapping unsafeStrategy = new(disc, static (_, _) => 128, optedInCustom,
            allowParallelSampling: true);
        BluRayStreamingGenerator.GenerateToStream(Stream.Null, options, unsafeStrategy);
        Require(optedInCustom.Count == 64 * 2048, "sampler opt-in cannot parallelize a custom luminance strategy");
        TrackingPayload payload = new(caller);
        BluRayStreamingGenerator.GenerateToStream(Stream.Null, options, payload);
        Require(payload.Count == 64, "custom payload receives every sector sequentially");
    }

    private static void TestCancellationAndOutputFailure()
    {
        BluRayDiscParameters disc = new();
        BluRayImagePayloadMapping image = new(disc, new BluRayGray8Raster(2, 2, [0, 255, 255, 0]),
            new BluRayRunLengthMapping());
        BluRayStreamingOptions options = new(disc, 0, 640);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        using MemoryStream untouched = new();
        Throws<OperationCanceledException>(() => BluRayStreamingGenerator.GenerateToStream(untouched,
            options, image, cancellationToken: cancelled.Token));
        Require(untouched.Length == 0, "pre-cancelled generation emits nothing");

        using CancellationTokenSource stopAfterWrite = new();
        using MemoryStream partial = new();
        Throws<OperationCanceledException>(() => BluRayStreamingGenerator.GenerateToStream(partial, options, image,
            new InlineProgress(value => { if (value.CompletedBytes > 0) stopAfterWrite.Cancel(); }), stopAfterWrite.Token));
        Require(partial.Length == 65536, "cancellation after the first write emits no later prepared clusters");

        using FailingOutput failure = new();
        Throws<IOException>(() => BluRayStreamingGenerator.GenerateToStream(failure, options, image));
        Require(failure.Attempts == 1, "output failure never retries or writes a subsequent prepared cluster");
    }

    private sealed class TrackingPayload(int caller) : IBluRayPayloadMapping
    {
        public uint Count { get; private set; }
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            Require(Environment.CurrentManagedThreadId == caller && context.Sector == Count,
                "custom payload remains on the caller thread and in sector order");
            Count++;
            destination.Fill(0x44);
        }
    }

    private sealed class ScalarRunLength(bool invert) : IBluRayLuminanceMapping
    {
        private readonly BluRayRunLengthMapping _mapping = new(invert);
        public byte Map(byte luminance, BluRayPayloadBytePosition position) => _mapping.Map(luminance, position);
    }

    private sealed class TrackingLuminance(int caller) : IBluRayLuminanceMapping
    {
        public int Count { get; private set; }
        public byte Map(byte luminance, BluRayPayloadBytePosition position)
        {
            Require(Environment.CurrentManagedThreadId == caller, "custom luminance strategy stays on its caller thread");
            Require(position.Sector == (uint)(Count / 2048) && position.ByteOffset == Count % 2048,
                "custom luminance calls preserve byte order");
            Count++;
            return luminance;
        }
    }

    private sealed class InlineProgress(Action<BluRayStreamingProgress> report) : IProgress<BluRayStreamingProgress>
    {
        public void Report(BluRayStreamingProgress value) => report(value);
    }

    private sealed class FailingOutput : MemoryStream
    {
        public int Attempts { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Attempts++;
            throw new IOException("Simulated destination failure.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("BD parallel generation: " + message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
