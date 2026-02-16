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
    /// Full path to the running executable.
    /// </summary>
    public string ExecutablePath { get; }

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
    /// <param name="executablePath">Full path to the executable</param>
    /// <param name="processId">OS process ID</param>
    /// <param name="launcherSource">Source launcher name</param>
    public GameDetectedEventArgs(string gameId, string gameName, string executablePath, int processId, string launcherSource)
    {
        GameId = gameId;
        GameName = gameName;
        ExecutablePath = executablePath;
        ProcessId = processId;
        LauncherSource = launcherSource;
    }
}
