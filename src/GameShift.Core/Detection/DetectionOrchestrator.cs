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
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore;

    private bool _isOptimizing;

    /// <summary>
    /// Active game-specific actions for the current game session.
    /// Populated in OnGameStarted, cleared in OnAllGamesStopped.
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
    {
        _detector = detector;
        _engine = engine;
        _store = store;
        _scanners = scanners;
        _profileManager = profileManager;
        _dpcMonitor = dpcMonitor;
        _hardwareScan = hardwareScan;
        _logger = SettingsManager.Logger;
        _semaphore = new SemaphoreSlim(1, 1);
        _isOptimizing = false;

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
    public Task InitializeAsync()
    {
        _logger.Information("Initializing detection orchestrator");

        // 1. Load known games from persistent store
        _store.Load();

        // 2. Scan all launcher libraries for installed games
        _detector.ScanLibraries();

        // 3. Merge scanned games into store (combines with manual additions)
        _store.MergeScannedGames(_detector.GetKnownGames());

        // 4. Sync store games back to detector (includes manual additions from disk)
        foreach (var game in _store.GetAllGames())
        {
            _detector.AddKnownGame(game);
        }

        // 5. Subscribe to game detection events
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;
        _detector.AllGamesStopped += OnAllGamesStopped;

        // 6. Start WMI process monitoring
        _detector.StartMonitoring();

        _logger.Information("Detection system initialized. Monitoring {Count} known games.",
            _store.GetAllGames().Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles game start events.
    /// Activates optimizations on first game start.
    /// For preset games (Valorant, LoL, Deadlock): auto-creates a profile on first detection
    /// and populates GameSpecificActions at runtime. Applies GameActions after engine activation.
    /// Multiple simultaneous games keep optimizations active.
    /// </summary>
    private async void OnGameStarted(object? sender, GameDetectedEventArgs e)
    {
        try
        {
            _logger.Information("Game started: {GameName} (PID: {ProcessId})", e.GameName, e.ProcessId);

            await _semaphore.WaitAsync();
            try
            {
                if (!_isOptimizing)
                {
                    // Extract executable name for preset lookup
                    var executableName = Path.GetFileName(e.ExecutablePath);

                    GameProfile profile;

                    if (CompetitivePresets.IsPresetGame(executableName))
                    {
                        // Preset game — check for existing custom profile
                        profile = _profileManager.GetProfileForGame(e.GameId);

                        if (profile.Id == "default")
                        {
                            // No custom profile on disk — auto-create from preset
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
                                // Preset returned null (shouldn't happen if IsPresetGame is true) — fall back to default
                                _logger.Warning("IsPresetGame returned true but GetPresetProfile returned null for {ExecutableName}", executableName);
                            }
                        }
                        else
                        {
                            // Custom profile already exists — preserve it unchanged
                            _logger.Information("Using existing custom profile for {GameName}, preset actions will still apply", e.GameName);
                        }

                        // Populate GameActions at runtime regardless of auto-creation or existing profile
                        profile.GameSpecificActions = CompetitivePresets.GetGameActions(executableName);
                    }
                    else
                    {
                        // Not a preset game — standard profile lookup
                        profile = _profileManager.GetProfileForGame(e.GameId);
                    }

                    profile.ProcessId = e.ProcessId;

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

                    await _engine.ActivateProfileAsync(profile);
                    _isOptimizing = true;

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
                            // No hardware scan — apply all Tier 1 actions (skip conditional filtering)
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
                                action.Apply(_actionSnapshot);
                                _logger.Information("Applied game action: {ActionName}", action.Name);
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
    /// Handles game stop events.
    /// No action taken here - we only deactivate when ALL games stop.
    /// </summary>
    private void OnGameStopped(object? sender, GameDetectedEventArgs e)
    {
        try
        {
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
    /// Handles all games stopped event.
    /// Reverts GameActions in reverse order BEFORE deactivating the engine.
    /// Then deactivates optimizations when the last game exits.
    /// </summary>
    private async void OnAllGamesStopped(object? sender, EventArgs e)
    {
        try
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_isOptimizing)
                {
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
                        _activeGameActions = null;
                        _actionSnapshot = null;
                    }

                    // Stop DPC monitoring
                    if (_dpcMonitor != null && _dpcMonitor.IsMonitoring)
                    {
                        _dpcMonitor.Stop();
                        _logger.Information("DPC monitoring stopped");
                    }

                    await _engine.DeactivateProfileAsync();
                    _isOptimizing = false;

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
        _detector.RemoveKnownGame(gameId);
        return _store.RemoveGame(gameId);
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
