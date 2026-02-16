namespace GameShift.Core.Detection;

/// <summary>
/// Common interface for all game launcher library scanners.
/// Each launcher implementation scans installed games from that launcher.
/// Defines the contract for game launcher library scanners.
/// </summary>
public interface ILibraryScanner
{
    /// <summary>
    /// Name of the launcher being scanned.
    /// Examples: "Steam", "Epic", "GOG"
    /// </summary>
    string LauncherName { get; }

    /// <summary>
    /// Whether this launcher is installed on the current system.
    /// Checked via registry keys or filesystem paths.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Scans for all games installed via this launcher.
    /// Returns empty list if launcher is not installed.
    /// Must not throw exceptions - handle errors gracefully.
    /// </summary>
    /// <returns>List of detected games</returns>
    List<GameInfo> ScanInstalledGames();
}
