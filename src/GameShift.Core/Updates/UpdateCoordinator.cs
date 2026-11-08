using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// A downloaded, verified installer waiting to be run.
/// </summary>
/// <param name="Path">The staged installer.</param>
/// <param name="ExpectedSha256">Digest the installer is re-checked against before it runs.</param>
public sealed record StagedUpdate(string Path, string? ExpectedSha256);

/// <summary>
/// Outcome of <see cref="UpdateCoordinator.DownloadAsync(UpdateInfo, IProgress{double}, CancellationToken)"/>.
/// </summary>
/// <param name="Staged">The verified installer, or null when nothing was staged.</param>
/// <param name="FailureReason">Why nothing was staged, in plain words for the user; null on success.</param>
public sealed record UpdateDownloadResult(StagedUpdate? Staged, string? FailureReason);

/// <summary>
/// What a portable copy needs to know to stage the installer in the default install folder.
/// </summary>
/// <param name="ProgramFilesDirectory">%ProgramFiles%, the parent of the default install folder.</param>
/// <param name="RegisteredInstallDirectory">Install folder the installer registered, or null.</param>
/// <param name="CheckFolder">Decides whether a folder is admin-only (<see cref="InstallFolderTrust.CheckFolder"/>).</param>
internal sealed record PortableUpdateTarget(
    string? ProgramFilesDirectory,
    string? RegisteredInstallDirectory,
    Func<string, InstallFolderTrustResult> CheckFolder)
{
    // Read at each use: an install made while this copy runs must stop it from staging over that install.
    public static PortableUpdateTarget Current => new(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        InstallationDetector.NormalizeDirectory(InstallationDetector.ReadRegisteredInstallLocation()),
        InstallFolderTrust.CheckFolder);
}

internal delegate string? ResolveUpdateFolderWithReason(out string? userReason);

internal delegate Task<bool> DownloadFile(
    string downloadUrl, string targetPath, long expectedSize, string? expectedSha256,
    IProgress<double> progress, CancellationToken ct);

/// <summary>
/// Downloads and runs the release installer. An installed copy stages it inside its install folder; a
/// portable copy stages it in the default install folder (%ProgramFiles%\GameShift), so the installer
/// installs GameShift there and starts the installed copy.
/// </summary>
public static class UpdateCoordinator
{
    /// <summary>Default install folder under %ProgramFiles% ({autopf}\GameShift in installer/GameShift.iss).</summary>
    public const string DefaultInstallFolderName = "GameShift";

    internal const string NoInstallerReason = "This release has no installer GameShift can download.";
    internal const string DownloadFailedReason = "The download failed or could not be verified.";

    /// <summary>
    /// Downloads and verifies the installer into a new staging folder. Nothing is staged if this copy may
    /// not update through the installer, or the download failed or could not be verified; the result then
    /// says why. Throws OperationCanceledException when cancelled.
    /// </summary>
    public static Task<UpdateDownloadResult> DownloadAsync(
        UpdateInfo update, IProgress<double> progress, CancellationToken ct) =>
        DownloadAsync(update, progress, ct,
            (out string? userReason) => ResolveUpdateFolder(update.Mode, PortableUpdateTarget.Current, create: true, out userReason),
            UpdateDownloader.DownloadAsync);

    internal static async Task<UpdateDownloadResult> DownloadAsync(
        UpdateInfo update, IProgress<double> progress, CancellationToken ct,
        ResolveUpdateFolderWithReason resolveUpdateFolder, DownloadFile download)
    {
        if (string.IsNullOrEmpty(update.DownloadUrl)) return new UpdateDownloadResult(null, NoInstallerReason);

        if (!string.Equals(update.AssetName, UpdateAssetSelector.InstallerAssetName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error("UpdateCoordinator: Update asset is not the installer ({Asset}) - not downloading it", update.AssetName);
            return new UpdateDownloadResult(null, NoInstallerReason);
        }

        var installDir = resolveUpdateFolder(out var refusal);
        if (installDir == null) return new UpdateDownloadResult(null, refusal ?? DownloadFailedReason);

        var installerPath = InstallerUpdate.CreateStagingPath(installDir);
        try
        {
            var downloaded = await download(
                update.DownloadUrl, installerPath, update.DownloadSize, update.ExpectedSha256, progress, ct);

            if (downloaded)
                return new UpdateDownloadResult(new StagedUpdate(installerPath, update.ExpectedSha256), null);

            InstallerUpdate.DiscardStaged(installerPath, installDir);
            return new UpdateDownloadResult(null, DownloadFailedReason);
        }
        catch
        {
            InstallerUpdate.DiscardStaged(installerPath, installDir);
            throw;
        }
    }

    /// <summary>
    /// Starts the staged installer. The caller MUST shut the app down when this returns true.
    /// </summary>
    public static bool Apply(StagedUpdate staged) =>
        Apply(staged,
            () => ResolveUpdateFolder(InstallationDetector.Current.Mode, PortableUpdateTarget.Current, create: false),
            InstallerUpdate.Launch);

    internal static bool Apply(
        StagedUpdate staged, Func<string?> resolveUpdateFolder, Func<string, string, string?, bool> launch)
    {
        // Checked again: the folder decides what runs elevated, and time has passed since the download.
        var installDir = resolveUpdateFolder();
        if (installDir == null)
        {
            Log.Error("UpdateCoordinator: No trusted folder for the staged installer - refusing to run it");
            return false;
        }

        return launch(staged.Path, installDir, staged.ExpectedSha256);
    }

    /// <summary>
    /// <see cref="Apply(StagedUpdate)"/>, and on a portable copy the running exe is recorded first, so
    /// the installed copy names the old file once. The caller MUST shut the app down when this returns true.
    /// </summary>
    public static bool ApplyRememberingPortableCopy(StagedUpdate staged) =>
        ApplyRememberingPortableCopy(InstallationDetector.Current, Environment.ProcessPath, () => Apply(staged));

    /// <summary>
    /// Records <paramref name="exePath"/> before <paramref name="apply"/> on a portable copy and removes
    /// the record again when the installer did not start or <paramref name="apply"/> threw; the exception
    /// still reaches the caller.
    /// </summary>
    internal static bool ApplyRememberingPortableCopy(InstallationInfo install, string? exePath, Func<bool> apply)
    {
        if (install.Mode != InstallMode.Portable || string.IsNullOrWhiteSpace(exePath))
            return apply();

        // The record only feeds a note, so a settings file that cannot be written does not stop the update.
        var recorded = TryUpdateSettings(s => s.MigratedPortableExePath = exePath);
        var started = false;
        try
        {
            started = apply();
            return started;
        }
        finally
        {
            if (!started && recorded)
                TryUpdateSettings(s => { if (s.MigratedPortableExePath == exePath) s.MigratedPortableExePath = null; });
        }
    }

    private static bool TryUpdateSettings(Action<AppSettings> mutate)
    {
        try
        {
            SettingsManager.Update(mutate);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UpdateCoordinator: Could not save the portable copy's path for the old-copy note");
            return false;
        }
    }

    /// <summary>
    /// Removes leftovers of an earlier update: the files the in-place updater of 3.9.0 and earlier left
    /// next to the exe, and the updates folder of the install or, when none is registered, of the default
    /// install folder, which is removed too once it is empty. Call at startup.
    /// </summary>
    public static void CleanupPreviousUpdate()
    {
        UpdateApplier.CleanupPreviousUpdate();

        var installDir = InstallationDetector.Current.InstallDirectory;
        if (installDir != null)
            InstallerUpdate.CleanupStaging(installDir);
        else
            CleanupPortableStaging(PortableUpdateTarget.Current);
    }

    internal static void CleanupPortableStaging(PortableUpdateTarget target)
    {
        if (target.RegisteredInstallDirectory != null) return;

        var folder = GetPortableUpdateFolder(target, create: false, out var refusal);
        if (folder == null)
        {
            if (refusal != null)
                Log.Warning("UpdateCoordinator: Not cleaning up the default install folder: {Reason}", refusal);
            return;
        }

        InstallerUpdate.CleanupStaging(folder);
        InstallerUpdate.RemoveFolderIfEmpty(Path.GetDirectoryName(folder)!, DefaultInstallFolderName);
    }

    internal static string? ResolveUpdateFolder(InstallMode mode, PortableUpdateTarget target, bool create) =>
        ResolveUpdateFolder(mode, target, create, out _);

    /// <param name="userReason">Why there is no folder, in plain words for the user; the log gets the details.</param>
    internal static string? ResolveUpdateFolder(InstallMode mode, PortableUpdateTarget target, bool create, out string? userReason)
    {
        userReason = null;
        if (mode == InstallMode.Installed)
        {
            var installDir = InstallationDetector.Current.InstallDirectory;
            if (installDir == null)
            {
                Log.Error("UpdateCoordinator: Installed-mode update without an install folder");
                userReason = "GameShift could not find its install folder.";
            }
            return installDir;
        }

        var folder = GetPortableUpdateFolder(target, create, out var refusal);
        if (folder == null)
        {
            Log.Warning("UpdateCoordinator: This portable copy cannot update through the installer: {Reason}",
                refusal ?? "the default install folder does not exist");
            userReason = target.RegisteredInstallDirectory != null
                ? $"GameShift is installed in {target.RegisteredInstallDirectory}. Only the copy in that folder installs updates, and only while the folder is protected so that just administrators can change it."
                : "GameShift could not prepare a protected folder under Program Files for the installer.";
        }
        return folder;
    }

    /// <summary>
    /// The default install folder, once %ProgramFiles% and the folder itself are admin-only and no install
    /// is registered; with <paramref name="create"/> it is created first if missing. Null with the reason
    /// otherwise, or with no reason when it is missing and was not to be created.
    /// </summary>
    internal static string? GetPortableUpdateFolder(PortableUpdateTarget target, bool create, out string? refusal)
    {
        refusal = null;
        if (target.RegisteredInstallDirectory != null)
        {
            refusal = $"GameShift is installed in {target.RegisteredInstallDirectory}";
            return null;
        }

        var programFiles = InstallationDetector.NormalizeDirectory(target.ProgramFilesDirectory);
        if (programFiles == null)
        {
            refusal = "the Program Files folder is unknown";
            return null;
        }

        // Checked before the folder below it is opened or created: its parents are then known not to be
        // replaceable by a standard user, so the path keeps naming the folder that was checked.
        if (!IsAdminOnly(target, programFiles, out refusal))
            return null;

        if (!InstallerUpdate.TryOpenFolder(programFiles, DefaultInstallFolderName, create, out var exists, out refusal) ||
            !exists)
            return null;

        var folder = Path.Combine(programFiles, DefaultInstallFolderName);
        return IsAdminOnly(target, folder, out refusal) ? folder : null;
    }

    private static bool IsAdminOnly(PortableUpdateTarget target, string folder, out string? refusal)
    {
        InstallFolderTrustResult result;
        try
        {
            result = target.CheckFolder(folder);
        }
        catch (Exception ex)
        {
            result = InstallFolderTrustResult.Untrusted($"the check failed: {ex.Message}");
        }

        refusal = result.Trusted ? null : $"{folder} is not admin-only ({string.Join("; ", result.Reasons)})";
        return result.Trusted;
    }
}
