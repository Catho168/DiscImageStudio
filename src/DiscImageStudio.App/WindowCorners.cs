using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiscImageStudio;

/// <summary>Win11 rounded corners for the app's borderless windows, which draw square corners
/// on their own. One recipe for every window so a new dialog cannot drift into a square one;
/// call from SourceInitialized, when the handle exists.</summary>
internal static class WindowCorners
{
    private const int DwmWindowAttributeCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    internal static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int preference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowAttributeCornerPreference, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
