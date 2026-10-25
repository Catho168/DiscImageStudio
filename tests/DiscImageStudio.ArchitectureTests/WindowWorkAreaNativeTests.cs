using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using DiscImageStudio.ViewModels;

internal static class WindowWorkAreaNativeTests
{
    internal static void Run()
    {
        Exception? failure = null;
        string dpiMode = "existing thread context";
        Thread thread = new(() =>
        {
            IntPtr previousDpiContext = IntPtr.Zero;
            try
            {
                // Match the application manifest without changing the test process
                // or another thread's DPI awareness. No Application is constructed.
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
                {
                    previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
                    if (previousDpiContext != IntPtr.Zero) dpiMode = "PerMonitorV2";
                }
                Size workAreaDip = TestHiddenWindow(new(720, 560), requireClamping: false);
                TestHiddenWindow(new(workAreaDip.Width + 96, workAreaDip.Height + 96), requireClamping: true);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                if (previousDpiContext != IntPtr.Zero) _ = SetThreadDpiAwarenessContext(previousDpiContext);
            }
        }) { IsBackground = true, Name = "Hidden window work-area protocol test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(15)), "hidden-window STA completed without a refresh loop");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine($"window-work-area-native: passed (hidden HWND, real WM_GETMINMAXINFO, WPF tracking, constrained minimum, refresh/dispose; DPI {dpiMode}; no recorder)");
    }

    private static Size TestHiddenWindow(Size preferredMinimum, bool requireClamping)
    {
        Window window = CreateWindow(preferredMinimum);
        IDisposable? helper = null;
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            Type helperType = typeof(ShellViewModel).Assembly
                .GetType("DiscImageStudio.Services.WindowWorkArea", throwOnError: true)!;
            helper = (IDisposable)Activator.CreateInstance(helperType,
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: [window], culture: null)!;
            hwnd = new WindowInteropHelper(window).EnsureHandle();
            Require(hwnd != IntPtr.Zero && IsWindow(hwnd) && !IsWindowVisible(hwnd) && !window.IsVisible,
                "EnsureHandle creates an invisible native window without showing or activating it");
            DrainDispatcher();

            MonitorInfo monitor = ReadMonitor(hwnd);
            double dpi = WindowDpi(window, hwnd);
            Size availableDip = new((monitor.Work.Right - monitor.Work.Left) / dpi,
                (monitor.Work.Bottom - monitor.Work.Top) / dpi);
            double expectedMinimumWidth = Math.Min(monitor.Work.Right - monitor.Work.Left,
                Math.Ceiling(preferredMinimum.Width * dpi)) / dpi;
            double expectedMinimumHeight = Math.Min(monitor.Work.Bottom - monitor.Work.Top,
                Math.Ceiling(preferredMinimum.Height * dpi)) / dpi;
            Near(expectedMinimumWidth, window.MinWidth, "WPF minimum width follows current native DPI and work area");
            Near(expectedMinimumHeight, window.MinHeight, "WPF minimum height follows current native DPI and work area");
            Require(window.MinWidth <= availableDip.Width + 1e-8 && window.MinHeight <= availableDip.Height + 1e-8,
                "WPF minimum dimensions never extend beyond the usable monitor at its current DPI");
            if (requireClamping)
                Require(window.MinWidth < preferredMinimum.Width && window.MinHeight < preferredMinimum.Height,
                    "an oversized preferred minimum is reduced in both dimensions");

            MinMaxInfo actual = QueryMinMax(hwnd);
            CheckMaximizeBounds(actual, monitor);
            // Compare with an otherwise identical WPF/WindowChrome HWND whose
            // minimum properties have already been clamped. This checks the final
            // native message chain, including WPF, rather than invoking the hook.
            Window control = CreateWindow(new(window.MinWidth, window.MinHeight));
            try
            {
                control.Left = window.Left;
                control.Top = window.Top;
                control.Width = window.Width;
                control.Height = window.Height;
                IntPtr controlHwnd = new WindowInteropHelper(control).EnsureHandle();
                Require(!IsWindowVisible(controlHwnd) && !control.IsVisible, "the WPF tracking control remains hidden");
                Near(dpi, WindowDpi(control, controlHwnd), "tracking comparison uses the same monitor DPI");
                MinMaxInfo expectedTracking = QueryMinMax(controlHwnd);
                Equal(expectedTracking.MinTrackSize, actual.MinTrackSize,
                    "minimum tracking fields are still produced by WPF after the work-area hook");
                Equal(expectedTracking.MaxTrackSize, actual.MaxTrackSize,
                    "maximum tracking fields retain WPF's normal constraints");
                Require(actual.MinTrackSize.X > 0 && actual.MinTrackSize.Y > 0,
                    "WPF processes WM_GETMINMAXINFO instead of leaving the caller's sentinel minima");
            }
            finally { control.Close(); }

            // A burst of notifications must drain. The timer prevents a queued
            // refresh cycle from hanging the architecture test process indefinitely.
            for (int count = 0; count < 8; count++)
                _ = SendMessage(hwnd, WmSettingChange, IntPtr.Zero, IntPtr.Zero);
            DrainDispatcher();
            CheckMaximizeBounds(QueryMinMax(hwnd), ReadMonitor(hwnd));
            Require(!IsWindowVisible(hwnd), "settings refresh does not show or activate the hidden window");

            // Dispose with a refresh pending: neither that callback nor later
            // native messages may restore the helper's captured minimum values.
            _ = SendMessage(hwnd, WmSettingChange, IntPtr.Zero, IntPtr.Zero);
            helper.Dispose();
            helper.Dispose();
            window.MinWidth = 123.25;
            window.MinHeight = 124.5;
            DrainDispatcher();
            _ = QueryMinMax(hwnd);
            Near(123.25, window.MinWidth, "disposed hooks and queued callbacks leave WPF minimum width alone");
            Near(124.5, window.MinHeight, "disposed hooks and queued callbacks leave WPF minimum height alone");
            return availableDip;
        }
        finally
        {
            helper?.Dispose();
            window.Close();
            if (hwnd != IntPtr.Zero) Require(!IsWindow(hwnd), "closing the test window destroys its invisible HWND");
        }
    }

    private static Window CreateWindow(Size minimum)
    {
        var window = new Window
        {
            Title = "Hidden work-area protocol test",
            Width = 800,
            Height = 640,
            MinWidth = minimum.Width,
            MinHeight = minimum.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 32,
            Top = 32,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            GlassFrameThickness = new Thickness(0),
            CaptionHeight = 32,
            CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = new Thickness(7),
            UseAeroCaptionButtons = false,
        });
        return window;
    }

    private static MonitorInfo ReadMonitor(IntPtr hwnd)
    {
        IntPtr handle = MonitorFromWindow(hwnd, 2);
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        Require(handle != IntPtr.Zero && GetMonitorInfo(handle, ref info), "Windows returns the HWND's current monitor information");
        Require(info.Work.Right > info.Work.Left && info.Work.Bottom > info.Work.Top, "current monitor has a usable work area");
        return info;
    }

    private static double WindowDpi(Window window, IntPtr hwnd)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        return VisualTreeHelper.GetDpi(window).DpiScaleX;
    }

    private static MinMaxInfo QueryMinMax(IntPtr hwnd)
    {
        MinMaxInfo initial = new()
        {
            Reserved = new(13, 17), MaxPosition = new(23, 29), MaxSize = new(31, 37),
            MinTrackSize = new(-1, -2), MaxTrackSize = new(30000, 30001),
        };
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>());
        try
        {
            Marshal.StructureToPtr(initial, memory, false);
            _ = SendMessage(hwnd, WmGetMinMaxInfo, IntPtr.Zero, memory);
            return Marshal.PtrToStructure<MinMaxInfo>(memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static void CheckMaximizeBounds(MinMaxInfo result, MonitorInfo monitor)
    {
        Equal(new(monitor.Work.Left - monitor.Monitor.Left, monitor.Work.Top - monitor.Monitor.Top),
            result.MaxPosition, "native maximize position is work-area-relative to the current monitor");
        Equal(new(monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top),
            result.MaxSize, "native maximize size exactly matches the current monitor work area in device pixels");
    }

    private static void DrainDispatcher()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherFrame frame = new();
        bool timedOut = false;
        DispatcherTimer timer = new(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timedOut = true; frame.Continue = false; };
        _ = dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(!timedOut, "settings and position refresh queues settle without an endless dispatcher cycle");
    }

    private static void Equal(NativePoint expected, NativePoint actual, string message)
        => Require(expected.X == actual.X && expected.Y == actual.Y,
            $"{message}: expected ({expected.X},{expected.Y}), actual ({actual.X},{actual.Y})");
    private static void Near(double expected, double actual, string message)
        => Require(double.IsFinite(actual) && Math.Abs(expected - actual) <= 1e-8,
            $"{message}: expected {expected:R}, actual {actual:R}");
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Window work-area native: " + message); }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmSettingChange = 0x001A;
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
}
