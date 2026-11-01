using System;
using System.Collections.Generic;
using System.IO;

namespace GameShift.Core.Updates;

/// <summary>What a starting copy does about an installed copy registered on this PC.</summary>
public enum HandoverKind
{
    /// <summary>Nothing to hand over: this is the installed copy, or nothing is installed.</summary>
    None,

    /// <summary>Offer to start the trusted installed copy at <see cref="StartupHandover.InstalledExePath"/>, then exit.</summary>
    OfferInstalledCopy,

    /// <summary>A logon start of a portable copy while a trusted install exists: the logon task belongs to that install.</summary>
    ExitQuietly,

    /// <summary>An install is registered but not trusted; keep running and tell the user why nothing is offered.</summary>
    InstallRefused
}

/// <param name="InstalledExePath">The exe the trust check passed, set only for <see cref="HandoverKind.OfferInstalledCopy"/>.
/// It is the only path that may be started.</param>
/// <param name="RefusedMessage">One plain line for the user, set only for <see cref="HandoverKind.InstallRefused"/>.</param>
/// <param name="Reasons">Why the install was refused, for the log.</param>
public sealed record StartupHandover(
    HandoverKind Kind,
    string? InstalledExePath = null,
    string? RefusedMessage = null,
    IReadOnlyList<string>? Reasons = null)
{
    public static StartupHandover None { get; } = new(HandoverKind.None);
}

/// <summary>What the installed copy says about the portable exe it replaced.</summary>
/// <param name="ClearRecord">The recorded path has been dealt with and is to be removed from the settings.</param>
/// <param name="PathToMention">The old exe, still present, to name in a one-time note; null for no note.</param>
public sealed record OldPortableCopyNote(bool ClearRecord, string? PathToMention);

/// <summary>
/// Decides how a portable copy and an installed copy hand over to each other at startup. Pure: the
/// file and trust checks are passed in.
/// </summary>
public static class InstallHandover
{
    /// <summary>Name of the app exe in the install folder ({app}\GameShift.App.exe in installer/GameShift.iss).</summary>
    public const string AppExeName = UpdateAssetSelector.PortableAssetName;

    /// <summary>Production checks: the exe must exist and its install must be admin-only.</summary>
    public static StartupHandover DecideAtStart(InstallationInfo install, bool startedAtLogon) =>
        DecideAtStart(install, startedAtLogon, File.Exists, InstallFolderTrust.Check);

    public static StartupHandover DecideAtStart(
        InstallationInfo install,
        bool startedAtLogon,
        Func<string, bool> exeExists,
        Func<string, InstallFolderTrustResult> checkInstall)
    {
        var installDir = install.RegisteredInstallDirectory;
        if (install.Mode == InstallMode.Installed || installDir == null)
            return StartupHandover.None;

        // Detect already found this copy's own folder untrusted; starting "the installed copy" would start this exe again.
        var exeDir = InstallationDetector.NormalizeDirectory(GetDirectoryOrNull(install.ExePath));
        if (exeDir != null && string.Equals(exeDir, installDir, StringComparison.OrdinalIgnoreCase))
            return Refused(installDir, "the running copy's own install folder is not admin-only");

        var installedExe = Path.Combine(installDir, AppExeName);
        if (!exeExists(installedExe))
            return new StartupHandover(HandoverKind.InstallRefused,
                RefusedMessage: $"GameShift is installed in {installDir}, but its program file is missing there, so this copy does not start it. Run the installer again to repair it.",
                Reasons: new[] { $"{AppExeName} is missing" });

        InstallFolderTrustResult trust;
        try
        {
            trust = checkInstall(installedExe);
        }
        catch (Exception ex)
        {
            trust = InstallFolderTrustResult.Untrusted($"the check failed: {ex.Message}");
        }

        if (!trust.Trusted)
            return Refused(installDir, trust.Reasons.Count > 0 ? trust.Reasons : new[] { "not admin-only" });

        return startedAtLogon
            ? new StartupHandover(HandoverKind.ExitQuietly)
            : new StartupHandover(HandoverKind.OfferInstalledCopy, InstalledExePath: installedExe);
    }

    /// <summary>
    /// A portable copy with nothing installed offers to install GameShift, once, and not on a logon
    /// start, where a dialog would greet the user before they asked for the app.
    /// </summary>
    public static bool ShouldOfferInstall(InstallationInfo install, bool offerShown, bool startedAtLogon) =>
        install.Mode == InstallMode.Portable &&
        install.RegisteredInstallDirectory == null &&
        !offerShown &&
        !startedAtLogon;

    /// <summary>
    /// The installed copy names the portable exe it replaced once, if that file is still there. The
    /// path comes from user-writable settings, so it is only shown, never opened or deleted.
    /// </summary>
    public static OldPortableCopyNote DecideOldCopyNote(
        InstallationInfo install, string? recordedExePath, Func<string, bool> fileExists)
    {
        if (install.Mode != InstallMode.Installed || string.IsNullOrWhiteSpace(recordedExePath))
            return new OldPortableCopyNote(false, null);

        var oldDir = InstallationDetector.NormalizeDirectory(GetDirectoryOrNull(recordedExePath));
        if (oldDir == null ||
            string.Equals(oldDir, install.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
            !fileExists(recordedExePath))
            return new OldPortableCopyNote(true, null);

        return new OldPortableCopyNote(true, Path.Combine(oldDir, Path.GetFileName(recordedExePath)));
    }

    private static StartupHandover Refused(string installDir, string reason) =>
        Refused(installDir, new[] { reason });

    private static StartupHandover Refused(string installDir, IReadOnlyList<string> reasons) =>
        new(HandoverKind.InstallRefused,
            RefusedMessage: $"GameShift is installed in {installDir}, but that folder is not protected from changes by other users, so this copy does not start it or update through it.",
            Reasons: reasons);

    private static string? GetDirectoryOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetDirectoryName(path.Trim()); }
        catch { return null; }
    }
}
