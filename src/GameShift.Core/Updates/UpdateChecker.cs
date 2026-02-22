using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace GameShift.Core.Updates;

/// <summary>
/// Checks for new releases on GitHub using the releases API.
/// Compares the latest tag against the current assembly version.
/// Non-blocking, fire-and-forget — never blocks app startup.
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
    /// Returns null on any error (network, parse, etc.) — never throws.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            using var doc = JsonDocument.Parse(response);
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

            var currentVersion = GetCurrentVersion();

            if (latestVersion > currentVersion)
            {
                Log.Information(
                    "UpdateChecker: Update available — current {Current}, latest {Latest}",
                    currentVersion, latestVersion);

                // Parse download URL from release assets
                string? downloadUrl = null;
                long downloadSize = 0;

                if (root.TryGetProperty("assets", out var assetsArray))
                {
                    foreach (var asset in assetsArray.EnumerateArray())
                    {
                        var assetName = asset.GetProperty("name").GetString();
                        if (assetName != null &&
                            (assetName.Equals("GameShift.App.exe", StringComparison.OrdinalIgnoreCase) ||
                             assetName.Equals("GameShift.exe", StringComparison.OrdinalIgnoreCase)))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            downloadSize = asset.GetProperty("size").GetInt64();
                            break;
                        }
                    }
                }

                return new UpdateInfo
                {
                    CurrentVersion = currentVersion.ToString(),
                    LatestVersion = latestVersion.ToString(),
                    TagName = tagName,
                    ReleaseUrl = htmlUrl ?? $"https://github.com/lhceist41/GameShift/releases/tag/{tagName}",
                    ReleaseNotes = body,
                    DownloadUrl = downloadUrl,
                    DownloadSize = downloadSize
                };
            }

            Log.Debug("UpdateChecker: Up to date (current {Current}, latest {Latest})",
                currentVersion, latestVersion);
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "UpdateChecker: Check failed (non-fatal)");
            return null;
        }
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

    /// <summary>Direct download URL for GameShift.exe from GitHub release assets.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>File size in bytes (from GitHub asset metadata). Used for progress display.</summary>
    public long DownloadSize { get; set; }
}
