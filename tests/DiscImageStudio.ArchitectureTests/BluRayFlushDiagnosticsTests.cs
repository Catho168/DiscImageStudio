using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

internal static class BluRayFlushDiagnosticsTests
{
    internal static void Run()
    {
        TestWriteAndFlushFailures();
        TestCancellationAndFlushFailures();
        TestDiagnosticCallbackIsolation();
        TestSuccessfulFlushIsUnchanged();
        Console.WriteLine("blu-ray-flush-diagnostics: passed (secondary HRESULT/elapsed, original write error, unchanged cleanup calls)");
    }

    private static void TestCancellationAndFlushFailures()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException original = new("Cancelled before the next WRITE.", cancellation.Token);
        using FakeSession session = new()
        {
            WriteFailure = original,
            FlushFailure = new COMException("Disconnected during cancellation cleanup.", unchecked((int)0x8007048f)),
        };
        List<string> warnings = [];
        Exception actual = RunBurn(session, warnings.Add) ?? throw new InvalidOperationException("Expected cancellation.");
        Require(ReferenceEquals(actual, original) && warnings.Count == 1,
            "a cancelled write preserves its primary exception while recording a failed flush");
        int releases = 0;
        try
        {
            WindowsImapiBurner.CompleteBurnCleanup(actual, warnings, () => releases++, () => releases++);
            throw new InvalidOperationException("Expected cancellation with cleanup diagnostics.");
        }
        catch (OperationCanceledException combined)
        {
            Require(combined.CancellationToken == cancellation.Token && ReferenceEquals(combined.InnerException, original)
                && combined.Message.Contains("0x8007048F", StringComparison.Ordinal) && releases == 2,
                "cleanup diagnostics retain cancellation type, original token/cause and both COM releases");
        }
    }

    private static void TestWriteAndFlushFailures()
    {
        using FakeSession session = new()
        {
            WriteFailure = new IOException("Original WRITE failure."),
            FlushFailure = new COMException("Simulated device no longer connected.", unchecked((int)0x8007048f)),
        };
        List<string> warnings = [];
        Exception actual = RunBurn(session, warnings.Add) ?? throw new InvalidOperationException("Expected WRITE failure.");
        Require(ReferenceEquals(actual, session.WriteFailure), "failed cleanup retains the original write exception");
        Require(warnings.Count == 1 && warnings[0].Contains("0x8007048F", StringComparison.Ordinal)
            && warnings[0].Contains("耗时", StringComparison.Ordinal)
            && warnings[0].Contains(session.FlushFailure!.Message, StringComparison.Ordinal),
            "flush diagnostic includes its HRESULT, elapsed time and message");
        Require(session.Operations.SequenceEqual(new[] { "acquire", "read", "configure", "write", "flush", "release" }),
            "failure attempts one existing flush and release without retrying WRITE or closing the disc");
        try
        {
            WindowsImapiBurner.CompleteBurnCleanup(actual, warnings, () => { }, () => { });
            throw new InvalidOperationException("Expected combined burn failure.");
        }
        catch (InvalidOperationException combined) when (ReferenceEquals(combined.InnerException, actual))
        {
            Require(combined.Message.StartsWith(actual.Message, StringComparison.Ordinal)
                && combined.Message.Contains("0x8007048F", StringComparison.Ordinal),
                "outer cleanup exposes the secondary error while retaining WRITE as its inner cause");
        }
    }

    private static void TestDiagnosticCallbackIsolation()
    {
        using FakeSession session = new()
        {
            WriteFailure = new IOException("Original write failure."),
            FlushFailure = new IOException("Secondary flush failure."),
        };
        Exception actual = RunBurn(session, _ => throw new InvalidOperationException("Observer failure."))
            ?? throw new InvalidOperationException("Expected WRITE failure.");
        Require(ReferenceEquals(actual, session.WriteFailure) && session.Operations[^1] == "release",
            "diagnostic observers cannot replace the write failure or prevent release");
    }

    private static void TestSuccessfulFlushIsUnchanged()
    {
        using FakeSession failedWrite = new() { WriteFailure = new IOException("Write failed before cleanup.") };
        List<string> warnings = [];
        Exception original = RunBurn(failedWrite, warnings.Add) ?? throw new InvalidOperationException("Expected WRITE failure.");
        Require(ReferenceEquals(original, failedWrite.WriteFailure) && warnings.Count == 0
            && failedWrite.Operations.Count(operation => operation == "flush") == 1,
            "successful failure-path flush adds no warning or extra command");
        using FakeSession successful = new();
        RunBurn(successful, warnings.Add, expectFailure: false);
        Require(warnings.Count == 0 && successful.Operations.SequenceEqual(new[]
            { "acquire", "read", "configure", "write", "flush", "close-track", "close-disc", "release" }),
            "successful burn preserves one flush and the original close order");
    }

    private static Exception? RunBurn(FakeSession session, Action<string> warning, bool expectFailure = true)
    {
        OpticalBurnBufferOptions buffers = new(256 * 1024, 256 * 1024);
        OpticalBurnRequest request = new("fake-device", OpticalBurnMediaKind.BluRayData, 32 * 2048,
            (output, token) => { token.ThrowIfCancellationRequested(); output.Write(new byte[32 * 2048]); },
            BufferOptions: buffers);
        using GeneratedContentComStream stream = new(request.ContentLength, request.ProduceContent, null,
            CancellationToken.None, buffers);
        stream.WaitUntilPrebuffered(CancellationToken.None);
        try
        {
            WindowsImapiBurner.BurnBluRay(session, stream, request, CancellationToken.None,
                reportCleanupWarning: warning);
        }
        catch (Exception exception) when (expectFailure) { return exception; }
        if (expectFailure) throw new InvalidOperationException("Expected simulated write failure.");
        return null;
    }

    private sealed class FakeSession : IBluRaySequentialSession
    {
        public List<string> Operations { get; } = [];
        public Exception? WriteFailure { get; init; }
        public Exception? FlushFailure { get; init; }
        public void Acquire() => Operations.Add("acquire");
        public BluRaySequentialMediaInfo ReadMedia()
        {
            Operations.Add("read");
            return new(0x41, true, 1, true, true, 0, 0, 12_219_392, 12_219_392, 12_219_392, 1);
        }
        public OpticalWriteSpeed ConfigureWrite(OpticalWriteSpeed? requested)
        {
            Operations.Add("configure");
            return new(4500, false);
        }
        public void Write(IStream content, uint sectorCount)
        {
            Operations.Add("write");
            if (WriteFailure is not null) throw WriteFailure;
        }
        public void CancelWrite() => Operations.Add("cancel");
        public void Flush()
        {
            Operations.Add("flush");
            if (FlushFailure is not null) throw FlushFailure;
        }
        public void CloseTrack(int trackNumber) => Operations.Add("close-track");
        public void CloseDisc() => Operations.Add("close-disc");
        public void Release() => Operations.Add("release");
        public void Dispose() { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("BD flush diagnostics: " + message);
    }
}
