using System.IO;
using DvdImageSolver.Encoding;
using DvdImageSolver.Solver;

internal static class DvdScrambledPatternTests
{
    private static readonly byte[] Candidates = [0xA5, 0x66, 0x64, 0xE4, 0xB9, 0x92];
    private static readonly byte[] GrayInputs = [0, 42, 43, 64, 85, 127, 128, 169, 170, 192, 228, 255];
    private static readonly byte[] GrayTargets = [0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0x92, 0x92, 0x92, 0x92, 0x92, 0x92];

    internal static void Run()
    {
        MultiBlockSolveOptions options = Options();
        byte[] payloads = TestScrambledBytesAndParallelism(options);
        TestModulationReplay(payloads, options);
        TestIndependentGeometry(options);
        TestLuminanceCallbackStillQuantizes(options);
        TestCancellation(options);
        Console.WriteLine("dvd-scrambled-pattern: passed (six exact Q bytes, independent PSN masks and word geometry, parallel parity, ECC/EFM replay, luminance compatibility, cancellation)");
    }

    private static MultiBlockSolveOptions Options()
        => new(StartLba: 810_000, FillSectors: 64, PsnOffset: 0x30030,
            IterationsPerBlock: 1, MutationBytes: 4, RandomSeed: 842, InitialTemperature: 64,
            FinalTemperature: 0.25, ZeroInitialPayload: false,
            ImageMapping: new(TotalSectors: 2_297_888, InnerRadiusMm: 23.9968875,
                OuterRadiusMm: 57.9779875, ChannelBitLengthNm: 133.33,
                StartAngleDegrees: 37, Clockwise: true, LuminanceThreshold: 128, AlphaThreshold: 1,
                PitchLinear: 0.18, PitchQuadratic: -0.12, PitchCubic: 0.06),
            Algorithm: SolverAlgorithm.Dispersion, FastParallelism: 1);

    private static byte[] TestScrambledBytesAndParallelism(MultiBlockSolveOptions options)
    {
        using MemoryStream serial = new(), parallel = new();
        List<FastDvdStreamProgress> updates = [];
        var summary = FastDispersionImageWriter.WriteScrambledCodewordPatternStream(
            sample => CandidateAt(sample.GlobalCodewordIndex), serial, options, updates.Add);
        FastDispersionImageWriter.WriteScrambledCodewordPatternStream(
            sample => CandidateAt(sample.GlobalCodewordIndex), parallel, options with { FastParallelism = 2 });
        byte[] actual = serial.ToArray();
        Require(actual.SequenceEqual(parallel.ToArray()), "parallelism must not change a mixed raw-Q pattern");
        Require(actual.Length == options.FillSectors * 2048L, "stream contains only the selected payload range, without an LBA prefix");
        Require(summary.BytesWritten == actual.Length && summary.ControlledWords == actual.Length
            && summary.SectorsWritten == options.FillSectors && summary.CompletedBlocks == options.FillSectors / 16,
            "raw-Q summary counts every controlled payload word");
        Require(updates.Count == 4 && updates.Select(update => update.CompletedBlocks).SequenceEqual([1, 2, 3, 4]),
            "progress reports complete blocks in order");
        for (int block = 0; block < updates.Count; block++)
            Require(updates[block].BlockLba == options.StartLba + 16 * block
                && updates[block].BytesWritten == (block + 1L) * 32768
                && updates[block].TotalBytes == actual.Length, "progress reports absolute LBAs and relative stream bytes");

        HashSet<byte> found = [];
        for (int sector = 0; sector < options.FillSectors; sector++)
        {
            byte[] mask = IndependentScramblerMask(options.StartLba + options.PsnOffset + (uint)sector);
            for (int index = 0; index < 2048; index++)
            {
                int payloadIndex = sector * 2048 + index;
                ulong globalWord = IndependentGlobalChannelCenter(options.StartLba, payloadIndex) / 16;
                byte q = (byte)(actual[payloadIndex] ^ mask[index]);
                Require(q == CandidateAt(globalWord), "P XOR the independent PSN mask must recover the requested Q without quantization");
                found.Add(q);
            }
        }
        Require(found.SetEquals(Candidates), "all six requested raw bytes, including 0x64 and 0xE4, survive generation");
        return actual;
    }

    private static void TestModulationReplay(byte[] payloads, MultiBlockSolveOptions options)
    {
        // Replay a real ECC block, including PI/PO, headers, EDC, SYNC and state
        // selection. These are channel transition checks, not optical predictions.
        var encoded = new DvdEccBlockEncoder().Encode(payloads.AsSpan(0, 32768),
            options.StartLba, options.PsnOffset, new ModulationBoundary(1, 0, true));
        Require(encoded.ChannelLevels.Length == 16 * 38688, "mixed pattern has a complete encoded ECC block");
        for (int index = 0; index < 32768; index++)
        {
            ulong globalCenter = IndependentGlobalChannelCenter(options.StartLba, index);
            int first = checked((int)(globalCenter - (ulong)options.StartLba * 38688)) - 8;
            byte q = CandidateAt(globalCenter / 16);
            int expectedTransitions = q == 0xA5 ? 5 : q == 0xB9 ? 3 : q == 0x92 ? 2 : 4;
            int transitions = 0;
            for (int bit = first; bit < first + 16; bit++)
                if (encoded.ChannelLevels[bit] != encoded.ChannelLevels[bit - 1]) transitions++;
            Require(transitions == expectedTransitions, "ECC/EFM replay retains the mixed byte's transition class at its payload word");
        }
        // Consecutive transitions within each data portion obey DVD's 3T..11T
        // spacing. The 32-bit SYNC field deliberately has separate rules.
        for (int frame = 0; frame < encoded.ChannelLevels.Length; frame += 1488)
        {
            int previousTransition = -1;
            for (int bit = frame + 32; bit < frame + 1488; bit++)
            {
                if (encoded.ChannelLevels[bit] == encoded.ChannelLevels[bit - 1]) continue;
                if (previousTransition >= 0)
                    Require(bit - previousTransition is >= 3 and <= 11, "mixed EFM data obeys the run-length constraint");
                previousTransition = bit;
            }
        }
    }

    private static void TestIndependentGeometry(MultiBlockSolveOptions options)
    {
        ImageMappingOptions mapping = options.ImageMapping;
        double fullLength = mapping.TotalSectors * 38688d * mapping.ChannelBitLengthNm * 1e-6;
        double Shape(double radius)
        {
            double x = (radius - mapping.InnerRadiusMm) / (mapping.OuterRadiusMm - mapping.InnerRadiusMm);
            return 1 + x * (mapping.PitchLinear + x * (mapping.PitchQuadratic + x * mapping.PitchCubic));
        }
        // Independently normalize reciprocal radial growth with Simpson quadrature
        // and bisection, instead of using the production spiral's inverse tables.
        double low = 0;
        double high = fullLength / Simpson(r => r / Shape(r), mapping.InnerRadiusMm, mapping.OuterRadiusMm);
        for (int iteration = 0; iteration < 70; iteration++)
        {
            double middle = (low + high) / 2;
            double arc = Simpson(r => Math.Sqrt(1 + Math.Pow(r * middle / Shape(r), 2)),
                mapping.InnerRadiusMm, mapping.OuterRadiusMm);
            if (arc > fullLength) high = middle;
            else low = middle;
        }
        double inverseGrowth = (low + high) / 2;
        foreach (uint start in new[] { 0u, options.StartLba, mapping.TotalSectors - 16 })
        foreach (bool clockwise in new[] { false, true })
        {
            int sampleIndex = 0;
            using MemoryStream output = new();
            FastDispersionImageWriter.WriteScrambledCodewordPatternStream(sample =>
            {
                int index = sampleIndex++;
                ulong expectedCenter = IndependentGlobalChannelCenter(start, index);
                Require(sample.GlobalCodewordIndex == expectedCenter / 16,
                    "callback word index includes data headers, PI/PO and both SYNC slots");
                if (index % 997 != 0 && index != 32767) return 0x64;
                double radius = Math.Sqrt(sample.Xmm * sample.Xmm + sample.Ymm * sample.Ymm);
                double arc = Simpson(r => Math.Sqrt(1 + Math.Pow(r * inverseGrowth / Shape(r), 2)),
                    mapping.InnerRadiusMm, radius);
                Near(expectedCenter * mapping.ChannelBitLengthNm * 1e-6, arc, 2e-5,
                    "callback position is at the payload word centre's independent arc length");
                double angle = Simpson(r => inverseGrowth / Shape(r), mapping.InnerRadiusMm, radius);
                Near(angle, sample.TrackAngleRadians, 5e-7, "callback retains the unwrapped spiral angle");
                double polar = mapping.StartAngleDegrees * Math.PI / 180 + (clockwise ? -angle : angle);
                Near(radius * Math.Cos(polar), sample.Xmm, 5e-5, "callback x includes start angle and direction");
                Near(-radius * Math.Sin(polar), sample.Ymm, 5e-5, "callback y follows image-down coordinates");
                return 0xE4;
            }, output, options with { StartLba = start, FillSectors = 16, ImageMapping = mapping with { Clockwise = clockwise } });
            Require(sampleIndex == 32768, "exactly one callback is made for every payload byte");
        }
    }

    private static void TestLuminanceCallbackStillQuantizes(MultiBlockSolveOptions options)
    {
        using MemoryStream gray = new(), q = new();
        FastDispersionImageWriter.WriteCodewordPatternStream(
            sample => GrayInputs[(int)(sample.GlobalCodewordIndex % (ulong)GrayInputs.Length)], gray, options);
        FastDispersionImageWriter.WriteScrambledCodewordPatternStream(
            sample => GrayTargets[(int)(sample.GlobalCodewordIndex % (ulong)GrayTargets.Length)], q, options);
        Require(gray.ToArray().SequenceEqual(q.ToArray()),
            "the luminance callback thresholds at128 and never emits the retired Q66/QB9 grays");
    }

    private static void TestCancellation(MultiBlockSolveOptions options)
    {
        using CancellationTokenSource before = new();
        before.Cancel();
        using MemoryStream output = new();
        int calls = 0;
        ExpectCancellation(() => FastDispersionImageWriter.WriteScrambledCodewordPatternStream(
            _ => { calls++; return 0x64; }, output, options, cancellationToken: before.Token));
        Require(calls == 0 && output.Length == 0, "pre-cancellation invokes no samples and writes no bytes");

        using CancellationTokenSource during = new();
        ExpectCancellation(() => FastDispersionImageWriter.WriteScrambledCodewordPatternStream(_ =>
        {
            if (++calls == 100) during.Cancel();
            return 0xE4;
        }, output, options, cancellationToken: during.Token));
        Require(output.Length == 0, "cancellation during generation does not write a partial batch");

        using CancellationTokenSource afterBlock = new();
        ExpectCancellation(() => FastDispersionImageWriter.WriteScrambledCodewordPatternStream(
            _ => 0x64, output, options with { FastParallelism = 2 }, _ => afterBlock.Cancel(), afterBlock.Token));
        Require(output.Length == 32768, "cancellation from progress stops after the first complete block");
    }

    private static byte CandidateAt(ulong word) => Candidates[(int)(word % (ulong)Candidates.Length)];

    private static ulong IndependentGlobalChannelCenter(uint firstLba, int payloadIndex)
    {
        // A sector contributes 12 data rows plus an interleaved PO row. Every row
        // has two 91-byte halves, each preceded by a 32-channel-bit SYNC field.
        int sector = payloadIndex / 2048;
        int frameByte = 12 + payloadIndex % 2048;
        int row = frameByte / 172;
        int column = frameByte % 172;
        return ((ulong)firstLba + (uint)sector) * 38688
            + (ulong)(row * 2 + column / 91) * 1488
            + 32 + (ulong)(column % 91) * 16 + 8;
    }

    private static byte[] IndependentScramblerMask(uint psn)
    {
        ushort[] seeds = [0x0001, 0x5500, 0x0002, 0x2A00, 0x0004, 0x5400, 0x0008, 0x2800,
            0x0010, 0x5000, 0x0020, 0x2001, 0x0040, 0x4002, 0x0080, 0x0005];
        int state = seeds[(psn >> 4) & 15];
        byte[] mask = new byte[2048];
        for (int index = 0; index < mask.Length; index++)
        {
            mask[index] = (byte)state;
            for (int bit = 0; bit < 8; bit++)
            {
                int incoming = ((state & 0x4000) == 0 ? 0 : 1) ^ ((state & 0x0400) == 0 ? 0 : 1);
                state = ((state * 2) + incoming) & 0x7FFF;
            }
        }
        return mask;
    }

    private static double Simpson(Func<double, double> function, double start, double end)
    {
        const int intervals = 1024;
        double width = (end - start) / intervals;
        double sum = function(start) + function(end);
        for (int index = 1; index < intervals; index++)
            sum += (index % 2 == 0 ? 2 : 4) * function(start + width * index);
        return sum * width / 3;
    }

    private static void ExpectCancellation(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("raw-Q stream ignored cancellation");
    }

    private static void Near(double expected, double actual, double tolerance, string message)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected:R}, got {actual:R}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
