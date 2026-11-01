using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// Removes what the in-place updater of 3.9.0 and earlier left next to the exe. A 3.9.0 portable copy
/// still replaces itself that way when it updates to a newer release.
/// </summary>
public static class UpdateApplier
{
    /// <summary>
    /// Gets the full path of the currently running executable.
    /// </summary>
    public static string GetCurrentExePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine current executable path");
    }

    /// <summary>
    /// Cleans up leftover update artifacts from a previous update.
    /// Call at app startup.
    /// </summary>
    public static void CleanupPreviousUpdate() => CleanupPreviousUpdate(GetCurrentExePath);

    internal static void CleanupPreviousUpdate(Func<string> getCurrentExePath)
    {
        try
        {
            var currentExe = getCurrentExePath();
            var currentDir = Path.GetDirectoryName(currentExe)!;

            var updateFile = currentExe + ".update";
            if (File.Exists(updateFile))
            {
                File.Delete(updateFile);
                Log.Debug("UpdateApplier: Cleaned up leftover .update file");
            }

            var sidecar = updateFile + ".sha256";
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
                Log.Debug("UpdateApplier: Cleaned up leftover .update.sha256 sidecar");
            }

            // Clean up any leftover update batch scripts (name includes a random GUID)
            foreach (var batchFile in Directory.GetFiles(currentDir, "gameshift-update-*.cmd"))
            {
                try
                {
                    File.Delete(batchFile);
                    Log.Debug("UpdateApplier: Cleaned up leftover update script {Path}", batchFile);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "UpdateApplier: Cleanup failed (non-fatal)");
        }
    }
}
