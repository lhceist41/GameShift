using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// Checks for new releases on GitHub using the releases API.
/// Compares the latest tag against the current assembly version.
/// Non-blocking, fire-and-forget - never blocks app startup.
/// </summary>
public static class UpdateChecker
{
    private const string GitHubApiUrl =
        "https://api.github.com/repos/lhceist41/GameShift/releases/latest";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static UpdateChecker()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "GameShift-UpdateChecker");
    }

    /// <summary>
    /// Checks GitHub for a newer release.
    /// Returns the new version string if an update is available, or null if current.
    /// Returns null on any error (network, parse, etc.) - never throws.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            return ParseRelease(response, GetCurrentVersion(), InstallationDetector.Current.Mode);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "UpdateChecker: Check failed (non-fatal)");
            return null;
        }
    }

    /// <summary>
    /// Parses a GitHub "latest release" response. Returns update details when the release is newer
    /// than <paramref name="currentVersion"/>, with the download asset chosen for <paramref name="mode"/>;
    /// returns null when current. Throws on a malformed response.
    /// </summary>
    internal static UpdateInfo? ParseRelease(string json, Version currentVersion, InstallMode mode)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString();
        var htmlUrl = root.GetProperty("html_url").GetString();
        var body = root.TryGetProperty("body", out var bodyProp)
            ? bodyProp.GetString()
            : null;

        if (string.IsNullOrEmpty(tagName)) return null;

        // Strip leading 'v' from tag (e.g., "v1.1.0" → "1.1.0")
        var versionStr = tagName.StartsWith('v') ? tagName[1..] : tagName;

        if (!Version.TryParse(versionStr, out var latestVersion))
        {
            Log.Debug("UpdateChecker: Could not parse version from tag '{Tag}'", tagName);
            return null;
        }

        if (latestVersion <= currentVersion)
        {
            Log.Debug("UpdateChecker: Up to date (current {Current}, latest {Latest})",
                currentVersion, latestVersion);
            return null;
        }

        Log.Information(
            "UpdateChecker: Update available - current {Current}, latest {Latest}",
            currentVersion, latestVersion);

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsArray))
        {
            foreach (var asset in assetsArray.EnumerateArray())
            {
                var assetName = asset.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(assetName)) continue;

                // GitHub reports a per-asset content digest ("sha256:<hex>"). The downloader
                // verifies the downloaded bytes against it before the binary is ever applied.
                var digest = asset.TryGetProperty("digest", out var digestProp)
                    ? digestProp.GetString()
                    : null;

                assets.Add(new ReleaseAsset(
                    assetName,
                    asset.GetProperty("browser_download_url").GetString(),
                    asset.GetProperty("size").GetInt64(),
                    digest));
            }
        }

        var selected = UpdateAssetSelector.Select(assets, mode);
        string? downloadUrl = selected?.DownloadUrl;
        long downloadSize = 0;
        string? downloadDigest = null;
        string? downloadAssetName = null;

        if (selected != null && downloadUrl != null)
        {
            downloadSize = selected.Size;
            downloadDigest = selected.Digest;
            downloadAssetName = selected.Name;
        }
        else if (mode == InstallMode.Portable)
        {
            // Last resort for a portable copy: GitHub's auto-generated source zipball. An installed copy
            // can only install through the installer; without one the UI opens the release page instead.
            downloadUrl = root.TryGetProperty("zipball_url", out var zipballProp)
                ? zipballProp.GetString()
                : null;
        }

        // Validate URLs before returning - reject any that point outside GitHub
        var releaseUrl = htmlUrl ?? $"https://github.com/lhceist41/GameShift/releases/tag/{tagName}";
        if (!GitHubUrlValidator.IsValid(releaseUrl))
        {
            Log.Warning("UpdateChecker: Rejected release URL outside allowed domains: {Url}", releaseUrl);
            return null;
        }
        if (downloadUrl != null && !GitHubUrlValidator.IsValid(downloadUrl))
        {
            Log.Warning("UpdateChecker: Rejected download URL outside allowed domains: {Url}", downloadUrl);
            downloadUrl = null;
            downloadSize = 0;
            downloadDigest = null;
            downloadAssetName = null;
        }

        return new UpdateInfo
        {
            CurrentVersion = currentVersion.ToString(),
            LatestVersion = latestVersion.ToString(),
            TagName = tagName,
            ReleaseUrl = releaseUrl,
            ReleaseNotes = body,
            DownloadUrl = downloadUrl,
            DownloadSize = downloadSize,
            ExpectedSha256 = downloadDigest,
            AssetName = downloadAssetName,
            Mode = mode
        };
    }

    private static Version GetCurrentVersion()
    {
        // Use the entry assembly (GameShift.App.exe) which carries the actual product version,
        // not typeof(UpdateChecker).Assembly which is GameShift.Core.dll.
        var assembly = global::System.Reflection.Assembly.GetEntryAssembly()
                       ?? typeof(UpdateChecker).Assembly;
        var version = assembly.GetName().Version;
        return version ?? new Version(1, 0, 0);
    }
}

/// <summary>
/// Contains information about an available update.
/// </summary>
public class UpdateInfo
{
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string TagName { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string? ReleaseNotes { get; set; }

    /// <summary>
    /// Direct download URL of the selected release asset (see <see cref="AssetName"/>). Null when the
    /// release has nothing this copy can install; the UI then opens <see cref="ReleaseUrl"/> instead.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Name of the release asset behind <see cref="DownloadUrl"/>, or null (e.g. source zipball).</summary>
    public string? AssetName { get; set; }

    /// <summary>Install mode the asset was selected for; decides how the download is applied.</summary>
    public InstallMode Mode { get; set; }

    /// <summary>File size in bytes (from GitHub asset metadata). Used for progress display.</summary>
    public long DownloadSize { get; set; }

    /// <summary>
    /// Expected content digest of the download asset, as reported by the GitHub releases API
    /// (format "sha256:&lt;hex&gt;"). The downloader verifies the downloaded bytes against this
    /// before the binary is staged/applied. Null when no digest is available (then the download
    /// is refused, since the update either replaces the running elevated executable or runs elevated
    /// as the installer).
    /// </summary>
    public string? ExpectedSha256 { get; set; }
}
