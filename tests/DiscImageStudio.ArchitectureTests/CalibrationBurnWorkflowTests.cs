using System.Globalization;
using System.IO;
using System.Reflection;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.ViewModels;

internal static class CalibrationBurnWorkflowTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-calibration-burn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (bool interleave in new[] { false, true })
            {
                TestCapturedRequest(directory, CalibrationDiscKind.Cd, interleave);
            }

            TestCapturedRequest(directory, CalibrationDiscKind.Dvd, false);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                File.Delete(file);
            }

            Directory.Delete(directory);
        }

        Console.WriteLine("calibration-burn-workflow: passed (memory-only requests, saved snapshots, reload isolation)");
    }

    private static void TestCapturedRequest(string directory, CalibrationDiscKind kind, bool interleave)
    {
        // Constructing the shared parameter store does not enumerate or access a
        // recorder. This test never creates a burner or calls BurnAsync.
        DiscParametersState state = new()
        {
            CdInnerRadius = "24.5", CdOuterRadius = "56.8", CdSectors = "8",
            CdInterleave = interleave, CdImagePath = "unused-image-must-not-be-read.png",
            DvdInnerRadius = "24.5", DvdOuterRadius = "56.8", DvdTotalSectors = "16",
            DvdImagePath = "unused-dvd-image-must-not-be-read.png",
            DvdDataDirectory = "unused-folder-must-not-be-read",
        };
        CalibrationParameters generated = ReadParameters(state, kind);
        CalibrationTarget target = CalibrationTarget.Create(generated);
        Require(target.SchemaVersion == 6 && target.ControlCells.Count == 4 && target.ControlPoints.Count == 8,
            "the burn request captures the current concentric-ring and cross geometry");
        CalibrationSession original = new(target, [], CdInterleave: state.CdInterleave);
        string path = Path.Combine(directory, $"{kind}-{interleave}.calibration.json");
        CalibrationSessionJson.Save(path, original);
        string originalJson = File.ReadAllText(path);
        CalibrationSession activeSession = CalibrationSessionJson.Load(path);
        Require(activeSession.Target.Parameters == generated && activeSession.Target.Id == target.Id,
            "durable session restores original generation parameters and target identity");
        Require(activeSession.Target.SchemaVersion == 6 && activeSession.Target.Cells.SequenceEqual(target.Cells),
            "durable records retain the exact cross target geometry");

        OpticalWriteSpeed speed = new(300, false);
        OpticalBurnRequest request = CreateRequest(activeSession.Target, activeSession.CdInterleave, speed);
        request.Validate();
        Require(request.DeviceId == "memory-only-test-device" && request.WriteSpeed == speed,
            "request preserves selected device and speed without accessing hardware");
        Require(request.MediaKind == (kind == CalibrationDiscKind.Cd
                ? OpticalBurnMediaKind.CdAudio : OpticalBurnMediaKind.DvdData),
            "request media type comes from the saved target");

        byte[] expected = GenerateDirect(target, interleave);
        Require(request.ContentLength == expected.Length, "request declares the complete generated sector length");
        Require(expected.Distinct().Count() > 1, "real whole-disc polar target produces nonconstant encoded content");

        // Main-page edits and opening a different saved calibration must not change
        // a request whose producer has already captured the original target.
        state.CdInnerRadius = "25.2";
        state.CdOuterRadius = "57.3";
        state.CdSectors = "32";
        state.CdInterleave = !interleave;
        state.DvdInnerRadius = "25.2";
        state.DvdOuterRadius = "57.3";
        state.DvdTotalSectors = "32";
        CalibrationTarget replacement = CalibrationTarget.Create(ReadParameters(state, kind));
        CalibrationSessionJson.Save(path, new(replacement, [], CdInterleave: state.CdInterleave));
        activeSession = CalibrationSessionJson.Load(path);
        Require(activeSession.Target.Parameters != generated && activeSession.Target.Id != target.Id,
            "test switches to a materially different saved target");

        byte[] captured = Produce(request);
        Require(captured.AsSpan().SequenceEqual(expected), "captured producer matches direct analytic encoding after state edits and session replacement");
        Require(Produce(request).AsSpan().SequenceEqual(captured), "captured producer is repeatable");
        Require(target.Parameters == generated, "original target parameters remain immutable");

        CalibrationSession reloadedOriginal = CalibrationSessionJson.Deserialize(originalJson);
        Require(reloadedOriginal.Target.SchemaVersion == 6 && reloadedOriginal.Target.Cells.SequenceEqual(target.Cells),
            "opening the saved snapshot does not reinterpret its engraving geometry");
        OpticalBurnRequest restoredRequest = CreateRequest(reloadedOriginal.Target, reloadedOriginal.CdInterleave, speed);
        Require(Produce(restoredRequest).AsSpan().SequenceEqual(expected),
            "reloading the original JSON reproduces the same CD/DVD payload despite current main-page values");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using MemoryStream cancelledOutput = new();
        try
        {
            request.ProduceContent(cancelledOutput, cancellation.Token);
            throw new InvalidOperationException("Calibration burn producer ignored cancellation.");
        }
        catch (OperationCanceledException)
        {
            Require(cancelledOutput.Length == 0, "pre-cancelled request produces no payload");
        }
    }

    private static OpticalBurnRequest CreateRequest(CalibrationTarget target, bool interleave, OpticalWriteSpeed speed)
    {
        MethodInfo factory = typeof(BurnViewModel).GetMethod("CreateCalibrationBurnRequest",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Calibration burn request factory was not found.");
        return (OpticalBurnRequest)(factory.Invoke(null, [target, interleave, "memory-only-test-device", speed])
            ?? throw new InvalidOperationException("Calibration burn request factory returned no request."));
    }

    private static CalibrationParameters ReadParameters(DiscParametersState state, CalibrationDiscKind kind)
        => kind == CalibrationDiscKind.Cd
            ? new(kind, Number(state.CdInnerRadius), Number(state.CdOuterRadius),
                long.Parse(state.CdSectors, CultureInfo.InvariantCulture), CdDiscParameters.StandardLinearVelocityMmPerSecond)
            : new(kind, Number(state.DvdInnerRadius), Number(state.DvdOuterRadius),
                long.Parse(state.DvdTotalSectors, CultureInfo.InvariantCulture), DvdStreamingOptions.StandardChannelBitLengthNm);

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static byte[] GenerateDirect(CalibrationTarget target, bool interleave)
    {
        CalibrationParameters p = target.Parameters;
        using MemoryStream output = new();
        if (p.Kind == CalibrationDiscKind.Cd)
        {
            CdDiscParameters cd = new(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.LinearDensity,
                StartAngleRadians: 0, ImageOuterRadiusMm: p.OuterRadiusMm);
            CdTrackGenerator.GeneratePatternToStream(target.Sample, output, cd, interleave,
                audioByteOrder: CdAudioByteOrder.LittleEndian);
        }
        else
        {
            DvdStreamingOptions dvd = new(checked((uint)p.Sectors), p.InnerRadiusMm, p.OuterRadiusMm,
                p.LinearDensity, StartAngleDegrees: 0);
            DvdStreamingGenerator.GeneratePattern(target.Sample, output, dvd);
        }

        return output.ToArray();
    }

    private static byte[] Produce(OpticalBurnRequest request)
    {
        using MemoryStream backing = new();
        using (ForwardOnlyWriteStream output = new(backing))
        {
            request.ProduceContent(output, CancellationToken.None);
        }

        return backing.ToArray();
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Calibration burn workflow: {name}.");
        }
    }
}
