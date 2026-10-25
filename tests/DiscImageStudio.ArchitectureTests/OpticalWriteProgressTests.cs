using System.Dynamic;
using DiscImageStudio.Burning;

internal static class OpticalWriteProgressTests
{
    internal static void Run()
    {
        TestDeviceLbasAndRollingRate();
        TestCdPayloadAndLeadIn();
        TestEngineUpdatesAndBounds();
        TestAcknowledgedCommands();
        TestPhaseResetAndUnavailableTelemetry();
        TestConsumerFailureIsIsolated();
        Require(new OpticalBurnProgress("buffer", "buffering", 0, 2048).BytesPerSecond is null,
            "existing four-argument progress calls have no fabricated measured speed");
        Console.WriteLine("optical-write-progress: passed (device LBAs, rolling rates, CD/DVD/BD sector sizes, phase reset; no device access)");
    }

    private static void TestDeviceLbasAndRollingRate()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.DvdData, 20_000L * 2048,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportDataUpdate(Event(100, 99, 4, lastReadLba: 19_999));
        Require(updates[^1].Stage == "write" && updates[^1].CompletedBytes == 0
            && updates[^1].BytesPerSecond is null, "initial device telemetry is measuring, even if source reads are far ahead");
        seconds = 1;
        reporter.ReportDataUpdate(Event(100, 1099, 4, lastReadLba: 19_999));
        Require(updates[^1].CompletedBytes == 1000L * 2048, "inclusive last-written LBA is relative to the write origin");
        EqualRate(2_048_000, updates[^1], "DVD rate uses actual written 2048-byte sectors");
        seconds = 2;
        reporter.ReportDataUpdate(Event(100, 2099, 4));
        EqualRate(2_048_000, updates[^1], "steady writing stays stable across the rolling window");
        seconds = 3;
        reporter.ReportDataUpdate(Event(100, 4099, 4));
        EqualRate(3_072_000, updates[^1], "the recent window reacts to acceleration instead of using a whole-burn average");
        seconds = 5;
        reporter.ReportDataUpdate(Event(100, 4099, 4));
        EqualRate(0, updates[^1], "unchanged device LBAs across a full window report a real stall");
        seconds = 8;
        reporter.ReportDataUpdate(Event(100, 5099, 4));
        Require(updates[^1].BytesPerSecond is null, "a long telemetry gap does not reuse an old speed");
    }

    private static void TestCdPayloadAndLeadIn()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.CdAudio, 10_000L * 2352,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportRawCdUpdate(Event(-4500, -500, 2));
        Require(updates[^1].Stage == "media" && updates[^1].CompletedBytes == 0
            && updates[^1].BytesPerSecond is null, "DAO lead-in does not consume audio payload progress");
        seconds = 1;
        reporter.ReportRawCdUpdate(Event(-4500, 99, 2));
        Require(updates[^1].CompletedBytes == 100L * 2352 && updates[^1].BytesPerSecond is null,
            "CD audio begins at nonnegative data LBAs and starts a fresh measurement");
        seconds = 2;
        reporter.ReportRawCdUpdate(Event(-4500, 174, 2));
        EqualRate(176_400, updates[^1], "75 CD audio sectors/second yield 176400 payload bytes/second");
        reporter.ReportRawCdUpdate(Event(-4500, 175, 3));
        Require(updates[^1].Stage == "finalize" && updates[^1].BytesPerSecond is null,
            "DAO finishing clears the live rate");
    }

    private static void TestEngineUpdatesAndBounds()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.BluRayData, 1024L * 2048,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportEngineUpdate(Event(0, -1));
        seconds = 1;
        reporter.ReportEngineUpdate(Event(0, 319));
        EqualRate(655_360, updates[^1], "BD write-engine events need no CurrentAction property and use 2048-byte sectors");
        seconds = 2;
        reporter.ReportEngineUpdate(Event(0, int.MaxValue));
        Require(updates[^1].CompletedBytes == 1024L * 2048 && updates[^1].Fraction == 1,
            "out-of-range device counters cannot exceed the requested content");
        seconds = 3;
        reporter.ReportEngineUpdate(Event(0, 100));
        Require(updates[^1].BytesPerSecond is null, "a counter reset cannot produce negative throughput");
        seconds = 4;
        reporter.ReportEngineUpdate(Event(200, 299));
        Require(updates[^1].BytesPerSecond is null, "a changed write origin starts a separate sample window");
        seconds = 1;
        reporter.ReportEngineUpdate(Event(200, 399));
        Require(updates[^1].BytesPerSecond is null, "a clock reset cannot produce invalid throughput");
    }

    private static void TestAcknowledgedCommands()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        const long length = 1024L * 2048;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.BluRayData, length,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportAcceptedBytes(0, length);
        Require(updates[^1].CompletedBytes == 0 && updates[^1].BytesPerSecond is null,
            "BD first command is pending until the recorder acknowledges data");
        seconds = 1;
        reporter.ReportAcceptedBytes(32L * 2048, length);
        EqualRate(65_536, updates[^1], "BD acknowledged-command rate counts exactly the accepted payload");
        Require(updates[^1].Message.Contains("刻录机已接收", StringComparison.Ordinal),
            "BD progress describes device acceptance without promising cache flush");
        reporter.ReportStage("finalize", "Flushing recorder cache");
        Require(updates[^1].CompletedBytes == 65_536 && updates[^1].BytesPerSecond is null,
            "cache draining retains acknowledged progress but clears transfer speed");
    }

    private static void TestPhaseResetAndUnavailableTelemetry()
    {
        List<OpticalBurnProgress> updates = [];
        double seconds = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.DvdData, 4096L * 2048,
            updates.Add, () => TimeSpan.FromSeconds(seconds));
        reporter.ReportDataUpdate(Event(0, 0, 4));
        seconds = 1;
        reporter.ReportDataUpdate(Event(0, 100, 4));
        Require(updates[^1].BytesPerSecond > 0, "fixture establishes a measured write rate");
        reporter.ReportDataUpdate(Event(0, 100, 5));
        Require(updates[^1].Stage == "finalize" && updates[^1].BytesPerSecond is null,
            "data-writer finalization never displays the last write rate");
        seconds = 2;
        reporter.ReportDataUpdate(Event(0, 200, 4));
        Require(updates[^1].BytesPerSecond is null, "writing after another phase begins measuring again");
        reporter.ReportDataUpdate(Event(0, 200, 2));
        Require(updates[^1].Stage == "media" && updates[^1].BytesPerSecond is null,
            "device preparation has no live throughput");
        reporter.ReportDataUpdate(new ExpandoObject());
        Require(updates[^1].BytesPerSecond is null, "unsupported event properties remain an unavailable measurement");
        reporter.ReportStage("buffer", "buffering");
        Require(updates[^1].Stage == "buffer" && updates[^1].BytesPerSecond is null,
            "buffering cannot inherit a device write speed");
    }

    private static object Event(int startLba, int lastWrittenLba, int? action = null, int lastReadLba = 0)
    {
        dynamic args = new ExpandoObject();
        args.StartLba = startLba;
        args.LastWrittenLba = lastWrittenLba;
        args.LastReadLba = lastReadLba;
        if (action is { } value) args.CurrentAction = value;
        return args;
    }

    private static void TestConsumerFailureIsIsolated()
    {
        int calls = 0;
        ImapiWriteProgressReporter reporter = new(OpticalBurnMediaKind.DvdData, 4096L * 2048, _ =>
        {
            calls++;
            throw new InvalidOperationException("A broken UI observer must not stop device writing.");
        });
        reporter.ReportDataUpdate(Event(0, 10, 4));
        reporter.ReportDataUpdate(Event(0, 10, 5));
        reporter.ReportRawCdUpdate(Event(0, 10, 2));
        reporter.ReportEngineUpdate(Event(0, 10));
        reporter.ReportStage("buffer", "buffering");
        Require(calls == 5, "each update isolates a throwing telemetry consumer and remains usable");
    }

    private static void EqualRate(double expected, OpticalBurnProgress actual, string message)
        => Require(actual.BytesPerSecond is { } rate && double.IsFinite(rate) && Math.Abs(rate - expected) < 0.0001, message);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Optical write progress: " + message);
    }
}
