using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.ViewModels;

internal static class ParameterNumberFormattingTests
{
    private static readonly Type Formatter = typeof(ShellViewModel).Assembly
        .GetType("DiscImageStudio.Services.ParameterNumberText", throwOnError: true)!;

    internal static void Run()
    {
        CultureInfo savedCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Require(Input(56.8) == "56.8" && Input(0.1) == "0.1" && Input(133.3) == "133.3",
                "editable ordinary decimals do not expose G17 binary tails or locale commas");
            foreach (double value in new[]
            {
                23.991460627731737, 57.964893657925366, -3.9256854319227684E-05,
                6.964795915429256E-05, -4.670918950137523E-05,
                Math.BitIncrement(24d), Math.BitDecrement(58d), double.Epsilon,
                1e-20, 0d, -0d,
            })
                RequireExact(value, Input(value), "editable precision survives formatting");

            Require(Summary(.0001333 * 1_000_000) == "133.3",
                "unit-conversion noise is rounded only in a read-only summary");
            Require(Summary(23.991460627731737) == "23.991461",
                "summary uses a bounded readable decimal precision");
            Require(Summary(-0d) == "0", "read-only summaries normalize negative zero");
            Require(double.Parse(Summary(1e-12, 10), CultureInfo.InvariantCulture) != 0,
                "a small nonzero pitch term is not falsely displayed as zero");

            TestPresetPrecision();
            TestAppliedCalibration(CalibrationDiscKind.Cd);
            TestAppliedCalibration(CalibrationDiscKind.Dvd);
            TestBurnCalibrationSummary();
            TestBluRayFullDataZoneSummaries();
        }
        finally { CultureInfo.CurrentCulture = savedCulture; }
        Console.WriteLine("parameter-number-formatting: passed (shortest editable decimals, bit-exact fitted values, compact unit conversions, tiny coefficients, culture independence, preset/calibration application and burn source summary)");
    }

    private static void TestBurnCalibrationSummary()
    {
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        DiscParametersState state = new();
        CalibrationViewModel calibration = new(shell, state);
        Set(shell, "<Calibration>k__BackingField", calibration);
        RejectingBurner burner = new();
        BurnViewModel burn = new(shell, state, burner);
        // Set only the selection state so this test calls the actual summary
        // operation without triggering the separate speed-refresh command.
        Set(burn, "_useCalibrationPattern", true);
        Set(burn, "_selectedMediaIndex", 1);
        CalibrationParameters parameters = new(CalibrationDiscKind.Dvd, 24, 56.8,
            2_297_888, .0001333 * 1_000_000);
        CalibrationTarget target = CalibrationTarget.Create(parameters);
        Set(calibration, "_target", target);
        burn.UpdateSourceSummary();
        Require(burn.SourceDetails.Contains("24–56.8 mm", StringComparison.Ordinal)
            && burn.SourceDetails.Contains("位长 133.3 nm", StringComparison.Ordinal),
            "actual burn calibration summary removes ordinary binary/unit-conversion tails under fr-FR");
        Require(ReferenceEquals(target.Parameters, parameters),
            "formatting the burn summary does not replace its exact saved parameters");

        // A synthetic positive sub-micron inner radius exercises the summary's
        // small-value fallback; normal disc radii do not reach this branch.
        parameters = parameters with { InnerRadiusMm = 1e-12 };
        target = CalibrationTarget.Create(parameters);
        Set(calibration, "_target", target);
        burn.UpdateSourceSummary();
        Require(burn.SourceDetails.Contains("1E-12–56.8 mm", StringComparison.Ordinal),
            "actual burn calibration summary keeps a small nonzero value visible");
        RequireExact(1e-12, Input(target.Parameters.InnerRadiusMm),
            "burn summary leaves the original small value unchanged");
        Require(burner.Calls == 0, "source summaries never enumerate, query, or write to an optical device");
    }

    private static void TestBluRayFullDataZoneSummaries()
    {
        const string innerText = "23.9885", outerText = "58.022875";
        const double inner = 23.9885, outer = 58.022875;
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        Set(shell, "_log", new StringBuilder());
        Set(shell, "_dispatcher", Dispatcher.CurrentDispatcher);
        Set(shell, "<Toasts>k__BackingField", new ObservableCollection<ToastItem>());
        DiscParametersState state = new();
        Set(shell, "<State>k__BackingField", state);
        BluRayViewModel bluRay = new(shell) { InnerRadius = innerText, OuterRadius = outerText };
        Set(shell, "<BluRay>k__BackingField", bluRay);
        CalibrationViewModel calibration = new(shell, state) { SelectedDiscType = "BD" };
        Set(shell, "<Calibration>k__BackingField", calibration);
        RejectingBurner recorder = new();
        BurnViewModel burn = new(shell, state, recorder);
        Set(burn, "_useCalibrationPattern", true);
        Set(burn, "_selectedMediaIndex", 2);
        Set(shell, "<Burn>k__BackingField", burn);

        RequireContainsInputRadii(bluRay.GeometryStatus, "Blu-ray generation summary", innerText, outerText);
        var before = bluRay.ReadParameters();
        RequireExact(inner, Input(before.InnerRadiusMm), "Blu-ray input inner radius");
        RequireExact(outer, Input(before.OuterRadiusMm), "Blu-ray input outer radius");

        // Exercise real BD full-zone layout preparation and target installation. This
        // creates only the small preview, never an ISO or an optical operation.
        calibration.CreateTargetCommand.Execute(null);
        CalibrationTarget target = calibration.Target
            ?? throw new InvalidOperationException("BD target creation failed: " + calibration.StatusText);
        burn.UpdateSourceSummary();
        Require(target.Parameters.Kind == CalibrationDiscKind.BluRay
            && target.Parameters.DrawingStartSector == 0
            && target.Parameters.DrawingEndSectorExclusive == target.Parameters.Sectors,
            "new Blu-ray target draws the complete data zone when no files are attached");
        Require(target.DrawingInnerRadiusMm == inner && target.DrawingOuterRadiusMm == outer,
            "new full-zone target has no UDF prefix or backup-metadata margin");
        foreach (string text in new[] { calibration.TargetSummary, burn.SourceDetails })
        {
            RequireContainsInputRadii(text, "saved BD target/burn summary", innerText, outerText);
            Require(text.Contains("完整数据区半径", StringComparison.Ordinal),
                "both summaries identify the complete data-zone radii");
            Require(!text.Contains("实际绘图范围", StringComparison.Ordinal)
                && !ContainsExactNumber(text, "23.990024") && !ContainsExactNumber(text, "58.022308"),
                "new target displays full data-zone radii without the previous UDF-clipped boundaries");
        }
        RequireExact(inner, Input(target.Parameters.InnerRadiusMm), "saved target retains exact original inner radius");
        RequireExact(outer, Input(target.Parameters.OuterRadiusMm), "saved target retains exact original outer radius");
        Require(bluRay.ReadParameters() == before, "creating and displaying the target does not modify BD generation parameters");

        // The calibration record describes the parameters at creation time,
        // even if the source page is subsequently edited for a different job.
        bluRay.InnerRadius = "24.1";
        bluRay.OuterRadius = "58.1";
        burn.UpdateSourceSummary();
        RequireContainsInputRadii(calibration.TargetSummary, "saved target after source edit", innerText, outerText);
        RequireContainsInputRadii(burn.SourceDetails, "burn snapshot after source edit", innerText, outerText);
        Require(ReferenceEquals(target, calibration.Target)
            && target.Parameters.InnerRadiusMm == inner && target.Parameters.OuterRadiusMm == outer,
            "source edits leave the saved target and its exact parameters unchanged");
        Require(recorder.Calls == 0, "BD summary verification never contacts a recorder");
    }

    private static void RequireContainsInputRadii(string text, string scenario, string inner, string outer)
        => Require(ContainsExactNumber(text, inner) && ContainsExactNumber(text, outer),
            scenario + " preserves both original radius values without binding the test to line breaks or labels");

    private static bool ContainsExactNumber(string text, string number)
        => Regex.IsMatch(text, @"(?<![\d.])" + Regex.Escape(number) + @"(?![\d.Ee])",
            RegexOptions.CultureInvariant);

    private static void TestPresetPrecision()
    {
        DiscParametersState state = new();
        DvdDiscPreset preset = new("precision", "Precision", "Test", 2_297_888,
            23.991460627731737, 57.964893657925366,
            PitchLinear: -3.9256854319227684E-05,
            PitchQuadratic: 6.964795915429256E-05, PitchCubic: -4.670918950137523E-05);
        state.ApplyDvdDiscPreset(preset);
        RequireExact(preset.InnerRadiusMm, state.DvdInnerRadius, "preset inner radius");
        RequireExact(preset.OuterRadiusMm, state.DvdActualOuterRadius, "preset actual outer radius");
        RequireExact(preset.PitchLinear, state.DvdPitchLinear, "preset linear coefficient");
        RequireExact(preset.PitchQuadratic, state.DvdPitchQuadratic, "preset quadratic coefficient");
        RequireExact(preset.PitchCubic, state.DvdActualPitchCubic, "preset actual cubic coefficient");
    }

    private static void TestAppliedCalibration(CalibrationDiscKind kind)
    {
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        Set(shell, "_log", new StringBuilder());
        Set(shell, "_dispatcher", Dispatcher.CurrentDispatcher);
        DiscParametersState state = new();
        CalibrationViewModel model = new(shell, state);
        CalibrationParameters parameters = new(kind, 23.991460627731737, 56.8,
            kind == CalibrationDiscKind.Dvd ? 2_297_888 : 360_000,
            kind == CalibrationDiscKind.Dvd ? 133.3 : 1200,
            PitchLinear: kind == CalibrationDiscKind.Dvd ? 0.1 : 0,
            PitchQuadratic: kind == CalibrationDiscKind.Dvd ? -3.9256854319227684E-05 : 0,
            PitchCubic: kind == CalibrationDiscKind.Dvd ? 1e-12 : 0);
        CalibrationViewModel.CoarseCandidate candidate = new("Selected", parameters, 0, 0);
        // Install an accepted fit/preview state without running the photo fitter
        // or creating optical-device, image-generation, or file-system work.
        Set(model, "_installing", true);
        model.IsPhotoStep = false;
        Set(model, "_photoTransform", new CalibrationPhotoTransform([1, 0, 0, 0, 1, 0, 0, 0, 1]));
        Set(model, "_fit", new CalibrationFitResult(true, parameters, 0, 0,
            [new(parameters, 0, 0)], []));
        model.CoarseCandidates = [candidate];
        model.SelectedCoarseCandidate = candidate;
        Set(model, "_previewReady", true);
        model.ContourConfirmed = true;
        Set(model, "_installing", false);
        Require(model.ApplyParametersCommand.CanExecute(null), "accepted candidate may be applied");
        model.ApplyParametersCommand.Execute(null);
        string inner = kind == CalibrationDiscKind.Dvd ? state.DvdInnerRadius : state.CdInnerRadius;
        string outer = kind == CalibrationDiscKind.Dvd ? state.DvdOuterRadius : state.CdOuterRadius;
        RequireExact(parameters.InnerRadiusMm, inner, "applied fit retains all meaningful radius bits");
        Require(outer == "56.8", "applying a fit does not expand 56.8 into a binary tail");
        if (kind == CalibrationDiscKind.Dvd)
        {
            Require(state.DvdPitchLinear == "0.1", "applied coefficient uses the same compact editable format");
            RequireExact(parameters.PitchQuadratic, state.DvdPitchQuadratic, "applied quadratic coefficient");
            RequireExact(parameters.PitchCubic, state.DvdActualPitchCubic, "applied tiny cubic coefficient");
        }
    }

    private static string Input(double value) => (string)Formatter.GetMethod("ForInput",
        BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [value])!;
    private static string Summary(double value, int decimals = 6) => (string)Formatter.GetMethod("ForSummary",
        BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [value, decimals])!;
    private static void Set(object instance, string name, object value)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static void RequireExact(double value, string text, string description)
        => Require(BitConverter.DoubleToInt64Bits(double.Parse(text, CultureInfo.InvariantCulture))
            == BitConverter.DoubleToInt64Bits(value), description);
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Parameter formatting: " + description);
    }

    private sealed class RejectingBurner : IOpticalDiscBurner
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Formatting tests must not enumerate recorders.");
        }
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Formatting tests must not query recorder speeds.");
        }
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Formatting tests must not write to a recorder.");
        }
    }
}
