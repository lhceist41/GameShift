using System.Collections.Concurrent;
using System.Diagnostics;
using GameShift.Core.Config;
using GameShift.Core.GameProfiles;
using Serilog;

namespace GameShift.Core.Detection;

/// <summary>
/// Event args for any process spawn detected via WMI.
/// Fired for ALL processes, not just game matches.
/// Used by ProcessPriorityPersistence and ProcessSnapshotService.
/// </summary>
public class ProcessSpawnedEventArgs : EventArgs
{
    public int ProcessId { get; }
    public string ProcessName { get; }

    public ProcessSpawnedEventArgs(int processId, string processName)
    {
        ProcessId = processId;
        ProcessName = processName;
    }
}

/// <summary>
/// Core game detection engine that monitors process creation/termination.
/// Matches running processes against known game install directories.
/// Uses ETW for sub-ms latency with WMI fallback via <see cref="ProcessMonitorFactory"/>.
/// </summary>
public class GameDetector : IDisposable
{
    private readonly IEnumerable<ILibraryScanner> _scanners;
    private readonly List<GameInfo> _knownGames;
    private readonly ConcurrentDictionary<int, GameInfo> _activeGames;
    private readonly object _lock = new();
    private readonly ILogger _logger;

    private IProcessMonitor? _processMonitor;
    private bool _disposed;

    /// <summary>
    /// Fired when a game process is detected.
    /// </summary>
    public event EventHandler<GameDetectedEventArgs>? GameStarted;

    /// <summary>
    /// Fired when a tracked game process exits.
    /// </summary>
    public event EventHandler<GameDetectedEventArgs>? GameStopped;

    /// <summary>
    /// Fired when the LAST tracked game process exits.
    /// Signal for OptimizationEngine to deactivate.
    /// </summary>
    public event EventHandler? AllGamesStopped;

    /// <summary>
    /// Fired for every process start detected via WMI, before game matching.
    /// Used by ProcessPriorityPersistence (replaces its duplicate WMI watcher)
    /// and ProcessSnapshotService (dirty flag for cache invalidation).
    /// </summary>
    public event EventHandler<ProcessSpawnedEventArgs>? ProcessSpawned;

    /// <summary>
    /// Creates a new game detector with the specified library scanners.
    /// </summary>
    /// <param name="scanners">Collection of launcher scanners to use</param>
    public GameDetector(IEnumerable<ILibraryScanner> scanners)
    {
        _scanners = scanners;
        _knownGames = new List<GameInfo>();
        _activeGames = new ConcurrentDictionary<int, GameInfo>();
        _logger = SettingsManager.Logger;
    }

    /// <summary>
    /// Scans all configured launcher libraries for installed games.
    /// Aggregates results and deduplicates by game ID.
    /// </summary>
    public void ScanLibraries()
    {
        _logger.Information("Starting library scan across all launchers");

        var allGames = new List<GameInfo>();

        foreach (var scanner in _scanners)
        {
            if (!scanner.IsInstalled)
            {
                _logger.Debug("Skipping {LauncherName} - not installed", scanner.LauncherName);
                continue;
            }

            var games = scanner.ScanInstalledGames();
            _logger.Debug("Found {Count} games from {LauncherName}", games.Count, scanner.LauncherName);
            allGames.AddRange(games);
        }

        // Deduplicate by ID
        _knownGames.Clear();
        var uniqueGames = allGames.GroupBy(g => g.Id).Select(g => g.First());
        _knownGames.AddRange(uniqueGames);

        _logger.Information("Scanned {ScannerCount} launchers, found {GameCount} installed games",
            _scanners.Count(), _knownGames.Count);
    }

    /// <summary>
    /// Manually adds a game to the known games list.
    /// Used for manual game additions.
    /// </summary>
    /// <param name="game">Game to add</param>
    public void AddKnownGame(GameInfo game)
    {
        // Check for duplicates by ID
        if (_knownGames.Any(g => g.Id == game.Id))
        {
            _logger.Debug("Game already exists in known games list: {GameName}", game.GameName);
            return;
        }

        _knownGames.Add(game);
        _logger.Information("Manually added game: {GameName}", game.GameName);
    }

    /// <summary>
    /// Removes a game from the known games list.
    /// </summary>
    /// <param name="gameId">ID of the game to remove</param>
    public void RemoveKnownGame(string gameId)
    {
        var game = _knownGames.FirstOrDefault(g => g.Id == gameId);
        if (game != null)
        {
            _knownGames.Remove(game);
            _logger.Information("Removed game: {GameName}", game.GameName);
        }
    }

    /// <summary>
    /// Gets a read-only snapshot of all known games.
    /// </summary>
    /// <returns>Read-only list of known games</returns>
    public IReadOnlyList<GameInfo> GetKnownGames()
    {
        return _knownGames.AsReadOnly();
    }

    /// <summary>
    /// Gets a read-only snapshot of currently active game processes.
    /// </summary>
    /// <returns>Read-only dictionary of active games (PID -> GameInfo)</returns>
    public IReadOnlyDictionary<int, GameInfo> GetActiveGames()
    {
        return _activeGames;
    }

    /// <summary>
    /// Starts monitoring for process creation and termination.
    /// Uses ETW (sub-ms latency) with WMI fallback via <see cref="ProcessMonitorFactory"/>.
    /// Handles failures gracefully (logs error, doesn't throw).
    /// </summary>
    public void StartMonitoring()
    {
        try
        {
            _processMonitor = ProcessMonitorFactory.Create(_logger);
            _processMonitor.ProcessStarted += OnProcessStarted;
            _processMonitor.ProcessStopped += OnProcessStopped;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start process monitoring. " +
                "Application may require administrator privileges. " +
                "Manual triggering will still be available.");
        }
    }

    /// <summary>
    /// Stops monitoring for process events and disposes the underlying monitor.
    /// </summary>
    public void StopMonitoring()
    {
        if (_processMonitor != null)
        {
            _processMonitor.Stop();
            _processMonitor.Dispose();
            _processMonitor = null;
        }

        _logger.Information("Process monitoring stopped");
    }

    /// <summary>
    /// Handles process start events from the active <see cref="IProcessMonitor"/>.
    /// ETW provides the full image path; WMI provides only the filename so we fall back
    /// to <see cref="Process.GetProcessById"/> to resolve the full path.
    /// </summary>
    private void OnProcessStarted(ProcessStartEventData data)
    {
        try
        {
            var processName = Path.GetFileName(data.ImageFileName);

            // Notify all subscribers of process spawn (before game matching filter)
            ProcessSpawned?.Invoke(this, new ProcessSpawnedEventArgs(data.ProcessId, processName));

            // ETW provides the full path directly; WMI provides only the filename.
            // Use the image path when it is rooted, otherwise resolve via Process handle.
            string? executablePath = data.ImageFileName;

            if (string.IsNullOrEmpty(executablePath) || !Path.IsPathRooted(executablePath))
            {
                try
                {
                    using var process = Process.GetProcessById(data.ProcessId);
                    executablePath = process.MainModule?.FileName;
                }
                catch
                {
                    // Process may have already exited, or access denied for system processes
                    return;
                }
            }

            if (string.IsNullOrEmpty(executablePath))
                return;

            // Try to match against known games
            MatchProcess(data.ProcessId, executablePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing process start event");
        }
    }

    /// <summary>
    /// Handles process stop events from the active <see cref="IProcessMonitor"/>.
    /// Checks if the stopped process was a tracked game and fires appropriate events.
    /// </summary>
    private void OnProcessStopped(ProcessStopEventData data)
    {
        try
        {
            if (_activeGames.TryRemove(data.ProcessId, out var gameInfo))
            {
                _logger.Information("Game exited: {GameName} (PID: {ProcessId})",
                    gameInfo.GameName, data.ProcessId);

                // Fire GameStopped event
                GameStopped?.Invoke(this, new GameDetectedEventArgs(
                    gameInfo.Id,
                    gameInfo.GameName,
                    gameInfo.ExecutablePath,
                    data.ProcessId,
                    gameInfo.LauncherSource));

                // Check if all games have stopped
                lock (_lock)
                {
                    if (_activeGames.IsEmpty)
                    {
                        _logger.Information("All games exited - ready for optimization revert");
                        AllGamesStopped?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing process stop event");
        }
    }

    /// <summary>
    /// Attempts to match a process against known game install directories.
    /// Returns the matched GameInfo if found, otherwise null.
    /// </summary>
    /// <param name="processId">Process ID</param>
    /// <param name="executablePath">Full path to the executable</param>
    /// <returns>Matched GameInfo or null</returns>
    private GameInfo? MatchProcess(int processId, string executablePath)
    {
        // Normalize path for comparison
        var normalizedPath = Path.GetFullPath(executablePath);

        foreach (var game in _knownGames)
        {
            // Primary matching strategy: check if executable is under install directory
            if (!string.IsNullOrEmpty(game.InstallDirectory))
            {
                if (normalizedPath.StartsWith(game.InstallDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return OnGameMatched(processId, normalizedPath, game);
                }
            }

            // Secondary matching strategy: exact executable path match
            if (!string.IsNullOrEmpty(game.ExecutablePath))
            {
                if (string.Equals(normalizedPath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    return OnGameMatched(processId, normalizedPath, game);
                }
            }
        }

        // Tertiary matching strategy: check exe name against BuiltInProfiles ProcessNames.
        // Catches games installed outside of scanned launcher directories (standalone launchers, etc.)
        var exeName = Path.GetFileName(normalizedPath);
        foreach (var builtIn in BuiltInProfiles.GetAll())
        {
            foreach (var pn in builtIn.ProcessNames)
            {
                if (string.Equals(exeName, pn, StringComparison.OrdinalIgnoreCase))
                {
                    var autoGame = new GameInfo
                    {
                        Id = GameInfo.GenerateId("builtin", builtIn.Id),
                        GameName = builtIn.DisplayName,
                        ExecutablePath = normalizedPath,
                        InstallDirectory = Path.GetDirectoryName(normalizedPath) ?? "",
                        LauncherSource = "BuiltIn"
                    };

                    // Add to known games so future launches are matched immediately
                    _knownGames.Add(autoGame);
                    _logger.Information(
                        "Auto-detected built-in profile game via process name: {GameName} ({ExeName})",
                        builtIn.DisplayName, exeName);

                    return OnGameMatched(processId, normalizedPath, autoGame);
                }
            }
        }

        // No match found - this is normal for most processes, so don't log
        return null;
    }

    /// <summary>
    /// Handles a successful game match.
    /// Adds to active games and fires the GameStarted event.
    /// </summary>
    private GameInfo OnGameMatched(int processId, string executablePath, GameInfo game)
    {
        _activeGames[processId] = game;

        _logger.Information("Game detected: {GameName} (PID: {ProcessId}, Source: {LauncherSource})",
            game.GameName, processId, game.LauncherSource);

        GameStarted?.Invoke(this, new GameDetectedEventArgs(
            game.Id,
            game.GameName,
            executablePath,
            processId,
            game.LauncherSource));

        return game;
    }

    /// <summary>
    /// Disposes WMI watchers and cleans up resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        StopMonitoring();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
