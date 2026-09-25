using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// Updates an installed copy by running the release installer. The installer is downloaded into a new
/// folder under &lt;install dir&gt;\updates, which inherits the install folder's admin-only ACL (Program
/// Files), so nothing a standard user writes can take its place. Nothing else is written: the expected
/// hash stays in memory, and the staged file is hashed again right before it runs.
/// </summary>
public static class InstallerUpdate
{
    /// <summary>Folder under the install directory that holds staged installers.</summary>
    public const string UpdatesFolderName = "updates";

    /// <summary>
    /// Installer command line: no wizard, no message boxes, no reboot, and close GameShift if it is
    /// still running. /RELAUNCH=1 asks the installer to start GameShift again when it is done; other
    /// silent installs leave it closed.
    /// </summary>
    public const string InstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1";

    private const string Sha256Prefix = "sha256:";

    /// <summary>&lt;install dir&gt;\updates</summary>
    public static string GetUpdatesRoot(string installDirectory) =>
        Path.Combine(installDirectory, UpdatesFolderName);

    /// <summary>
    /// Creates a new, randomly named folder under &lt;install dir&gt;\updates and returns the path the
    /// installer should be downloaded to inside it.
    /// </summary>
    public static string CreateStagingPath(string installDirectory)
    {
        var folderName = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var stagingDir = Path.Combine(GetUpdatesRoot(installDirectory), folderName);
        Directory.CreateDirectory(stagingDir);
        return Path.Combine(stagingDir, UpdateAssetSelector.InstallerAssetName);
    }

    /// <summary>
    /// Verifies the staged installer against <paramref name="expectedSha256"/> and starts it with
    /// <see cref="InstallerArguments"/>. The caller MUST shut the app down when this returns true, so the
    /// installer can replace its files. Returns false, without starting anything, if the path is not a
    /// staged installer of this install, the digest is missing, or the hash does not match.
    /// </summary>
    public static bool Launch(string installerPath, string installDirectory, string? expectedSha256) =>
        Launch(installerPath, installDirectory, expectedSha256, StartProcess);

    internal static bool Launch(
        string installerPath,
        string installDirectory,
        string? expectedSha256,
        Func<ProcessStartInfo, bool> startProcess)
    {
        try
        {
            if (!IsStagedInstallerPath(installerPath, installDirectory))
            {
                Log.Error("InstallerUpdate: Refusing to run {Path}: not a staged installer under {Root}",
                    installerPath, GetUpdatesRoot(installDirectory));
                return false;
            }

            if (!TryParseSha256(expectedSha256, out var expectedHex))
            {
                Log.Error("InstallerUpdate: No valid SHA-256 digest for the staged installer - refusing to run it");
                DiscardStaged(installerPath, installDirectory);
                return false;
            }

            bool hashMatched;
            bool started = false;

            // Hold the file open without write or delete sharing from the hash until the process has
            // started, so the bytes that run are the bytes that were hashed.
            using (var stream = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actualHex = Convert.ToHexString(SHA256.HashData(stream));
                hashMatched = actualHex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);

                if (!hashMatched)
                {
                    Log.Error("InstallerUpdate: Staged installer hash mismatch (expected {Expected}, got {Actual}) - refusing to run it",
                        expectedHex, actualHex);
                }
                else
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = InstallerArguments,
                        WorkingDirectory = Path.GetDirectoryName(installerPath)!,
                        UseShellExecute = false
                    };
                    started = startProcess(psi);
                }
            }

            if (!hashMatched)
            {
                DiscardStaged(installerPath, installDirectory);
                return false;
            }

            if (!started)
            {
                Log.Error("InstallerUpdate: Could not start the installer at {Path}", installerPath);
                return false;
            }

            Log.Information("InstallerUpdate: Installer started from {Path}, app will now exit", installerPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "InstallerUpdate: Failed to verify or start the staged installer");
            return false;
        }
    }

    /// <summary>
    /// Deletes the staging folder of <paramref name="installerPath"/> (after a failed download or a
    /// refused launch). Does nothing unless the path is a staged installer of this install. Best effort.
    /// </summary>
    public static void DiscardStaged(string installerPath, string installDirectory)
    {
        try
        {
            if (!IsStagedInstallerPath(installerPath, installDirectory)) return;

            var stagingDir = Path.GetDirectoryName(Path.GetFullPath(installerPath));
            if (stagingDir != null && Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "InstallerUpdate: Could not delete staging folder of {Path}", installerPath);
        }
    }

    /// <summary>
    /// Removes &lt;install dir&gt;\updates left by an earlier update. Call at startup. A folder that is
    /// still in use (the installer relaunches GameShift before it exits) is left for the next start.
    /// </summary>
    public static void CleanupStaging(string installDirectory)
    {
        var root = GetUpdatesRoot(installDirectory);
        try
        {
            if (!Directory.Exists(root)) return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "InstallerUpdate: Could not remove {Path} yet", entry);
                }
            }

            Directory.Delete(root);
            Log.Debug("InstallerUpdate: Cleaned up {Root}", root);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "InstallerUpdate: Cleanup of {Root} incomplete (non-fatal)", root);
        }
    }

    /// <summary>
    /// True only for &lt;install dir&gt;\updates\&lt;one folder&gt;\GameShift-Setup.exe.
    /// </summary>
    internal static bool IsStagedInstallerPath(string installerPath, string installDirectory)
    {
        var installDir = InstallationDetector.NormalizeDirectory(installDirectory);
        if (installDir == null || string.IsNullOrWhiteSpace(installerPath) ||
            !Path.IsPathFullyQualified(installerPath))
            return false;

        var fullPath = Path.GetFullPath(installerPath);
        if (!Path.GetFileName(fullPath).Equals(UpdateAssetSelector.InstallerAssetName, StringComparison.OrdinalIgnoreCase))
            return false;

        var stagingDir = Path.GetDirectoryName(fullPath);
        var updatesRoot = stagingDir == null ? null : Path.GetDirectoryName(stagingDir);
        return updatesRoot != null &&
               string.Equals(updatesRoot, GetUpdatesRoot(installDir), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses "sha256:&lt;64 hex chars&gt;" as reported by the GitHub releases API.</summary>
    internal static bool TryParseSha256(string? digest, out string hex)
    {
        hex = "";
        if (string.IsNullOrWhiteSpace(digest) ||
            !digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = digest[Sha256Prefix.Length..].Trim();
        if (candidate.Length != 64 || !IsHex(candidate))
            return false;

        hex = candidate;
        return true;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    private static bool StartProcess(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi);
        return process != null;
    }
}
