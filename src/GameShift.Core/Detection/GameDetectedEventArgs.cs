namespace GameShift.Core.Detection;

/// <summary>
/// Event arguments for game detection events (started/stopped).
/// Carries game identity and process information for detection events.
/// </summary>
public class GameDetectedEventArgs : EventArgs
{
    /// <summary>
    /// Stable unique game identifier from GameInfo.Id (e.g. "steam_12345").
    /// Used for profile lookup in ProfileManager.
    /// </summary>
    public string GameId { get; }

    /// <summary>
    /// Display name of the detected game.
    /// </summary>
    public string GameName { get; }

    /// <summary>
    /// Rooted full path to the running executable, or "" when the path could not be observed.
    /// Never a bare filename - consumers that need the process name must read
    /// <see cref="ProcessName"/> instead, and consumers that need a real path (Defender exclusions,
    /// IFEO image targeting) rely on "" meaning "no path", which is their fail-closed guard.
    /// </summary>
    public string ExecutablePath { get; }

    /// <summary>
    /// Filename with extension of the observed process image (e.g. "r5apex.exe").
    /// Always populated, including when <see cref="ExecutablePath"/> is empty.
    /// </summary>
    public string ProcessName { get; }

    /// <summary>
    /// OS process ID.
    /// </summary>
    public int ProcessId { get; }

    /// <summary>
    /// Source from which this game was detected.
    /// Values: "Steam", "Epic", "GOG", "Manual"
    /// </summary>
    public string LauncherSource { get; }

    /// <summary>
    /// Creates a new instance of game detection event arguments.
    /// </summary>
    /// <param name="gameId">Stable game identifier from GameInfo.Id</param>
    /// <param name="gameName">Display name of the game</param>
    /// <param name="executablePath">Rooted full path to the executable, or "" if unknown</param>
    /// <param name="processName">Observed process filename with extension</param>
    /// <param name="processId">OS process ID</param>
    /// <param name="launcherSource">Source launcher name</param>
    public GameDetectedEventArgs(
        string gameId,
        string gameName,
        string executablePath,
        string processName,
        int processId,
        string launcherSource)
    {
        GameId = gameId;
        GameName = gameName;

        // Structural guard for the full-path-or-empty contract: a bare filename reaching a
        // path-consuming subscriber (Defender exclusion, IFEO image path, profile ExecutablePath)
        // would be acted on as if it were a real location. Fully qualified rather than merely
        // rooted, for the same reason as ProcessProbe: "\Device\..." names no usable location.
        ExecutablePath = !string.IsNullOrEmpty(executablePath) && Path.IsPathFullyQualified(executablePath)
            ? executablePath
            : string.Empty;

        ProcessName = processName;
        ProcessId = processId;
        LauncherSource = launcherSource;
    }
}
