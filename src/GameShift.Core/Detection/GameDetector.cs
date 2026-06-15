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
    private global::System.Timers.Timer? _livenessTimer;
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
        var uniqueGames = allGames.GroupBy(g => g.Id).Select(g => g.First()).ToList();

        lock (_lock)
        {
            _knownGames.Clear();
            _knownGames.AddRange(uniqueGames);
        }

        _logger.Information("Scanned {ScannerCount} launchers, found {GameCount} installed games",
            _scanners.Count(), uniqueGames.Count);
    }

    /// <summary>
    /// Manually adds a game to the known games list.
    /// Used for manual game additions.
    /// </summary>
    /// <param name="game">Game to add</param>
    public void AddKnownGame(GameInfo game)
    {
        lock (_lock)
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
    }

    /// <summary>
    /// Removes a game from the known games list.
    /// </summary>
    /// <param name="gameId">ID of the game to remove</param>
    public void RemoveKnownGame(string gameId)
    {
        lock (_lock)
        {
            var game = _knownGames.FirstOrDefault(g => g.Id == gameId);
            if (game != null)
            {
                _knownGames.Remove(game);
                _logger.Information("Removed game: {GameName}", game.GameName);
            }
        }
    }

    /// <summary>
    /// Gets a read-only snapshot of all known games.
    /// </summary>
    /// <returns>Read-only list of known games</returns>
    public IReadOnlyList<GameInfo> GetKnownGames()
    {
        lock (_lock)
        {
            return _knownGames.ToList().AsReadOnly();
        }
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

            // Liveness sweep: a self-heal for a dropped/missed process-stop event (the WMI fallback
            // can drop Win32_ProcessStopTrace under load). Without this, a missed stop would leave
            // the game in _activeGames forever, so optimizations would never revert until restart.
            _livenessTimer = new global::System.Timers.Timer(7000) { AutoReset = true };
            _livenessTimer.Elapsed += ReconcileActiveGames;
            _livenessTimer.Start();
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
        if (_livenessTimer != null)
        {
            _livenessTimer.Stop();
            _livenessTimer.Dispose();
            _livenessTimer = null;
        }

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
            HandleGameExited(data.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing process stop event");
        }
    }

    /// <summary>
    /// Removes a tracked game by PID and fires GameStopped (and AllGamesStopped when the last one
    /// exits). Shared by the process-stop event and the liveness sweep. TryRemove is atomic, so if
    /// both fire for the same PID only one wins and the events fire exactly once.
    /// </summary>
    private void HandleGameExited(int processId)
    {
        if (_activeGames.TryRemove(processId, out var gameInfo))
        {
            _logger.Information("Game exited: {GameName} (PID: {ProcessId})",
                gameInfo.GameName, processId);

            // Fire GameStopped event
            GameStopped?.Invoke(this, new GameDetectedEventArgs(
                gameInfo.Id,
                gameInfo.GameName,
                gameInfo.ExecutablePath,
                processId,
                gameInfo.LauncherSource));

            // Check if all games have stopped. _activeGames is a ConcurrentDictionary
            // so IsEmpty is thread-safe on its own. Fire the event OUTSIDE any lock
            // so subscribers can safely call back into the detector without deadlock risk.
            if (_activeGames.IsEmpty)
            {
                _logger.Information("All games exited - ready for optimization revert");
                AllGamesStopped?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Periodic reconciliation: drops tracked games whose process has actually exited but whose
    /// stop event was missed. Conservative - only acts when a process is provably gone (no such PID,
    /// or the PID now belongs to a differently-named process); transient/access-denied lookups are
    /// treated as "still alive" so a game is never reverted out from under the user on uncertainty.
    /// </summary>
    private void ReconcileActiveGames(object? sender, global::System.Timers.ElapsedEventArgs e)
    {
        try
        {
            foreach (var (pid, gameInfo) in _activeGames.ToArray())
            {
                if (IsTrackedGameGone(pid, gameInfo))
                {
                    _logger.Information(
                        "Liveness sweep: tracked game {GameName} (PID: {ProcessId}) is gone - reconciling missed stop event",
                        gameInfo.GameName, pid);
                    HandleGameExited(pid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error during active-game liveness sweep");
        }
    }

    private static bool IsTrackedGameGone(int processId, GameInfo gameInfo)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            // PID exists but now belongs to a different image -> the original game exited and the
            // PID was reused. Treat as gone (compare against the tracked exe name).
            var expected = Path.GetFileNameWithoutExtension(gameInfo.ExecutablePath);
            return !string.IsNullOrEmpty(expected)
                && !string.Equals(process.ProcessName, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return true; // no process with this PID -> definitely gone
        }
        catch
        {
            return false; // access denied / transient -> assume alive; never revert on uncertainty
        }
    }

    /// <summary>
    /// Executables that can live inside a game's install directory but are NOT the game: platform
    /// stubs, anti-cheat launchers, crash reporters, redistributables/installers. Matching any of
    /// these as "the game" would optimize the wrong process and cause a premature revert when it
    /// exits. Compared case-insensitively; crash-handler variants are matched by substring below.
    /// </summary>
    private static readonly HashSet<string> NonGameHelperExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "start_protected_game.exe",
        "easyanticheat.exe", "easyanticheat_eos.exe", "easyanticheat_setup.exe",
        "beservice.exe", "belauncher.exe", "battleye.exe",
        "unrealcefsubprocess.exe", "unrealversionselector.exe",
        "vc_redist.x64.exe", "vc_redist.x86.exe", "vcredist_x64.exe", "vcredist_x86.exe",
        "dxsetup.exe", "setup.exe", "uninstall.exe",
    };

    private static bool IsNonGameHelper(string exeName) =>
        NonGameHelperExes.Contains(exeName)
        || exeName.Contains("crashpad", StringComparison.OrdinalIgnoreCase)
        || exeName.Contains("crashhandler", StringComparison.OrdinalIgnoreCase)
        || exeName.Contains("crashreport", StringComparison.OrdinalIgnoreCase);

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

        // Never treat a known non-game helper (platform stub, anti-cheat launcher, crash reporter,
        // redist/installer) as the game, even when it lives under the game's install directory.
        // Matching one would optimize the wrong process and, when it exits, trigger a premature
        // full revert while the real game is still running.
        var exeName = Path.GetFileName(normalizedPath);
        if (IsNonGameHelper(exeName))
            return null;

        // Take a snapshot under the lock so we can iterate safely without
        // holding the lock for the entire matching duration.
        List<GameInfo> snapshot;
        lock (_lock)
        {
            snapshot = _knownGames.ToList();
        }

        foreach (var game in snapshot)
        {
            // Primary matching strategy: check if executable is under install directory
            if (!string.IsNullOrEmpty(game.InstallDirectory))
            {
                var installDir = game.InstallDirectory.TrimEnd('\\') + '\\';
                if (normalizedPath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
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

                    // Add to known games so future launches are matched immediately.
                    // Safe: we iterate 'snapshot', not '_knownGames'.
                    lock (_lock)
                    {
                        _knownGames.Add(autoGame);
                    }

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
