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
    // Install folders of launcher games the user removed, normalized with a trailing '\'. Guarded by _lock.
    private readonly List<string> _suppressedInstallDirectories = new();
    private readonly ConcurrentDictionary<int, ActiveGame> _activeGames;
    private readonly ProcessProbe _probe;
    private readonly Func<int, string> _liveProcessNameProbe;
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
        : this(scanners, ProcessProbe.Default)
    {
    }

    /// <summary>
    /// Creates a detector with an explicit process classifier and live process-name lookup. Used by
    /// tests to drive the dead/unreadable/unknown branches and the name-only corroboration step
    /// deterministically without touching the real process table.
    /// </summary>
    /// <param name="scanners">Collection of launcher scanners to use.</param>
    /// <param name="probe">Liveness/path classifier.</param>
    /// <param name="liveProcessNameProbe">
    /// Resolves the name of the process that currently owns a PID, throwing the same way
    /// <see cref="Process.GetProcessById(int)"/> does. Defaults to
    /// <see cref="DefaultProcessNameProbe"/>.
    /// </param>
    internal GameDetector(
        IEnumerable<ILibraryScanner> scanners,
        ProcessProbe probe,
        Func<int, string>? liveProcessNameProbe = null)
    {
        _scanners = scanners;
        _knownGames = new List<GameInfo>();
        _activeGames = new ConcurrentDictionary<int, ActiveGame>();
        _probe = probe;
        _liveProcessNameProbe = liveProcessNameProbe ?? DefaultProcessNameProbe;
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
    /// Stops matching processes started from a removed launcher game's install folder, under any
    /// identity they would otherwise match: another known game, a built-in profile by name, or the
    /// runtime built-in record. A manually added game with that exact executable path still matches,
    /// since that is the user explicitly bringing the game back.
    ///
    /// Relative paths and drive roots are ignored, so a bad scanner entry cannot switch off
    /// detection for a whole drive.
    /// </summary>
    public void SuppressInstallDirectory(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Path.IsPathFullyQualified(installDirectory))
            return;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(installDirectory).TrimEnd('\\') + '\\';
        }
        catch (ArgumentException)
        {
            return;
        }

        if (string.Equals(normalized, Path.GetPathRoot(normalized), StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
        {
            if (!_suppressedInstallDirectories.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                _suppressedInstallDirectories.Add(normalized);
                _logger.Information("Detection suppressed for removed game folder: {Directory}", normalized);
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
    /// Projects the tracked identities only; the runtime evidence held alongside them
    /// (<see cref="ActiveGame"/>) is an internal detail and is deliberately not exposed.
    /// </summary>
    /// <returns>Read-only dictionary of active games (PID -> GameInfo)</returns>
    public IReadOnlyDictionary<int, GameInfo> GetActiveGames()
    {
        var snapshot = new Dictionary<int, GameInfo>(_activeGames.Count);
        foreach (var (processId, active) in _activeGames)
            snapshot[processId] = active.Game;
        return snapshot;
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

            // One-time rundown of already-running processes: a game launched BEFORE GameShift
            // started produces no start event, so without this it is never matched or optimized for
            // the whole session. Runs on a background thread (MainModule reads can be slow);
            // OnGameMatched is idempotent so it cannot double-fire with a concurrent live event.
            global::System.Threading.Tasks.Task.Run(ScanRunningProcesses);
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
    /// One-time scan of already-running processes at startup. A game launched before GameShift
    /// started emits no ETW/WMI start event, so without this it would never be matched or optimized.
    /// Reuses <see cref="MatchProcess"/> (same non-game-helper filter and known-game matching as
    /// live events). Must run after the library scan has populated known games.
    ///
    /// The enumeration is used ONLY to collect candidate PIDs. The snapshot's own name and module
    /// data are never trusted - a snapshot entry can already be stale, and a stale name is exactly
    /// the evidence that must not register a game. Each PID is re-observed individually by
    /// <see cref="ScanRunningProcess"/>.
    /// </summary>
    public void ScanRunningProcesses()
    {
        int matched = 0;
        try
        {
            int currentPid = Environment.ProcessId;

            var candidatePids = new List<int>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    candidatePids.Add(process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (var pid in candidatePids)
            {
                // Skip self and the Idle (0) / System (4) pseudo-processes, and anything a live
                // event already matched before the rundown reached it.
                if (pid == currentPid || pid <= 4 || _activeGames.ContainsKey(pid))
                    continue;

                try
                {
                    if (ScanRunningProcess(pid) != null)
                        matched++;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Startup rundown skipped PID {ProcessId}", pid);
                }
            }

            _logger.Information("Startup process rundown complete - matched {Count} already-running game(s)", matched);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error during startup process rundown");
        }
    }

    /// <summary>
    /// Rundown observation of a single PID. Only a fully resolved observation (alive, rooted path)
    /// may match. The rundown has no live start event behind it, so it has no trustworthy observed
    /// process name and must never take the name-only route; an alive-but-unreadable, gone, or
    /// unknown process is skipped, and a later start event can still match it.
    /// </summary>
    internal GameInfo? ScanRunningProcess(int processId)
    {
        var observation = _probe.Probe(processId);

        if (observation.Status != ProcessLiveness.Resolved || string.IsNullOrEmpty(observation.ExecutablePath))
            return null;

        return MatchProcess(processId, observation.ExecutablePath);
    }

    /// <summary>
    /// Handles process start events from the active <see cref="IProcessMonitor"/>.
    ///
    /// A rooted image path in the event keeps the existing readable-path matching unchanged. WMI
    /// carries only the filename, and the kernel's ETW name may be a short one; that pathless case
    /// is classified by
    /// <see cref="ProcessProbe"/> instead of <c>Process.MainModule</c>, which needs rights a
    /// protected game refuses and cannot tell "already dead" apart from "alive but unreadable".
    ///
    /// On that pathless route a bare name never registers a game without proven liveness. A resolved
    /// path can still register a process that exited just before the probe (a Tier-2 result does
    /// not prove liveness), and the rooted route is left unprobed with the same race. The liveness
    /// sweep reconciles both, as it did before this repair.
    /// </summary>
    internal void OnProcessStarted(ProcessStartEventData data)
    {
        try
        {
            var processName = Path.GetFileName(data.ImageFileName);

            // Notify all subscribers of process spawn (before game matching filter)
            ProcessSpawned?.Invoke(this, new ProcessSpawnedEventArgs(data.ProcessId, processName));

            // Rooted path: the event already carries the real image location.
            if (!string.IsNullOrEmpty(data.ImageFileName) && Path.IsPathRooted(data.ImageFileName))
            {
                MatchProcess(data.ProcessId, data.ImageFileName);
                return;
            }

            // WMI path: filename only. With no name there is nothing to fall back on either.
            if (string.IsNullOrEmpty(processName))
                return;

            var observation = _probe.Probe(data.ProcessId);

            switch (observation.Status)
            {
                case ProcessLiveness.Resolved:
                    MatchProcess(data.ProcessId, observation.ExecutablePath!);
                    break;

                case ProcessLiveness.AliveNoPath:
                    // Liveness was proven for whatever process owns this PID now, which is not
                    // necessarily the process the event described: the event's process could have
                    // exited and the PID been reused before the probe ran. Corroborate the event's
                    // name against the PID's current occupant before admitting that name as the
                    // sole matching evidence, then apply the narrow gate in MatchProcessByNameOnly.
                    // The live name is matched rather than the event's: a kernel short name can be
                    // truncated (r5apex_dx12.exe arriving as r5apex_dx12.ex), the process table's
                    // name is not.
                    if (LiveNameCorroborates(data.ProcessId, processName, out var liveName))
                        MatchProcessByNameOnly(data.ProcessId, liveName + ".exe");
                    break;

                default:
                    // Gone / Unknown: no GameStarted, no active entry, no built-in record.
                    _logger.Debug(
                        "Pathless start for PID {ProcessId} ({ProcessName}) classified {Status} - not matched",
                        data.ProcessId, processName, observation.Status);
                    break;
            }
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
    internal void OnProcessStopped(ProcessStopEventData data)
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
        if (_activeGames.TryRemove(processId, out var active))
        {
            var gameInfo = active.Game;

            _logger.Information("Game exited: {GameName} (PID: {ProcessId})",
                gameInfo.GameName, processId);

            // Fire GameStopped event with the evidence actually observed for this PID, not the
            // persistent metadata (which is legitimately empty for launcher-only entries).
            GameStopped?.Invoke(this, new GameDetectedEventArgs(
                gameInfo.Id,
                gameInfo.GameName,
                active.ObservedExecutablePath ?? string.Empty,
                active.ObservedProcessName,
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
        ReconcileActiveGamesOnce();
    }

    /// <summary>
    /// Runs a single liveness reconciliation pass. This is the whole body of the periodic sweep;
    /// the timer callback is only the scheduling wrapper around it.
    /// </summary>
    internal void ReconcileActiveGamesOnce()
    {
        try
        {
            foreach (var (pid, active) in _activeGames.ToArray())
            {
                if (IsTrackedGameGone(pid, active))
                {
                    _logger.Information(
                        "Liveness sweep: tracked game {GameName} (PID: {ProcessId}) is gone - reconciling missed stop event",
                        active.Game.GameName, pid);
                    HandleGameExited(pid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error during active-game liveness sweep");
        }
    }

    /// <summary>
    /// Resolves the running process name for a PID. Throws the same exceptions
    /// <see cref="Process.GetProcessById(int)"/> does, which is how liveness is decided.
    /// Held in a static readonly field so the sweep allocates no delegate per call.
    /// </summary>
    private static readonly Func<int, string> DefaultProcessNameProbe = ResolveProcessName;

    private static string ResolveProcessName(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.ProcessName;
    }

    private static bool IsTrackedGameGone(int processId, ActiveGame active)
        => IsTrackedGameGone(processId, active, DefaultProcessNameProbe);

    /// <summary>
    /// Liveness decision for one tracked game, with the process-name lookup injected so the
    /// access-denied / transient / missing-PID branches are testable without the real process table.
    /// The production path passes <see cref="DefaultProcessNameProbe"/>.
    ///
    /// The expected name comes from the RUNTIME observation (<see cref="ActiveGame.ObservedProcessName"/>),
    /// not from <see cref="GameInfo.ExecutablePath"/>. Launcher entries - Steam/Xbox carry an install
    /// directory and no executable path - previously had no expected name at all, so PID reuse was
    /// undetectable for them and a missed stop event kept the game (and its optimizations) forever.
    /// The observed name is always populated, so that gap is closed.
    ///
    /// The probe is invoked BEFORE the expected-name check on purpose: it is also what reports a
    /// missing PID (ArgumentException -> gone), so deferring it would reintroduce the same leak.
    /// </summary>
    internal static bool IsTrackedGameGone(int processId, ActiveGame active, Func<int, string> processNameProbe)
    {
        try
        {
            var actualName = processNameProbe(processId);

            // PID exists but now belongs to a different image -> the original game exited and the
            // PID was reused. Treat as gone (compare against the observed running exe name).
            // Process.ProcessName drops a trailing ".exe" and nothing else, so strip exactly that:
            // stripping any extension would call a live "Foo.bin" a different process and revert
            // under it.
            var observed = active.ObservedProcessName;
            var expected = observed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? observed[..^4]
                : observed;
            return !string.IsNullOrEmpty(expected)
                && !string.Equals(actualName, expected, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Process names that are real game process names for some built-in profile but are shared with
    /// unrelated software, so the name alone is not evidence of that game. Matching one would
    /// optimize (and later revert around) whatever program actually owns the process.
    ///
    /// javaw.exe is the Minecraft Java process name and simultaneously the process name of every
    /// other Java desktop application. Blocking it removes standalone Minecraft Java
    /// auto-detection: Minecraft is still matched when a launcher scanner knows its install
    /// directory or when it is added manually, but an uncorrelated javaw.exe is no longer claimed.
    /// </summary>
    internal static readonly IReadOnlySet<string> AmbiguousProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "javaw.exe",
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
        bool suppressed;
        lock (_lock)
        {
            snapshot = _knownGames.ToList();
            suppressed = _suppressedInstallDirectories.Any(
                dir => normalizedPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
        }

        // A removed launcher game's folder: only the user's own manual entry for this exact
        // executable may match, which is how a removed game is brought back.
        if (suppressed)
        {
            var manual = snapshot.FirstOrDefault(g =>
                string.Equals(g.LauncherSource, "Manual", StringComparison.OrdinalIgnoreCase)
                && string.Equals(normalizedPath, g.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            return manual == null ? null : OnGameMatched(processId, normalizedPath, exeName, manual);
        }

        foreach (var game in snapshot)
        {
            // Primary matching strategy: check if executable is under install directory
            if (!string.IsNullOrEmpty(game.InstallDirectory))
            {
                var installDir = game.InstallDirectory.TrimEnd('\\') + '\\';
                if (normalizedPath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                {
                    return OnGameMatched(processId, normalizedPath, exeName, game);
                }
            }

            // Secondary matching strategy: exact executable path match
            if (!string.IsNullOrEmpty(game.ExecutablePath))
            {
                if (string.Equals(normalizedPath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    return OnGameMatched(processId, normalizedPath, exeName, game);
                }
            }
        }

        // Tertiary matching strategy: check exe name against BuiltInProfiles ProcessNames.
        // Catches games installed outside of scanned launcher directories (standalone launchers, etc.)
        // Names shared with unrelated software are excluded - see AmbiguousProcessNames.
        if (!AmbiguousProcessNames.Contains(exeName))
        {
            foreach (var builtIn in BuiltInProfiles.GetAll())
            {
                foreach (var pn in builtIn.ProcessNames)
                {
                    if (string.Equals(exeName, pn, StringComparison.OrdinalIgnoreCase))
                    {
                        var autoGame = ResolveBuiltInRecord(builtIn, normalizedPath);

                        _logger.Information(
                            "Auto-detected built-in profile game via process name: {GameName} ({ExeName})",
                            builtIn.DisplayName, exeName);

                        return OnGameMatched(processId, normalizedPath, exeName, autoGame);
                    }
                }
            }
        }

        // No match found - this is normal for most processes, so don't log
        return null;
    }

    /// <summary>
    /// Confirms that the PID's CURRENT occupant still carries the name the start event reported.
    ///
    /// <see cref="ProcessProbe"/> opens the process by PID, so its liveness proof belongs to
    /// whoever owns that PID at probe time - not necessarily to the process the event named. This
    /// re-reads the name from the live process table and requires the two to agree, which closes
    /// the window where a PID recycled between the event and the probe would be adopted under the
    /// dead process's name.
    ///
    /// Fails closed on mismatch and on any lookup failure: a name that cannot be corroborated is
    /// not evidence. The lookup is only a fresh <see cref="Process.GetProcessById(int)"/>-style
    /// observation; no assumption is made about how the runtime obtains the name, or about whether
    /// it succeeds for a kernel-anti-cheat-protected image.
    ///
    /// A process can still exit right after this observation, and a PID could in principle be
    /// reused by a process with the same name. Those remain the ordinary races the stop event and
    /// the liveness sweep exist to heal.
    /// </summary>
    /// <param name="liveName">The PID's current process-table name (no ".exe"), set on success.</param>
    private bool LiveNameCorroborates(int processId, string eventProcessName, out string liveName)
    {
        liveName = string.Empty;
        var expected = Path.GetFileNameWithoutExtension(eventProcessName);
        if (string.IsNullOrEmpty(expected))
            return false;

        string actual;
        try
        {
            actual = _liveProcessNameProbe(processId);
        }
        catch (Exception ex)
        {
            _logger.Debug(
                ex,
                "Name-only fallback rejected for PID {ProcessId} ({ProcessName}): the PID's current process name could not be read",
                processId, eventProcessName);
            return false;
        }

        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            liveName = actual;
            return true;
        }

        _logger.Debug(
            "Name-only fallback rejected for PID {ProcessId}: start event reported {EventName} but the PID now runs {ActualName}",
            processId, eventProcessName, actual);
        return false;
    }

    /// <summary>
    /// Matches a process by observed name alone. Reachable ONLY from a pathless start event whose
    /// PID was independently proven alive (<see cref="ProcessLiveness.AliveNoPath"/>) AND whose
    /// name was corroborated against the PID's current occupant by
    /// <see cref="LiveNameCorroborates"/> - a name with no liveness behind it is exactly the
    /// evidence that used to register dead processes.
    ///
    /// The gate is deliberately closed by default: a non-empty name that is not a known helper, not
    /// ambiguous, and matched by EXACTLY ONE built-in profile that has opted in via
    /// <see cref="GameSessionConfig.AllowNameOnlyFallback"/>. Zero or multiple matches fail closed.
    ///
    /// Eligibility is read from <see cref="BuiltInProfiles"/> directly and never from
    /// <c>GameProfileManager</c>: that list includes persisted user-editable profiles, and the flag
    /// is a security decision, not a preference. Known games are not scanned by filename either -
    /// a filename is not an identity.
    /// </summary>
    private GameInfo? MatchProcessByNameOnly(int processId, string processName)
    {
        if (string.IsNullOrEmpty(processName))
            return null;

        if (IsNonGameHelper(processName))
            return null;

        if (AmbiguousProcessNames.Contains(processName))
            return null;

        // Single materialization of the built-in list per attempt.
        GameSessionConfig? eligible = null;
        foreach (var builtIn in BuiltInProfiles.GetAll())
        {
            if (!builtIn.AllowNameOnlyFallback)
                continue;

            bool nameMatches = false;
            foreach (var pn in builtIn.ProcessNames)
            {
                if (string.Equals(processName, pn, StringComparison.OrdinalIgnoreCase))
                {
                    nameMatches = true;
                    break;
                }
            }

            if (!nameMatches)
                continue;

            if (eligible != null)
            {
                _logger.Debug(
                    "Name-only fallback rejected for {ProcessName}: more than one eligible built-in profile",
                    processName);
                return null;
            }

            eligible = builtIn;
        }

        if (eligible == null)
            return null;

        // There is no path to test against a removed game's folder, so use the evidence there is:
        // a removed launcher game's folder holds an executable of this name.
        List<string> suppressedDirectories;
        lock (_lock)
        {
            suppressedDirectories = _suppressedInstallDirectories.ToList();
        }

        if (suppressedDirectories.Any(dir => File.Exists(Path.Combine(dir, processName))))
        {
            _logger.Debug(
                "Name-only fallback rejected for {ProcessName}: a removed game's folder holds that executable",
                processName);
            return null;
        }

        var game = ResolveBuiltInRecord(eligible, null);

        _logger.Information(
            "Name-only built-in match for live path-unreadable process: {GameName} ({ProcessName}, PID {ProcessId})",
            eligible.DisplayName, processName, processId);

        return OnGameMatched(processId, null, processName, game);
    }

    /// <summary>
    /// Finds or creates the single runtime known-game record for a built-in profile, keyed by the
    /// stable generated built-in ID. Identity is the ID and never the display name: a launcher or
    /// manual entry for the same title is a different identity and must keep its own record.
    ///
    /// Path handling is one-directional - an unreadable observation may never erase a real path,
    /// and a later readable observation may fill an empty one but never replace one already known.
    /// This record is session/runtime state and is deliberately not written to KnownGamesStore.
    /// </summary>
    private GameInfo ResolveBuiltInRecord(GameSessionConfig builtIn, string? observedRootedPath)
    {
        var builtInId = GameInfo.GenerateId("builtin", builtIn.Id);

        lock (_lock)
        {
            var existing = _knownGames.FirstOrDefault(g => g.Id == builtInId);

            if (existing == null)
            {
                existing = new GameInfo
                {
                    Id = builtInId,
                    GameName = builtIn.DisplayName,
                    ExecutablePath = observedRootedPath ?? "",
                    InstallDirectory = observedRootedPath != null
                        ? Path.GetDirectoryName(observedRootedPath) ?? ""
                        : "",
                    LauncherSource = "BuiltIn"
                };
                _knownGames.Add(existing);
            }
            else if (observedRootedPath != null && string.IsNullOrEmpty(existing.ExecutablePath))
            {
                existing.ExecutablePath = observedRootedPath;
                existing.InstallDirectory = Path.GetDirectoryName(observedRootedPath) ?? "";
            }

            return existing;
        }
    }

    /// <summary>
    /// Commits a successful game match: binds the runtime evidence to the PID and fires GameStarted.
    /// The evidence is kept in <see cref="ActiveGame"/> rather than written back into the matched
    /// <see cref="GameInfo"/>, which stays persistent/scanned metadata.
    /// </summary>
    /// <param name="processId">Observed process ID.</param>
    /// <param name="observedExecutablePath">Rooted observed path, or null when unreadable.</param>
    /// <param name="observedProcessName">Observed filename with extension. Always populated.</param>
    /// <param name="game">The matched game identity.</param>
    private GameInfo OnGameMatched(int processId, string? observedExecutablePath, string observedProcessName, GameInfo game)
    {
        var active = new ActiveGame(game, observedProcessName, observedExecutablePath);

        // Idempotent: the startup process rundown and a live ETW/WMI start event can both match the
        // same PID. TryAdd ensures the game is tracked and GameStarted fires exactly once. PID reuse
        // is handled separately (HandleGameExited removes the old PID before a new game can reuse it).
        if (!_activeGames.TryAdd(processId, active))
            return game;

        _logger.Information("Game detected: {GameName} (PID: {ProcessId}, Source: {LauncherSource})",
            game.GameName, processId, game.LauncherSource);

        GameStarted?.Invoke(this, new GameDetectedEventArgs(
            game.Id,
            game.GameName,
            observedExecutablePath ?? string.Empty,
            observedProcessName,
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
