using System.Text.RegularExpressions;
using GameShift.Core.Config;
using Microsoft.Win32;

namespace GameShift.Core.Detection;

/// <summary>
/// Scans Steam library for installed games.
/// Reads registry for Steam path, parses VDF files for library folders and game manifests.
/// Scans Steam registry for install path, parses VDF manifests for installed games.
/// </summary>
public class SteamLibraryScanner : ILibraryScanner
{
    public string LauncherName => "Steam";

    public bool IsInstalled
    {
        get
        {
            var steamPath = GetSteamInstallPath();
            return !string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath);
        }
    }

    public List<GameInfo> ScanInstalledGames()
    {
        var games = new List<GameInfo>();

        try
        {
            var steamPath = GetSteamInstallPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                SettingsManager.Logger.Debug("Steam not installed (registry path not found)");
                return games;
            }

            if (!Directory.Exists(steamPath))
            {
                SettingsManager.Logger.Debug("Steam path from registry does not exist: {Path}", steamPath);
                return games;
            }

            SettingsManager.Logger.Information("Scanning Steam library at {Path}", steamPath);

            // Get all library folders (including main Steam folder)
            var libraryPaths = GetLibraryFolderPaths(steamPath);
            SettingsManager.Logger.Debug("Found {Count} Steam library folders", libraryPaths.Count);

            // Scan each library folder for game manifests
            foreach (var libraryPath in libraryPaths)
            {
                var steamappsPath = Path.Combine(libraryPath, "steamapps");
                if (!Directory.Exists(steamappsPath))
                {
                    SettingsManager.Logger.Debug("Steamapps directory not found at {Path}", steamappsPath);
                    continue;
                }

                var manifestFiles = Directory.GetFiles(steamappsPath, "appmanifest_*.acf");
                SettingsManager.Logger.Debug("Found {Count} manifests in {Path}", manifestFiles.Length, steamappsPath);

                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        var gameInfo = ParseAppManifest(manifestFile, steamappsPath);
                        if (gameInfo != null)
                        {
                            games.Add(gameInfo);
                            SettingsManager.Logger.Debug("Found Steam game: {Game}", gameInfo);
                        }
                    }
                    catch (Exception ex)
                    {
                        SettingsManager.Logger.Warning(ex, "Failed to parse Steam manifest: {File}", manifestFile);
                    }
                }
            }

            SettingsManager.Logger.Information("Steam scan complete: {Count} games found", games.Count);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "Steam library scan failed");
        }

        return games;
    }

    /// <summary>
    /// Gets Steam install path from registry.
    /// Tries HKLM WOW6432Node first, then HKCU.
    /// </summary>
    private string? GetSteamInstallPath()
    {
        try
        {
            // Try HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam
            var path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            // Try HKEY_CURRENT_USER\SOFTWARE\Valve\Steam
            path = Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null) as string;
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "Failed to read Steam install path from registry");
        }

        return null;
    }

    /// <summary>
    /// Parses libraryfolders.vdf to get all Steam library folder paths.
    /// Returns the main Steam path plus any additional library folders.
    /// </summary>
    private List<string> GetLibraryFolderPaths(string steamPath)
    {
        var libraryPaths = new List<string> { steamPath };

        try
        {
            var libraryFoldersFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersFile))
            {
                SettingsManager.Logger.Debug("libraryfolders.vdf not found at {Path}", libraryFoldersFile);
                return libraryPaths;
            }

            var content = File.ReadAllText(libraryFoldersFile);
            var pathMatches = ParseVdfKeyValue(content, "path");

            foreach (var path in pathMatches)
            {
                // VDF uses escaped backslashes - unescape them
                var unescapedPath = path.Replace(@"\\", @"\");
                if (Directory.Exists(unescapedPath) &&
                    !libraryPaths.Any(p => string.Equals(p, unescapedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    libraryPaths.Add(unescapedPath);
                    SettingsManager.Logger.Debug("Found additional library folder: {Path}", unescapedPath);
                }
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "Failed to parse libraryfolders.vdf");
        }

        return libraryPaths;
    }

    /// <summary>
    /// Parses a Steam appmanifest_*.acf file to extract game information.
    /// </summary>
    private GameInfo? ParseAppManifest(string manifestPath, string steamappsPath)
    {
        var content = File.ReadAllText(manifestPath);

        var appId = ParseVdfKeyValue(content, "appid").FirstOrDefault();
        var name = ParseVdfKeyValue(content, "name").FirstOrDefault();
        var installDir = ParseVdfKeyValue(content, "installdir").FirstOrDefault();

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir))
        {
            SettingsManager.Logger.Debug("Incomplete manifest data in {File}", manifestPath);
            return null;
        }

        var fullInstallPath = Path.Combine(steamappsPath, "common", installDir);
        if (!Directory.Exists(fullInstallPath))
        {
            SettingsManager.Logger.Debug("Install directory not found for {Game}: {Path}", name, fullInstallPath);
            return null;
        }

        return new GameInfo
        {
            Id = GameInfo.GenerateId("Steam", appId),
            GameName = name,
            ExecutablePath = string.Empty, // Steam games may have multiple .exe files - matching done by directory
            InstallDirectory = fullInstallPath,
            LauncherSource = "Steam",
            LauncherId = appId
        };
    }

    /// <summary>
    /// Parses VDF/ACF key-value pairs using regex.
    /// Extracts values for a given key from Valve KeyValues format.
    /// Format: "key"		"value" (tab-separated)
    /// </summary>
    private List<string> ParseVdfKeyValue(string content, string key)
    {
        var values = new List<string>();

        // Match: "key"		"value" or "key"  "value" (with tabs or spaces)
        var pattern = $@"""{Regex.Escape(key)}""\s+""([^""]+)""";
        var matches = Regex.Matches(content, pattern, RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            if (match.Success && match.Groups.Count > 1)
            {
                values.Add(match.Groups[1].Value);
            }
        }

        return values;
    }
}
