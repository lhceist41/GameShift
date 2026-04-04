using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.System;

/// <summary>
/// Manages Windows startup registration via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Per-user, no admin required for reading (admin available for writing since app runs elevated).
/// Static utility class consistent with AdminHelper pattern.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppName = "GameShift";

    /// <summary>
    /// Returns the current executable path suitable for the registry value.
    /// Uses Environment.ProcessPath (preferred in .NET 6+, works with single-file publish).
    /// </summary>
    private static string GetExePath()
    {
        return Environment.ProcessPath ??
               global::System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ??
               "GameShift.exe";
    }

    /// <summary>
    /// Sets whether GameShift should start with Windows.
    /// Creates or removes the HKCU Run registry entry.
    /// </summary>
    /// <param name="enable">true to register for startup, false to unregister</param>
    public static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                Log.Warning("Could not open HKCU Run registry key");
                return;
            }

            if (enable)
            {
                var exePath = GetExePath();
                key.SetValue(AppName, $"\"{exePath}\"");
                EnsureStartupApproved(true);
                Log.Information("Registered GameShift for Windows startup: {Path}", exePath);
            }
            else
            {
                // Only delete if it exists (avoid exception)
                if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, throwOnMissingValue: false);
                    EnsureStartupApproved(false);
                    Log.Information("Unregistered GameShift from Windows startup");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to {Action} Windows startup registration",
                enable ? "set" : "remove");
        }
    }

    /// <summary>
    /// Checks if GameShift is currently registered to start with Windows.
    /// Reads from HKCU Run registry key.
    /// </summary>
    /// <returns>true if registered, false otherwise</returns>
    public static bool IsRegisteredForStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check Windows startup registration");
            return false;
        }
    }

    /// <summary>
    /// Windows 11 uses the StartupApproved\Run key to gate whether Run entries
    /// actually launch. A missing or disabled entry silently blocks startup.
    /// </summary>
    private static void EnsureStartupApproved(bool enable)
    {
        try
        {
            if (enable)
            {
                using var key = Registry.CurrentUser.CreateSubKey(StartupApprovedKeyPath);
                // 12-byte value: first DWORD 02 = enabled, remaining 8 bytes zero
                byte[] enabled = [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
                key.SetValue(AppName, enabled, RegistryValueKind.Binary);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true);
                key?.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update StartupApproved registry entry");
        }
    }
}
