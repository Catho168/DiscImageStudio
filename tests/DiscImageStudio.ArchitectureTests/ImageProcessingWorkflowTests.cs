using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.BluRay;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class ImageProcessingWorkflowTests
{
    internal static void Run()
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try { TestPreparationAndConfirmation(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        Console.WriteLine("image-processing-workflow: passed (CD/DVD request snapshots, BD file/stream/preview consumers, three-family confirmation invalidation; no recorder access)");
    }

    private static void TestPreparationAndConfirmation()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"image-processing-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.png");
            WriteSource(source);
            ShellViewModel shell = CreateShell();
            DiscParametersState state = new();
            BluRayViewModel bluRay = new(shell);
            SetShellProperty(shell, nameof(ShellViewModel.BluRay), bluRay);
            RecordingBurner recorder = new();
            BurnViewModel burn = new(shell, state, recorder);
            SetShellProperty(shell, nameof(ShellViewModel.Burn), burn);
            // Subscribe the production handler without starting the real application,
            // loading user presets, enumerating devices or constructing a main window.
            MethodInfo stateChanged = typeof(ShellViewModel).GetMethod("State_PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            state.PropertyChanged += stateChanged.CreateDelegate<PropertyChangedEventHandler>(shell);
            CdViewModel cd = new(shell, state);
            DvdViewModel dvd = new(shell, state);
            Require(!state.CdImageProcessingEnabled && !state.CdGammaCorrectionEnabled
                && !state.DvdImageProcessingEnabled && !state.DvdGammaCorrectionEnabled
                && !bluRay.ImageProcessingEnabled && !bluRay.GammaCorrectionEnabled,
                "all three disc families retain unprocessed defaults");

            foreach (bool isDvd in new[] { false, true })
            {
                object generation = isDvd ? dvd : cd;
                string method = isDvd ? "CreateDvdImageRequest" : "CreateCdImageRequest";
                foreach (int layout in new[] { 0, 1 })
                {
                    if (isDvd) state.DvdImageProcessingModeIndex = layout;
                    else state.CdImageProcessingModeIndex = layout;
                    byte[]? withoutGamma = null;
                    foreach ((bool processing, bool gamma) in new[] { (false, true), (true, false), (true, true) })
                    {
                        if (isDvd)
                        {
                            state.DvdImageProcessingEnabled = processing;
                            state.DvdGammaCorrectionEnabled = gamma;
                        }
                        else
                        {
                            state.CdImageProcessingEnabled = processing;
                            state.CdGammaCorrectionEnabled = gamma;
                        }
                        using PreparedPixels generated = Prepare(generation, method, source);
                        using PreparedPixels streamed = Prepare(burn, method, source);
                        Require(generated.Pixels.SequenceEqual(streamed.Pixels),
                            $"{method}: file generation and streaming burn consume identical pixels (layout={layout}, processing={processing}, gamma={gamma})");
                        if (layout == 0)
                            Require((generated.Path == source) == !processing,
                                $"{method}: the master switch controls the direct-mapping preprocessing entry point independently of gamma");
                        if (processing && !gamma) withoutGamma = generated.Pixels;
                        if (processing && gamma)
                            Require(withoutGamma is not null && !withoutGamma.SequenceEqual(generated.Pixels),
                                $"{method}: the gamma selection reaches the real image processor in both layouts");
                    }
                }
                object capturedRequest = CaptureRequest(generation, method, source);
                using PreparedPixels expected = PrepareCaptured(capturedRequest);
                if (isDvd)
                {
                    state.DvdImageProcessingEnabled = state.DvdGammaCorrectionEnabled = false;
                    state.DvdImageProcessingModeIndex = 0;
                }
                else
                {
                    state.CdImageProcessingEnabled = state.CdGammaCorrectionEnabled = false;
                    state.CdImageProcessingModeIndex = 0;
                }
                using PreparedPixels deferred = PrepareCaptured(capturedRequest);
                using PreparedPixels current = Prepare(generation, method, source);
                Require(expected.Pixels.SequenceEqual(deferred.Pixels) && current.Path == source,
                    $"{method}: queued requests retain captured processing, gamma and ring layout after UI edits");
            }

            state.CdImageProcessingEnabled = state.DvdImageProcessingEnabled = false;
            state.CdGammaCorrectionEnabled = state.DvdGammaCorrectionEnabled = false;
            foreach (int family in new[] { 0, 1, 2 })
            {
                burn.SelectedMediaIndex = family;
                burn.UpdateSourceSummary();
                Action[] changes = family switch
                {
                    0 => [() => state.CdImageProcessingEnabled = true, () => state.CdGammaCorrectionEnabled = true,
                        () => state.CdImageProcessingEnabled = false, () => state.CdGammaCorrectionEnabled = false],
                    1 => [() => state.DvdImageProcessingEnabled = true, () => state.DvdGammaCorrectionEnabled = true,
                        () => state.DvdImageProcessingEnabled = false, () => state.DvdGammaCorrectionEnabled = false],
                    _ => [() => bluRay.ImageProcessingEnabled = true, () => bluRay.GammaCorrectionEnabled = true,
                        () => bluRay.ImageProcessingEnabled = false, () => bluRay.GammaCorrectionEnabled = false],
                };
                foreach (Action change in changes)
                {
                    burn.ConfirmChecked = true;
                    change();
                    Require(!burn.ConfirmChecked, $"family {family}: each image-processing edit automatically invalidates burn confirmation");
                }
                burn.ConfirmChecked = true;
                if (family == 0) state.DvdGammaCorrectionEnabled = !state.DvdGammaCorrectionEnabled;
                else state.CdGammaCorrectionEnabled = !state.CdGammaCorrectionEnabled;
                Require(burn.ConfirmChecked, $"family {family}: editing an inactive disc family preserves this source confirmation");
                // Reset the other family without changing the active source.
                if (family == 0) state.DvdGammaCorrectionEnabled = false;
                else state.CdGammaCorrectionEnabled = false;
            }
            TestBluRayConsumers(directory, source);
            Require(recorder.BurnCalls == 0, "workflow regression never starts optical writing");
        }
        finally
        {
            foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void TestBluRayConsumers(string directory, string source)
    {
        // One BD cluster (64 KiB) exercises the real image mapping and both
        // output paths without a recorder, full-size ISO or filesystem fixture.
        BluRayDiscParameters disc = new(TotalSectors: 32);
        BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc);
        BluRayRunLengthMapping polarity = new();
        byte[]? unprocessedPayload = null, ditheredPayload = null, ditheredPreview = null;
        foreach ((bool processing, bool gamma) in new[] { (false, true), (true, false), (true, true) })
        {
            Type service = typeof(ShellViewModel).Assembly.GetType("DiscImageStudio.Services.RingImagePreparation")!;
            object result = service.GetMethod("PrepareImage", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [source, null, "bd-workflow-test", processing, gamma, CancellationToken.None, false, null])!;
            using PreparedPixels prepared = ReadPrepared(result);
            BluRayGray8Raster raster = BluRayImageLoader.Load(prepared.Path);
            if (processing)
            {
                byte[] expectedGray = Enumerable.Range(0, prepared.Pixels.Length / 4)
                    .Select(index => prepared.Pixels[index * 4]).ToArray();
                Require(raster.Pixels.SequenceEqual(expectedGray),
                    "BD raster decoding preserves every prepared dither pixel before intensity mapping");
            }
            BluRayImagePayloadMapping fileMapping = BluRayImageLoader.CreateMapping(prepared.Path, disc, polarity);
            // Streaming burn captures an immutable raster, while file generation
            // uses CreateMapping; compare these actual production constructions.
            BluRayImagePayloadMapping burnMapping = new(disc, raster, polarity);
            string iso = Path.Combine(directory, $"bd-{processing}-{gamma}.iso");
            BluRayIsoGenerator.GenerateFile(iso, plan, fileMapping);
            using MemoryStream stream = new();
            BluRayIsoGenerator.GenerateToStream(stream, plan, burnMapping);
            byte[] payload = File.ReadAllBytes(iso);
            Require(payload.Length == 32 * 2048 && payload.SequenceEqual(stream.ToArray()),
                $"BD file and captured-raster burn mappings produce identical bounded payloads (processing={processing}, gamma={gamma})");
            string projection = Path.Combine(directory, $"bd-{processing}-{gamma}-projection.png");
            BluRayPreviewRenderer.RenderCalibration(prepared.Path, projection, disc, disc, 0, 32, 256, invert: false);
            byte[] preview = ReadPixels(projection);
            if (!processing) unprocessedPayload = payload;
            else if (!gamma)
            {
                ditheredPayload = payload;
                ditheredPreview = preview;
                Require(unprocessedPayload is not null && !unprocessedPayload.SequenceEqual(payload),
                    "BD image processing reaches the encoded output rather than affecting only the UI");
            }
            else
            {
                Require(ditheredPayload is not null && !ditheredPayload.SequenceEqual(payload),
                    "BD gamma selection reaches the generated and streamed payload");
                Require(ditheredPreview is not null && !ditheredPreview.SequenceEqual(preview),
                    "BD calibration projection consumes the same gamma-corrected source as generation");
            }
        }
    }
    // Called within LivePreviewViewportTests' existing Application and STA lifetime.
    internal static void RunInCurrentApplication()
    {
        Require(Application.Current is not null && Application.Current.Dispatcher.CheckAccess(),
            "binding checks use the existing WPF application");
        ShellViewModel shell = CreateShell();
        DiscParametersState state = new();
        BluRayViewModel bluRay = new(shell);
        SetShellProperty(shell, nameof(ShellViewModel.BluRay), bluRay);
        LivePreviewViewModel previewModel = new(Dispatcher.CurrentDispatcher, shell, state);
        SetShellProperty(shell, nameof(ShellViewModel.LivePreview), previewModel);
        LivePreviewView preview = new(previewModel);
        FrameworkElement[] views = [new CdView(new(shell, state)), new DvdView(new(shell, state)), new BluRayView(bluRay)];
        string[] familyNames = ["cd", "dvd", "bd"];
        try
        {
            previewModel.IsCalibrationPreview = true;
            for (int family = 0; family < views.Length; family++)
            {
                FrameworkElement page = views[family];
                previewModel.SelectedDiscTypeIndex = family;
                Layout(page);
                Layout(preview);
                string prefix = family switch { 0 => "State.Cd", 1 => "State.Dvd", _ => string.Empty };
                string previewPrefix = family == 2 ? "BluRay." : prefix;
                CheckBox enabled = BoundCheckbox(page, prefix + "ImageProcessingEnabled");
                CheckBox gamma = BoundCheckbox(page, prefix + "GammaCorrectionEnabled");
                CheckBox previewEnabled = BoundCheckbox(preview, previewPrefix + "ImageProcessingEnabled");
                CheckBox previewGamma = BoundCheckbox(preview, previewPrefix + "GammaCorrectionEnabled");
                Require(enabled.IsChecked == false && previewEnabled.IsChecked == false && !gamma.IsEnabled && !previewGamma.IsEnabled,
                    $"{familyNames[family]}: gamma controls begin disabled on both pages");
                enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(page);
                Layout(preview);
                Require(previewEnabled.IsChecked == true && gamma.IsEnabled && previewGamma.IsEnabled,
                    $"{familyNames[family]}: enabling processing on the production page enables gamma on both pages");
                previewGamma.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(page);
                Require(gamma.IsChecked == true,
                    $"{familyNames[family]}: changing gamma in the preview writes back to the production page");
                enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Layout(page);
                Layout(preview);
                Require(previewEnabled.IsChecked == false && !gamma.IsEnabled && !previewGamma.IsEnabled
                    && gamma.IsChecked == true && previewGamma.IsChecked == true,
                    $"{familyNames[family]}: bypassing processing preserves the independent gamma choice while disabling it");
                previewEnabled.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(page);
                Layout(preview);
                Require(enabled.IsChecked == true && gamma.IsEnabled && gamma.IsChecked == true,
                    $"{familyNames[family]}: preview re-enables production processing and retains the gamma selection");
                gamma.BringIntoView();
                Layout(page);
                // BringIntoView only guarantees the checkbox itself is in the
                // viewport; frame its surrounding explanation away from the
                // production page's bottom fade for useful visual evidence.
                ScrollViewer pageScroll = Descendants<ScrollViewer>(page).First();
                double toggleTop = enabled.TranslatePoint(new Point(), page).Y;
                if (toggleTop > 300)
                {
                    pageScroll.ScrollToVerticalOffset(pageScroll.VerticalOffset + toggleTop - 300);
                    Layout(page);
                }
                UiLayoutSnapshot.Save(page, $"image-processing-{familyNames[family]}");
                previewGamma.BringIntoView();
                Layout(preview);
                UiLayoutSnapshot.Save(preview, $"image-processing-preview-{familyNames[family]}");
            }
        }
        finally
        {
            preview.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            foreach (FrameworkElement page in views) page.DataContext = null;
            preview.DataContext = null;
        }
        Console.WriteLine("image-processing-bindings: passed (three-family production/preview synchronization, gamma enablement and retained choices)");
    }

    private static ShellViewModel CreateShell()
    {
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        typeof(ShellViewModel).GetField("_log", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, new StringBuilder());
        return shell;
    }

    private static PreparedPixels Prepare(object model, string methodName, string source)
        => PrepareCaptured(CaptureRequest(model, methodName, source));

    private static object CaptureRequest(object model, string methodName, string source)
        => model.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [source, 256])!;

    private static PreparedPixels PrepareCaptured(object request)
    {
        // Execute the same immutable request used by the UI's background job
        // on this test's STA without generating a full disc.
        MethodInfo prepare = request.GetType().GetMethod("Prepare", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        return ReadPrepared(prepare.Invoke(request, [CancellationToken.None, false, null])!);
    }

    private static PreparedPixels ReadPrepared(object prepared)
    {
        string path = (string)prepared.GetType().GetProperty("Path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(prepared)!;
        try { return new((IDisposable)prepared, path, ReadPixels(path)); }
        catch { ((IDisposable)prepared).Dispose(); throw; }
    }

    private static byte[] ReadPixels(string path)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap bitmap = new(frame, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static void WriteSource(string path)
    {
        byte[] pixels = new byte[64 * 40 * 4];
        for (int y = 0; y < 40; y++)
        for (int x = 0; x < 64; x++)
        {
            int offset = (y * 64 + x) * 4;
            pixels[offset] = (byte)(40 + x * 2);
            pixels[offset + 1] = (byte)(75 + y * 3);
            pixels[offset + 2] = (byte)(200 - x);
            pixels[offset + 3] = 255;
        }
        BitmapSource source = BitmapSource.Create(64, 40, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static CheckBox BoundCheckbox(DependencyObject view, string path)
        => Descendants<CheckBox>(view).Single(control => BindingOperations.GetBinding(control, ToggleButton.IsCheckedProperty)?.Path.Path == path);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Layout(FrameworkElement view)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            view.Measure(new Size(1100, 900));
            view.Arrange(new Rect(0, 0, 1100, 900));
            view.UpdateLayout();
            view.Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);
        }
    }

    private static void SetShellProperty(ShellViewModel shell, string property, object value)
        => typeof(ShellViewModel).GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Image processing workflow: " + message);
    }

    private sealed record PreparedPixels(IDisposable Prepared, string Path, byte[] Pixels) : IDisposable
    {
        public void Dispose() => Prepared.Dispose();
    }

    private sealed class RecordingBurner : IOpticalDiscBurner
    {
        internal int BurnCalls { get; private set; }
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([]);
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalWriteSpeed>>([]);
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            BurnCalls++;
            throw new InvalidOperationException("The image processing tests must never start physical writing.");
        }
    }
}
