using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class BluRayCalibrationTests
{
    internal static void Run()
    {
        TestGeometry();
        TestReferenceRasterParity();
        string directory = Path.Combine(Path.GetTempPath(), $"bd-calibration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { TestCapturedWorkflow(directory); TestLegacyUdfSessions(directory); }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("bluray-calibration: full-zone raw creation, saved polarity, file/stream parity, legacy UDF session replay, geometry and UI routing passed");
    }

    private static void TestGeometry()
    {
        Require(CalibrationParameters.BluRayChannelBitsPerRecordingUnit == BluRayDiscParameters.ChannelBitsPerRecordingUnit,
            "calibration includes Blu-ray recording-unit run-in/out bits");
        foreach (uint sectors in new uint[] { 64, 128, 1_048_576, 12_219_392 })
        {
            var disc = new BluRayDiscParameters(sectors);
            var parameters = new CalibrationParameters(CalibrationDiscKind.BluRay, 24, 58, sectors, 74.5);
            var target = CalibrationTarget.Create(parameters);
            var actual = parameters with { InnerRadiusMm = 24.0017, OuterRadiusMm = 57.9989 };
            var generatedGeometry = new BluRayTrackGeometry(disc);
            var actualGeometry = new BluRayTrackGeometry(disc with
                { InnerRadiusMm = actual.InnerRadiusMm, OuterRadiusMm = actual.OuterRadiusMm });
            Near(parameters.TrackLengthMm, generatedGeometry.TrackLengthPerLayerMm, 1e-8, "physical track length agrees");
            foreach (double progress in new[] { 0.0, 0.01, 0.37, 0.9, 1.0 })
            {
                var source = generatedGeometry.AtLayerFraction(0, progress);
                var destination = actualGeometry.AtLayerFraction(0, progress);
                var mapped = target.MapPoint(new(source.Xmm, -source.Ymm), actual);
                Near(destination.Xmm, mapped.X, 3e-6, "forward X matches independent Blu-ray encoder");
                Near(-destination.Ymm, mapped.Y, 3e-6, "forward Y is converted once to calibration coordinates");
            }
        }
        Throws<ArgumentException>(() => CalibrationTarget.Create(new(CalibrationDiscKind.BluRay, 24, 58, 96, 74.5,
            PitchLinear: 0.01)), "Blu-ray cannot accept DVD pitch coefficients");
        Throws<ArgumentException>(() => CalibrationTarget.Create(new(CalibrationDiscKind.BluRay, 24, 58, 97, 74.5)),
            "Blu-ray calibration retains full cluster alignment");
    }

    private static void TestCapturedWorkflow(string directory)
    {
        ShellViewModel shell = CreateShell();
        var bluray = new BluRayViewModel(shell)
        {
            TotalSectors = "1024", InnerRadius = "24.25", OuterRadius = "57.75", InvertMapping = true,
            ImagePath = "unused-image.png", DataDirectory = "unused-inner-files-must-not-be-read",
        };
        Set(shell, "<BluRay>k__BackingField", bluray);
        var model = new CalibrationViewModel(shell, new()) { SelectedDiscType = "BD" };
        Set(shell, "<Calibration>k__BackingField", model);
        model.CreateTargetCommand.Execute(null);
        CalibrationTarget target = model.Target ?? throw new InvalidOperationException(model.StatusText);
        Require(target.Parameters.Kind == CalibrationDiscKind.BluRay && target.Parameters.Sectors == 1024
            && target.Parameters.BluRayInvertMapping
            && !model.IsDvdTarget && !model.FitDvdPitch,
            "BD creation captures the custom capacity and polarity without DVD controls");
        var disc = new BluRayDiscParameters(1024, InnerRadiusMm: 24.25, OuterRadiusMm: 57.75);
        BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc);
        Require(!plan.HasFileSystem && target.Parameters.DrawingStartSector == 0
            && target.Parameters.DrawingEndSectorExclusive == disc.TotalSectors
            && target.Parameters.BluRayIsoTimestamp is null,
            "new calibration captures raw full-zone drawing rather than the source page's file directory");
        Near(disc.InnerRadiusMm, target.DrawingInnerRadiusMm, 1e-10, "raw calibration starts at the input inner radius");
        Near(disc.OuterRadiusMm, target.DrawingOuterRadiusMm, 1e-10, "raw calibration ends at the input outer radius");
        Require(target.Groups.All(group => group.SpineInnerRadiusMm == target.DrawingInnerRadiusMm
            && group.SpineOuterRadiusMm == target.DrawingOuterRadiusMm),
            "new calibration controls cover both ends of the full data zone");

        string path = Path.Combine(directory, "calibration.iso");
        model.OutputPath = path;
        model.GenerateImageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(File.Exists(path), "BD calibration generates its full-zone image: " + model.StatusText);
        byte[] fromFile = File.ReadAllBytes(path);
        Require(fromFile.Length == disc.ContentLength, "raw output has the complete captured custom capacity");
        using (var serialReference = new MemoryStream())
        {
            // Preserve the exact procedural sampler, including its drawable
            // annulus. This independent sequential producer predates parallel opt-in.
            var serialMapping = new BluRayImagePayloadMapping(disc, (x, y) => target.Sample(x, -y),
                new BluRayRunLengthMapping(target.Parameters.BluRayInvertMapping));
            BluRayStreamingGenerator.GenerateToStream(serialReference, new(disc, 0, disc.TotalSectors), serialMapping);
            Require(serialReference.ToArray().AsSpan().SequenceEqual(fromFile),
                "raw calibration export including first/last sectors matches direct sequential streaming");
        }
        string sessionPath = Path.ChangeExtension(path, ".calibration.json");
        var restored = CalibrationSessionJson.Load(sessionPath);
        Require(restored.Target.Parameters == target.Parameters, "saved calibration preserves BD capacity, polarity and full-zone drawing bounds");

        bluray.TotalSectors = "2048"; bluray.InnerRadius = "25"; bluray.InvertMapping = false;
        var loadedModel = new CalibrationViewModel(shell, new());
        InvokeTask(loadedModel, "LoadSessionFromAsync", sessionPath);
        Require(loadedModel.SelectedDiscType == "BD" && loadedModel.Target?.Parameters == target.Parameters,
            "loading BD session is independent of current Blu-ray settings");

        var factory = typeof(BurnViewModel).GetMethod("CreateCalibrationBurnRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        var request = (OpticalBurnRequest)factory.Invoke(null,
            [restored.Target, true, "memory-only-bd-test", new OpticalWriteSpeed(300, false)])!;
        Require(request.MediaKind == OpticalBurnMediaKind.BluRayData && request.ContentLength == fromFile.Length,
            "calibration burn uses BD media and captured capacity");
        using var memory = new MemoryStream();
        using (var output = new ForwardOnlyWriteStream(memory)) request.ProduceContent(output, CancellationToken.None);
        Require(memory.ToArray().AsSpan().SequenceEqual(fromFile), "raw file and forward-only burn replay the same saved full-zone pattern");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var empty = new MemoryStream();
        Throws<OperationCanceledException>(() => request.ProduceContent(empty, cancelled.Token), "BD calibration burn cancellation");
        Require(empty.Length == 0, "cancelled producer emits no bytes");
    }

    private static void TestLegacyUdfSessions(string directory)
    {
        var disc = new BluRayDiscParameters(1024, InnerRadiusMm: 24.25, OuterRadiusMm: 57.75);
        DateTimeOffset timestamp = new(2025, 7, 18, 9, 10, 11, TimeSpan.FromHours(8));
        DateTimeOffset fallback = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (DateTimeOffset? savedTimestamp in new DateTimeOffset?[] { timestamp, null })
        {
            string name = savedTimestamp is null ? "legacy-no-timestamp" : "legacy-timestamp";
            DateTimeOffset effectiveTimestamp = savedTimestamp ?? fallback;
            BluRayIsoPlan originalPlan = BluRayIsoGenerator.Prepare(disc, timestamp: effectiveTimestamp);
            Require(originalPlan.HasFileSystem && originalPlan.DrawingStartSector > 0
                && originalPlan.DrawingEndSectorExclusive < disc.TotalSectors,
                "legacy Prepare keeps its UDF metadata prefix and backup tail");
            var originalTarget = CalibrationTarget.Create(new(CalibrationDiscKind.BluRay,
                disc.InnerRadiusMm, disc.OuterRadiusMm, disc.TotalSectors, BluRayDiscParameters.ChannelBitLengthNm,
                BluRayInvertMapping: true, DrawingStartSector: originalPlan.DrawingStartSector,
                BluRayIsoTimestamp: savedTimestamp, DrawingEndSectorExclusive: originalPlan.DrawingEndSectorExclusive));
            string sessionPath = Path.Combine(directory, name + ".calibration.json");
            CalibrationSessionJson.Save(sessionPath, new(originalTarget, []));

            ShellViewModel shell = CreateShell();
            Set(shell, "<BluRay>k__BackingField", new BluRayViewModel(shell)
            {
                TotalSectors = "2048", InnerRadius = "25", OuterRadius = "58", InvertMapping = false,
            });
            var model = new CalibrationViewModel(shell, new());
            Set(shell, "<Calibration>k__BackingField", model);
            InvokeTask(model, "LoadSessionFromAsync", sessionPath);
            CalibrationTarget loaded = model.Target ?? throw new InvalidOperationException(model.StatusText);
            Require(model.SelectedDiscType == "BD" && loaded.Id == originalTarget.Id
                && loaded.Parameters == originalTarget.Parameters,
                "legacy session loading retains its original bounds, polarity and optional timestamp");
            var planFactory = typeof(CalibrationViewModel).GetMethod("BluRayPlan", BindingFlags.NonPublic | BindingFlags.Static)!;
            var restoredPlan = (BluRayIsoPlan)planFactory.Invoke(null, [loaded])!;
            Require(restoredPlan.HasFileSystem && restoredPlan.DrawingStartSector == originalPlan.DrawingStartSector
                && restoredPlan.DrawingEndSectorExclusive == originalPlan.DrawingEndSectorExclusive,
                "saved UDF bounds select legacy replay even when no timestamp was saved");
            Near(originalPlan.DrawingStartRadiusMm, loaded.DrawingInnerRadiusMm, 1e-10,
                "legacy session keeps the inner metadata exclusion");
            Near(originalPlan.DrawingEndRadiusMm, loaded.DrawingOuterRadiusMm, 1e-10,
                "legacy session keeps the outer backup metadata exclusion");
            Require(loaded.Sample(0, -(disc.InnerRadiusMm + loaded.DrawingInnerRadiusMm) / 2) == 255
                && loaded.Sample(0, -(loaded.DrawingOuterRadiusMm + disc.OuterRadiusMm) / 2) == 255
                && loaded.Groups.All(group => group.SpineInnerRadiusMm == loaded.DrawingInnerRadiusMm
                    && group.SpineOuterRadiusMm == loaded.DrawingOuterRadiusMm),
                "legacy target sampling and controls remain clipped to the saved drawable annulus");

            string imagePath = Path.Combine(directory, name + ".iso");
            model.OutputPath = imagePath;
            model.GenerateImageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Require(File.Exists(imagePath), "legacy UDF export succeeds: " + model.StatusText);
            byte[] fromFile = File.ReadAllBytes(imagePath);
            Require(fromFile.LongLength == disc.ContentLength, "legacy UDF replay keeps complete disc length");
            using (var reference = new MemoryStream())
            {
                var mapping = new BluRayImagePayloadMapping(disc, (x, y) => originalTarget.Sample(x, -y),
                    new BluRayRunLengthMapping(originalTarget.Parameters.BluRayInvertMapping));
                BluRayIsoGenerator.GenerateToStream(reference, originalPlan, mapping);
                Require(reference.ToArray().AsSpan().SequenceEqual(fromFile),
                    "loaded legacy target exports the same drawing and filesystem bytes as its captured UDF plan");
            }
            Require(UdfFilesystemTests.Inspect(fromFile, disc.TotalSectors, "BD_IMAGE",
                originalPlan.DrawingStartSector, originalPlan.DrawingEndSectorExclusive).Count == 0,
                "legacy replay remains an independently readable UDF volume");
            CheckTimestamp(fromFile, effectiveTimestamp);
            var resaved = CalibrationSessionJson.Load(Path.ChangeExtension(imagePath, ".calibration.json"));
            Require(resaved.Target.Parameters == originalTarget.Parameters,
                "exporting a legacy session does not migrate its saved layout or optional timestamp");

            var requestFactory = typeof(BurnViewModel).GetMethod("CreateCalibrationBurnRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
            var request = (OpticalBurnRequest)requestFactory.Invoke(null,
                [resaved.Target, true, "memory-only-bd-test", new OpticalWriteSpeed(300, false)])!;
            using var memory = new MemoryStream();
            using (var output = new ForwardOnlyWriteStream(memory)) request.ProduceContent(output, CancellationToken.None);
            Require(request.MediaKind == OpticalBurnMediaKind.BluRayData && request.ContentLength == fromFile.LongLength
                && memory.ToArray().AsSpan().SequenceEqual(fromFile),
                "legacy UDF export and forward-only burn replay the same saved bytes without a recorder");
        }
    }

    private static void TestReferenceRasterParity()
    {
        using var source = new MemoryStream(CalibrationReferencePattern.ReadPngBytes());
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight];
        image.CopyPixels(pixels, image.PixelWidth, 0);
        var raster = new BluRayGray8Raster(image.PixelWidth, image.PixelHeight, pixels);
        var disc = new BluRayDiscParameters(1024);
        var plan = BluRayIsoGenerator.Prepare(disc);
        var factory = typeof(CalibrationViewModel).GetMethod("BluRayPatternMapping", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (bool invert in new[] { false, true })
        {
            var target = CalibrationTarget.Create(new(CalibrationDiscKind.BluRay, 24, 58, 1024, 74.5,
                BluRayInvertMapping: invert, DrawingStartSector: plan.DrawingStartSector, DrawingEndSectorExclusive: plan.DrawingEndSectorExclusive));
            var ordinary = new BluRayImagePayloadMapping(disc, raster, new BluRayRunLengthMapping(invert));
            var procedural = (IBluRayPayloadMapping)factory.Invoke(null, [target])!;
            byte[] expected = new byte[2048], actual = new byte[2048];
            for (uint sector = plan.DrawingStartSector; sector < plan.DrawingEndSectorExclusive; sector++)
            {
                var context = new BluRaySectorContext(sector, disc.GetPhysicalSectorNumber(sector), disc);
                ordinary.FillScrambledPayload(context, expected);
                procedural.FillScrambledPayload(context, actual);
                Require(expected.AsSpan().SequenceEqual(actual), "reference PNG and procedural calibration match every payload byte");
            }
        }
    }

    private static void CheckTimestamp(byte[] image, DateTimeOffset timestamp)
    {
        DateTime utc = timestamp.UtcDateTime;
        int offset = 32 * 2048 + 376;
        Require(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 2)) == utc.Year
            && image[offset + 4] == utc.Month && image[offset + 5] == utc.Day
            && image[offset + 6] == utc.Hour && image[offset + 7] == utc.Minute && image[offset + 8] == utc.Second,
            "UDF creation date is taken from the saved target, not the latest export time");
    }

    private static ShellViewModel CreateShell()
    {
        var shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        Set(shell, "_log", new StringBuilder()); Set(shell, "_dispatcher", Dispatcher.CurrentDispatcher);
        Set(shell, "<Toasts>k__BackingField", new ObservableCollection<ToastItem>());
        foreach (string name in new[] { "OpenOutputCommand", "BrowseCdImageCommand", "BrowseDvdImageCommand", "BrowseCdTrackCommand", "BrowseDvdIsoCommand" })
            Set(shell, $"<{name}>k__BackingField", new RelayCommand(() => { }));
        return shell;
    }

    private static void InvokeTask(object instance, string method, params object[] args)
        => ((Task)instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args)!).GetAwaiter().GetResult();
    private static void Set(object instance, string name, object value)
        => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void Near(double expected, double actual, double tolerance, string message)
        => Require(double.IsFinite(actual) && Math.Abs(expected - actual) < tolerance, $"{message}: {expected:G17} != {actual:G17}");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blu-ray calibration: " + message);
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Blu-ray calibration: " + message);
    }
}
