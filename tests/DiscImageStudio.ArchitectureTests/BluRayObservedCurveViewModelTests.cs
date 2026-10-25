using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ViewModels;

internal static class BluRayObservedCurveViewModelTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-bd-observed-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { TestWorkflow(directory); }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("blu-ray-observed-curve-ui: passed (31 measured points, legacy session load/save, fit/preview, confirmation/apply, failure and cancellation gates)");
    }

    private static void TestWorkflow(string directory)
    {
        CalibrationParameters parameters = BluRayObservedCurveRegressionTests.LegacyParameters();
        CalibrationTarget target = CalibrationTarget.Create(parameters);
        CalibrationCrossTrace trace = BluRayObservedCurveRegressionTests.RealTrace();
        // Only the photograph is synthetic. The saved generation parameters and all
        // 31 rectified coordinates remain the user's exact regression dataset.
        // Fitting consumes those coordinates, not pixels from the placeholder photo.
        CalibrationPhotoSession photo = CreatePhoto(trace);
        CalibrationSession original = new(target, [], BluRayObservedCurveRegressionTests.FitOptions(parameters), true, photo);
        string path = Path.Combine(directory, "real-31-point.calibration.json");
        CalibrationSessionJson.Save(path, original);
        byte[] savedBefore = File.ReadAllBytes(path);
        (CalibrationViewModel model, BluRayViewModel bluRay) = CreateModel(directory);
        Await(model, "LoadSessionFromAsync", path);
        Require(model.Target?.Parameters == parameters && model.IsTraceStep && model.IsRectificationReady
            && model.PhotoImage is not null && model.RectifiedPhoto is not null && model.SolveCommand.CanExecute(null),
            "the legacy BD session loads into the trace step with its geometry, photo and transform intact: " + model.StatusText);
        AssertPoints(model, trace, "loading");
        Require(!model.ApplyParametersCommand.CanExecute(null), "loading a session does not create an applicable fit");
        Require(model.SelectedCoarseSearchRange.RadiusRangeMm == .5, "the saved wide search range is retained");

        model.SolveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        CalibrationViewModel.CoarseCandidate best = model.SelectedCoarseCandidate
            ?? throw new InvalidOperationException("BD observed UI: no candidate: " + model.StatusText);
        Require(!model.IsBusy && best.RmsErrorMm < .12 && model.CrossPredictionContours.Count == 1,
            "the actual App fit completes and produces a low-error continuous prediction: " + model.StatusText);
        Require(model.CrossPredictionContours[0].Length is > 100 and < 20000,
            "the prediction stays within the existing complete-outline budget");
        double minimum = trace.Points.Min(Radius), maximum = trace.Points.Max(Radius);
        var outline = model.CrossPredictionContours[0];
        Near(minimum, Math.Sqrt(outline[0].X * outline[0].X + outline[0].Y * outline[0].Y), 1e-9,
            "prediction starts at the first observed radius inside the generation annulus");
        Near(maximum, Math.Sqrt(outline[^1].X * outline[^1].X + outline[^1].Y * outline[^1].Y), 1e-9,
            "prediction ends at the final observed radius outside the generation annulus");
        AssertPoints(model, trace, "fitting and prediction");
        Require(File.ReadAllBytes(path).SequenceEqual(savedBefore), "load, fit and preview never rewrite the user's source record");
        Require(!model.ContourConfirmed && !model.ApplyParametersCommand.CanExecute(null),
            "a successful low-error prediction still requires explicit visual confirmation");
        model.ContourConfirmed = true;
        Require(model.ApplyParametersCommand.CanExecute(null), "confirmation enables application only after a complete accepted prediction");
        model.ApplyParametersCommand.Execute(null);
        Near(best.Parameters.InnerRadiusMm, double.Parse(bluRay.InnerRadius, CultureInfo.InvariantCulture), 0,
            "application preserves the fitted inner radius without rounding");
        Near(best.Parameters.OuterRadiusMm, double.Parse(bluRay.OuterRadius, CultureInfo.InvariantCulture), 0,
            "application preserves the fitted outer radius without rounding");
        Require(bluRay.SelectedPreset?.IsCustom == true && model.Target?.Parameters == parameters,
            "applying sets the production geometry to manual while preserving the original generation baseline");

        string roundtrip = Path.Combine(directory, "roundtrip.calibration.json");
        Invoke(model, "SaveSessionTo", roundtrip);
        CalibrationSession reloaded = CalibrationSessionJson.Load(roundtrip);
        Require(reloaded.Target.Parameters == parameters && reloaded.Photo!.Traces.Single().Points.SequenceEqual(trace.Points)
            && reloaded.Photo.ImagePngBase64 == photo.ImagePngBase64
            && reloaded.Photo.OuterBoundary.SequenceEqual(photo.OuterBoundary)
            && reloaded.Photo.HoleBoundary.SequenceEqual(photo.HoleBoundary)
            && reloaded.FitOptions == original.FitOptions,
            "saving after fitting preserves all measured coordinates, photo, boundaries and original fit settings");

        var highResidual = best with { RmsErrorMm = 3.2 };
        SelectWithoutAutoPreview(model, highResidual);
        Await(model, "ShowCandidateAsync", highResidual);
        model.ContourConfirmed = true;
        Require(model.CrossPredictionContours.Count == 1 && !model.ApplyParametersCommand.CanExecute(null),
            "even a complete and confirmed outline cannot make a high-residual candidate applicable");

        // This deliberately implausible candidate has a very large winding count.
        // It exercises the App's failed-preview cleanup without relaxing the core cap.
        var impossiblePreview = best with { Parameters = best.Parameters with { InnerRadiusMm = 12 } };
        SelectWithoutAutoPreview(model, impossiblePreview);
        Await(model, "ShowCandidateAsync", impossiblePreview);
        model.ContourConfirmed = true;
        Require(model.CrossPredictionContours.Count == 0 && !Field<bool>(model, "_previewReady")
            && !model.ApplyParametersCommand.CanExecute(null),
            "a failed complete-curve preview clears old contours and cannot be confirmed into an applicable result");
        AssertPoints(model, trace, "failed preview");

        bool cancelledAtPublication = false;
        System.ComponentModel.PropertyChangedEventHandler cancel = (_, change) =>
        {
            if (!cancelledAtPublication && model.IsBusy && change.PropertyName == nameof(model.CoarseCandidates)
                && model.CoarseCandidates.Count > 0)
            {
                cancelledAtPublication = true;
                model.CancelCommand.Execute(null);
            }
        };
        model.PropertyChanged += cancel;
        try { model.SolveCommand.ExecuteAsync(null).GetAwaiter().GetResult(); }
        finally { model.PropertyChanged -= cancel; }
        model.ContourConfirmed = true;
        Require(cancelledAtPublication && !model.IsBusy && model.CoarseCandidates.Count == 0
            && model.CrossPredictionContours.Count == 0 && !model.ApplyParametersCommand.CanExecute(null),
            "cancellation at result publication removes candidates and cannot leave a stale applicable fit");
        AssertPoints(model, trace, "cancellation");
        Require(File.ReadAllBytes(path).SequenceEqual(savedBefore), "failure and cancellation also preserve the original record bytes");
        Invoke(model, "OnWindowClosed");
    }

    private static CalibrationPhotoSession CreatePhoto(CalibrationCrossTrace trace)
    {
        const int size = 192;
        byte[] pixels = Enumerable.Repeat((byte)210, size * size).ToArray();
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bitmap.Freeze();
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream output = new();
        encoder.Save(output);
        CalibrationPoint[] Boundary(double radius) => Enumerable.Range(0, 8).Select(i =>
        {
            double angle = -Math.PI / 2 + i * Math.PI / 4;
            return new CalibrationPoint(96 + 1.4 * radius * Math.Cos(angle), 96 + 1.4 * radius * Math.Sin(angle));
        }).ToArray();
        return new(Convert.ToBase64String(output.ToArray()), Boundary(60), Boundary(7.5), 60, true, [trace]);
    }

    private static (CalibrationViewModel, BluRayViewModel) CreateModel(string directory)
    {
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        Set(shell, "_log", new StringBuilder());
        Set(shell, "_dispatcher", Dispatcher.CurrentDispatcher);
        Set(shell, "<Toasts>k__BackingField", new ObservableCollection<ToastItem>());
        foreach (string name in new[] { "OpenOutputCommand", "BrowseCdImageCommand", "BrowseDvdImageCommand", "BrowseCdTrackCommand", "BrowseDvdIsoCommand" })
            Set(shell, $"<{name}>k__BackingField", new RelayCommand(() => { }));
        BluRayViewModel bluRay = new(shell, presetJsonPath: Path.Combine(directory, "unused-bd-presets.json"));
        Set(shell, "<BluRay>k__BackingField", bluRay);
        return (new(shell, new DiscParametersState()), bluRay);
    }

    private static void SelectWithoutAutoPreview(CalibrationViewModel model, CalibrationViewModel.CoarseCandidate candidate)
    {
        Set(model, "_installing", true);
        try { model.CoarseCandidates = [candidate]; model.SelectedCoarseCandidate = candidate; }
        finally { Set(model, "_installing", false); }
    }
    private static void AssertPoints(CalibrationViewModel model, CalibrationCrossTrace trace, string stage)
        => Require(model.TracePoints.Count == 31 && model.TracePoints.Select(point => new CalibrationPoint(point.XMm, point.YMm)).SequenceEqual(trace.Points),
            stage + " retains every original measured coordinate without clipping or rounding");
    private static double Radius(CalibrationPoint point) => Math.Sqrt(point.X * point.X + point.Y * point.Y);
    private static void Await(object instance, string name, params object?[] arguments)
        => ((Task)Invoke(instance, name, arguments)!).GetAwaiter().GetResult();
    private static object? Invoke(object instance, string name, params object?[] arguments)
        => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, arguments);
    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value)
        => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void Near(double expected, double actual, double tolerance, string message)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, message);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("BD observed UI: " + message); }
}
