using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace GameShift.Core.System;

/// <summary>
/// Provides utilities for checking and requesting administrator privileges (UAC elevation).
/// </summary>
public static class AdminHelper
{
    /// <summary>
    /// Checks if the current process is running with administrator privileges.
    /// Uses WindowsPrincipal to verify the user is in the Administrator role.
    /// </summary>
    /// <returns>True if running as administrator, false otherwise.</returns>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // If we can't determine admin status, assume not admin for safety
            return false;
        }
    }

    /// <summary>
    /// Attempts to restart the current application with administrator privileges.
    /// Shows the UAC prompt to the user and restarts the executable if approved.
    /// </summary>
    /// <returns>
    /// True if restart was initiated successfully, false if user declined UAC or error occurred.
    /// </returns>
    public static bool RestartAsAdmin()
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine current executable path"),
                UseShellExecute = true,
                Verb = "runas" // This triggers the UAC elevation prompt
            };

            Process.Start(processInfo);
            return true;
        }
        catch (SecurityException)
        {
            // User clicked "No" on UAC prompt
            return false;
        }
        catch (Exception)
        {
            // Other errors (e.g., executable not found, permissions issues)
            return false;
        }
    }
}
