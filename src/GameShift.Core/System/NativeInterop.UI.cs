using System.Runtime.InteropServices;

namespace GameShift.Core.System;

/// <summary>
/// UI-related P/Invoke declarations for NativeInterop.
/// Partial class extension for Windows animation and visual effects control.
/// </summary>
public static partial class NativeInterop
{
    // ============================================================
    // user32.dll - User Interface and Animation Control
    // ============================================================

    /// <summary>
    /// Retrieves or sets system-wide parameters including animations.
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-systemparametersinfow
    /// </summary>
    /// <param name="uiAction">System parameter to query or set (e.g., SPI_SETANIMATION)</param>
    /// <param name="uiParam">Parameter value or size, depends on uiAction</param>
    /// <param name="pvParam">Pointer to structure or value, depends on uiAction</param>
    /// <param name="fWinIni">Flags to broadcast changes (e.g., SPIF_SENDCHANGE)</param>
    /// <returns>True if successful, false otherwise</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        IntPtr pvParam,
        uint fWinIni);

    /// <summary>
    /// System parameter action: Sets minimizing and restoring animation.
    /// Used with ANIMATIONINFO structure to enable/disable window animations.
    /// </summary>
    internal const uint SPI_SETANIMATION = 0x0049;

    /// <summary>
    /// System parameter action: Retrieves minimizing and restoring animation setting.
    /// Used with ANIMATIONINFO structure to query current animation state.
    /// </summary>
    internal const uint SPI_GETANIMATION = 0x0048;

    /// <summary>
    /// SystemParametersInfo flag: Broadcasts WM_SETTINGCHANGE to all top-level windows.
    /// Notifies applications that a system parameter has changed.
    /// </summary>
    internal const uint SPIF_SENDCHANGE = 0x0002;

    /// <summary>
    /// Contains information about minimizing and restoring animation.
    /// Used with SPI_GETANIMATION and SPI_SETANIMATION.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ANIMATIONINFO
    {
        /// <summary>Size of this structure in bytes. Must be set before use.</summary>
        public uint cbSize;

        /// <summary>
        /// Animation setting: 0 = animations disabled, nonzero = animations enabled.
        /// </summary>
        public int iMinAnimate;
    }
}
