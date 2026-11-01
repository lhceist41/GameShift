using System;
using System.Collections.Generic;

namespace GameShift.Core.Updates;

/// <summary>
/// One asset of a GitHub release, as reported by the releases API.
/// </summary>
/// <param name="Digest">Content digest reported by GitHub ("sha256:&lt;hex&gt;"), or null.</param>
public sealed record ReleaseAsset(string Name, string? DownloadUrl, long Size, string? Digest);

/// <summary>
/// Picks the release asset an update downloads.
/// </summary>
public static class UpdateAssetSelector
{
    /// <summary>
    /// Portable executable. Every release must keep shipping it under exactly this name: the portable
    /// updater of 3.9.0 and earlier takes this name first, and 3.8.x and earlier otherwise fall back to
    /// any .exe asset, which would be the installer.
    /// </summary>
    public const string PortableAssetName = "GameShift.App.exe";

    /// <summary>Installer asset, built from installer/GameShift.iss.</summary>
    public const string InstallerAssetName = "GameShift-Setup.exe";

    /// <summary>Stable download link to the installer of the latest release.</summary>
    public const string LatestInstallerUrl =
        "https://github.com/lhceist41/GameShift/releases/latest/download/" + InstallerAssetName;

    /// <summary>
    /// The installer, by exact name, for an installed and a portable copy alike: a portable copy updates
    /// by installing GameShift. Without an installer asset there is nothing to update with.
    /// </summary>
    public static ReleaseAsset? Select(IReadOnlyList<ReleaseAsset> assets)
    {
        foreach (var asset in assets)
        {
            if (asset.Name.Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                return asset;
        }
        return null;
    }
}
