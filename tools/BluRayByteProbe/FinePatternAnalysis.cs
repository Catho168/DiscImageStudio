using System.Collections.Concurrent;
using System.Diagnostics;
using DiscImageStudio.BluRay.Encoding;

/// <summary>
/// Places periodic post-scrambler bits at their physical ECC positions, then runs the
/// actual EDC/LDC/BIS/DC/17PP chain. This is a diagnostic, not an optical mapping.
/// </summary>
internal static class FinePatternAnalysis
{
    private static readonly string[] Layouts =
        ["payload-sequential", "physical-ecc", "physical-17pp-input", "dc-block-reset"];
    private static readonly uint[] Psns = [0x100000, 0x234560, 0x6D3A00, 0xCA73E0];
    private static readonly Position[] Positions = CreatePositions();

    public static object Run(string[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        patterns = patterns.Distinct(StringComparer.Ordinal).ToArray();
        foreach (string pattern in patterns)
            if (pattern.Length is < 2 or > 64 || pattern.Any(c => c is not ('0' or '1')))
                throw new ArgumentException("Supply binary patterns of 2..64 bits.", nameof(patterns));

        Stopwatch watch = Stopwatch.StartNew();
        // Obtain the exact common run-in state from the complete production path.
        byte[] referenceEcc = CreateEcc("00", "payload-sequential", 0, Psns[0]);
        BluRayRecordingUnitResult reference = BluRayRecordingUnitEncoder.EncodeEccCluster(referenceEcc, Psns[0]);
        byte initialLevel = reference.ChannelLevels[2760 - 1];
        long initialSum = 0;
        foreach (byte bit in reference.ChannelLevels.AsSpan(0, 2760)) initialSum += bit == 0 ? -1 : 1;
        FineMetrics checkedMetrics = ScanEcc(referenceEcc, initialLevel, initialSum, "greedy");
        long referenceTransitions = 0;
        foreach (byte bit in reference.ModulationBits.AsSpan(2760, 496 * 1932)) referenceTransitions += bit;
        if (referenceTransitions != checkedMetrics.Transitions)
            throw new InvalidOperationException("Fine-pattern scanner disagrees with the full RUB path.");

        List<Case> cases = [];
        foreach (string pattern in patterns)
            foreach (string layout in Layouts)
                for (int phase = 0; phase < pattern.Length; phase++)
                    cases.Add(new(pattern, layout, phase, Psns[0]));
        ConcurrentBag<FineResult> results = [];
        int completed = 0;
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, item =>
        {
            results.Add(MeasureCase(item, initialLevel, initialSum));
            int count = Interlocked.Increment(ref completed);
            if (count % 16 == 0 || count == cases.Count)
                Console.WriteLine($"Fine pattern phases {count}/{cases.Count}; {watch.Elapsed.TotalSeconds:F1}s.");
        });
        FineResult[] phaseSweep = results.OrderBy(r => r.Pattern).ThenBy(r => r.Layout).ThenBy(r => r.Phase).ToArray();

        // Confirm both empirical phase extremes, avoiding an assumed black/white
        // meaning or a hard-coded 'low pattern'/'high pattern' classification.
        FineResult[] representatives = phaseSweep.GroupBy(r => (r.Pattern, r.Layout))
            .SelectMany(group => new[] { group.MinBy(r => r.Greedy.Density)!, group.MaxBy(r => r.Greedy.Density)! })
            .DistinctBy(r => (r.Pattern, r.Layout, r.Phase)).ToArray();
        List<Case> addressCases = [];
        foreach (FineResult item in representatives)
            foreach (uint psn in Psns.Skip(1)) addressCases.Add(new(item.Pattern, item.Layout, item.Phase, psn));
        ConcurrentBag<FineResult> confirmations = new(representatives);
        completed = 0;
        Parallel.ForEach(addressCases, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, item =>
        {
            confirmations.Add(MeasureCase(item, initialLevel, initialSum));
            int count = Interlocked.Increment(ref completed);
            if (count % 16 == 0 || count == addressCases.Count)
                Console.WriteLine($"Fine pattern addresses {count}/{addressCases.Count}; {watch.Elapsed.TotalSeconds:F1}s.");
        });

        return new
        {
            SchemaVersion = 1,
            Scope = "Single-layer BD-R. All patterns specify post-scrambler user-data bits Q, MSB first. Each cluster restarts the coordinate origin.",
            LayoutDefinitions = new Dictionary<string, string>
            {
                ["payload-sequential"] = "Pattern index = (sectorWithinCluster*2048+payloadByteOffset)*8 + bitWithinByte. LDC reorders this stream.",
                ["physical-ecc"] = "Pattern index = (recordingFrame*155+eccColumn)*8 + bitWithinByte. EDC, BIS and parity holes advance the phase without being controlled.",
                ["physical-17pp-input"] = "Pattern index = recordingFrame*1268 + rawFrameBit + DCbefore(rawFrameBit). DCbefore(j)=0 for j<25, else 1+(j-25)/45. Unknown DC bits occupy their slots; sync is not 17PP input.",
                ["dc-block-reset"] = "Pattern index = rawFrameBit for the first 25 data bits, else (rawFrameBit-25)%45. Every 25/45-bit data segment starts at the selected phase; DC bits remain drive-selected. EDC/BIS holes retain their within-segment positions."
            },
            Phase = "Pattern[(coordinate+phase)%period]; every phase tested at PSN 0x00100000. Lowest/highest greedy-density phases per layout rechecked at three additional PSNs.",
            Verification = "All 65536 controllable Q bytes are compared byte-for-byte against their actual post-LDC/BIS ECC locations for every generated cluster. Placement must be a one-to-one scatter.",
            Density = "Transitions/channel bits over all 496*1932 bits including sync, EDC, LDC parity and BIS. Run-in/out/Guard3 excluded; run-in level/DSV retained.",
            DataRegion = "First 432 frames contain payload plus EDC/BIS/DC/sync. Their density is not a payload-only attribution. Last 64 frames are LDC parity plus BIS/DC/sync.",
            Dc = "Greedy diagnostic, future bits provisionally zero; all-zero/all-one/random policies are sensitivity probes, not driver predictions or DC-balanced prescriptions.",
            PhaseSweep = phaseSweep,
            AddressConfirmations = confirmations.OrderBy(r => r.Pattern).ThenBy(r => r.Layout).ThenBy(r => r.Phase).ThenBy(r => r.Psn).ToArray(),
            ElapsedSeconds = watch.Elapsed.TotalSeconds,
        };
    }

    private static FineResult MeasureCase(Case item, byte initialLevel, long initialSum)
    {
        byte[] ecc = CreateEcc(item.Pattern, item.Layout, item.Phase, item.Psn);
        FineMetrics greedy = ScanEcc(ecc, initialLevel, initialSum, "greedy");
        FineMetrics zero = ScanEcc(ecc, initialLevel, initialSum, "zero");
        FineMetrics one = ScanEcc(ecc, initialLevel, initialSum, "one");
        FineMetrics random = ScanEcc(ecc, initialLevel, initialSum, "random");
        return new(item.Pattern, item.Pattern.Length, item.Layout, item.Phase, $"0x{item.Psn:X8}",
            greedy, zero.Density, one.Density, random.Density, 65536);
    }

    private static byte[] CreateEcc(string pattern, string layout, int phase, uint psn)
    {
        byte[] payload = new byte[65536];
        byte[] expectedQ = new byte[payload.Length];
        byte[] mask = BluRayScrambler.CreateMask(psn, 2048);
        for (int i = 0; i < payload.Length; i++)
        {
            Position position = Positions[i];
            byte q = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int rawBit = position.EccColumn * 8 + bit;
                int dcBefore = rawBit < 25 ? 0 : 1 + (rawBit - 25) / 45;
                int coordinate = layout switch
                {
                    "payload-sequential" => i * 8 + bit,
                    "physical-ecc" => position.EccIndex * 8 + bit,
                    "physical-17pp-input" => position.Frame * 1268 + rawBit + dcBefore,
                    "dc-block-reset" => rawBit < 25 ? rawBit : (rawBit - 25) % 45,
                    _ => throw new ArgumentException("Unknown pattern placement.", nameof(layout)),
                };
                q = (byte)((q << 1) | (pattern[(coordinate + phase) % pattern.Length] - '0'));
            }
            expectedQ[i] = q;
            payload[i] = (byte)(q ^ mask[i % 2048]);
        }
        byte[] ecc = BluRayEccEncoder.Encode(payload, psn);
        for (int i = 0; i < expectedQ.Length; i++)
            if (ecc[Positions[i].EccIndex] != expectedQ[i])
                throw new InvalidOperationException("Inverse interleaving failed to preserve a target payload byte.");
        return ecc;
    }

    private static Position[] CreatePositions()
    {
        Position[] positions = new Position[65536];
        HashSet<int> assigned = [];
        for (int i = 0; i < positions.Length; i++)
        {
            int serial = (i / 2048) * 2052 + i % 2048;
            int column = serial / 216;
            int row = serial % 216;
            int frame = 2 * row + column % 2;
            int ldcColumn = (column / 2 + 152 - (row * 3) % 152) % 152;
            int eccColumn = ldcColumn + ldcColumn / 38;
            int eccIndex = frame * 155 + eccColumn;
            if (frame >= 432 || !assigned.Add(eccIndex))
                throw new InvalidOperationException("Invalid/duplicate physical payload position.");
            positions[i] = new(frame, eccColumn, eccIndex);
        }
        return positions;
    }

    private static FineMetrics ScanEcc(byte[] ecc, byte initialLevel, long initialSum, string policy)
    {
        bool previousTermination = false;
        byte level = initialLevel;
        long sum = initialSum;
        long transitions = 0, dataTransitions = 0, parityTransitions = 0, ones = 0;
        long[] runs = new long[10];
        long lastTransition = -1, position = 0;
        Random random = new(0x17BB);
        byte[] controls = new byte[28];
        for (int frame = 0; frame < 496; frame++)
        {
            ReadOnlySpan<byte> current = ecc.AsSpan(frame * 155, 155);
            BluRayRecordingFrameResult result;
            if (policy == "greedy")
                result = BluRayGreedyDcControlEncoder.Encode(current, frame % 31,
                    frame == 495 ? default : ecc.AsSpan((frame + 1) * 155, 155), previousTermination, level, sum).Frame;
            else
            {
                for (int i = 0; i < controls.Length; i++)
                    controls[i] = policy switch { "zero" => 0, "one" => 1, "random" => (byte)random.Next(2), _ => throw new ArgumentException("Unknown DC policy.") };
                result = BluRayRecordingFrameEncoder.Encode(current, frame % 31, controls, previousTermination, level);
            }
            foreach (byte transition in result.ModulationBits)
            {
                if (transition == 1)
                {
                    transitions++;
                    if (frame < 432) dataTransitions++; else parityTransitions++;
                    if (lastTransition >= 0)
                    {
                        long gap = position - lastTransition;
                        if (gap < 2 || gap > 9) throw new InvalidOperationException("Invalid full-cluster RLL interval.");
                        runs[gap]++;
                    }
                    lastTransition = position;
                }
                position++;
            }
            foreach (byte bit in result.ChannelLevels) ones += bit;
            level = result.FinalChannelLevel;
            sum += result.DigitalSum;
            previousTermination = result.UsedTermination;
        }
        if (position != 958272) throw new InvalidOperationException("Incorrect physical-cluster length.");
        return new(position, transitions, ones, dataTransitions, parityTransitions, runs);
    }

    private sealed record Case(string Pattern, string Layout, int Phase, uint Psn);
    private readonly record struct Position(int Frame, int EccColumn, int EccIndex);
    private sealed record FineResult(string Pattern, int Period, string Layout, int Phase, string Psn,
        FineMetrics Greedy, double ZeroDcDensity, double OneDcDensity, double RandomDcDensity, int VerifiedPayloadBytes);
    private sealed record FineMetrics(long ChannelBits, long Transitions, long Ones, long DataRegionTransitions,
        long ParityRegionTransitions, long[] RunCounts)
    {
        public double Density => (double)Transitions / ChannelBits;
        public double OneFraction => (double)Ones / ChannelBits;
        public double DataRegionDensity => (double)DataRegionTransitions / (432 * 1932);
        public double ParityRegionDensity => (double)ParityRegionTransitions / (64 * 1932);
    }
}
