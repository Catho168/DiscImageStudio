using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DiscImageStudio;

internal static class UiPreviewRenderer
{
    internal static int Render(
        string outputPath,
        int selectedTab = 0,
        string? previewPath = null,
        string? liveInputPath = null,
        string? liveDisc = null,
        string? liveMode = null,
        string? liveProcessing = null,
        int width = 1366,
        int height = 768,
        string? calibrationSessionPath = null,
        string? calibrationView = null)
    {
        if (width < 800 || height < 600 || width > 4096 || height > 2160)
            throw new ArgumentOutOfRangeException(nameof(width), "Snapshot dimensions must be 800–4096 × 600–2160.");
        Application application = AppHost.CreateApplication(ShutdownMode.OnExplicitShutdown);
        MainWindow window = new()
        {
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
        };
        window.ConfigureSnapshot(
            selectedTab,
            previewPath,
            liveInputPath,
            liveDisc,
            liveMode,
            liveProcessing);
        window.Show();
        window.UpdateLayout();
        if (!string.IsNullOrWhiteSpace(calibrationSessionPath))
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                Task loading = window.LoadCalibrationSnapshotAsync(calibrationSessionPath);
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (!loading.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(60))
                {
                    DispatcherFrame frame = new();
                    DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start();
                    Dispatcher.PushFrame(frame);
                }
                if (!loading.IsCompleted) throw new TimeoutException("Calibration snapshot did not finish within the timeout.");
                loading.GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            window.UpdateLayout();
        }
        if (!string.IsNullOrWhiteSpace(calibrationView))
        {
            window.ConfigureCalibrationSnapshotView(calibrationView);
            window.UpdateLayout();
        }
        if (!string.IsNullOrWhiteSpace(liveInputPath))
        {
            WaitForLivePreview(window, TimeSpan.FromSeconds(30));
            window.UpdateLayout();
        }


        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream output = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        window.Close();
        application.Shutdown();
        Console.WriteLine($"UI snapshot complete: output={fullPath}");
        return 0;
    }

    private static void WaitForLivePreview(MainWindow window, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!window.IsLivePreviewReady && stopwatch.Elapsed < timeout)
        {
            DispatcherFrame frame = new();
            DispatcherTimer timer = new()
            {
                Interval = TimeSpan.FromMilliseconds(50),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        if (!window.IsLivePreviewReady)
        {
            throw new TimeoutException("Live preview did not finish within the snapshot timeout.");
        }
    }

}
