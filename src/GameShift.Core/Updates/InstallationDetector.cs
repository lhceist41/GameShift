using System;
using System.IO;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// How this copy of GameShift was deployed, which decides how it updates.
/// </summary>
public enum InstallMode
{
    /// <summary>Started from a loose GameShift.App.exe, often in a folder standard users can write to.</summary>
    Portable,

    /// <summary>Started from the folder the installer registered (Program Files by default).</summary>
    Installed
}

/// <summary>
/// Result of <see cref="InstallationDetector.Detect"/>.
/// </summary>
/// <param name="Mode">Installed only when the running exe sits in the registered install folder.</param>
/// <param name="ExePath">Path of the running executable, as reported by the process.</param>
/// <param name="RegisteredInstallDirectory">Normalized install folder the installer registered, or null
/// when no installed copy is registered. Set in portable mode too when an installed copy exists elsewhere.</param>
public sealed record InstallationInfo(InstallMode Mode, string? ExePath, string? RegisteredInstallDirectory)
{
    /// <summary>The install folder of the running copy; null unless <see cref="Mode"/> is Installed.</summary>
    public string? InstallDirectory => Mode == InstallMode.Installed ? RegisteredInstallDirectory : null;
}

/// <summary>
/// Tells an installed copy apart from a portable one by comparing the running exe's folder with the
/// install location the installer wrote to its uninstall key. That key lives under HKLM, which standard
/// users cannot write, so a portable copy cannot make itself look installed.
/// </summary>
public static class InstallationDetector
{
    /// <summary>
    /// AppId of the installer (installer/GameShift.iss). Inno Setup registers the install under
    /// Uninstall\&lt;AppId&gt;_is1. It must never change once released, and must match the script.
    /// </summary>
    public const string InstallerAppId = "{E56444A0-53F3-4443-86A2-D7B59168BC56}";

    internal const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + InstallerAppId + "_is1";

    internal const string InstallLocationValueName = "InstallLocation";

    private static readonly Lazy<InstallationInfo> _current =
        new(() => Detect(Environment.ProcessPath, ReadRegisteredInstallLocation()));

    /// <summary>Deployment of the running process. Computed once; it cannot change while the process runs.</summary>
    public static InstallationInfo Current => _current.Value;

    /// <summary>
    /// Decides the install mode from the running exe path and the registered install location.
    /// Anything that cannot be read or compared reliably yields Portable, which keeps the update path
    /// every release before the installer used.
    /// </summary>
    public static InstallationInfo Detect(string? exePath, string? registeredInstallLocation)
    {
        var registeredDir = NormalizeDirectory(registeredInstallLocation);
        var exeDir = NormalizeDirectory(GetDirectoryOrNull(exePath));

        var mode = registeredDir != null && exeDir != null &&
                   string.Equals(exeDir, registeredDir, StringComparison.OrdinalIgnoreCase)
            ? InstallMode.Installed
            : InstallMode.Portable;

        return new InstallationInfo(mode, exePath, registeredDir);
    }

    /// <summary>
    /// Reads InstallLocation from the installer's uninstall key in the 64-bit registry view, where the
    /// 64-bit installer writes it. Returns null when GameShift is not installed or the key is unreadable.
    /// </summary>
    internal static string? ReadRegisteredInstallLocation()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(UninstallKeyPath);
            return key?.GetValue(InstallLocationValueName) as string;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "InstallationDetector: Could not read the installer's uninstall key");
            return null;
        }
    }

    private static string? GetDirectoryOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }

    /// <summary>
    /// Full path without a trailing separator, or null for blank, relative, or malformed input.
    /// Inno Setup writes InstallLocation with a trailing backslash; the exe folder has none. The process
    /// path keeps a \\?\ prefix if it was launched with one, which GetFullPath does not remove.
    /// </summary>
    internal static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var trimmed = path.Trim();
            if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                trimmed = @"\\" + trimmed[@"\\?\UNC\".Length..];
            else if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
                trimmed = trimmed[@"\\?\".Length..];

            if (!Path.IsPathFullyQualified(trimmed)) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch
        {
            return null;
        }
    }
}
