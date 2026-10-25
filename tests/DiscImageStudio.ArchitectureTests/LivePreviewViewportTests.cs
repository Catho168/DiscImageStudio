using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class LivePreviewViewportTests
{
    internal static void Run()
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try { TestViewport(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        Console.WriteLine("live-preview-viewport: passed (fit, device-pixel 1:1, anchored zoom, clipping, refresh preservation, input reset)");
    }

    private static void TestViewport()
    {
        Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // These are the same two theme dictionaries installed by AppHost. Qualifying the
        // assembly keeps their resolution independent of the console test executable.
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/DiscImageStudio;component/Themes/Tokens.xaml", UriKind.Relative),
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/DiscImageStudio;component/Themes/Controls.xaml", UriKind.Relative),
        });
        LivePreviewView? view = null;
        try
        {
            ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
            DiscParametersState state = new();
            LivePreviewViewModel model = new(Dispatcher.CurrentDispatcher, shell, state);
            SetShellProperty(shell, nameof(ShellViewModel.State), state);
            SetShellProperty(shell, nameof(ShellViewModel.LivePreview), model);
            SetShellProperty(shell, nameof(ShellViewModel.BluRay), new BluRayViewModel(shell));
            model.ResultImage = Bitmap(2048);
            model.HasResultImage = true;
            view = new LivePreviewView(model);
            view.Measure(new Size(1200, 900));
            view.Arrange(new Rect(0, 0, 1200, 900));
            view.UpdateLayout();
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Canvas viewport = (Canvas)view.FindName("PreviewViewport");
            Image image = (Image)view.FindName("PreviewImage");
            TextBlock percent = (TextBlock)view.FindName("ZoomLabel");
            Require(viewport.ActualWidth > 0 && viewport.ActualHeight > 0 && viewport.ClipToBounds,
                "the image viewport receives layout space and clips enlarged content");
            double fit = Math.Min(viewport.ActualWidth / 2048, viewport.ActualHeight / 2048);
            Near(fit, image.RenderTransform.Value.M11, "the initial image fits the viewport");
            Near(viewport.ActualWidth / 2, image.RenderTransform.Transform(new Point(1024, 1024)).X,
                "fitting centers the source pixels horizontally");

            Invoke(view, "ActualPixelsButton_Click", view, new RoutedEventArgs());
            double dpi = VisualTreeHelper.GetDpi(viewport).DpiScaleX;
            Near(1, image.RenderTransform.Value.M11 * dpi, "1:1 means one source pixel per device pixel");
            Require(percent.Text == "100%" && RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.NearestNeighbor,
                "1:1 displays actual percentage and preserves individual pixel edges");

            Point anchor = new(viewport.ActualWidth / 2 + 25, viewport.ActualHeight / 2 - 20);
            Point sourcePoint = image.RenderTransform.Inverse!.Transform(anchor);
            double zoom = (double)GetField(view, "_zoom")!;
            Invoke(view, "ZoomAt", zoom * 1.2, anchor);
            Point anchoredAfterZoom = image.RenderTransform.Transform(sourcePoint);
            Near(anchor.X, anchoredAfterZoom.X, "zoom retains the image pixel under the cursor horizontally");
            Near(anchor.Y, anchoredAfterZoom.Y, "zoom retains the image pixel under the cursor vertically");

            Matrix beforeRefresh = image.RenderTransform.Value;
            model.ResultImage = Bitmap(2048);
            Require(image.RenderTransform.Value == beforeRefresh, "same-size live refresh keeps the current detail view");
            state.DvdImagePath = "inactive-dvd.png";
            Require(image.RenderTransform.Value == beforeRefresh, "changing an inactive source leaves the current viewport intact");

            // A large drag request is bounded by the rendered image rather than allowing
            // the entire image to disappear beyond the clipped viewport.
            typeof(LivePreviewView).GetField("_pan", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(view, new Vector(100000, -100000));
            Invoke(view, "UpdateViewport");
            Rect imageBounds = image.RenderTransform.TransformBounds(new Rect(0, 0, 2048, 2048));
            Require(imageBounds.Left <= 0.000001 && imageBounds.Right >= viewport.ActualWidth - 0.000001
                && imageBounds.Top <= 0.000001 && imageBounds.Bottom >= viewport.ActualHeight - 0.000001,
                "panning cannot lose an enlarged image outside the viewport");

            state.CdImagePath = "new-cd.png";
            Near(fit, image.RenderTransform.Value.M11, "changing the active source resets to fit");
            Require(RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.HighQuality,
                "fitting a large image uses high-quality downsampling");
            Invoke(view, "ActualPixelsButton_Click", view, new RoutedEventArgs());
            model.ResultImage = Bitmap(1024);
            Near(Math.Min(viewport.ActualWidth / 1024, viewport.ActualHeight / 1024), image.RenderTransform.Value.M11,
                "a newly rendered resolution resets the viewport to fit");
            Invoke(view, "ActualPixelsButton_Click", view, new RoutedEventArgs());
            model.SelectedDiscTypeIndex = 2;
            model.ResultImage = Bitmap(1024);
            model.HasResultImage = true;
            Near(1, (double)GetField(view, "_zoom")!, "switching disc families starts at fit");
            Invoke(view, "ActualPixelsButton_Click", view, new RoutedEventArgs());
            model.IsReadbackPreview = true;
            Near(1, (double)GetField(view, "_zoom")!, "switching between source and ISO resets the viewport");
            CalibrationLayoutTests.RunInCurrentApplication();
            ImageProcessingWorkflowTests.RunInCurrentApplication();
            LivePreviewLayoutTests.Run();
        }
        finally
        {
            view?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            application.Shutdown();
        }
    }

    private static BitmapSource Bitmap(int size)
    {
        BitmapSource source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, new byte[size * size], size);
        source.Freeze();
        return source;
    }

    private static object? GetField(LivePreviewView view, string name)
        => typeof(LivePreviewView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view);

    private static void Invoke(LivePreviewView view, string method, params object[] arguments)
        => typeof(LivePreviewView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, arguments);

    private static void SetShellProperty(ShellViewModel shell, string name, object value)
        => typeof(ShellViewModel).GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(shell, value);

    private static void Near(double expected, double actual, string message)
        => Require(Math.Abs(expected - actual) < 0.000001, message);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Live preview viewport: " + message);
    }
}
