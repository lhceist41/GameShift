using System.Runtime.InteropServices;

namespace GameShift.Core.System;

/// <summary>
/// Input-related P/Invoke declarations for NativeInterop.
/// Partial class extension for user idle detection.
/// </summary>
public static partial class NativeInterop
{
    // ============================================================
    // user32.dll - User Input Tracking
    // ============================================================

    /// <summary>
    /// Retrieves the time of the last input event (mouse/keyboard).
    /// Used by BackgroundMode to detect user idle state for power plan switching.
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getlastinputinfo
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>
    /// Contains the time of the last input event.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        /// <summary>Size of the structure in bytes. Must be set to Marshal.SizeOf(typeof(LASTINPUTINFO)).</summary>
        public uint cbSize;

        /// <summary>Tick count when the last input event was received (GetTickCount units, wraps at ~49 days).</summary>
        public uint dwTime;
    }
}
