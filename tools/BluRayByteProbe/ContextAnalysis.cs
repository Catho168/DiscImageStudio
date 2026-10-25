using DiscImageStudio.BluRay.Encoding;

// Synthetic-frame diagnostics. ECC, scrambler, BIS and RUB overhead are deliberately absent.
internal static class ContextAnalysis
{
    private const int FrameBytes = 155;
    private const int SyncBits = 30;
    private const int DataChannelBits = 1902;

    public static object Run(byte[] candidates)
    {
        var masks = CreateMasks();
        var allBytes = new List<object>();
        for (int value = 0; value < 256; value++)
        {
            byte[] frame = Enumerable.Repeat((byte)value, FrameBytes).ToArray();
            var total = new Aggregate();
            var byMask = new List<object>();
            foreach (var mask in masks)
            {
                var masked = new Aggregate();
                for (int syncIndex = 0; syncIndex < 31; syncIndex++)
                for (int previousTermination = 0; previousTermination < 2; previousTermination++)
                {
                    var encoded = BluRayRecordingFrameEncoder.Encode(frame, syncIndex,
                        mask.Bits, previousTermination != 0);
                    ReadOnlySpan<byte> data = encoded.ModulationBits.AsSpan(SyncBits);
                    total.Add(data, encoded.UsedTermination);
                    masked.Add(data, encoded.UsedTermination);
                }
                byMask.Add(new { mask.Name, Statistics = masked.Export() });
            }
            allBytes.Add(new { Value = value, Hex = $"{value:X2}", Statistics = total.Export(), ByDcMask = byMask });
        }

        byte[] distinctCandidates = candidates.Distinct().Order().ToArray();
        var contexts = new List<object>();
        foreach (byte candidate in distinctCandidates)
        {
            // Reset seed per candidate: positions, neighbors and masks are matched across candidates.
            var rng = new Random(0x30190);
            var central = new Aggregate();
            for (int sample = 0; sample < 512; sample++)
            {
                byte[] frame = new byte[FrameBytes];
                rng.NextBytes(frame);
                int blockStart = 8 + rng.Next(76);
                frame.AsSpan(blockStart, 64).Fill(candidate);
                byte[] mask = RandomBits(rng, 28);
                var encoded = BluRayRecordingFrameEncoder.Encode(frame, rng.Next(31), mask, rng.Next(2) != 0);
                // 2-byte guards remove direct random-neighbor codewords; fixed rate 2:3 provides
                // nominal positions only, not a claim that individual bytes own these bits.
                var (start, end) = CentralWindow(blockStart + 2, blockStart + 62);
                central.Add(encoded.ModulationBits.AsSpan(start, end - start), encoded.UsedTermination);
            }
            contexts.Add(new { Value = candidate, Hex = $"{candidate:X2}", CentralBlock = central.Export() });
        }

        var mixtures = new List<object>();
        for (int a = 0; a < distinctCandidates.Length; a++)
        for (int b = a + 1; b < distinctCandidates.Length; b++)
        foreach (int blockBytes in new[] { 1, 8, 32 })
        {
            var rng = new Random(0x17CC + blockBytes);
            var stats = new Aggregate();
            var wholeFrame = new Aggregate();
            for (int sample = 0; sample < 128; sample++)
            {
                byte[] frame = new byte[FrameBytes];
                int phase = sample % (2 * blockBytes);
                for (int i = 0; i < frame.Length; i++)
                    frame[i] = ((i + phase) / blockBytes & 1) == 0 ? distinctCandidates[a] : distinctCandidates[b];
                var encoded = BluRayRecordingFrameEncoder.Encode(frame, rng.Next(31), RandomBits(rng, 28), rng.Next(2) != 0);
                wholeFrame.Add(encoded.ModulationBits.AsSpan(SyncBits), encoded.UsedTermination);
                var (start, end) = CentralWindow(16, 144);
                stats.Add(encoded.ModulationBits.AsSpan(start, end - start), encoded.UsedTermination);
            }
            mixtures.Add(new { A = $"{distinctCandidates[a]:X2}", B = $"{distinctCandidates[b]:X2}", BlockBytes = blockBytes, RawBytesEachCandidateInCentralWindow = 64, Statistics = stats.Export(), WholeFrameDataStatistics = wholeFrame.Export() });
        }

        return new
        {
            Method = new
            {
                Meaning = "Synthetic post-ECC 155-byte frames; these are not user payload byte encodings. Full user-data pipeline results must be used for actual burn plans.",
                Density = "Number of modulation ONEs divided by inspected channel bits; a modulation ONE is one NRZI level transition. All metrics exclude the 30-bit frame sync.",
                RepeatedFrame = "256 bytes; 31 sync indices x 2 previous-termination values x 8 deterministic DC masks = 496 frames per byte. 155 identical bytes per frame; 1902 inspected channel bits per sample.",
                Context = "Each candidate: 512 matched pseudorandom frames, 64-byte central repeated block at offsets 8..83, random neighbors, random DC mask, sync index and previous termination. Inspect inner 60 bytes using nominal 2:3 rate positions after DC insertion, with two-byte guards. This is a local stable-block metric, not an independently encoded per-byte metric.",
                Mixtures = "Unordered candidate pairs, block lengths 1/8/32 bytes, 128 frames each with cycling phase and matched pseudorandom DC masks/sync contexts. Statistics measures the nominal channel window for raw bytes16..143: 128 bytes, exactly64 of each candidate at any cyclic phase before the nominal pair-boundary trim. WholeFrameDataStatistics additionally measures all155bytes, whose A/B proportion varies by phase for longer blocks. Short patterns may interact nonlinearly.",
                RunCounts = "Distances between successive modulation ONEs fully inside the inspected window. Counts exclude incomplete boundary runs, sync-crossing runs and sync. Fractions normalize by all complete runs; mean run length uses these complete intervals.",
                Weighting = "Overall mean density is total transitions/total channel bits; minimum/maximum and standard deviation are per-sample densities. DC masks and sync contexts are deliberately sampled diagnostics, not an estimate of real-drive probabilities.",
                InitialLevel = "Initial channel level fixed at zero; NRZI complement changes mark/space polarity but cannot change transition density.",
                Limitation = "Neither density nor NRZI level occupancy establishes reflectance or black/white polarity. Actual recorder DC selection and write strategy may differ. Repeated ECC-frame and mixture diagnostics bypass scrambling/ECC/BIS."
            },
            RepeatedFrameBytes = FrameBytes,
            RepeatedFrameDataChannelBits = DataChannelBits,
            AllBytes = allBytes,
            CandidateContexts = contexts,
            CandidateMixtures = mixtures
        };
    }

    public static object RunPeriodicPatterns()
    {
        var patterns = new[]
        {
            (Name: "low_8T", Bits: "0000101101110010"),
            (Name: "high_2T3T", Bits: "0111011110"),
            (Name: "robust_high_6bit", Bits: "001111"),
            (Name: "baseline_11", Bits: "0001"),
            (Name: "baseline_3D", Bits: "00111101")
        };
        var masks = CreateMasks();
        var results = new List<object>();
        foreach (var pattern in patterns)
        foreach (string scheme in new[] { "continuous_raw_bits", "reserve_dc_pattern_slots", "restart_each_raw_segment" })
        {
            var fixedOverall = new Aggregate();
            var randomOverall = new Aggregate();
            var greedyOverall = new Aggregate();
            var phases = new List<object>();
            for (int phase = 0; phase < pattern.Bits.Length; phase++)
            {
                byte[] frame = MakePeriodicFrame(pattern.Bits, phase, scheme);
                var fixedStats = new Aggregate();
                var randomStats = new Aggregate();
                var greedyStats = new Aggregate();
                var byMask = new List<object>();
                foreach (var mask in masks)
                {
                    var perMask = new Aggregate();
                    for (int syncIndex = 0; syncIndex < 31; syncIndex++)
                    {
                        var encoded = BluRayRecordingFrameEncoder.Encode(frame, syncIndex, mask.Bits);
                        var bits = encoded.ModulationBits.AsSpan(SyncBits);
                        fixedStats.Add(bits, encoded.UsedTermination);
                        fixedOverall.Add(bits, encoded.UsedTermination);
                        perMask.Add(bits, encoded.UsedTermination);
                    }
                    byMask.Add(new { mask.Name, Statistics = perMask.Export() });
                }
                var rng = new Random(0x17CC);
                for (int sample = 0; sample < 64; sample++)
                {
                    var encoded = BluRayRecordingFrameEncoder.Encode(frame, rng.Next(31), RandomBits(rng, 28));
                    randomStats.Add(encoded.ModulationBits.AsSpan(SyncBits), encoded.UsedTermination);
                    randomOverall.Add(encoded.ModulationBits.AsSpan(SyncBits), encoded.UsedTermination);
                }
                foreach (int syncIndex in new[] { 0, 1, 3, 7, 15, 30 })
                foreach (long initialDsv in new long[] { -64, 0, 64 })
                {
                    var encoded = BluRayGreedyDcControlEncoder.Encode(frame, syncIndex, nextEccFrame: frame, initialDigitalSum: initialDsv).Frame;
                    greedyStats.Add(encoded.ModulationBits.AsSpan(SyncBits), encoded.UsedTermination);
                    greedyOverall.Add(encoded.ModulationBits.AsSpan(SyncBits), encoded.UsedTermination);
                }
                phases.Add(new { Phase = phase, FixedMasks = fixedStats.Export(), RandomMasks = randomStats.Export(), Greedy = greedyStats.Export(), ByDcMask = byMask });
            }
            results.Add(new { pattern.Name, PatternBits = pattern.Bits, Scheme = scheme, AllPhasesFixedMasks = fixedOverall.Export(), AllPhasesRandomMasks = randomOverall.Export(), AllPhasesGreedy = greedyOverall.Export(), Phases = phases });
        }
        return new
        {
            Method = "Synthetic post-ECC frames only, 155bytes/1240 raw bits plus28 DC bits, 1902 measured channel bits excluding sync. All cyclic phases are tested. continuous_raw_bits uses pattern[(rawIndex+phase)%period]. reserve_dc_pattern_slots uses pattern[(rawIndex+precedingDcCount+phase)%period], so the 28 actual DC bits overwrite reserved pattern positions. restart_each_raw_segment starts the pattern again after each raw segment (25 then45bits). Fixed-mask experiment uses8 masks x31sync indices; random-mask experiment uses64 matched masks/sync choices; greedy uses6sync indices x initialDSV(-64,0,+64), initialNRZI level0, next identical ECCframe for lookahead. All-phases means are phase-sensitivity summaries, not a recommended recipe. These results bypass parity, scrambling, BIS and full-cluster context.",
            Results = results
        };
    }

    private static byte[] MakePeriodicFrame(string pattern, int phase, string scheme)
    {
        byte[] frame = new byte[FrameBytes];
        for (int raw = 0; raw < FrameBytes * 8; raw++)
        {
            int index = scheme switch
            {
                "continuous_raw_bits" => raw,
                "reserve_dc_pattern_slots" => raw + InsertedControlsBefore(raw),
                "restart_each_raw_segment" => raw < 25 ? raw : (raw - 25) % 45,
                _ => throw new ArgumentOutOfRangeException(nameof(scheme))
            };
            if (pattern[(index + phase) % pattern.Length] == '1') frame[raw / 8] |= (byte)(1 << (7 - raw % 8));
        }
        return frame;
    }
    private static (int Start, int End) CentralWindow(int startByte, int endByte)
    {
        int startData = startByte * 8 + InsertedControlsBefore(startByte * 8);
        int endData = endByte * 8 + InsertedControlsBefore(endByte * 8);
        startData = (startData + 1) / 2 * 2;
        endData = endData / 2 * 2;
        return (SyncBits + startData / 2 * 3, SyncBits + endData / 2 * 3);
    }

    private static int InsertedControlsBefore(int inputBit)
        => inputBit < 25 ? 0 : 1 + (inputBit - 25) / 45;

    private static List<(string Name, byte[] Bits)> CreateMasks()
    {
        var masks = new List<(string Name, byte[] Bits)>
        {
            ("all_zero", new byte[28]),
            ("all_one", Enumerable.Repeat((byte)1, 28).ToArray()),
            ("alternating_01", Enumerable.Range(0, 28).Select(i => (byte)(i & 1)).ToArray()),
            ("alternating_10", Enumerable.Range(0, 28).Select(i => (byte)((i + 1) & 1)).ToArray())
        };
        var rng = new Random(0x30190);
        for (int i = 0; i < 4; i++) masks.Add(($"random_seed30190_{i}", RandomBits(rng, 28)));
        return masks;
    }

    private static byte[] RandomBits(Random rng, int count)
        => Enumerable.Range(0, count).Select(_ => (byte)rng.Next(2)).ToArray();

    private sealed class Aggregate
    {
        private int _samples;
        private long _transitions;
        private long _bits;
        private double _sumDensity;
        private double _sumDensitySquared;
        private double _minimum = double.PositiveInfinity;
        private double _maximum;
        private int _terminations;
        private readonly long[] _runs = new long[9];
        private long _otherRuns;
        private long _runLength;

        public void Add(ReadOnlySpan<byte> bits, bool termination)
        {
            int count = 0;
            int previousTransition = -1;
            for (int i = 0; i < bits.Length; i++)
            {
                if (bits[i] == 0) continue;
                count++;
                if (previousTransition >= 0)
                {
                    int run = i - previousTransition;
                    if (run >= 2 && run <= 8) _runs[run]++;
                    else _otherRuns++;
                    _runLength += run;
                }
                previousTransition = i;
            }
            double density = (double)count / bits.Length;
            _samples++;
            _transitions += count;
            _bits += bits.Length;
            _sumDensity += density;
            _sumDensitySquared += density * density;
            _minimum = Math.Min(_minimum, density);
            _maximum = Math.Max(_maximum, density);
            if (termination) _terminations++;
        }

        public object Export()
        {
            long completeRuns = _runs.Sum() + _otherRuns;
            double average = _sumDensity / _samples;
            return new
            {
                Samples = _samples,
                TotalTransitions = _transitions,
                TotalChannelBits = _bits,
                MeanTransitionDensity = (double)_transitions / _bits,
                MinimumTransitionDensity = _minimum,
                MaximumTransitionDensity = _maximum,
                TransitionDensityStandardDeviation = Math.Sqrt(Math.Max(0, _sumDensitySquared / _samples - average * average)),
                FrameTerminationFraction = (double)_terminations / _samples,
                CompleteRunCount = completeRuns,
                MeanCompleteRunLengthT = (double)_runLength / completeRuns,
                RunCounts2TTo8T = _runs.Skip(2).ToArray(),
                RunFractions2TTo8T = _runs.Skip(2).Select(n => (double)n / completeRuns).ToArray(),
                RunCountOutside2TTo8T = _otherRuns
            };
        }
    }
}
