using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// Applies a downloaded update by scheduling a file replacement via cmd.exe.
/// The current process must exit after calling ApplyUpdate() for the replacement to succeed.
/// </summary>
public static class UpdateApplier
{
    /// <summary>
    /// Gets the path where the downloaded update should be saved.
    /// This is next to the running executable with a ".update" extension.
    /// </summary>
    public static string GetUpdateStagingPath()
    {
        return GetCurrentExePath() + ".update";
    }

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
    /// Launches a cmd.exe process that waits for this process to exit,
    /// replaces the running exe with the downloaded update, and relaunches.
    /// The caller MUST call Application.Current.Shutdown() after this returns true.
    /// </summary>
    public static bool ApplyUpdate()
    {
        try
        {
            var currentExe = GetCurrentExePath();
            var updateFile = GetUpdateStagingPath();

            if (!File.Exists(updateFile))
            {
                Log.Error("UpdateApplier: Staged update not found at {Path}", updateFile);
                return false;
            }

            // Re-verify the staged binary against its sidecar hash before moving it over the running
            // elevated exe. The download is verified in-session, but the staged file can persist
            // across sessions in a possibly user-writable directory, so re-check here (fail closed).
            if (!VerifyStagedUpdate())
            {
                Log.Error("UpdateApplier: Staged update failed hash verification - refusing to apply and discarding");
                TryDeleteStaged();
                return false;
            }

            var currentDir = Path.GetDirectoryName(currentExe)!;

            // Escape paths for safe interpolation into a batch script.
            var safeUpdateFile = EscapeForBatch(updateFile);
            var safeCurrentExe = EscapeForBatch(currentExe);

            // Batch script: wait for app to exit, replace exe, relaunch.
            // Uses ping for delay because timeout doesn't work in hidden cmd windows.
            var script = $"""
                @echo off
                echo Waiting for GameShift to exit...
                ping 127.0.0.1 -n 3 > nul
                echo Applying update...
                move /y "{safeUpdateFile}" "{safeCurrentExe}"
                if errorlevel 1 (
                    echo ERROR: Failed to replace executable.
                    del "{safeUpdateFile}" 2>nul
                    timeout /t 5
                    exit /b 1
                )
                echo Update applied. Relaunching...
                start "" "{safeCurrentExe}"
                exit
                """;

            var batchName = $"gameshift-update-{Guid.NewGuid():N}.cmd";
            var batchPath = Path.Combine(currentDir, batchName);
            File.WriteAllText(batchPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = NativeInterop.SystemExePath("cmd.exe"),
                Arguments = $"/c \"{batchPath}\"",
                WorkingDirectory = currentDir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            Process.Start(psi);
            Log.Information("UpdateApplier: Update script launched, app will now exit");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpdateApplier: Failed to launch update process");
            return false;
        }
    }

    /// <summary>
    /// Re-verifies the staged update file against its sidecar hash (written by UpdateDownloader at
    /// verified-download time). Guards the apply path: the staged file is moved over the running
    /// ELEVATED exe and its directory may be user-writable, so it must be re-checked before apply.
    /// Returns false (fail closed) if the staged file or sidecar is missing, empty, or the hash differs.
    /// </summary>
    public static bool VerifyStagedUpdate()
    {
        try
        {
            var updateFile = GetUpdateStagingPath();
            var sidecar = updateFile + ".sha256";
            if (!File.Exists(updateFile) || !File.Exists(sidecar))
                return false;

            var expectedHex = File.ReadAllText(sidecar).Trim();
            if (expectedHex.Length == 0)
                return false;

            using var stream = File.OpenRead(updateFile);
            var actualHex = Convert.ToHexString(SHA256.HashData(stream));
            var match = actualHex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
            if (!match)
                Log.Error("UpdateApplier: Staged update hash mismatch (expected {Expected}, got {Actual})",
                    expectedHex, actualHex);
            return match;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpdateApplier: Failed to verify staged update");
            return false;
        }
    }

    private static void TryDeleteStaged()
    {
        var updateFile = GetUpdateStagingPath();
        try { if (File.Exists(updateFile)) File.Delete(updateFile); } catch { /* best effort */ }
        try { if (File.Exists(updateFile + ".sha256")) File.Delete(updateFile + ".sha256"); } catch { /* best effort */ }
    }

    /// <summary>
    /// Cleans up leftover update artifacts from a previous update.
    /// Call at app startup.
    /// </summary>
    public static void CleanupPreviousUpdate()
    {
        try
        {
            var currentExe = GetCurrentExePath();
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

    /// <summary>
    /// Escapes a file path for safe use inside a double-quoted batch script string.
    /// The % character is the only special char that is interpreted inside double quotes
    /// by cmd.exe, so we escape it as %%.
    /// </summary>
    private static string EscapeForBatch(string path) => path.Replace("%", "%%");
}
