using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DiscImageStudio.Burning;
using DiscImageStudio.ViewModels;

internal static class BurnRateViewModelTests
{
    internal static void Run()
    {
        // Construct only the presentation model. The injected burner rejects even device
        // discovery, so synthetic progress events never reach physical recorder APIs.
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        typeof(ShellViewModel).GetField("_log", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(shell, new StringBuilder());
        RejectingBurner burner = new();
        BurnViewModel model = new(shell, new DiscParametersState(), burner);
        Require(model.CurrentWriteRateText == "—" && model.BurnProgressValue == 0,
            "a fresh burn starts without a measured write rate");

        List<string?> changed = [];
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Apply(model, new("transfer", "Reading producer bytes", 900, 1000, 90_000_000), OpticalBurnMediaKind.BluRayData);
        Require(model.CurrentWriteRateText == "—" && model.BurnProgressValue == 0
            && model.BurnStatusText == "Reading producer bytes",
            "producer transfer before the first write may update status but cannot claim write speed or physical progress");
        Apply(model, new("write", "First recorder position", 100, 1000), OpticalBurnMediaKind.BluRayData);
        Require(model.CurrentWriteRateText == "测量中…" && model.BurnProgressValue == 10,
            "the first write without a rate reports measurement pending and retains recorder progress");
        Require(changed.Contains(nameof(model.CurrentWriteRateText)),
            "write-rate transitions notify the displayed text binding");

        // Decimal MB/s and each medium's independent 1x unit are deliberately checked
        // against known examples, rather than calling the production speed formatter.
        (OpticalBurnMediaKind Kind, double BytesPerSecond, double Megabytes, double Multiplier)[] examples =
        [
            (OpticalBurnMediaKind.CdAudio, 1_764_000, 1.764, 10),
            (OpticalBurnMediaKind.DvdData, 2_770_000, 2.77, 2),
            (OpticalBurnMediaKind.BluRayData, 9_000_000, 9, 2),
            (OpticalBurnMediaKind.BluRayData, 9_500_000, 9.5, 9.5 / 4.5),
        ];
        foreach (var example in examples)
        {
            Reset(model);
            Apply(model, new("write", "Measured recorder write", 250, 1000, example.BytesPerSecond), example.Kind);
            RequireRate(model.CurrentWriteRateText, example.Megabytes, example.Multiplier, example.Kind.ToString());
            Require(model.BurnProgressValue == 25, "measured rate does not replace the recorded write position");

            string measured = model.CurrentWriteRateText;
            string status = model.BurnStatusText;
            Apply(model, new("transfer", "Producer read ahead", 1000, 1000, 500_000_000), example.Kind);
            Require(model.CurrentWriteRateText == measured && model.BurnProgressValue == 25
                && model.BurnStatusText == status,
                "producer read-ahead after recorder progress cannot overwrite measured speed, progress, or status");
        }

        foreach (double invalidRate in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
        {
            Apply(model, new("write", "Valid sample", 300, 1000, 9_000_000), OpticalBurnMediaKind.BluRayData);
            Apply(model, new("write", "Unavailable sample", 400, 1000, invalidRate), OpticalBurnMediaKind.BluRayData);
            Require(model.CurrentWriteRateText == "测量中…" && model.BurnProgressValue == 40,
                "nonfinite and negative rates never display NaN, infinity, a negative speed, or a stale measurement");
        }
        Apply(model, new("write", "Stationary write position", 400, 1000, 0), OpticalBurnMediaKind.BluRayData);
        RequireRate(model.CurrentWriteRateText, 0, 0, "zero rate");
        Require(model.BurnProgressValue == 40, "zero measured throughput is valid and leaves write progress intact");

        foreach (string stage in new[] { "buffer", "finalize", "complete" })
        {
            Reset(model);
            Apply(model, new("write", "Writing", 500, 1000, 9_000_000), OpticalBurnMediaKind.BluRayData);
            string stageMessage = "Stage " + stage;
            Apply(model, new(stage, stageMessage, 800, 1000, 9_000_000), OpticalBurnMediaKind.BluRayData);
            Require(model.CurrentWriteRateText == "—" && model.BurnStatusText == stageMessage
                && model.BurnProgressValue == 80,
                $"{stage} clears measured writing speed even if a producer supplies a rate on that stage");
            Apply(model, new("transfer", "Late producer event", 1000, 1000, 30_000_000), OpticalBurnMediaKind.BluRayData);
            Require(model.CurrentWriteRateText == "—" && model.BurnStatusText == stageMessage
                && model.BurnProgressValue == 80,
                $"late producer events cannot overwrite {stage} after device writing has begun");
        }

        Apply(model, new("write", "Previous job", 900, 1000, 9_000_000), OpticalBurnMediaKind.BluRayData);
        Reset(model);
        Require(model.CurrentWriteRateText == "—" && model.BurnProgressValue == 0,
            "reset clears the previous job's measurement and progress");
        Apply(model, new("transfer", "New job buffering", 700, 1000, 100_000_000), OpticalBurnMediaKind.CdAudio);
        Require(model.BurnStatusText == "New job buffering" && model.CurrentWriteRateText == "—"
            && model.BurnProgressValue == 0,
            "reset also clears the write-progress guard so a new job can report its buffering status");
        Apply(model, new("write", "New job first position", 50, 1000), OpticalBurnMediaKind.CdAudio);
        Require(model.CurrentWriteRateText == "测量中…" && model.BurnProgressValue == 5,
            "the next job measures afresh rather than reusing the previous job's rate");
        TestStaleWriteRates(model);
        TestDiagnosticLogs(model, shell);
        Require(burner.Calls == 0, "rate presentation never enumerates, queries, or writes to a recorder");
        Console.WriteLine("burn-rate-ui: passed (CD/DVD/BD units, pending/zero/invalid rates, monotonic expiry, stage reset, transfer isolation; no recorder access)");
    }

    private static void TestDiagnosticLogs(BurnViewModel model, ShellViewModel shell)
    {
        long start = 100 * Stopwatch.Frequency;
        Reset(model);
        model.IsBurnRunning = true;
        BeginTracking(model, start);
        Apply(model, new("write", "Accepted data", 450, 1000, 9_500_000), OpticalBurnMediaKind.BluRayData, start);
        string measured = model.CurrentWriteRateText;
        foreach (string stage in new[] { "speed", "diagnostic" })
        {
            string message = "Logged " + stage;
            Apply(model, new(stage, message, 0, 1000), OpticalBurnMediaKind.BluRayData,
                start + Stopwatch.Frequency);
            Require(shell.LogText.Contains(message, StringComparison.Ordinal),
                "device settings and timing diagnostics remain available in the log during writing");
            Require(model.CurrentWriteRateText == measured && model.BurnProgressValue == 45
                && model.BurnStatusText == "Accepted data",
                "diagnostic records do not reset or replace measured rate, position or status");
        }
        Age(model, start + 2 * Stopwatch.Frequency);
        Require(model.CurrentWriteRateText == "等待写入进度…",
            "diagnostic records cannot keep an old device-write sample fresh");
        model.IsBurnRunning = false;
        Reset(model);
    }

    private static void TestStaleWriteRates(BurnViewModel model)
    {
        long second = Stopwatch.Frequency;
        long start = 100 * second;
        Require(typeof(BurnViewModel).GetField("_writeRateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(model) is null, "constructing the presentation model creates no dispatcher timer");
        Reset(model);
        model.IsBurnRunning = true;
        BeginTracking(model, start);
        string initialStatus = model.BurnStatusText;
        Age(model, start + 19 * second / 10);
        Require(model.CurrentWriteRateText == "—", "initial waiting does not prematurely invent a measured rate");
        Age(model, start + 2 * second);
        Require(model.CurrentWriteRateText == "等待写入进度…" && model.BurnProgressValue == 0
            && model.BurnStatusText == initialStatus,
            "two seconds without the first device sample shows neutral waiting without changing status or progress");

        Apply(model, new("write", "Fresh sample", 450, 1000, 9_000_000), OpticalBurnMediaKind.BluRayData, start + 3 * second);
        RequireRate(model.CurrentWriteRateText, 9, 2, "fresh sample after waiting");
        string measured = model.CurrentWriteRateText;
        Apply(model, new("transfer", "Producer continues filling", 1000, 1000, 90_000_000),
            OpticalBurnMediaKind.BluRayData, start + 49 * second / 10);
        Age(model, start + 49 * second / 10);
        Require(model.CurrentWriteRateText == measured, "a measured device rate remains visible within its freshness window");
        Age(model, start + 5 * second);
        Require(model.CurrentWriteRateText == "等待写入进度…" && model.BurnProgressValue == 45
            && model.BurnStatusText == "Fresh sample",
            "producer activity cannot keep an expired device rate alive or fabricate new write progress");

        Apply(model, new("write", "Rate not measured yet", 500, 1000), OpticalBurnMediaKind.BluRayData, start + 6 * second);
        Require(model.CurrentWriteRateText == "测量中…", "a fresh rate-less device sample replaces stale waiting with measurement pending");
        Age(model, start + 8 * second);
        Require(model.CurrentWriteRateText == "等待写入进度…" && model.BurnProgressValue == 50,
            "an initial pending rate also expires when device callbacks stop");
        Apply(model, new("write", "Measured stationary position", 500, 1000, 0),
            OpticalBurnMediaKind.BluRayData, start + 9 * second);
        RequireRate(model.CurrentWriteRateText, 0, 0, "fresh measured zero after waiting");
        Age(model, start + 11 * second);
        Require(model.CurrentWriteRateText == "等待写入进度…",
            "expiration does not replace unknown throughput with a fabricated zero");

        Reset(model);
        BeginTracking(model, start);
        Apply(model, new("transfer", "Still generating source", 800, 1000, 90_000_000),
            OpticalBurnMediaKind.BluRayData, start + 4 * second);
        Require(model.CurrentWriteRateText == "等待写入进度…" && model.BurnProgressValue == 0
            && model.BurnStatusText == "Still generating source",
            "waiting before device writing stays neutral when generation itself is still buffering");

        foreach (string stage in new[] { "finalize", "complete" })
        {
            Reset(model);
            BeginTracking(model, start);
            Apply(model, new("write", "Writing", 500, 1000, 9_000_000), OpticalBurnMediaKind.BluRayData, start);
            Apply(model, new(stage, "Terminal " + stage, 800, 1000), OpticalBurnMediaKind.BluRayData, start + second);
            Age(model, start + 200 * second);
            Apply(model, new("write", "Late stale callback", 700, 1000, 9_000_000),
                OpticalBurnMediaKind.BluRayData, start + 201 * second);
            Require(model.CurrentWriteRateText == "—" && model.BurnStatusText == "Terminal " + stage
                && model.BurnProgressValue == 80,
                "timer expiry and delayed write callbacks cannot resurrect throughput during " + stage);
        }

        model.IsBurnRunning = false;
        Reset(model);
        BeginTracking(model, start);
        Age(model, start + 1000 * second);
        Require(model.CurrentWriteRateText == "—" && model.BurnProgressValue == 0,
            "a stopped job and a reset ignore old timeout ticks");
        Require(typeof(BurnViewModel).GetField("_writeRateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(model) is null, "clock-driven regression tests need no Dispatcher or sleep");
    }

    private static void Apply(BurnViewModel model, OpticalBurnProgress progress, OpticalBurnMediaKind kind, long? timestamp = null)
        => typeof(BurnViewModel).GetMethod("ApplyBurnProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [progress, kind, timestamp]);

    private static void BeginTracking(BurnViewModel model, long timestamp)
        => typeof(BurnViewModel).GetMethod("BeginWriteRateTracking", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [timestamp]);

    private static void Age(BurnViewModel model, long timestamp)
        => typeof(BurnViewModel).GetMethod("UpdateWriteRateAge", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [timestamp]);

    private static void Reset(BurnViewModel model)
        => typeof(BurnViewModel).GetMethod("ResetBurnProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, null);

    private static void RequireRate(string text, double megabytes, double multiplier, string scenario)
    {
        Require(Math.Abs(NumberBeforeUnit(text, "MB/s") - megabytes) <= 0.0051,
            scenario + " displays decimal megabytes per second");
        Require(Math.Abs(NumberBeforeUnit(text, "×") - multiplier) <= 0.051,
            scenario + " displays the correct medium-specific speed multiplier");
    }

    private static double NumberBeforeUnit(string text, string unit)
    {
        Match match = Regex.Match(text, @"(?<number>\d+(?:[.,]\d+)?)\s*" + Regex.Escape(unit));
        Require(match.Success, "rate text contains a numeric " + unit + " value: " + text);
        return double.Parse(match.Groups["number"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Burn rate UI: " + message);
    }

    private sealed class RejectingBurner : IOpticalDiscBurner
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Rate tests must not enumerate recorder devices.");
        }

        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Rate tests must not query recorder speeds.");
        }

        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Rate tests must not write to a recorder.");
        }
    }
}
