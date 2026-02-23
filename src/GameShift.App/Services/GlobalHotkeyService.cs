using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace GameShift.App.Services;

/// <summary>
/// Manages system-wide global hotkey registration using Win32 RegisterHotKey/UnregisterHotKey.
/// Uses an HwndSource hook to receive WM_HOTKEY messages without requiring app window focus.
/// Provides a global hotkey toggle for optimization pause state.
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9001; // Unique ID for GameShift hotkey

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Modifier key constants (Win32 API)
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private IntPtr _windowHandle;
    private HwndSource? _hwndSource;
    private bool _isRegistered;
    private bool _disposed;

    /// <summary>Fires when the registered hotkey is pressed.</summary>
    public event Action? HotkeyPressed;

    /// <summary>
    /// Registers the global hotkey. Must be called after a WPF window handle is available.
    /// </summary>
    /// <param name="window">Any visible WPF window used to hook WndProc for WM_HOTKEY messages</param>
    /// <param name="hotkeyBinding">Hotkey string like "Ctrl+Shift+G" (format: modifiers + key separated by +)</param>
    /// <returns>True if registration succeeded, false otherwise (non-fatal — app continues without hotkey)</returns>
    public bool Register(Window window, string hotkeyBinding)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            _windowHandle = helper.Handle;

            if (_windowHandle == IntPtr.Zero)
            {
                Log.Warning("GlobalHotkeyService: Window handle is zero, cannot register hotkey");
                return false;
            }

            _hwndSource = HwndSource.FromHwnd(_windowHandle);
            _hwndSource?.AddHook(WndProc);

            // Parse the binding string into Win32 modifier flags + virtual key code
            if (!TryParseBinding(hotkeyBinding, out uint modifiers, out uint vk))
            {
                Log.Warning("GlobalHotkeyService: Failed to parse hotkey binding '{Binding}'", hotkeyBinding);
                return false;
            }

            // MOD_NOREPEAT prevents repeated events when key is held down
            _isRegistered = RegisterHotKey(_windowHandle, HOTKEY_ID, modifiers | MOD_NOREPEAT, vk);

            if (_isRegistered)
            {
                Log.Information("GlobalHotkeyService: Registered hotkey '{Binding}'", hotkeyBinding);
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                Log.Warning(
                    "GlobalHotkeyService: Failed to register hotkey '{Binding}' (Win32 error {Error}). " +
                    "Another app may be using this hotkey combination.",
                    hotkeyBinding, error);
            }

            return _isRegistered;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GlobalHotkeyService: Exception during hotkey registration");
            return false;
        }
    }

    /// <summary>Unregisters the hotkey and removes the WndProc hook.</summary>
    public void Unregister()
    {
        if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            _isRegistered = false;
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    /// <summary>
    /// Parses a binding string like "Ctrl+Shift+G" into Win32 modifier flags and virtual key code.
    /// Supported modifiers: Ctrl, Control, Shift, Alt.
    /// Supported keys: single alphanumeric characters (A-Z, 0-9).
    /// </summary>
    private static bool TryParseBinding(string binding, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(binding)) return false;

        var parts = binding.Split('+', StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= MOD_CONTROL;
                    break;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    break;
                case "ALT":
                    modifiers |= MOD_ALT;
                    break;
                default:
                    // Single alphanumeric character — use its ASCII/Unicode value as virtual key
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                    {
                        vk = (uint)char.ToUpperInvariant(part[0]);
                    }
                    else
                    {
                        // Unrecognized key — could extend with full VK_ lookup table in future
                        Log.Warning(
                            "GlobalHotkeyService: Unsupported key '{Key}' in binding '{Binding}'",
                            part, binding);
                        return false;
                    }
                    break;
            }
        }

        return modifiers > 0 && vk > 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            Log.Debug("GlobalHotkeyService: Hotkey WM_HOTKEY received, invoking HotkeyPressed");
            HotkeyPressed?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }
}
