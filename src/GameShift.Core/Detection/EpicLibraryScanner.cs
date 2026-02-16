using System.Text.Json;
using GameShift.Core.Config;

namespace GameShift.Core.Detection;

/// <summary>
/// Scans Epic Games Store library for installed games.
/// Reads JSON .item manifest files from ProgramData.
/// Reads Epic Games Store manifest files to discover installed games.
/// </summary>
public class EpicLibraryScanner : ILibraryScanner
{
    public string LauncherName => "Epic";

    public bool IsInstalled
    {
        get
        {
            var manifestsPath = GetManifestsPath();
            return Directory.Exists(manifestsPath);
        }
    }

    public List<GameInfo> ScanInstalledGames()
    {
        var games = new List<GameInfo>();

        try
        {
            var manifestsPath = GetManifestsPath();
            if (!Directory.Exists(manifestsPath))
            {
                SettingsManager.Logger.Debug("Epic Games Store not installed (manifests directory not found)");
                return games;
            }

            SettingsManager.Logger.Information("Scanning Epic Games Store library at {Path}", manifestsPath);

            var manifestFiles = Directory.GetFiles(manifestsPath, "*.item");
            SettingsManager.Logger.Debug("Found {Count} Epic manifest files", manifestFiles.Length);

            foreach (var manifestFile in manifestFiles)
            {
                try
                {
                    var gameInfo = ParseManifest(manifestFile);
                    if (gameInfo != null)
                    {
                        games.Add(gameInfo);
                        SettingsManager.Logger.Debug("Found Epic game: {Game}", gameInfo);
                    }
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(ex, "Failed to parse Epic manifest: {File}", manifestFile);
                }
            }

            SettingsManager.Logger.Information("Epic scan complete: {Count} games found", games.Count);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "Epic library scan failed");
        }

        return games;
    }

    /// <summary>
    /// Gets the path to Epic Games Store manifests directory.
    /// </summary>
    private string GetManifestsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "EpicGamesLauncher",
            "Data",
            "Manifests");
    }

    /// <summary>
    /// Parses an Epic .item manifest file to extract game information.
    /// </summary>
    private GameInfo? ParseManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Skip incomplete installations
        if (root.TryGetProperty("bIsIncompleteInstall", out var isIncomplete) && isIncomplete.GetBoolean())
        {
            SettingsManager.Logger.Debug("Skipping incomplete install: {File}", manifestPath);
            return null;
        }

        // Extract required fields
        if (!root.TryGetProperty("InstallLocation", out var installLocationProp) ||
            !root.TryGetProperty("DisplayName", out var displayNameProp) ||
            !root.TryGetProperty("CatalogItemId", out var catalogItemIdProp))
        {
            SettingsManager.Logger.Debug("Missing required fields in Epic manifest: {File}", manifestPath);
            return null;
        }

        var installLocation = installLocationProp.GetString();
        var displayName = displayNameProp.GetString();
        var catalogItemId = catalogItemIdProp.GetString();

        if (string.IsNullOrEmpty(installLocation) || string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(catalogItemId))
        {
            SettingsManager.Logger.Debug("Empty required fields in Epic manifest: {File}", manifestPath);
            return null;
        }

        // Check if install directory exists
        if (!Directory.Exists(installLocation))
        {
            SettingsManager.Logger.Debug("Install directory not found for {Game}: {Path}", displayName, installLocation);
            return null;
        }

        // Extract executable path if available
        var executablePath = string.Empty;
        if (root.TryGetProperty("LaunchExecutable", out var launchExecProp))
        {
            var launchExec = launchExecProp.GetString();
            if (!string.IsNullOrEmpty(launchExec))
            {
                executablePath = Path.Combine(installLocation, launchExec);

                // Verify executable exists
                if (!File.Exists(executablePath))
                {
                    SettingsManager.Logger.Debug("LaunchExecutable not found for {Game}: {Path}", displayName, executablePath);
                    executablePath = string.Empty;
                }
            }
        }

        return new GameInfo
        {
            Id = GameInfo.GenerateId("Epic", catalogItemId),
            GameName = displayName,
            ExecutablePath = executablePath,
            InstallDirectory = installLocation,
            LauncherSource = "Epic",
            LauncherId = catalogItemId
        };
    }
}
