using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class BluRayBurnWorkflowTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-bd-burn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            TestImageProducer(directory);
            TestCalibrationProducer();
            TestSelectionAndConfirmation();
            TestPreparationCancellation(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("blu-ray-burn-workflow: passed (ISO producers, exact media matching, confirmation invalidation; mocked recorder)");
    }

    private static void TestImageProducer(string directory)
    {
        string files = Path.Combine(directory, "files");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "readme.txt"), "Inner files are part of the same Blu-ray ISO stream.");
        BluRayDiscParameters disc = new(TotalSectors: 1024);
        BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(disc, files, "BD_BURN_TEST");
        BluRayGray8Raster raster = new(2, 2, new byte[] { 0, 255, 255, 0 });
        IBluRayPayloadMapping mapping = new BluRayImagePayloadMapping(disc, raster, new BluRayRunLengthMapping());
        OpticalWriteSpeed speed = new(4500, false);
        OpticalBurnRequest request = CreateImageRequest(plan, mapping, speed);
        request.Validate();
        Require(request.MediaKind == OpticalBurnMediaKind.BluRayData && request.ContentLength == 1024L * 2048,
            "image burn request declares the full manual capacity in 2048-byte sectors");
        Require(request.DeviceId == "memory-only-bd-device" && request.WriteSpeed == speed,
            "request retains selected recorder identity and speed");
        byte[] produced = Produce(request);
        Require(produced.LongLength == request.ContentLength && produced.Length % 2048 == 0,
            "forward-only producer emits exactly the declared sector stream");
        Require(System.Text.Encoding.ASCII.GetString(produced, 17 * 2048 + 1, 5) == "NSR03",
            "producer includes a readable ISO volume descriptor before drawing data");
        Require(plan.FileCount == 1 && plan.DrawingStartSector > 16 && plan.DrawingStartSector < disc.TotalSectors,
            "inner files reserve a prefix inside the requested capacity");

        string outputPath = Path.Combine(directory, "drawing.iso");
        BluRayIsoGenerator.GenerateFile(outputPath, plan, mapping);
        Require(File.ReadAllBytes(outputPath).AsSpan().SequenceEqual(produced),
            "ISO file output and streaming burn producer contain identical complete data");
        Require(Produce(request).AsSpan().SequenceEqual(produced), "prepared image producer is repeatable");
        TestCancellation(request);
    }

    private static void TestCalibrationProducer()
    {
        BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(new(TotalSectors: 1024));
        CalibrationParameters parameters = new(CalibrationDiscKind.BluRay, 24, 58, 1024,
            BluRayDiscParameters.ChannelBitLengthNm, DrawingStartSector: plan.DrawingStartSector, DrawingEndSectorExclusive: plan.DrawingEndSectorExclusive);
        CalibrationTarget target = CalibrationTarget.Create(parameters);
        OpticalBurnRequest request = CreateCalibrationRequest(target);
        request.Validate();
        Require(request.MediaKind == OpticalBurnMediaKind.BluRayData && request.ContentLength == 1024L * 2048,
            "saved Blu-ray calibration selects BluRayData and its own sector count");
        CalibrationTarget reloaded = CalibrationSessionJson.Deserialize(
            CalibrationSessionJson.Serialize(new(target, []))).Target;
        byte[] expected = Produce(request);
        int drawingOffset = checked((int)plan.DrawingStartSector * 2048);
        Require(Produce(CreateCalibrationRequest(reloaded)).AsSpan(drawingOffset).SequenceEqual(expected.AsSpan(drawingOffset)),
            "restored calibration target reproduces original drawing bytes independently of ISO timestamps");
        Require(System.Text.Encoding.ASCII.GetString(expected, 17 * 2048 + 1, 5) == "NSR03",
            "calibration burn output includes the ISO filesystem");
        TestCancellation(request);
    }

    private static void TestSelectionAndConfirmation()
    {
        // Avoid application startup and all physical recorder APIs. The injected
        // burner answers speed queries immediately and rejects any real burn call.
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        DiscParametersState state = new();
        BluRayViewModel bluRay = new(shell);
        SetAutoProperty(shell, nameof(ShellViewModel.BluRay), bluRay);
        CalibrationViewModel calibration = new(shell, state);
        SetAutoProperty(shell, nameof(ShellViewModel.Calibration), calibration);
        RecordingBurner burner = new();
        BurnViewModel model = new(shell, state, burner);
        SetAutoProperty(shell, nameof(ShellViewModel.Burn), model);
        model.UpdateSourceSummary();
        model.SelectedDevice = new("mock-device", "Mock recorder", []);
        model.IsDvdBurn = true;
        Require(model.SelectedMediaKind == OpticalBurnMediaKind.DvdData && !model.IsBluRayBurn,
            "legacy DVD selector selects only DVD");
        model.ConfirmChecked = true;
        model.IsBluRayBurn = true;
        Require(model.SelectedMediaKind == OpticalBurnMediaKind.BluRayData && !model.IsDvdBurn
            && !model.ConfirmChecked && burner.RequestedKinds.Last() == OpticalBurnMediaKind.BluRayData,
            "Blu-ray selection replaces DVD, clears confirmation and queries Blu-ray speeds");
        Require(model.Speeds[1].DisplayName.StartsWith("2×", StringComparison.Ordinal),
            "Blu-ray speed labels use Blu-ray 1x units");
        model.ConfirmChecked = true;
        model.SelectedSpeed = model.Speeds[1];
        Require(!model.ConfirmChecked, "speed changes invalidate previous confirmation");

        Action[] changes =
        [
            () => bluRay.TotalSectors = "1024",
            () => bluRay.InnerRadius = "24.1",
            () => bluRay.OuterRadius = "57.9",
            () => bluRay.InvertMapping = !bluRay.InvertMapping,
            () => bluRay.ImagePath = "missing-bd-image.png",
            () => bluRay.DataDirectory = "changed-inner-directory",
            () => bluRay.VolumeLabel = "CHANGED_BD",
        ];
        foreach (Action change in changes)
        {
            model.ConfirmChecked = true;
            change();
            Require(!model.ConfirmChecked, "each Blu-ray source input invalidates confirmation automatically");
        }

        model.UseCalibrationPattern = true;
        CalibrationDiscKind[] kinds = [CalibrationDiscKind.Cd, CalibrationDiscKind.Dvd, CalibrationDiscKind.BluRay];
        foreach (CalibrationDiscKind kind in kinds)
        {
            CalibrationTarget target = CalibrationTarget.Create(new(kind, 24, 58, 1024,
                kind == CalibrationDiscKind.Cd ? 1200 : kind == CalibrationDiscKind.Dvd ? 133.3 : 74.5));
            typeof(CalibrationViewModel).GetField("_target", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(calibration, target);
            model.OnCalibrationTargetChanged();
            for (int index = 0; index < 3; index++)
            {
                model.SelectedMediaIndex = index;
                model.ConfirmChecked = true;
                Require(model.StartBurnCommand.CanExecute(null) == (index == Array.IndexOf(kinds, kind)),
                    $"{kind} calibration enables only its exact media kind, never a CD/DVD boolean fallback");
            }
        }
        Require(burner.BurnCalls == 0, "workflow checks never call a physical burn API");
    }

    private static void TestPreparationCancellation(string directory)
    {
        // A 2-by-2 PNG and a 2 MiB declared disc exercise the real preparation
        // path. Every scenario stops before BurnAsync and emits no ISO file.
        string source = Path.Combine(directory, "cancel-source.png");
        BitmapSource bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray8, null,
            new byte[] { 0, 255, 255, 0 }, 2);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream image = File.Create(source)) encoder.Save(image);

        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        typeof(ShellViewModel).GetField("_log", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(shell, new StringBuilder());
        DiscParametersState state = new();
        BluRayViewModel bluRay = new(shell) { TotalSectors = "1024", ImagePath = source };
        SetAutoProperty(shell, nameof(ShellViewModel.BluRay), bluRay);
        SetAutoProperty(shell, nameof(ShellViewModel.Calibration), new CalibrationViewModel(shell, state));
        RecordingBurner burner = new();
        BurnViewModel model = new(shell, state, burner);
        SetAutoProperty(shell, nameof(ShellViewModel.Burn), model);
        model.IsBluRayBurn = true;
        model.SelectedDevice = new("mock-device", "Mock recorder", []);

        int confirmationCalls = 0;
        SetAutoProperty(shell, "ConfirmHandler", (Func<string, string, string, Task<bool>>)((_, _, _) =>
        {
            confirmationCalls++;
            return Task.FromResult(true);
        }));
        System.ComponentModel.PropertyChangedEventHandler cancelImmediately = (_, args) =>
        {
            if (args.PropertyName == nameof(model.IsSetupEnabled) && !model.IsSetupEnabled)
            {
                Require(model.CancelBurnCommand.CanExecute(null), "cancel is enabled as soon as preparation begins");
                model.CancelBurnCommand.Execute(null);
            }
        };
        model.PropertyChanged += cancelImmediately;
        model.ConfirmChecked = true;
        model.StartBurnCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        model.PropertyChanged -= cancelImmediately;
        Require(confirmationCalls == 0 && burner.BurnCalls == 0 && model.IsSetupEnabled
            && !model.CancelBurnCommand.CanExecute(null) && !model.ConfirmChecked,
            "cancelling preparation skips the confirmation modal and restores setup without writing");

        SetAutoProperty(shell, "ConfirmHandler", (Func<string, string, string, Task<bool>>)((_, _, _) =>
        {
            Require(model.CancelBurnCommand.CanExecute(null), "preparation cancellation remains active during confirmation");
            model.CancelBurnCommand.Execute(null);
            return Task.FromResult(true);
        }));
        model.ConfirmChecked = true;
        model.StartBurnCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(burner.BurnCalls == 0 && model.BurnStatusText.Contains("尚未开始写入", StringComparison.Ordinal)
            && !model.ConfirmChecked && model.IsSetupEnabled,
            "a cancellation during confirmation cannot be bypassed by an affirmative modal result");

        SetAutoProperty(shell, "ConfirmHandler", (Func<string, string, string, Task<bool>>)((_, _, _) =>
        {
            bluRay.VolumeLabel = "CHANGED_DURING_CONFIRM";
            return Task.FromResult(true);
        }));
        model.ConfirmChecked = true;
        model.StartBurnCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(burner.BurnCalls == 0 && model.BurnStatusText.Contains("来源或选项已变化", StringComparison.Ordinal),
            "source edits while the confirmation modal is open prevent physical writing");
    }

    private static OpticalBurnRequest CreateImageRequest(BluRayIsoPlan plan, IBluRayPayloadMapping mapping,
        OpticalWriteSpeed speed)
        => (OpticalBurnRequest)typeof(BurnViewModel).GetMethod("CreateBluRayBurnRequest",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                [plan, mapping, "memory-only-bd-device", speed])!;

    private static OpticalBurnRequest CreateCalibrationRequest(CalibrationTarget target)
        => (OpticalBurnRequest)typeof(BurnViewModel).GetMethod("CreateCalibrationBurnRequest",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                [target, false, "memory-only-bd-device", null])!;

    private static byte[] Produce(OpticalBurnRequest request)
    {
        using MemoryStream backing = new();
        using (ForwardOnlyWriteStream output = new(backing)) request.ProduceContent(output, CancellationToken.None);
        return backing.ToArray();
    }

    private static void TestCancellation(OpticalBurnRequest request)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using MemoryStream output = new();
        try
        {
            request.ProduceContent(output, cancellation.Token);
            throw new InvalidOperationException("Blu-ray producer ignored cancellation.");
        }
        catch (OperationCanceledException)
        {
            Require(output.Length == 0, "pre-cancelled burn producer writes no data");
        }
    }

    private static void SetAutoProperty(object target, string property, object value)
        => target.GetType().GetField($"<{property}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blu-ray burn workflow: " + message);
    }

    private sealed class RecordingBurner : IOpticalDiscBurner
    {
        public List<OpticalBurnMediaKind> RequestedKinds { get; } = [];
        public int BurnCalls { get; private set; }

        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([]);

        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
        {
            RequestedKinds.Add(mediaKind);
            return Task.FromResult<IReadOnlyList<OpticalWriteSpeed>>([new(4395, false)]);
        }

        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            BurnCalls++;
            throw new InvalidOperationException("These tests must not start physical writing.");
        }
    }
}
