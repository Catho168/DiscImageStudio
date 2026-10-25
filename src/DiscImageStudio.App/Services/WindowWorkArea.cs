using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DiscImageStudio.Services;

/// <summary>Keeps custom window chrome inside the current monitor's usable area.</summary>
internal sealed class WindowWorkArea : IDisposable
{
    private readonly Window _window;
    private readonly Size _preferredMinimum;
    private HwndSource? _source;
    private bool _refreshQueued;
    private bool _updatingMinimum;
    private bool _positioning;
    private bool _disposed;

    internal WindowWorkArea(Window window)
    {
        _window = window;
        _preferredMinimum = new(window.MinWidth, window.MinHeight);
        window.SourceInitialized += SourceInitialized;
        window.StateChanged += StateChanged;
    }

    internal static WindowWorkAreaBounds Calculate(Rect monitorPixels, Rect workAreaPixels,
        Size minimumSizeDip, DpiScale dpi)
    {
        Size maximum = workAreaPixels.Size;
        Size minimum = new(Math.Min(maximum.Width, Math.Ceiling(minimumSizeDip.Width * dpi.DpiScaleX)),
            Math.Min(maximum.Height, Math.Ceiling(minimumSizeDip.Height * dpi.DpiScaleY)));
        return new(new(workAreaPixels.Left - monitorPixels.Left, workAreaPixels.Top - monitorPixels.Top),
            maximum, minimum, new(minimum.Width / dpi.DpiScaleX, minimum.Height / dpi.DpiScaleY));
    }

    private void SourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WindowProc);
        Refresh();
    }

    private void StateChanged(object? sender, EventArgs e) => QueueRefresh();

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo && lParam != IntPtr.Zero
            && TryGetBounds(hwnd, out _, out WindowWorkAreaBounds bounds))
        {
            UpdateMinimum(bounds.MinimumSizeDip);
            MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaxPosition = new((int)bounds.MaxPosition.X, (int)bounds.MaxPosition.Y);
            info.MaxSize = new((int)bounds.MaxSize.Width, (int)bounds.MaxSize.Height);
            Marshal.StructureToPtr(info, lParam, false);
            // Leave handled false and preserve both tracking fields: WPF still needs
            // to enforce the window's (work-area-constrained) MinWidth/MinHeight.
            // WM_GETMINMAXINFO uses device pixels, not WPF DIPs:
            // https://learn.microsoft.com/windows/win32/winmsg/wm-getminmaxinfo
        }
        else if (message is WmDpiChanged or WmSettingChange or WmDisplayChange
            || (message == WmWindowPosChanged && !_positioning))
        {
            // Wait for WPF to apply the new DPI/state before adjusting constraints.
            QueueRefresh();
        }
        return IntPtr.Zero;
    }

    private void QueueRefresh()
    {
        if (_disposed || _refreshQueued) return;
        _refreshQueued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _refreshQueued = false;
            if (!_disposed) Refresh();
        }));
    }

    private void Refresh()
    {
        if (_source is null || _source.IsDisposed
            || !TryGetBounds(_source.Handle, out NativeRect work, out WindowWorkAreaBounds bounds)) return;
        UpdateMinimum(bounds.MinimumSizeDip);
        // Work-area changes while already maximized need not generate another
        // maximize command. Re-query after monitor/DPI/taskbar changes as well.
        if (_window.WindowState == WindowState.Maximized
            && GetWindowRect(_source.Handle, out NativeRect current)
            && (current.Left != work.Left || current.Top != work.Top
                || current.Right != work.Right || current.Bottom != work.Bottom))
        {
            _positioning = true;
            try
            {
                _ = SetWindowPos(_source.Handle, IntPtr.Zero, work.Left, work.Top,
                    work.Right - work.Left, work.Bottom - work.Top, SwpNoZOrder | SwpNoActivate);
            }
            finally { _positioning = false; }
        }
    }

    private void UpdateMinimum(Size minimum)
    {
        if (_updatingMinimum) return;
        _updatingMinimum = true;
        try
        {
            if (_window.MinWidth != minimum.Width) _window.MinWidth = minimum.Width;
            if (_window.MinHeight != minimum.Height) _window.MinHeight = minimum.Height;
        }
        finally { _updatingMinimum = false; }
    }

    private bool TryGetBounds(IntPtr hwnd, out NativeRect work, out WindowWorkAreaBounds bounds)
    {
        work = default;
        bounds = default;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)
            || info.Work.Right <= info.Work.Left || info.Work.Bottom <= info.Work.Top) return false;
        work = info.Work;
        DpiScale dpi = VisualTreeHelper.GetDpi(_window);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            uint nativeDpi = GetDpiForWindow(hwnd);
            if (nativeDpi > 0) dpi = new(nativeDpi / 96.0, nativeDpi / 96.0);
        }
        bounds = Calculate(info.Monitor.ToRect(), work.ToRect(), _preferredMinimum, dpi);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= SourceInitialized;
        _window.StateChanged -= StateChanged;
        if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProc);
        _source = null;
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmDpiChanged = 0x02E0;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

internal readonly record struct WindowWorkAreaBounds(Point MaxPosition, Size MaxSize,
    Size MinTrackSize, Size MinimumSizeDip);
