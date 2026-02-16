using GameShift.Core.Config;
using Microsoft.Win32;

namespace GameShift.Core.Detection;

/// <summary>
/// Scans GOG Galaxy library for installed games.
/// Reads registry entries under HKLM\SOFTWARE\WOW6432Node\GOG.com\Games.
/// Reads GOG Galaxy registry entries to discover installed games.
/// </summary>
public class GogLibraryScanner : ILibraryScanner
{
    private const string GogRegistryPath = @"SOFTWARE\WOW6432Node\GOG.com\Games";

    public string LauncherName => "GOG";

    public bool IsInstalled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(GogRegistryPath);
                return key != null && key.GetSubKeyNames().Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public List<GameInfo> ScanInstalledGames()
    {
        var games = new List<GameInfo>();

        try
        {
            using var gamesKey = Registry.LocalMachine.OpenSubKey(GogRegistryPath);
            if (gamesKey == null)
            {
                SettingsManager.Logger.Debug("GOG not installed (registry key not found)");
                return games;
            }

            var gameSubKeys = gamesKey.GetSubKeyNames();
            SettingsManager.Logger.Information("Scanning GOG library: {Count} registry entries found", gameSubKeys.Length);

            foreach (var subKeyName in gameSubKeys)
            {
                try
                {
                    using var gameKey = gamesKey.OpenSubKey(subKeyName);
                    if (gameKey == null)
                    {
                        continue;
                    }

                    var gameInfo = ParseGameRegistryKey(gameKey, subKeyName);
                    if (gameInfo != null)
                    {
                        games.Add(gameInfo);
                        SettingsManager.Logger.Debug("Found GOG game: {Game}", gameInfo);
                    }
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(ex, "Failed to parse GOG registry entry: {SubKey}", subKeyName);
                }
            }

            SettingsManager.Logger.Information("GOG scan complete: {Count} games found", games.Count);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "GOG library scan failed");
        }

        return games;
    }

    /// <summary>
    /// Parses a GOG game registry key to extract game information.
    /// </summary>
    private GameInfo? ParseGameRegistryKey(RegistryKey gameKey, string subKeyName)
    {
        // Try both casing variants for each value
        var gameName = GetRegistryValue(gameKey, "gameName", "GAMENAME");
        var path = GetRegistryValue(gameKey, "path", "PATH");
        var gameId = GetRegistryValue(gameKey, "gameID", "GAMEID");
        var exe = GetRegistryValue(gameKey, "exe", "EXE");

        if (string.IsNullOrEmpty(gameName))
        {
            SettingsManager.Logger.Debug("Missing gameName in GOG registry key: {SubKey}", subKeyName);
            return null;
        }

        if (string.IsNullOrEmpty(path))
        {
            SettingsManager.Logger.Debug("Missing path in GOG registry key: {SubKey}", subKeyName);
            return null;
        }

        // Check if install directory exists (game may be uninstalled but registry not cleaned)
        if (!Directory.Exists(path))
        {
            SettingsManager.Logger.Debug("Install directory not found for {Game}: {Path}", gameName, path);
            return null;
        }

        // Use gameId if available, otherwise use subKeyName as fallback
        var launcherId = !string.IsNullOrEmpty(gameId) ? gameId : subKeyName;

        // Extract executable path if available
        var executablePath = string.Empty;
        if (!string.IsNullOrEmpty(exe))
        {
            // EXE value may include arguments - extract just the path
            var exeParts = exe.Split(new[] { " /" }, StringSplitOptions.None);
            var exePath = exeParts[0].Trim('"');

            // If path is relative, combine with install directory
            if (!Path.IsPathRooted(exePath))
            {
                exePath = Path.Combine(path, exePath);
            }

            if (File.Exists(exePath))
            {
                executablePath = exePath;
            }
            else
            {
                SettingsManager.Logger.Debug("Executable not found for {Game}: {Path}", gameName, exePath);
            }
        }

        return new GameInfo
        {
            Id = GameInfo.GenerateId("GOG", launcherId),
            GameName = gameName,
            ExecutablePath = executablePath,
            InstallDirectory = path,
            LauncherSource = "GOG",
            LauncherId = launcherId
        };
    }

    /// <summary>
    /// Gets a registry value trying multiple key names (case variants).
    /// </summary>
    private string? GetRegistryValue(RegistryKey key, params string[] names)
    {
        foreach (var name in names)
        {
            var value = key.GetValue(name) as string;
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        return null;
    }
}
