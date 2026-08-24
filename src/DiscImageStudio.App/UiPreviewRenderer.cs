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
        string? liveAngle = null,
        string? liveDisc = null,
        string? liveRing = null)
    {
        Application application = new()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        MainWindow window = new()
        {
            Width = 1366,
            Height = 768,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
        };
        window.ConfigureSnapshot(selectedTab, previewPath, liveInputPath, liveDisc, liveRing);
        window.Show();
        window.UpdateLayout();
        if (!string.IsNullOrWhiteSpace(liveInputPath))
        {
            WaitForLivePreview(window, TimeSpan.FromSeconds(30));
            if (!string.IsNullOrWhiteSpace(liveAngle))
            {
                window.SetLivePreviewAngleForSnapshot(liveAngle);
                WaitForLivePreview(window, TimeSpan.FromSeconds(30));
            }
            window.UpdateLayout();
        }

        const int width = 1366;
        const int height = 768;
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
