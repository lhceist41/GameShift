using GameShift.Core.Config;

namespace GameShift.Core.BackgroundMode;

/// <summary>
/// Master orchestrator for all Background Mode services.
/// Manages lifecycle (start/stop) of all 5 sub-services based on settings.
/// Provides gaming session hooks for services that change behavior during games
/// (TaskDeferral, PowerPlanManager).
/// </summary>
public class BackgroundModeService : IDisposable
{
    private readonly StandbyListCleaner _standbyListCleaner = new();
    private readonly TimerResolutionService _timerResolution = new();
    private readonly PowerPlanManager _powerPlan = new();
    private readonly TaskDeferralService _taskDeferral = new();
    private readonly ProcessPriorityPersistence _processPriority = new();

    private bool _enabled;

    /// <summary>Whether Background Mode is currently active.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Exposes StandbyListCleaner for dashboard status display.</summary>
    public StandbyListCleaner StandbyListCleaner => _standbyListCleaner;

    /// <summary>Exposes TimerResolutionService for dashboard status display.</summary>
    public TimerResolutionService TimerResolution => _timerResolution;

    /// <summary>Exposes PowerPlanManager for dashboard and conflict resolution.</summary>
    public PowerPlanManager PowerPlan => _powerPlan;

    /// <summary>Exposes TaskDeferralService for dashboard status display.</summary>
    public TaskDeferralService TaskDeferral => _taskDeferral;

    /// <summary>Exposes ProcessPriorityPersistence for dashboard status display.</summary>
    public ProcessPriorityPersistence ProcessPriority => _processPriority;

    /// <summary>
    /// Starts all enabled Background Mode services based on current settings.
    /// Called at app startup if Background Mode is enabled, or when user enables it.
    /// </summary>
    public void Start()
    {
        var settings = SettingsManager.Load();
        var bgSettings = settings.BackgroundMode;
        if (bgSettings == null || !bgSettings.Enabled)
        {
            SettingsManager.Logger.Information("[BackgroundMode] Not enabled, skipping start");
            return;
        }

        SettingsManager.Logger.Information("[BackgroundMode] Starting services...");

        if (bgSettings.StandbyListCleanerEnabled)
            _standbyListCleaner.Start(bgSettings);

        if (bgSettings.TimerResolutionEnabled)
            _timerResolution.Start(bgSettings);

        if (bgSettings.PowerPlanEnabled)
            _powerPlan.Start(bgSettings);

        if (bgSettings.ProcessPriorityEnabled)
            _processPriority.Start(bgSettings);

        // TaskDeferral is event-driven (starts/stops with gaming sessions), not always-on
        // It will be triggered by OnGamingStart/OnGamingStop

        _enabled = true;
        SettingsManager.Logger.Information("[BackgroundMode] All enabled services started");
    }

    /// <summary>
    /// Stops all Background Mode services. Called when user disables Background Mode
    /// or on app shutdown.
    /// </summary>
    public void Stop()
    {
        SettingsManager.Logger.Information("[BackgroundMode] Stopping all services...");

        _standbyListCleaner.Stop();
        _timerResolution.Stop();
        _powerPlan.Stop();
        _taskDeferral.RestoreTasks(); // Ensure tasks are re-enabled
        _processPriority.Stop();

        _enabled = false;
        SettingsManager.Logger.Information("[BackgroundMode] All services stopped");
    }

    /// <summary>
    /// Notifies Background Mode that a gaming session has started.
    /// Triggers task deferral and switches power plan to Gaming state.
    /// </summary>
    public void OnGamingStart()
    {
        if (!_enabled) return;

        var settings = SettingsManager.Load();
        var bgSettings = settings.BackgroundMode;
        if (bgSettings == null) return;

        if (bgSettings.TaskDeferralEnabled)
            _taskDeferral.DeferTasks();

        if (bgSettings.PowerPlanEnabled)
            _powerPlan.OnGamingStart();

        SettingsManager.Logger.Information("[BackgroundMode] Gaming session started — task deferral and power plan adjusted");
    }

    /// <summary>
    /// Notifies Background Mode that a gaming session has ended.
    /// Restores deferred tasks and returns power plan to Desktop state.
    /// </summary>
    public void OnGamingStop()
    {
        if (!_enabled) return;

        _taskDeferral.RestoreTasks();
        _powerPlan.OnGamingStop();

        SettingsManager.Logger.Information("[BackgroundMode] Gaming session ended — tasks restored, power plan adjusted");
    }

    /// <summary>
    /// Applies updated settings without full restart.
    /// Stops services that were disabled, starts services that were enabled.
    /// </summary>
    public void ApplySettings()
    {
        var settings = SettingsManager.Load();
        var bgSettings = settings.BackgroundMode;

        if (bgSettings == null || !bgSettings.Enabled)
        {
            if (_enabled) Stop();
            return;
        }

        if (!_enabled)
        {
            Start();
            return;
        }

        // Granular toggle: stop/start individual services as needed
        if (bgSettings.StandbyListCleanerEnabled && !_standbyListCleaner.IsRunning)
            _standbyListCleaner.Start(bgSettings);
        else if (!bgSettings.StandbyListCleanerEnabled && _standbyListCleaner.IsRunning)
            _standbyListCleaner.Stop();

        if (bgSettings.TimerResolutionEnabled && !_timerResolution.IsLocked)
            _timerResolution.Start(bgSettings);
        else if (!bgSettings.TimerResolutionEnabled && _timerResolution.IsLocked)
            _timerResolution.Stop();

        if (bgSettings.PowerPlanEnabled && !_powerPlan.IsRunning)
            _powerPlan.Start(bgSettings);
        else if (!bgSettings.PowerPlanEnabled && _powerPlan.IsRunning)
            _powerPlan.Stop();

        if (bgSettings.ProcessPriorityEnabled && !_processPriority.IsRunning)
            _processPriority.Start(bgSettings);
        else if (!bgSettings.ProcessPriorityEnabled && _processPriority.IsRunning)
            _processPriority.Stop();
    }

    public void Dispose()
    {
        Stop();
        _standbyListCleaner.Dispose();
        _timerResolution.Dispose();
        _powerPlan.Dispose();
        _processPriority.Dispose();
    }
}
