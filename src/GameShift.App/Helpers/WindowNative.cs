using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace GameShift.App.Helpers;

/// <summary>
/// Win32 window queries and DWM frame settings shared by the app's windows.
/// </summary>
internal static class WindowNative
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// Asks DWM for a dark or a light title bar. Creates the window handle if it does not exist yet.
    /// </summary>
    public static void SetDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // Silently ignore on older Windows versions
        }
    }

    /// <summary>
    /// The work area of the monitor the window is on, in the window's device-independent units.
    /// False when the window has no handle yet or the monitor cannot be read.
    /// </summary>
    /// <remarks>
    /// SystemParameters.WorkArea describes the primary monitor only.
    /// </remarks>
    public static bool TryGetWorkArea(Window window, out Rect workArea)
    {
        workArea = Rect.Empty;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            var toDip = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformFromDevice;
            if (toDip is not { } transform) return false;

            var r = info.rcWork;
            var area = new Rect(transform.Transform(new Point(r.Left, r.Top)),
                                transform.Transform(new Point(r.Right, r.Bottom)));
            if (area.Width <= 0 || area.Height <= 0) return false;

            workArea = area;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the work area of the window's monitor");
            return false;
        }
    }
}
