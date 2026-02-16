using System.Security.Cryptography;
using System.Text;

namespace GameShift.Core.Detection;

/// <summary>
/// Represents a detected or manually-added game.
/// Stores identity and location information for process matching.
/// Separate from GameProfile (which is about optimization settings).
/// Stores identity and location information for process matching.
/// </summary>
public class GameInfo
{
    /// <summary>
    /// Unique identifier for this game.
    /// Format: "{source}_{hash}" where source is launcher name or "manual".
    /// Example: "steam_12345", "epic_abcd1234", "manual_cyberpunk2077"
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the game.
    /// Example: "Cyberpunk 2077"
    /// </summary>
    public string GameName { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the game executable.
    /// May be empty for launcher-detected games where only install directory is known.
    /// Example: "D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe"
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the game's installation directory.
    /// Used for process matching when ExecutablePath is unknown.
    /// Example: "D:\SteamLibrary\steamapps\common\Cyberpunk 2077"
    /// </summary>
    public string InstallDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Source from which this game was detected.
    /// Values: "Steam", "Epic", "GOG", "Manual"
    /// </summary>
    public string LauncherSource { get; set; } = string.Empty;

    /// <summary>
    /// Launcher-specific unique identifier.
    /// Steam: appid, Epic: catalog item ID, GOG: game ID.
    /// Empty for manually-added games.
    /// </summary>
    public string LauncherId { get; set; } = string.Empty;

    /// <summary>
    /// Generates a unique ID for a game based on launcher source and identifier.
    /// </summary>
    /// <param name="launcherSource">Source launcher name (Steam, Epic, GOG, Manual)</param>
    /// <param name="launcherId">Launcher-specific ID or game name for manual entries</param>
    /// <returns>Unique ID string</returns>
    public static string GenerateId(string launcherSource, string launcherId)
    {
        var source = launcherSource.ToLowerInvariant();
        var identifier = launcherId.ToLowerInvariant();

        // For launchers with numeric IDs, use them directly
        if (source is "steam" or "epic" or "gog" && !string.IsNullOrWhiteSpace(identifier))
        {
            return $"{source}_{identifier}";
        }

        // For manual entries or when launcher ID is missing, hash the game name
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(identifier));
        var hashString = Convert.ToHexString(hashBytes)[..16]; // First 16 hex chars (8 bytes)
        return $"{source}_{hashString.ToLowerInvariant()}";
    }

    /// <summary>
    /// Returns a human-readable string representation.
    /// </summary>
    public override string ToString()
    {
        return $"{GameName} ({LauncherSource})";
    }

    /// <summary>
    /// Equality based on unique ID.
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is GameInfo other)
        {
            return Id == other.Id;
        }
        return false;
    }

    /// <summary>
    /// Hash code based on unique ID.
    /// </summary>
    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }
}
