using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Detection;

/// <summary>
/// Orchestrates the complete game detection pipeline.
/// Bridges GameDetector events to OptimizationEngine activate/deactivate.
/// Manages the startup sequence: load store -> scan libraries -> merge -> monitor.
/// Implements the end-to-end detection-to-optimization flow.
/// Also handles preset auto-creation and GameAction lifecycle
/// (apply after optimizations, revert before optimizations) for competitive games.
/// Every way a session ends (the last game exiting, pause, exit, a revert from the dashboard)
/// goes through one teardown, <see cref="EndSessionCoreAsync"/>, so no path can skip the
/// game-specific actions.
/// </summary>
public class DetectionOrchestrator
{
    private readonly GameDetector _detector;
    private readonly OptimizationEngine _engine;
    private readonly KnownGamesStore _store;
    private readonly IEnumerable<ILibraryScanner> _scanners;
    private readonly ProfileManager _profileManager;
    private readonly DpcLatencyMonitor? _dpcMonitor;
    private readonly HardwareScanResult? _hardwareScan;
    private readonly Func<string, List<GameAction>> _gameActionsFor;
    private readonly Action _startMonitoring;
    private readonly Action _stopMonitoring;
    private readonly ILogger _logger;

    private readonly Func<IReadOnlyList<string>> _removeLeftoverFirewallRules;

    // Serializes session starts and ends. Its holder never needs the UI thread: the public entry
    // points run on the thread pool, no await here or in the engine resumes on the caller's context,
    // and the events raised under it only queue work for the UI thread. App.OnExit blocks the UI
    // thread until ShutdownAsync gets this semaphore, so a holder that waited for that thread would
    // never release it.
    private readonly SemaphoreSlim _semaphore;

    // Session state. Written only while holding _semaphore.
    private bool _isOptimizing;
    private bool _isPaused;
    private bool _isShutDown;
    private bool _initialized;

    /// <summary>
    /// Start events of the games the detector is tracking, oldest first, so resuming can optimize a
    /// game that kept running while monitoring was paused. Guarded by locking the list itself.
    /// </summary>
    private readonly List<GameDetectedEventArgs> _trackedStarts = new();

    /// <summary>
    /// Active game-specific actions for the current game session.
    /// Populated when a game session starts, cleared when it ends.
    /// Null when no game is active.
    /// </summary>
    private List<GameAction>? _activeGameActions;

    /// <summary>
    /// Snapshot captured at game action apply time.
    /// Passed to each action's Revert call for consistent state restoration.
    /// Null when no game is active.
    /// </summary>
    private SystemStateSnapshot? _actionSnapshot;

    /// <summary>
    /// Gets whether optimizations are currently active.
    /// </summary>
    public bool IsOptimizing => _isOptimizing;

    /// <summary>
    /// Gets whether monitoring is paused (<see cref="PauseAsync"/>) and not yet resumed.
    /// </summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Exposes the DPC latency monitor instance for dashboard ViewModel
    /// to subscribe to LatencySampled events and display live data.
    /// </summary>
    public DpcLatencyMonitor? DpcMonitor => _dpcMonitor;

    /// <summary>Hardware scan results for UI display. Null if scan not yet complete.</summary>
    public HardwareScanResult? HardwareScan => _hardwareScan;

    /// <summary>
    /// Creates a new DetectionOrchestrator instance.
    /// </summary>
    /// <param name="detector">Game detector for process monitoring</param>
    /// <param name="engine">Optimization engine to activate/deactivate</param>
    /// <param name="store">Known games store for persistence</param>
    /// <param name="scanners">Library scanners for game detection</param>
    /// <param name="profileManager">Profile manager for per-game optimization profiles</param>
    /// <param name="dpcMonitor">Optional DPC latency monitor (passive, not an IOptimization)</param>
    /// <param name="hardwareScan">Optional hardware scan result for conditional action filtering</param>
    public DetectionOrchestrator(
        GameDetector detector,
        OptimizationEngine engine,
        KnownGamesStore store,
        IEnumerable<ILibraryScanner> scanners,
        ProfileManager profileManager,
        DpcLatencyMonitor? dpcMonitor = null,
        HardwareScanResult? hardwareScan = null)
        : this(detector, engine, store, scanners, profileManager, dpcMonitor, hardwareScan,
            CompetitivePresets.GetGameActions, detector.StartMonitoring, detector.StopMonitoring,
            () => FirewallRuleAction.RemoveLeftovers(CompetitivePresets.GetFirewallRules()))
    {
    }

    /// <summary>
    /// Creates an orchestrator that takes preset games' actions from <paramref name="gameActionsFor"/>,
    /// starts and stops process monitoring through the given callbacks and removes leftover firewall
    /// rules through <paramref name="removeLeftoverFirewallRules"/>. Used by tests, so they never
    /// reach Defender, the firewall, the registry or a real process monitor.
    /// </summary>
    internal DetectionOrchestrator(
        GameDetector detector,
        OptimizationEngine engine,
        KnownGamesStore store,
        IEnumerable<ILibraryScanner> scanners,
        ProfileManager profileManager,
        DpcLatencyMonitor? dpcMonitor,
        HardwareScanResult? hardwareScan,
        Func<string, List<GameAction>> gameActionsFor,
        Action startMonitoring,
        Action stopMonitoring,
        Func<IReadOnlyList<string>> removeLeftoverFirewallRules)
    {
        _detector = detector;
        _engine = engine;
        _store = store;
        _scanners = scanners;
        _profileManager = profileManager;
        _dpcMonitor = dpcMonitor;
        _hardwareScan = hardwareScan;
        _gameActionsFor = gameActionsFor;
        _startMonitoring = startMonitoring;
        _stopMonitoring = stopMonitoring;
        _removeLeftoverFirewallRules = removeLeftoverFirewallRules;
        _logger = SettingsManager.Logger;
        _semaphore = new SemaphoreSlim(1, 1);
        _isOptimizing = false;
        _engine.ProtectSessionGames(() => _detector.GetActiveGames().Keys);

        // Subscribe to DPC spike events for CSV logging
        if (_dpcMonitor != null)
        {
            _dpcMonitor.DpcSpikeDetected += OnDpcSpikeDetected;
        }
    }

    /// <summary>
    /// Initializes the complete detection system.
    /// Startup sequence: load store -> scan libraries -> merge -> sync to detector -> subscribe to events -> start monitoring.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.Information("Initializing detection orchestrator");

        LoadAndSyncLibrary();

        // 5. Subscribe to game detection events
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;
        _detector.AllGamesStopped += OnAllGamesStopped;

        // 6. Start process monitoring, unless the user paused or exited while startup was still
        // running (the tray is live before this point). Resuming starts it then.
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            _initialized = true;
            if (!_isPaused && !_isShutDown)
                _startMonitoring();
        }
        finally
        {
            _semaphore.Release();
        }

        _logger.Information("Detection system initialized. Monitoring {Count} known games.",
            _store.GetAllGames().Count);
    }

    /// <summary>
    /// Startup steps 1-4, without starting monitoring: load the store, scan launchers, merge, and
    /// make the store the detector's authoritative list.
    /// </summary>
    internal void LoadAndSyncLibrary()
    {
        // 1. Load known games from persistent store
        _store.Load();

        // 2. Scan all launcher libraries for installed games
        _detector.ScanLibraries();
        var scannedGames = _detector.GetKnownGames();

        // 3. Merge scanned games into store (combines with manual additions)
        _store.MergeScannedGames(scannedGames);

        // Launchers keep reporting games the user removed. This scan has their current install
        // folders, which is where the game would start from under a built-in identity.
        foreach (var game in scannedGames)
        {
            if (_store.IsIgnored(game.Id))
                _detector.SuppressInstallDirectory(game.InstallDirectory);
        }

        // 4. Sync store games back to detector (includes manual additions from disk)
        var storeGames = _store.GetAllGames();
        var storeGameIds = storeGames
            .Select(game => game.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var game in _detector.GetKnownGames())
        {
            if (!storeGameIds.Contains(game.Id))
            {
                _detector.RemoveKnownGame(game.Id);
            }
        }

        foreach (var game in storeGames)
        {
            _detector.AddKnownGame(game);
        }
    }

    /// <summary>
    /// Handles game start events.
    /// Activates optimizations on first game start.
    /// For preset games (Valorant, LoL, Deadlock): auto-creates a profile on first detection
    /// and populates GameSpecificActions at runtime. Applies GameActions after engine activation.
    /// Multiple simultaneous games keep optimizations active.
    /// While monitoring is paused or GameShift is exiting, no session starts; the start is still
    /// remembered, so resuming can optimize the game if it is still running.
    /// </summary>
    private async void OnGameStarted(object? sender, GameDetectedEventArgs e)
    {
        try
        {
            _logger.Information("Game started: {GameName} (PID: {ProcessId})", e.GameName, e.ProcessId);

            // Recorded before waiting, so it is in place before this game's stop event can remove it.
            lock (_trackedStarts)
            {
                _trackedStarts.RemoveAll(s => s.ProcessId == e.ProcessId);
                _trackedStarts.Add(e);
            }

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isShutDown)
                {
                    _logger.Information("GameShift is exiting, not optimizing {GameName}", e.GameName);
                }
                else if (_isPaused)
                {
                    _logger.Information("Monitoring is paused, not optimizing {GameName} until it is resumed", e.GameName);
                }
                else if (!_isOptimizing)
                {
                    await ActivateSessionCoreAsync(e).ConfigureAwait(false);
                }
                else
                {
                    // Additional game detected while optimizations already active
                    _logger.Information("Additional game detected: {GameName}. Optimizations already active.", e.GameName);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error handling game start event for {GameName}", e.GameName);
        }
    }

    /// <summary>
    /// Starts the session for a detected game: resolves its profile, activates the engine, then
    /// applies the game-specific actions. Used for a game start and for a game that is still running
    /// when monitoring resumes. The caller holds the semaphore.
    /// </summary>
    private async Task ActivateSessionCoreAsync(GameDetectedEventArgs e)
    {
        // Executable name for preset lookup. Read from the event's observed process
        // name, not from the path: a protected game can be detected with no readable
        // path, and deriving the name from an empty path would silently match nothing.
        var executableName = e.ProcessName;

        GameProfile profile;

        if (CompetitivePresets.IsPresetGame(executableName))
        {
            // Preset game - check for existing custom profile
            profile = _profileManager.GetProfileForGame(e.GameId);

            if (profile.Id == "default")
            {
                // No custom profile on disk - auto-create from preset
                var presetProfile = CompetitivePresets.GetPresetProfile(executableName);
                if (presetProfile != null)
                {
                    presetProfile.Id = e.GameId;
                    presetProfile.ExecutablePath = e.ExecutablePath;
                    presetProfile.LauncherSource = e.LauncherSource;
                    _profileManager.SaveProfile(presetProfile);
                    _logger.Information("Auto-created competitive preset profile for {GameName}", e.GameName);
                    profile = presetProfile;
                }
                else
                {
                    // Preset returned null (shouldn't happen if IsPresetGame is true) - fall back to default
                    _logger.Warning("IsPresetGame returned true but GetPresetProfile returned null for {ExecutableName}", executableName);
                }
            }
            else
            {
                // Custom profile already exists - preserve it unchanged
                _logger.Information("Using existing custom profile for {GameName}, preset actions will still apply", e.GameName);
            }

            // Populate GameActions at runtime regardless of auto-creation or existing profile
            profile.GameSpecificActions = _gameActionsFor(executableName);
        }
        else
        {
            // Not a preset game - standard profile lookup
            profile = _profileManager.GetProfileForGame(e.GameId);
        }

        profile.ProcessId = e.ProcessId;

        // Ensure the profile carries the detected executable so the IFEO fallback can
        // target the image and the apply modules can verify process identity.
        if (string.IsNullOrEmpty(profile.ExecutableName))
            profile.ExecutableName = executableName;
        if (string.IsNullOrEmpty(profile.ExecutablePath))
            profile.ExecutablePath = e.ExecutablePath;

        // Backfill anti-cheat metadata for built-in titles that resolved to the default
        // profile (their kernel anti-cheat is known in BuiltInProfiles but not on the
        // default GameProfile). This routes EAC/BattlEye games through the IFEO-at-launch
        // path instead of a direct priority/affinity write the anti-cheat would just block.
        if (profile.AntiCheat == AntiCheatType.None)
        {
            var builtIn = GameShift.Core.GameProfiles.BuiltInProfiles.GetAll().FirstOrDefault(b =>
                b.ProcessNames.Any(pn => string.Equals(pn, executableName, StringComparison.OrdinalIgnoreCase)));
            if (builtIn != null && builtIn.AntiCheat != AntiCheatType.None)
            {
                profile.AntiCheat = builtIn.AntiCheat;
                _logger.Information(
                    "Backfilled anti-cheat {AntiCheat} for {GameName} from built-in profile",
                    builtIn.AntiCheat, e.GameName);
            }
        }

        // If profile has no custom memory threshold, use global setting
        if (profile.MemoryThresholdMB == 0)
        {
            var settings = SettingsManager.Load();
            profile.MemoryThresholdMB = settings.MemoryThresholdMB;
        }

        // Auto-set intensity only for the default catch-all profile;
        // game-specific profiles use their saved Intensity value
        if (profile.Id == "default")
            profile.Intensity = GetIntensityForExecutable(executableName);

        _logger.Information("Using profile '{ProfileId}' for {GameName} (intensity: {Intensity})",
            profile.Id, e.GameName, profile.Intensity);

        // Counted as active before anything is applied, so a failure part-way through is still torn
        // down when the session ends.
        _isOptimizing = true;
        await _engine.ActivateProfileAsync(profile).ConfigureAwait(false);

        _logger.Information("Optimizations activated for: {GameName}", e.GameName);

        // Apply game-specific actions after IOptimization.Apply completes
        // Filter to Tier 1 + hardware-matched actions for auto-apply
        if (profile.GameSpecificActions.Count > 0)
        {
            if (_hardwareScan != null)
            {
                _activeGameActions = new List<GameAction>(
                    profile.GameSpecificActions
                        .Where(a => a.Tier == 1 && a.IsHardwareMatch(_hardwareScan)));
                _logger.Information(
                    "Filtered {Total} game actions to {Applied} for auto-apply (Tier 1 + hardware match)",
                    profile.GameSpecificActions.Count, _activeGameActions.Count);
            }
            else
            {
                // No hardware scan - apply all Tier 1 actions (skip conditional filtering)
                _activeGameActions = new List<GameAction>(
                    profile.GameSpecificActions.Where(a => a.Tier == 1));
                _logger.Information(
                    "No hardware scan available, applying {Count} Tier 1 actions (unfiltered)",
                    _activeGameActions.Count);
            }

            _actionSnapshot = SystemStateSnapshot.Capture();

            foreach (var action in _activeGameActions)
            {
                try
                {
                    action.ExecutablePath = e.ExecutablePath;
                    action.Apply(_actionSnapshot);
                    _logger.Information("Applied game action: {ActionName}", action.Name);

                    // Journal a reversible record so the watchdog can undo this action's
                    // persistent state (registry values, firewall rules) after a crash.
                    var crashRecord = action.GetCrashRevertRecord();
                    if (crashRecord != null)
                        _engine.Journal.RecordGameAction(crashRecord.Kind, crashRecord.Payload);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Game action failed (non-blocking): {ActionName}", action.Name);
                }
            }

            _logger.Information("Applied {Count} game-specific actions", _activeGameActions.Count);

            // Process Tier 3 tips (one-time, self-guarded via DismissedTips)
            var tips = profile.GameSpecificActions.Where(a => a.Tier == 3);
            foreach (var tip in tips)
            {
                try
                {
                    tip.Apply(_actionSnapshot);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Tip action failed (non-blocking): {ActionName}", tip.Name);
                }
            }
        }

        // Start DPC monitoring if enabled in profile
        if (_dpcMonitor != null && profile.EnableDpcMonitoring)
        {
            var settings = SettingsManager.Load();
            var threshold = profile.DpcThresholdMicroseconds > 0
                ? profile.DpcThresholdMicroseconds
                : settings.DefaultDpcThresholdMicroseconds;
            _dpcMonitor.Start(threshold);
            _logger.Information("DPC monitoring started with threshold {Threshold}us", threshold);
        }
    }

    /// <summary>
    /// Handles game stop events.
    /// No action taken here - we only deactivate when ALL games stop.
    /// </summary>
    private void OnGameStopped(object? sender, GameDetectedEventArgs e)
    {
        try
        {
            lock (_trackedStarts)
                _trackedStarts.RemoveAll(s => s.ProcessId == e.ProcessId);

            var remainingCount = _detector.GetActiveGames().Count;
            _logger.Information("Game stopped: {GameName} (PID: {ProcessId}). {RemainingCount} game(s) still running.",
                e.GameName, e.ProcessId, remainingCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error handling game stop event for {GameName}", e.GameName);
        }
    }

    /// <summary>
    /// Handles all games stopped event: ends the game session when the last game exits.
    /// A Quick Optimize session started without a game is not part of it and stays.
    /// </summary>
    private async void OnAllGamesStopped(object? sender, EventArgs e)
    {
        try
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isOptimizing)
                {
                    await EndSessionCoreAsync("all games exited").ConfigureAwait(false);
                    _logger.Information("All games exited. Optimizations deactivated.");
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error handling all games stopped event");
        }
    }

    /// <summary>
    /// Ends the current session at the user's request (the dashboard's revert). A game session ends
    /// as a whole, game-specific actions included, and the game runs on without one; a Quick Optimize
    /// session has only the engine's optimizations to revert.
    /// </summary>
    public Task EndSessionAsync() => RunExclusiveAsync(async () =>
    {
        // The user's revert wins over pausing: resuming must not optimize the games of this session
        // again. A game started from now on is a new start event.
        if (_isOptimizing)
        {
            lock (_trackedStarts)
                _trackedStarts.Clear();
        }

        await EndSessionCoreAsync("reverted by the user").ConfigureAwait(false);
    });

    /// <summary>
    /// Pauses monitoring: stops process monitoring and ends the game session. No session starts until
    /// <see cref="ResumeAsync"/>, including for start events already in flight. A Quick Optimize
    /// session is not part of monitoring and stays.
    /// </summary>
    public Task PauseAsync() => RunExclusiveAsync(PauseCoreAsync);

    /// <summary>
    /// Resumes monitoring after <see cref="PauseAsync"/>. Nothing watched for process exits while
    /// paused, so games that exited meanwhile are released first and their stop events fire now. The
    /// oldest game still running then gets its session back from its remembered start, and restarting
    /// monitoring picks up games started while paused.
    /// </summary>
    public Task ResumeAsync() => RunExclusiveAsync(ResumeCoreAsync);

    /// <summary>
    /// Pauses monitoring if it is running, otherwise resumes it, deciding under the lock so that two
    /// quick toggles always pause and then resume. Returns whether monitoring is paused afterwards.
    /// </summary>
    public async Task<bool> TogglePauseAsync()
    {
        var paused = false;
        await RunExclusiveAsync(async () =>
        {
            if (_isPaused)
                await ResumeCoreAsync().ConfigureAwait(false);
            else
                await PauseCoreAsync().ConfigureAwait(false);
            paused = _isPaused;
        }).ConfigureAwait(false);
        return paused;
    }

    /// <summary>
    /// Ends whatever session is active and stops monitoring, for GameShift exiting. No session starts
    /// afterwards, including for start events already in flight.
    /// </summary>
    public Task ShutdownAsync() => RunExclusiveAsync(async () =>
    {
        _isShutDown = true;
        _stopMonitoring();
        await EndSessionCoreAsync("GameShift is exiting").ConfigureAwait(false);
    });

    /// <summary>
    /// Runs <paramref name="body"/> on the thread pool while holding the semaphore. A session's
    /// game actions call PowerShell, for up to 15 seconds each, so a UI caller must not run them.
    /// </summary>
    private Task RunExclusiveAsync(Func<Task> body) => Task.Run(async () =>
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    });

    /// <summary><see cref="PauseAsync"/>'s work. The caller holds the semaphore.</summary>
    private async Task PauseCoreAsync()
    {
        if (_isPaused || _isShutDown)
            return;

        _isPaused = true;
        _stopMonitoring();

        if (_isOptimizing)
            await EndSessionCoreAsync("monitoring paused").ConfigureAwait(false);

        _logger.Information("Game monitoring paused");
    }

    /// <summary><see cref="ResumeAsync"/>'s work. The caller holds the semaphore.</summary>
    private async Task ResumeCoreAsync()
    {
        if (!_isPaused || _isShutDown)
            return;

        _isPaused = false;

        try
        {
            _detector.ReconcileActiveGamesOnce();

            var stillRunning = OldestTrackedStart();
            if (stillRunning != null && !_isOptimizing)
            {
                _logger.Information("{GameName} (PID: {ProcessId}) is still running, optimizing it again",
                    stillRunning.GameName, stillRunning.ProcessId);
                await ActivateSessionCoreAsync(stillRunning).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Monitoring must restart regardless, or GameShift would stay silently paused.
            _logger.Error(ex, "Could not optimize a still-running game on resume");
        }

        if (_initialized)
            _startMonitoring();

        _logger.Information("Game monitoring resumed");
    }

    /// <summary>
    /// Removes the preset games' firewall rules that an earlier version or a crash left behind. Holds
    /// the semaphore and does nothing while a game session is active, whose rules are its own. Call
    /// once at startup before <see cref="InitializeAsync"/>, so a game detected at startup waits for
    /// it. Never throws.
    /// </summary>
    public async Task RemoveLeftoverFirewallRulesAsync()
    {
        try
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isOptimizing || _isShutDown)
                    return;

                var removed = await Task.Run(_removeLeftoverFirewallRules).ConfigureAwait(false);
                foreach (var ruleName in removed)
                    _logger.Information("Removed leftover firewall rule {RuleName}", ruleName);
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not check for leftover firewall rules");
        }
    }

    /// <summary>
    /// The one teardown for every way a session ends. Game-specific actions are reverted first, in
    /// reverse order, then DPC monitoring stops and the engine reverts its optimizations. Does nothing
    /// when there is neither a game session nor an engine optimization to revert, which also leaves an
    /// earlier run's journal untouched. The caller holds the semaphore.
    /// </summary>
    private async Task EndSessionCoreAsync(string reason)
    {
        if (!_isOptimizing && _engine.AppliedCount == 0)
            return;

        _logger.Information("Ending optimization session: {Reason}", reason);

        // Revert game-specific actions BEFORE IOptimization.Revert (reverse order)
        if (_activeGameActions != null && _activeGameActions.Count > 0)
        {
            for (int i = _activeGameActions.Count - 1; i >= 0; i--)
            {
                try
                {
                    _activeGameActions[i].Revert(_actionSnapshot!);
                    _logger.Information("Reverted game action: {ActionName}", _activeGameActions[i].Name);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Game action revert failed (non-blocking): {ActionName}", _activeGameActions[i].Name);
                }
            }

            _logger.Information("Reverted {Count} game-specific actions", _activeGameActions.Count);
        }

        _activeGameActions = null;
        _actionSnapshot = null;

        // Stop DPC monitoring
        if (_dpcMonitor != null && _dpcMonitor.IsMonitoring)
        {
            _dpcMonitor.Stop();
            _logger.Information("DPC monitoring stopped");
        }

        await _engine.DeactivateProfileAsync().ConfigureAwait(false);
        _isOptimizing = false;
    }

    /// <summary>
    /// The oldest remembered start whose game the detector still tracks under that PID, or null.
    /// Starts the detector no longer tracks are dropped; one can linger when a game's stop event was
    /// raised before its start event reached this class.
    /// </summary>
    private GameDetectedEventArgs? OldestTrackedStart()
    {
        var tracked = _detector.GetActiveGames();
        lock (_trackedStarts)
        {
            _trackedStarts.RemoveAll(s => !tracked.TryGetValue(s.ProcessId, out var game) || game.Id != s.GameId);
            return _trackedStarts.FirstOrDefault();
        }
    }

    /// <summary>
    /// Completes once every operation that was already waiting for or holding the semaphore has
    /// finished. Game events are handled by fire-and-forget handlers; tests use this to wait for them.
    /// </summary>
    internal async Task WhenIdleAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        _semaphore.Release();
    }

    /// <summary>
    /// Logs DPC spikes to CSV for later analysis.
    /// Fire-and-forget: exceptions are logged but never block the monitor or game session.
    /// CSV format: timestamp, latency_us, driver, game_active
    /// </summary>
    private void OnDpcSpikeDetected(object? sender, DpcSpikeEventArgs e)
    {
        try
        {
            var logsDir = SettingsManager.GetLogsPath();
            if (!Directory.Exists(logsDir))
                Directory.CreateDirectory(logsDir);

            var csvPath = Path.Combine(logsDir, "dpc_log.csv");
            bool writeHeader = !File.Exists(csvPath);

            using var writer = new StreamWriter(csvPath, append: true);
            if (writeHeader)
                writer.WriteLine("timestamp,latency_us,driver,game_active");

            writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{e.LatencyMicroseconds:F0},{e.DriverName ?? ""},{(_isOptimizing ? "true" : "false")}");

            _logger.Information("DPC spike logged: {Latency}us (driver: {Driver})",
                e.LatencyMicroseconds, e.DriverName ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to log DPC spike to CSV (non-blocking)");
        }
    }

    /// <summary>
    /// Manually adds a game by executable path.
    /// Delegates to store for persistence and syncs to detector for live matching.
    /// </summary>
    /// <param name="exePath">Full path to game executable</param>
    /// <returns>Created GameInfo or null if path is invalid</returns>
    public GameInfo? AddManualGame(string exePath)
    {
        var gameInfo = _store.AddManualGame(exePath);
        if (gameInfo != null)
        {
            // Add to detector's live matching list
            _detector.AddKnownGame(gameInfo);
        }
        return gameInfo;
    }

    /// <summary>
    /// Removes a game from both the store and detector.
    /// </summary>
    /// <param name="gameId">Unique game ID</param>
    /// <returns>True if game was found and removed</returns>
    public bool RemoveGame(string gameId)
    {
        var game = _store.GetAllGames().FirstOrDefault(g => g.Id == gameId);
        var removed = _store.RemoveGame(gameId);
        if (!removed)
        {
            return false;
        }

        _detector.RemoveKnownGame(gameId);

        // A removed launcher game must not come back through a built-in profile this session either.
        if (game != null && _store.IsIgnored(gameId))
            _detector.SuppressInstallDirectory(game.InstallDirectory);

        return true;
    }

    /// <summary>
    /// Gets all known games from the store.
    /// </summary>
    /// <returns>Read-only list of known games</returns>
    public IReadOnlyList<GameInfo> GetKnownGames()
    {
        return _store.GetAllGames();
    }

    /// <summary>
    /// Gets currently active game processes from the detector.
    /// </summary>
    /// <returns>Read-only dictionary of active games (PID -> GameInfo)</returns>
    public IReadOnlyDictionary<int, GameInfo> GetActiveGames()
    {
        return _detector.GetActiveGames();
    }

    /// <summary>
    /// Unsubscribes all event handlers registered during construction and InitializeAsync(),
    /// and releases the internal SemaphoreSlim. Call during application shutdown to prevent
    /// leaks and stale callbacks.
    /// </summary>
    public void Cleanup()
    {
        _detector.GameStarted -= OnGameStarted;
        _detector.GameStopped -= OnGameStopped;
        _detector.AllGamesStopped -= OnAllGamesStopped;
        if (_dpcMonitor != null) _dpcMonitor.DpcSpikeDetected -= OnDpcSpikeDetected;
        _semaphore.Dispose();
    }

    /// <summary>
    /// Maps executable names to optimization intensity tiers.
    /// Competitive: FPS, arena shooters, rhythm games (latency-critical).
    /// Casual: RPGs, MOBAs, open-world (default for all unknown games).
    /// </summary>
    private static OptimizationIntensity GetIntensityForExecutable(string executableName)
    {
        return executableName.ToLowerInvariant() switch
        {
            "overwatch.exe" => OptimizationIntensity.Competitive,
            "valorant-win64-shipping.exe" => OptimizationIntensity.Competitive,
            "cs2.exe" => OptimizationIntensity.Competitive,
            "project8.exe" => OptimizationIntensity.Competitive,
            "fortnite" or "fortniteclient-win64-shipping.exe" => OptimizationIntensity.Competitive,
            "cod.exe" => OptimizationIntensity.Competitive,
            "rustclient.exe" => OptimizationIntensity.Competitive,
            "r5apex.exe" or "r5apex_dx12.exe" => OptimizationIntensity.Competitive,
            "osu!.exe" => OptimizationIntensity.Competitive,
            _ => OptimizationIntensity.Casual
        };
    }
}
