using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DiscImageStudio.BluRay.Encoding;

if (args.Length > 0 && args[0] == "--verify-reference")
{
    var vectors = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!;
    foreach ((string hex, string expected) in vectors)
    {
        byte value = byte.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte[] bits = Enumerable.Range(0, 32).Select(i => (byte)((value >> (7 - i % 8)) & 1)).ToArray();
        string actual = string.Concat(BluRay17PpEncoder.Encode(bits).ModulationBits.Select(b => b == 0 ? '0' : '1'));
        if (actual != expected) throw new InvalidOperationException($"Independent reference mismatch: {hex}.");
    }
    Console.WriteLine($"Independent Figure 41 reference: {vectors.Count} finite vectors matched bit for bit.");
    return;
}

if (args.Length > 0 && args[0] == "--verify-patterns")
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(args[1]));
    int count = 0;
    foreach (JsonProperty entry in document.RootElement.EnumerateObject())
    {
        byte[] bits = entry.Value.GetProperty("data_bits").GetString()!.Select(c => (byte)(c - '0')).ToArray();
        byte previous = entry.Value.GetProperty("previous_modulation_bit").GetByte();
        string expected = entry.Value.GetProperty("modulation_bits").GetString()!;
        string actual = string.Concat(BluRay17PpEncoder.Encode(bits, previous).ModulationBits.Select(b => b == 0 ? '0' : '1'));
        if (actual != expected) throw new InvalidOperationException($"Independent pattern reference mismatch: {entry.Name}.");
        count++;
    }
    Console.WriteLine($"Independent periodic-pattern reference: {count} finite vectors matched bit for bit.");
    return;
}

if (args.Length > 0 && args[0] == "--fine")
{
    string destination = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/bluray-byte-density");
    Directory.CreateDirectory(destination);
    object fine = FinePatternAnalysis.Run(["0001", "00111101", "001111", "0000101101110010", "0111011110"]);
    File.WriteAllText(Path.Combine(destination, "fine-patterns.json"), JsonSerializer.Serialize(fine, new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText(Path.Combine(destination, "periodic-context.json"), JsonSerializer.Serialize(ContextAnalysis.RunPeriodicPatterns(), new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Fine-pattern analysis written to {destination}.");
    return;
}

string outputDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/bluray-byte-density");
int parallelism = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : Math.Min(4, Environment.ProcessorCount);
if (parallelism is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(parallelism));
Directory.CreateDirectory(outputDirectory);
JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
Stopwatch stopwatch = Stopwatch.StartNew();

// One full reference RUB both supplies the exact common run-in state and validates
// our faster physical-cluster-only scanner against the production diagnostic path.
byte[] referenceEcc = CreateEcc(0, 0x100000, scrambledDomain: true);
BluRayRecordingUnitResult reference = BluRayRecordingUnitEncoder.EncodeEccCluster(referenceEcc, 0x100000);
byte initialLevel = reference.ChannelLevels[2760 - 1];
long initialSum = SumLevels(reference.ChannelLevels.AsSpan(0, 2760));
byte[] runIn = reference.ModulationBits[..2760];
Scan referenceScan = AnalyzeEcc(referenceEcc, initialLevel, initialSum, "greedy");
long expectedTransitions = CountOnes(reference.ModulationBits.AsSpan(2760, 496 * 1932));
if (referenceScan.Transitions != expectedTransitions
    || referenceScan.Ones != CountOnes(reference.ChannelLevels.AsSpan(2760, 496 * 1932)))
    throw new InvalidOperationException("Scanner disagrees with full diagnostic RUB.");
Console.WriteLine($"Reference cross-check passed; {stopwatch.Elapsed.TotalSeconds:F1}s.");

ByteResult[] results = new ByteResult[256];
int completed = 0;
object progressLock = new();
string progressPath = Path.Combine(outputDirectory, "progress.jsonl");
File.WriteAllText(progressPath, string.Empty);
Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, value =>
{
    byte q = (byte)value;
    byte[] repeatBits = new byte[16_384 * 8];
    for (int i = 0; i < repeatBits.Length; i++) repeatBits[i] = (byte)((q >> (7 - i % 8)) & 1);
    byte[] repeated = BluRay17PpEncoder.Encode(repeatBits).ModulationBits;
    // Trimming avoids treating frame termination as part of the infinite repeated pattern.
    Scan pure = Measure(repeated.AsSpan(2048, repeated.Length - 4096), 0);
    byte[] ecc = value == 0 ? referenceEcc : CreateEcc(q, 0x100000, scrambledDomain: true);
    Scan greedy = value == 0 ? referenceScan : AnalyzeEcc(ecc, initialLevel, initialSum, "greedy");
    Scan zero = AnalyzeEcc(ecc, initialLevel, initialSum, "zero");
    Scan ones = AnalyzeEcc(ecc, initialLevel, initialSum, "one");
    Scan random = AnalyzeEcc(ecc, initialLevel, initialSum, "random");
    results[value] = new(q, $"{q:X2}", pure, greedy, zero.Density, ones.Density, random.Density);
    lock (progressLock)
    {
        File.AppendAllText(progressPath, JsonSerializer.Serialize(results[value]) + "\n");
        int count = ++completed;
        if (count % 16 == 0) Console.WriteLine($"Full-cluster scan {count}/256; {stopwatch.Elapsed.TotalSeconds:F1}s.");
    }
});
File.WriteAllText(Path.Combine(outputDirectory, "all-bytes.json"), JsonSerializer.Serialize(results, jsonOptions));
StringBuilder csv = new("byte,hex,pure_17pp_density,cluster_greedy_density,cluster_zero_dc_density,cluster_one_dc_density,cluster_random_dc_density,cluster_nrzi_one_fraction,mean_run_T,run_2T_fraction,run_3T_fraction,run_4T_fraction,run_5T_fraction,run_6T_fraction,run_7T_fraction,run_8T_fraction,run_9T_fraction\n");
foreach (ByteResult value in results)
{
    Scan s = value.Cluster;
    csv.Append(value.Byte).Append(',').Append(value.Hex).Append(',')
        .Append(F(value.Pure.Density)).Append(',').Append(F(s.Density)).Append(',')
        .Append(F(value.ZeroDcDensity)).Append(',').Append(F(value.OneDcDensity)).Append(',').Append(F(value.RandomDcDensity)).Append(',')
        .Append(F(s.OneFraction)).Append(',').Append(F(s.MeanRun));
    for (int run = 2; run <= 9; run++) csv.Append(',').Append(F((double)s.RunCounts[run] / s.CompletedRuns));
    csv.AppendLine();
}
File.WriteAllText(Path.Combine(outputDirectory, "all-bytes.csv"), csv.ToString(), new UTF8Encoding(true));
ByteResult[] ordered = results.OrderBy(result => result.Cluster.Density).ToArray();
Console.WriteLine("Lowest: " + string.Join(" ", ordered.Take(16).Select(r => $"{r.Hex}:{r.Cluster.Density:P3}")));
Console.WriteLine("Highest: " + string.Join(" ", ordered.TakeLast(16).Reverse().Select(r => $"{r.Hex}:{r.Cluster.Density:P3}")));

// Preserve structural low/high representatives as well as the empirical extrema.
byte[] candidates = ordered.Take(4).Concat(ordered.TakeLast(4)).Select(r => r.Byte)
    .Concat(new byte[] { 0x00, 0xFF, 0x55, 0xAA, 0x11, 0x22, 0x3D, 0x7A }).Distinct().Order().ToArray();
uint[] psns = [0x100000, 0x234560, 0x6D3A00, 0xCA73E0];
List<object> confirmations = [];
foreach (byte q in candidates)
{
    foreach (uint psn in psns)
    {
        Scan scan = psn == 0x100000 ? results[q].Cluster
            : AnalyzeEcc(CreateEcc(q, psn, scrambledDomain: true), initialLevel, initialSum, "greedy");
        confirmations.Add(new { Byte = q, Hex = $"{q:X2}", Psn = $"0x{psn:X8}", Stats = scan });
    }
    Console.WriteLine($"Address confirmation {q:X2}; {stopwatch.Elapsed.TotalSeconds:F1}s.");
}
File.WriteAllText(Path.Combine(outputDirectory, "address-confirmation.json"), JsonSerializer.Serialize(confirmations, jsonOptions));
List<object> hostControls = [];
foreach (byte q in candidates)
{
    Scan scan = AnalyzeEcc(CreateEcc(q, 0x100000, scrambledDomain: false), initialLevel, initialSum, "greedy");
    hostControls.Add(new { Byte = q, Hex = $"{q:X2}", Stats = scan });
}
File.WriteAllText(Path.Combine(outputDirectory, "raw-host-control.json"), JsonSerializer.Serialize(hostControls, jsonOptions));

// ContextAnalysis is kept separate so the full-chain and context sweeps can be reviewed independently.
object context = ContextAnalysis.Run(candidates);
File.WriteAllText(Path.Combine(outputDirectory, "context.json"), JsonSerializer.Serialize(context, jsonOptions));
File.WriteAllText(Path.Combine(outputDirectory, "method.json"), JsonSerializer.Serialize(new
{
    ByteDomain = "Post-scramble user byte Q. Host sectors contain Q XOR PSN-derived scrambling mask.",
    PhysicalCluster = "65536 host bytes -> EDC/scramble -> LDC/BIS/ECC -> DC bits -> 17PP/sync; 496*1932=958272 channel bits.",
    Density = "Number of modulation ONEs / number of channel bits; each ONE is one NRZI transition.",
    Pure = "16384 repeated bytes, no DC/ECC/sync; discard first and last 2048 channel bits; approximate periodic reference.",
    Dc = "Greedy diagnostic policy with future control bits provisionally zero; not a drive-identical or fully optimal policy.",
    OtherDcPolicies = "All-zero/all-one/seeded-random controls are sensitivity probes, not DC-balanced recording prescriptions.",
    Psn = "0x00100000 for all 256; shortlisted bytes rechecked at 0x00234560, 0x006D3A00, 0x00CA73E0.",
    ControlData = "16 address flags and 576 user-control bytes zero; nominal single-layer BD-R model.",
    Excluded = "Run-in/out/Guard3 excluded from ranked physical-cluster densities. Run-in state retained for greedy policy.",
    Polarity = "Initial NRZI polarity changes level occupancy but never transition positions; density does not determine visual black/white.",
    Candidates = candidates.Select(q => $"{q:X2}"),
    ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
    RunInTransitions = CountOnes(runIn), InitialLevel = initialLevel, InitialDigitalSum = initialSum,
}, jsonOptions));
Console.WriteLine($"Complete: {outputDirectory}; {stopwatch.Elapsed.TotalSeconds:F1}s.");

static byte[] CreateEcc(byte q, uint psn, bool scrambledDomain)
{
    byte[] payload = new byte[65536];
    byte[] mask = BluRayScrambler.CreateMask(psn, 2048);
    for (int i = 0; i < payload.Length; i++) payload[i] = scrambledDomain ? (byte)(q ^ mask[i % 2048]) : q;
    return BluRayEccEncoder.Encode(payload, psn);
}

static Scan AnalyzeEcc(byte[] ecc, byte initialLevel, long initialSum, string mode)
{
    StatsAccumulator stats = new(initialLevel);
    bool previousTermination = false;
    byte level = initialLevel;
    long sum = initialSum;
    Random random = new(0x17BB);
    byte[] controls = new byte[28];
    for (int frame = 0; frame < 496; frame++)
    {
        ReadOnlySpan<byte> current = ecc.AsSpan(frame * 155, 155);
        BluRayRecordingFrameResult result;
        if (mode == "greedy")
            result = BluRayGreedyDcControlEncoder.Encode(current, frame % 31,
                frame == 495 ? default : ecc.AsSpan((frame + 1) * 155, 155), previousTermination, level, sum).Frame;
        else
        {
            for (int i = 0; i < controls.Length; i++) controls[i] = mode switch
            { "zero" => 0, "one" => 1, "random" => (byte)random.Next(2), _ => throw new ArgumentException("Unknown DC policy.") };
            result = BluRayRecordingFrameEncoder.Encode(current, frame % 31, controls, previousTermination, level);
        }
        stats.Add(result.ModulationBits);
        level = result.FinalChannelLevel;
        sum += result.DigitalSum;
        previousTermination = result.UsedTermination;
    }
    Scan measured = stats.Finish();
    if (measured.ChannelBits != 958272 || measured.RunCounts.Skip(10).Any(c => c != 0) || measured.RunCounts[1] != 0)
        throw new InvalidOperationException("Physical-cluster length or RLL boundaries invalid.");
    return measured;
}

static Scan Measure(ReadOnlySpan<byte> modulation, byte initialLevel)
{
    StatsAccumulator accumulator = new(initialLevel);
    accumulator.Add(modulation);
    return accumulator.Finish();
}
static long CountOnes(ReadOnlySpan<byte> bits) { long result = 0; foreach (byte bit in bits) result += bit; return result; }
static long SumLevels(ReadOnlySpan<byte> bits) { long result = 0; foreach (byte bit in bits) result += bit == 0 ? -1 : 1; return result; }
static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

internal sealed record ByteResult(byte Byte, string Hex, Scan Pure, Scan Cluster, double ZeroDcDensity, double OneDcDensity, double RandomDcDensity);
internal sealed record Scan(long ChannelBits, long Transitions, long Ones, long[] RunCounts, long CompletedRuns, long CompletedRunBits)
{
    public double Density => (double)Transitions / ChannelBits;
    public double OneFraction => (double)Ones / ChannelBits;
    public double MeanRun => CompletedRuns == 0 ? 0 : (double)CompletedRunBits / CompletedRuns;
}
internal sealed class StatsAccumulator(byte initialLevel)
{
    private long _bits, _transitions, _ones, _lastTransition = -1, _runBits, _runs;
    private readonly long[] _histogram = new long[32];
    private byte _level = initialLevel;
    internal void Add(ReadOnlySpan<byte> modulation)
    {
        foreach (byte bit in modulation)
        {
            if (bit > 1) throw new InvalidOperationException("Non-binary modulation value.");
            if (bit == 1)
            {
                _transitions++;
                if (_lastTransition >= 0)
                {
                    long gap = _bits - _lastTransition;
                    if (gap >= _histogram.Length) throw new InvalidOperationException("Run exceeds histogram range.");
                    _histogram[gap]++; _runBits += gap; _runs++;
                }
                _lastTransition = _bits;
            }
            _level ^= bit;
            _ones += _level;
            _bits++;
        }
    }
    internal Scan Finish() => new(_bits, _transitions, _ones, _histogram, _runs, _runBits);
}
