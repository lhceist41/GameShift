using System;
using System.Collections.Generic;

namespace GameShift.Core.Updates;

/// <summary>
/// One asset of a GitHub release, as reported by the releases API.
/// </summary>
/// <param name="Digest">Content digest reported by GitHub ("sha256:&lt;hex&gt;"), or null.</param>
public sealed record ReleaseAsset(string Name, string? DownloadUrl, long Size, string? Digest);

/// <summary>
/// Picks the release asset an update downloads, depending on how this copy is deployed.
/// </summary>
public static class UpdateAssetSelector
{
    /// <summary>
    /// Portable executable. Every release must keep shipping it under exactly this name: updaters in
    /// 3.8.x and earlier take this name first and otherwise fall back to any .exe asset, which would
    /// be the installer.
    /// </summary>
    public const string PortableAssetName = "GameShift.App.exe";

    /// <summary>Portable name used by very old releases, still accepted by the portable updater.</summary>
    public const string LegacyPortableAssetName = "GameShift.exe";

    /// <summary>Installer asset, built from installer/GameShift.iss.</summary>
    public const string InstallerAssetName = "GameShift-Setup.exe";

    /// <summary>Stable download link to the installer of the latest release.</summary>
    public const string LatestInstallerUrl =
        "https://github.com/lhceist41/GameShift/releases/latest/download/" + InstallerAssetName;

    /// <summary>
    /// Installed: only the installer, by exact name; with no installer asset there is nothing to install.
    /// Portable: the portable exe by exact name, else another .exe, else a .zip. The portable path moves
    /// the download over the running exe, so it never takes the installer as a fallback.
    /// </summary>
    public static ReleaseAsset? Select(IReadOnlyList<ReleaseAsset> assets, InstallMode mode)
    {
        if (mode == InstallMode.Installed)
        {
            foreach (var asset in assets)
            {
                if (asset.Name.Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                    return asset;
            }
            return null;
        }

        ReleaseAsset? fallbackExe = null;
        ReleaseAsset? fallbackZip = null;

        foreach (var asset in assets)
        {
            if (asset.Name.Equals(PortableAssetName, StringComparison.OrdinalIgnoreCase) ||
                asset.Name.Equals(LegacyPortableAssetName, StringComparison.OrdinalIgnoreCase))
                return asset;

            if (fallbackExe == null &&
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !asset.Name.Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                fallbackExe = asset;

            if (fallbackZip == null && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                fallbackZip = asset;
        }

        return fallbackExe ?? fallbackZip;
    }
}
