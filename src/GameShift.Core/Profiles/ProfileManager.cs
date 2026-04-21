using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using Serilog;

namespace GameShift.Core.Profiles;

/// <summary>
/// Manages per-game profile persistence to %AppData%/GameShift/profiles/ as individual JSON files.
/// Provides CRUD operations and default profile support.
/// Supports per-game profiles and a default profile for unmatched games.
/// </summary>
public class ProfileManager
{
    private readonly string _profilesDirectory;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private GameProfile? _cachedDefault;

    /// <summary>
    /// Strips path traversal characters and invalid filename characters from a game ID
    /// to prevent directory traversal attacks when constructing file paths.
    /// Also guards against empty results and Windows reserved device names.
    /// </summary>
    internal static string SanitizeGameId(string gameId)
    {
        var sanitized = gameId
            .Replace("..", "")
            .Replace("/", "_")
            .Replace("\\", "_");
        foreach (var c in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(c, '_');

        // Guard: empty result (e.g., input was ".." or only invalid chars)
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "unknown";

        // Guard: Windows reserved device names (CON, PRN, AUX, NUL, COM1-9, LPT1-9)
        // Windows refuses to create files with these base names even with extensions.
        var upper = sanitized.ToUpperInvariant();
        var reserved = new[] { "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (reserved.Contains(upper))
            sanitized = "_" + sanitized;

        return sanitized;
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Creates a new ProfileManager instance.
    /// Initializes the profiles directory at %AppData%/GameShift/profiles/.
    /// </summary>
    public ProfileManager()
    {
        _profilesDirectory = Path.Combine(SettingsManager.GetAppDataPath(), "profiles");
        _logger = SettingsManager.Logger;

        // Ensure profiles directory exists
        if (!Directory.Exists(_profilesDirectory))
        {
            Directory.CreateDirectory(_profilesDirectory);
            _logger.Information("Created profiles directory at {Path}", _profilesDirectory);
        }
    }

    /// <summary>
    /// Gets the default profile used for games without a custom profile.
    /// Loads from default.json if it exists; otherwise returns hardcoded defaults.
    /// Caches the result for repeated calls; cache is invalidated when default profile is saved.
    /// </summary>
    /// <returns>The default GameProfile</returns>
    public GameProfile GetDefaultProfile()
    {
        lock (_lock)
        {
            if (_cachedDefault != null)
            {
                return _cachedDefault;
            }

            var defaultPath = Path.Combine(_profilesDirectory, "default.json");

            try
            {
                if (File.Exists(defaultPath))
                {
                    var json = File.ReadAllText(defaultPath);
                    var profile = JsonSerializer.Deserialize<GameProfile>(json);

                    if (profile != null)
                    {
                        _cachedDefault = profile;
                        _logger.Debug("Loaded default profile from {Path}", defaultPath);
                        return _cachedDefault;
                    }

                    _logger.Warning("Default profile file was null after deserialization, using hardcoded defaults");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to load default profile from {Path}, using hardcoded defaults", defaultPath);
            }

            _cachedDefault = GameProfile.CreateDefault();
            return _cachedDefault;
        }
    }

    /// <summary>
    /// Gets the profile for a specific game by its ID.
    /// Returns the game-specific profile if one exists, otherwise falls back to the default profile.
    /// Games without custom profiles use the default profile.
    /// </summary>
    /// <param name="gameId">Unique game identifier (e.g. "steam_12345")</param>
    /// <returns>The game-specific profile or default profile</returns>
    public GameProfile GetProfileForGame(string gameId)
    {
        lock (_lock)
        {
            var safeId = SanitizeGameId(gameId);
            var profilePath = Path.Combine(_profilesDirectory, $"{safeId}.json");

            try
            {
                if (File.Exists(profilePath))
                {
                    var json = File.ReadAllText(profilePath);
                    var profile = JsonSerializer.Deserialize<GameProfile>(json);

                    if (profile != null)
                    {
                        _logger.Debug("Loaded custom profile for game {GameId}", gameId);
                        return profile;
                    }

                    _logger.Warning("Profile file for {GameId} was null after deserialization, using default", gameId);
                }
                else
                {
                    _logger.Debug("No custom profile found for game {GameId}, using default profile", gameId);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to load profile for game {GameId}, using default profile", gameId);
            }

            return GetDefaultProfile();
        }
    }

    /// <summary>
    /// Saves a profile to disk as {profile.Id}.json in the profiles directory.
    /// If the profile ID is "default", the cached default profile is invalidated.
    /// </summary>
    /// <param name="profile">The profile to save</param>
    public void SaveProfile(GameProfile profile)
    {
        lock (_lock)
        {
            try
            {
                var safeId = SanitizeGameId(profile.Id);
                var profilePath = Path.Combine(_profilesDirectory, $"{safeId}.json");
                var json = JsonSerializer.Serialize(profile, WriteOptions);
                File.WriteAllText(profilePath, json);

                // Invalidate cached default if saving the default profile
                if (profile.Id == "default")
                {
                    _cachedDefault = null;
                }

                _logger.Information("Saved profile {ProfileId} for game {GameName}", profile.Id, profile.GameName);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save profile {ProfileId}", profile.Id);
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes a game profile by its ID.
    /// The default profile cannot be deleted.
    /// </summary>
    /// <param name="gameId">Unique game identifier to delete</param>
    /// <returns>True if the profile was found and deleted; false if not found or if attempting to delete the default profile</returns>
    public bool DeleteProfile(string gameId)
    {
        lock (_lock)
        {
            if (gameId == "default")
            {
                _logger.Warning("Cannot delete the default profile");
                return false;
            }

            var safeId = SanitizeGameId(gameId);
            var profilePath = Path.Combine(_profilesDirectory, $"{safeId}.json");

            if (File.Exists(profilePath))
            {
                try
                {
                    File.Delete(profilePath);
                    _logger.Information("Deleted profile for game {GameId}", gameId);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to delete profile for game {GameId}", gameId);
                    return false;
                }
            }

            _logger.Debug("Profile not found for game {GameId}, nothing to delete", gameId);
            return false;
        }
    }

    /// <summary>
    /// Gets all profiles from the profiles directory, including the default profile.
    /// Skips files that fail to deserialize.
    /// </summary>
    /// <returns>Read-only list of all stored profiles</returns>
    public IReadOnlyList<GameProfile> GetAllProfiles()
    {
        lock (_lock)
        {
            var profiles = new List<GameProfile>();

            try
            {
                var files = Directory.GetFiles(_profilesDirectory, "*.json");

                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var profile = JsonSerializer.Deserialize<GameProfile>(json);

                        if (profile != null)
                        {
                            profiles.Add(profile);
                        }
                        else
                        {
                            _logger.Warning("Profile file {File} was null after deserialization, skipping", file);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to deserialize profile from {File}, skipping", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to enumerate profiles in {Directory}", _profilesDirectory);
            }

            // Ensure default profile is always included
            if (!profiles.Any(p => p.Id == "default"))
            {
                profiles.Insert(0, GetDefaultProfile());
            }

            return profiles.AsReadOnly();
        }
    }

    /// <summary>
    /// Creates a new GameProfile from a GameInfo instance.
    /// Populates game identity fields from the detection data.
    /// All optimization toggles use class defaults (all enabled except UsePerformanceCoresOnly).
    /// Does NOT auto-save; caller should modify as needed then call SaveProfile().
    /// </summary>
    /// <param name="gameInfo">Game detection information to create profile from</param>
    /// <returns>A new GameProfile populated from the GameInfo</returns>
    public GameProfile CreateProfileFromGameInfo(GameInfo gameInfo)
    {
        return new GameProfile
        {
            Id = gameInfo.Id,
            GameName = gameInfo.GameName,
            ExecutableName = !string.IsNullOrEmpty(gameInfo.ExecutablePath)
                ? Path.GetFileName(gameInfo.ExecutablePath)
                : string.Empty,
            ExecutablePath = gameInfo.ExecutablePath,
            LauncherSource = gameInfo.LauncherSource
        };
    }
}
