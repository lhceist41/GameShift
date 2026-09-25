using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// A downloaded, verified update waiting to be applied.
/// </summary>
/// <param name="Mode">Decides how it is applied: the installer (Installed) or in-place replacement (Portable).</param>
/// <param name="Path">Staged file: the installer, or the portable exe's ".update" file.</param>
/// <param name="ExpectedSha256">Digest the installer is re-checked against before it runs (Installed only;
/// the portable path re-checks against its sidecar).</param>
public sealed record StagedUpdate(InstallMode Mode, string Path, string? ExpectedSha256);

/// <summary>
/// Downloads and applies updates the way this copy is deployed: an installed copy runs the release
/// installer from inside its install folder, a portable copy keeps replacing its own exe.
/// </summary>
public static class UpdateCoordinator
{
    /// <summary>
    /// A verified portable update left staged by an earlier session, or null. Installed copies never keep
    /// a staged installer across sessions; their updates folder is cleared at startup.
    /// </summary>
    public static StagedUpdate? FindStagedUpdateFromPreviousSession()
    {
        if (InstallationDetector.Current.Mode != InstallMode.Portable) return null;

        var path = UpdateApplier.GetUpdateStagingPath();
        return File.Exists(path) ? new StagedUpdate(InstallMode.Portable, path, null) : null;
    }

    /// <summary>
    /// Downloads and verifies the update's asset into the staging location for its install mode.
    /// Returns null if the download failed or could not be verified; throws OperationCanceledException
    /// when cancelled.
    /// </summary>
    public static async Task<StagedUpdate?> DownloadAsync(
        UpdateInfo update, IProgress<double> progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(update.DownloadUrl)) return null;

        if (update.Mode != InstallMode.Installed)
        {
            var target = UpdateApplier.GetUpdateStagingPath();
            var ok = await UpdateDownloader.DownloadAsync(
                update.DownloadUrl, target, update.DownloadSize, update.ExpectedSha256, progress, ct);
            return ok ? new StagedUpdate(InstallMode.Portable, target, null) : null;
        }

        var installDir = InstallationDetector.Current.InstallDirectory;
        if (installDir == null ||
            !string.Equals(update.AssetName, UpdateAssetSelector.InstallerAssetName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error("UpdateCoordinator: Installed-mode update without an install folder or installer asset ({Asset})",
                update.AssetName);
            return null;
        }

        var installerPath = InstallerUpdate.CreateStagingPath(installDir);
        try
        {
            var downloaded = await UpdateDownloader.DownloadAsync(
                update.DownloadUrl, installerPath, update.DownloadSize, update.ExpectedSha256, progress, ct,
                writeHashSidecar: false);

            if (downloaded)
                return new StagedUpdate(InstallMode.Installed, installerPath, update.ExpectedSha256);

            InstallerUpdate.DiscardStaged(installerPath, installDir);
            return null;
        }
        catch
        {
            InstallerUpdate.DiscardStaged(installerPath, installDir);
            throw;
        }
    }

    /// <summary>
    /// Starts applying a staged update. The caller MUST shut the app down when this returns true.
    /// </summary>
    public static bool Apply(StagedUpdate staged)
    {
        if (staged.Mode != InstallMode.Installed)
            return UpdateApplier.ApplyUpdate();

        var installDir = InstallationDetector.Current.InstallDirectory;
        if (installDir == null)
        {
            Log.Error("UpdateCoordinator: Staged installer but this copy is not installed - refusing to run it");
            return false;
        }

        return InstallerUpdate.Launch(staged.Path, installDir, staged.ExpectedSha256);
    }

    /// <summary>
    /// Removes leftovers of an earlier update: the portable ".update"/script files and, for an installed
    /// copy, the updates folder. Call at startup.
    /// </summary>
    public static void CleanupPreviousUpdate()
    {
        UpdateApplier.CleanupPreviousUpdate();

        var installDir = InstallationDetector.Current.InstallDirectory;
        if (installDir != null)
            InstallerUpdate.CleanupStaging(installDir);
    }
}
