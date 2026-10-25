using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using DiscImageStudio.ViewModels;

internal static class MaximizedWindowBoundsTests
{
    // Pure geometry checks: no Application, Window/HWND, monitor query or desktop interaction.
    internal static void Run()
    {
        TestMonitorWorkAreas();
        TestDpiConstrainedMinimum();
        TestNativePixelRounding();
        Console.WriteLine("maximized-window-bounds: passed (all taskbar edges, negative monitor origins, physical pixel bounds, DPI-constrained minimum sizes)");
    }

    private static void TestMonitorWorkAreas()
    {
        (string Name, Rect Monitor, Rect Work, Point ExpectedPosition, Size ExpectedSize)[] cases =
        [
            ("bottom taskbar", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), new(0, 0), new(1920, 1040)),
            ("top taskbar", new(0, 0, 1920, 1080), new(0, 48, 1920, 1032), new(0, 48), new(1920, 1032)),
            ("left taskbar", new(0, 0, 1920, 1080), new(64, 0, 1856, 1080), new(64, 0), new(1856, 1080)),
            ("right taskbar", new(0, 0, 1920, 1080), new(0, 0, 1848, 1080), new(0, 0), new(1848, 1080)),
            ("no reserved area", new(0, 0, 1920, 1080), new(0, 0, 1920, 1080), new(0, 0), new(1920, 1080)),
            ("right secondary monitor", new(1920, 160, 2560, 1440), new(1920, 160, 2560, 1392), new(0, 0), new(2560, 1392)),
            ("left secondary monitor", new(-1920, -200, 1920, 1080), new(-1872, -170, 1872, 1010), new(48, 30), new(1872, 1010)),
            ("upper-left secondary monitor", new(-2560, -1440, 2560, 1440), new(-2560, -1400, 2560, 1400), new(0, 40), new(2560, 1400)),
            ("reserved space at multiple edges", new(-1600, 50, 1600, 1000), new(-1580, 80, 1540, 920), new(20, 30), new(1540, 920)),
        ];
        foreach (var test in cases)
        foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.5, 3 })
        {
            Bounds result = Calculate(test.Monitor, test.Work, new(720, 560), new(scale, scale));
            Equal(test.ExpectedPosition, result.MaxPosition, test.Name + ": maximize position is monitor-relative physical pixels");
            Equal(test.ExpectedSize, result.MaxSize, test.Name + ": maximize size is exactly the current monitor work area at every DPI");
            Rect reconstructed = new(new Point(test.Monitor.Left + result.MaxPosition.X, test.Monitor.Top + result.MaxPosition.Y), result.MaxSize);
            Equal(test.Work, reconstructed, test.Name + ": window edges reconstruct the work area without taskbar or border offsets");
            Require(test.Monitor.Contains(reconstructed), test.Name + ": maximized bounds stay on the selected monitor");
            CheckMinimum(result, test.Work, new(720, 560), new(scale, scale), test.Name);
        }
    }

    private static void TestDpiConstrainedMinimum()
    {
        // The former 720 x 560 DIP window minimum cannot fit these valid work areas.
        // Maximize must not be enlarged back over the taskbar by WPF/Win32 minimums.
        (Rect Monitor, Rect Work, DpiScale Dpi, Size ExpectedTrack)[] cases =
        [
            (new(0, 0, 1366, 768), new(0, 0, 1366, 728), new(1.5, 1.5), new(1080, 728)),
            (new(0, 0, 1920, 1080), new(0, 0, 1920, 1032), new(2, 2), new(1440, 1032)),
            (new(0, 0, 1024, 768), new(0, 0, 1024, 720), new(2, 2), new(1024, 720)),
            (new(-1366, 0, 1366, 768), new(-1366, 36, 1366, 732), new(1.75, 1.75), new(1260, 732)),
            (new(0, 0, 800, 720), new(0, 0, 777, 680), new(1.25, 1.5), new(777, 680)),
            (new(0, 0, 1, 1), new(0, 0, 1, 1), new(3, 3), new(1, 1)),
        ];
        foreach (var test in cases)
        {
            Bounds result = Calculate(test.Monitor, test.Work, new(720, 560), test.Dpi);
            Equal(test.ExpectedTrack, result.MinTrackSize, "native minimum is capped independently by each available work-area dimension");
            CheckMinimum(result, test.Work, new(720, 560), test.Dpi, "constrained DPI");
            Require(result.MinimumSizeDip.Height < 560,
                "a short high-DPI monitor lowers the WPF minimum height instead of extending under the taskbar");
        }

        Bounds largeMonitor = Calculate(new(0, 0, 3840, 2160), new(0, 0, 3840, 2080), new(720, 560), new(2, 2));
        Equal(new Size(720, 560), largeMonitor.MinimumSizeDip,
            "moving back to a sufficiently large monitor restores the preferred DIP minimum without permanently shrinking it");
        Equal(new Size(1440, 1120), largeMonitor.MinTrackSize, "restored native minimum uses current monitor DPI");
    }

    private static void TestNativePixelRounding()
    {
        Rect monitor = new(1920, -100, 1920, 1080);
        Rect work = new(1920, -75, 1920, 1015);
        Size preferred = new(720.25, 560.25);
        DpiScale dpi = new(1.25, 1.25);
        Bounds result = Calculate(monitor, work, preferred, dpi);
        Equal(new Size(901, 701), result.MinTrackSize, "fractional DIP minimum rounds upward to whole native pixels");
        Equal(new Size(720.8, 560.8), result.MinimumSizeDip, "WPF minimum is converted back from the rounded native constraint");
        Equal(new Point(0, 25), result.MaxPosition, "fractional scaling never rescales monitor-relative physical position");
        Equal(new Size(1920, 1015), result.MaxSize, "fractional scaling never rescales physical work-area size");
        CheckMinimum(result, work, preferred, dpi, "native rounding");
    }

    private static void CheckMinimum(Bounds result, Rect work, Size preferred, DpiScale dpi, string context)
    {
        double expectedWidth = Math.Min(work.Width, Math.Ceiling(preferred.Width * dpi.DpiScaleX));
        double expectedHeight = Math.Min(work.Height, Math.Ceiling(preferred.Height * dpi.DpiScaleY));
        Equal(new Size(expectedWidth, expectedHeight), result.MinTrackSize, context + ": independent native-pixel minimum calculation");
        Near(result.MinTrackSize.Width, result.MinimumSizeDip.Width * dpi.DpiScaleX,
            context + ": WPF minimum width round-trips to native pixels");
        Near(result.MinTrackSize.Height, result.MinimumSizeDip.Height * dpi.DpiScaleY,
            context + ": WPF minimum height round-trips to native pixels");
        Require(result.MinTrackSize.Width <= result.MaxSize.Width && result.MinTrackSize.Height <= result.MaxSize.Height,
            context + ": minimum track size cannot exceed maximized work-area size");
    }

    private static Bounds Calculate(Rect monitor, Rect work, Size minimum, DpiScale dpi)
    {
        Type helper = typeof(ShellViewModel).Assembly.GetType("DiscImageStudio.Services.WindowWorkArea", throwOnError: true)!;
        MethodInfo calculate = helper.GetMethod("Calculate", BindingFlags.Static | BindingFlags.NonPublic)!;
        object value;
        try { value = calculate.Invoke(null, [monitor, work, minimum, dpi])!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        T Read<T>(string property) => (T)value.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!;
        return new(Read<Point>("MaxPosition"), Read<Size>("MaxSize"), Read<Size>("MinTrackSize"), Read<Size>("MinimumSizeDip"));
    }
    private sealed record Bounds(Point MaxPosition, Size MaxSize, Size MinTrackSize, Size MinimumSizeDip);
    private static void Equal<T>(T expected, T actual, string message)
        => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, actual {actual}");
    private static void Near(double expected, double actual, string message)
        => Require(double.IsFinite(actual) && Math.Abs(expected - actual) <= 1e-9, message);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Maximized window: " + message); }
}
