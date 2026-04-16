using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.BackgroundMode;
using GameShift.Core.GameProfiles;
using GameShift.Core.Updates;

namespace GameShift.App.ViewModels;

/// <summary>
/// Displays live optimization status on the dashboard.
/// Subscribes to engine and detector events for real-time updates.
/// All event handlers dispatch to UI thread since core events fire on background threads.
/// </summary>
public class DashboardViewModel : INotifyPropertyChanged
{
    private readonly DetectionOrchestrator _orchestrator;
    private readonly OptimizationEngine _engine;
    private readonly GameDetector _detector;
    private readonly IReadOnlyList<IOptimization> _optimizations;

    // Activity entries: max 50 in memory
    private const int MaxActivityEntries = 50;

    private string _statusText = "Idle";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private int _activeGameCount;
    private string _activeGameNames = "None";

    private string _optimizationSummary = "0/0 Active";
    private string _gpuInfo = "Unknown";
    private string _gpuOptimizationState = "Standby";

    // Background Mode status
    private string _bgModeStatus = "Disabled";
    private string _bgModeDetails = "";

    // Game Profile status
    private string _activeProfileName = "No Profile";
    private string _activeProfileDetails = "";

    // Session history state
    private readonly SessionHistoryStore? _sessionStore;
    private readonly SessionTracker? _sessionTracker;

    // Optimization failure tracking
    private int _sessionFailedCount;

    // Advanced Mode toggle
    private bool _isAdvancedMode;

    // Stored handler for AllActivities.CollectionChanged so we can unsubscribe
    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _activitiesChangedHandler;

    // ── Sub-ViewModels ──────────────────────────────────────────────────
    public UpdateManagementViewModel Update { get; }
    public HeroOptimizeViewModel Hero { get; }
    public DpcMonitoringViewModel Dpc { get; }
    public PerformanceMonitorViewModel Perf { get; }
    public PingMonitorViewModel Ping { get; }
    public VbsAdvisoryViewModel Vbs { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Shared static collection of all activity entries, accessible by ActivityLogPage.
    /// Max 50 entries; newest entries are at index 0.
    /// </summary>
    public static ObservableCollection<ActivityEntry> AllActivities { get; } = new();

    /// <summary>
    /// Current optimization status: "Idle", "Optimizing", or "Paused".
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Brush for the status indicator dot.
    /// Gray (#808080) for idle, Green (#4CAF50) for optimizing, Yellow (#FFC107) for paused.
    /// Uses Brush type directly — no converter needed.
    /// </summary>
    public Brush StatusBrush
    {
        get => _statusBrush;
        private set { _statusBrush = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Number of currently running game processes.
    /// </summary>
    public int ActiveGameCount
    {
        get => _activeGameCount;
        private set { _activeGameCount = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Comma-separated names of active games, or "None".
    /// </summary>
    public string ActiveGameNames
    {
        get => _activeGameNames;
        private set { _activeGameNames = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Per-optimization status entries displayed in the optimization list.
    /// Uses ExpandableOptimizationItem to support expand/collapse and per-profile toggle.
    /// </summary>
    public ObservableCollection<ExpandableOptimizationItem> Optimizations { get; } = new();

    /// <summary>
    /// All activity entries (newest first), max 50 entries in memory.
    /// </summary>
    public ObservableCollection<ActivityEntry> ActivityEntries => AllActivities;

    /// <summary>
    /// The 5 most recent activity entries for the dashboard feed.
    /// Updated whenever AllActivities changes.
    /// </summary>
    public ObservableCollection<ActivityEntry> RecentActivities { get; } = new();

    /// <summary>
    /// Summary of active optimizations, e.g. "3/11 Active".
    /// </summary>
    public string OptimizationSummary
    {
        get => _optimizationSummary;
        private set { _optimizationSummary = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// GPU vendor string from settings (GpuVendorOverride), or "Unknown".
    /// </summary>
    public string GpuInfo
    {
        get => _gpuInfo;
        private set { _gpuInfo = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// GPU optimization state: "Applied" or "Standby".
    /// </summary>
    public string GpuOptimizationState
    {
        get => _gpuOptimizationState;
        private set { _gpuOptimizationState = value; OnPropertyChanged(); }
    }

    // ── HAGS / ReBAR status (Sprint 8D/8E) ────────────────────────────────

    private string _hagsStatus = "Unknown";
    private string _hagsAdvisory = "";
    private bool _showHagsAdvisory;
    private string _reBarStatus = "Unknown";
    private string _reBarAdvisory = "";
    private bool _showReBarAdvisory;

    /// <summary>HAGS state: "Enabled", "Disabled", or "Unknown".</summary>
    public string HagsStatus { get => _hagsStatus; private set { _hagsStatus = value; OnPropertyChanged(); } }
    public string HagsAdvisory { get => _hagsAdvisory; private set { _hagsAdvisory = value; OnPropertyChanged(); } }
    public bool ShowHagsAdvisory { get => _showHagsAdvisory; private set { _showHagsAdvisory = value; OnPropertyChanged(); } }

    /// <summary>ReBAR/SAM state: "Enabled", "Not detected", or "Unknown".</summary>
    public string ReBarStatus { get => _reBarStatus; private set { _reBarStatus = value; OnPropertyChanged(); } }
    public string ReBarAdvisory { get => _reBarAdvisory; private set { _reBarAdvisory = value; OnPropertyChanged(); } }
    public bool ShowReBarAdvisory { get => _showReBarAdvisory; private set { _showReBarAdvisory = value; OnPropertyChanged(); } }

    // ── Session History properties ────────────────────────────────────────
    public ObservableCollection<GameSession> RecentSessions { get; } = new();

    // ── Background Mode properties ───────────────────────────────────────
    public string BgModeStatus
    {
        get => _bgModeStatus;
        private set { _bgModeStatus = value; OnPropertyChanged(); }
    }

    public string BgModeDetails
    {
        get => _bgModeDetails;
        private set { _bgModeDetails = value; OnPropertyChanged(); }
    }

    // ── Game Profile properties ──────────────────────────────────────
    public string ActiveProfileName
    {
        get => _activeProfileName;
        private set { _activeProfileName = value; OnPropertyChanged(); }
    }

    public string ActiveProfileDetails
    {
        get => _activeProfileDetails;
        private set { _activeProfileDetails = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Creates the dashboard ViewModel.
    /// </summary>
    /// <param name="orchestrator">For IsOptimizing state</param>
    /// <param name="engine">For OptimizationApplied/Reverted events</param>
    /// <param name="detector">For GameStarted/GameStopped events and GetActiveGames()</param>
    /// <param name="optimizations">The optimization modules to display status for</param>
    /// <param name="vbsHvciToggle">Optional VBS/HVCI toggle for banner display</param>
    /// <param name="dpcMonitor">Optional DPC latency monitor for real-time indicator</param>
    public DashboardViewModel(
        DetectionOrchestrator orchestrator,
        OptimizationEngine engine,
        GameDetector detector,
        IReadOnlyList<IOptimization> optimizations,
        VbsHvciToggle? vbsHvciToggle = null,
        DpcLatencyMonitor? dpcMonitor = null,
        SystemPerformanceMonitor? perfMonitor = null,
        PingMonitor? pingMonitor = null,
        SessionHistoryStore? sessionStore = null,
        SessionTracker? sessionTracker = null)
    {
        _orchestrator = orchestrator;
        _engine = engine;
        _detector = detector;
        _optimizations = optimizations;
        _sessionStore = sessionStore;
        _sessionTracker = sessionTracker;

        // Create sub-ViewModels
        Update = new UpdateManagementViewModel();
        Hero = new HeroOptimizeViewModel(optimizations, engine);
        Dpc = new DpcMonitoringViewModel(dpcMonitor);
        Perf = new PerformanceMonitorViewModel(perfMonitor);
        Ping = new PingMonitorViewModel(pingMonitor);
        Vbs = new VbsAdvisoryViewModel(vbsHvciToggle);

        // Subscribe to engine events for optimization state changes
        _engine.OptimizationApplied += OnOptimizationApplied;
        _engine.OptimizationReverted += OnOptimizationReverted;
        _engine.OptimizationFailed += OnOptimizationFailed;

        // Subscribe to detector events for active game tracking
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;

        // Start sub-VM event subscriptions
        Dpc.Start();
        Perf.Start();
        Ping.Start();

        // Subscribe to session tracker
        if (_sessionTracker != null)
        {
            _sessionTracker.SessionEnded += OnSessionEnded;
        }

        // Load recent sessions
        LoadRecentSessions();

        // Set GPU info from live WMI detection (single source of truth)
        _gpuInfo = GpuDetector.GetGpuName();

        // Populate HAGS / ReBAR status from hardware scan
        UpdateGpuFeatureStatus();

        // Initialize Advanced Mode from persisted setting
        _isAdvancedMode = SettingsManager.Load().AdvancedMode;

        // Set initial state
        RefreshStatus();
        RefreshOptimizations();

        // Subscribe to AllActivities changes to keep RecentActivities updated
        _activitiesChangedHandler = (s, e) => UpdateRecentActivities();
        AllActivities.CollectionChanged += _activitiesChangedHandler;
        UpdateRecentActivities();
    }

    // ── Session event handlers ──────────────────────────────────────────────

    private void OnSessionEnded(object? sender, GameSession e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LoadRecentSessions();
            AddActivity(new ActivityEntry
            {
                Timestamp = DateTime.Now,
                Type = "SessionEnded",
                Description = $"Session ended: {e.GameName} ({e.Duration.TotalMinutes:F0} min)",
                TypeIcon = "[S]"
            });
        });
    }

    private void LoadRecentSessions()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            RecentSessions.Clear();
            if (_sessionStore != null)
            {
                foreach (var session in _sessionStore.GetRecent(5))
                    RecentSessions.Add(session);
            }
        });
    }

    /// <summary>
    /// Updates RecentActivities to contain the first 5 items from AllActivities.
    /// Must be called on the UI thread (or dispatched).
    /// </summary>
    private void UpdateRecentActivities()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            RecentActivities.Clear();
            foreach (var entry in AllActivities.Take(5))
            {
                RecentActivities.Add(entry);
            }
        });
    }

    /// <summary>
    /// Adds an activity entry to AllActivities, keeping max 50 entries.
    /// Inserts at position 0 (newest first). Must be called from UI thread.
    /// </summary>
    private void AddActivity(ActivityEntry entry)
    {
        AllActivities.Insert(0, entry);
        while (AllActivities.Count > MaxActivityEntries)
        {
            AllActivities.RemoveAt(AllActivities.Count - 1);
        }
    }

    /// <summary>
    /// Updates status text, brush, and active game info.
    /// Status is derived from actual observable state (IOptimization.IsApplied and active games)
    /// rather than the orchestrator's IsOptimizing flag, which can be stale due to async timing.
    /// Dispatches to UI thread since this may be called from background event handlers.
    /// </summary>
    private void RefreshStatus()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var activeGames = _detector.GetActiveGames();
            ActiveGameCount = activeGames.Count;

            if (activeGames.Count > 0)
            {
                ActiveGameNames = string.Join(", ", activeGames.Values.Select(g => g.GameName).Distinct());
            }
            else
            {
                ActiveGameNames = "None";
            }

            // Derive optimizing state from actual optimization modules, not the orchestrator flag.
            // The orchestrator's _isOptimizing flag suffers from async timing: it's set to false
            // AFTER DeactivateProfileAsync completes, but OptimizationReverted events fire DURING
            // deactivation, so the flag is still true when those events reach the dashboard.
            var anyApplied = _optimizations.Any(o => o.IsApplied);

            if (anyApplied)
            {
                StatusText = activeGames.Count > 0
                    ? $"Optimizing \u2014 {ActiveGameNames}"
                    : "Optimizing";
                StatusBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // #4CAF50
            }
            else if (activeGames.Count > 0)
            {
                // Games running but no optimizations applied (e.g. all disabled in profile)
                StatusText = $"Monitoring \u2014 {ActiveGameNames}";
                StatusBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)); // #FFC107 yellow
            }
            else
            {
                StatusText = "Idle";
                StatusBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)); // #808080
            }

            // Update DPC indicator visibility via sub-VM
            Dpc.RefreshIndicatorVisibility();

            // Background Mode status
            var bg = App.Services.BackgroundMode;
            if (bg != null && bg.IsEnabled)
            {
                BgModeStatus = "Active";
                var parts = new List<string>();
                if (bg.StandbyListCleaner.IsRunning) parts.Add("Memory");
                if (bg.TimerResolution.IsLocked) parts.Add("Timer");
                if (bg.PowerPlan.IsRunning) parts.Add(bg.PowerPlan.IsIdle ? "Power (idle)" : "Power");
                if (bg.TaskDeferral.IsDeferred) parts.Add($"Tasks ({bg.TaskDeferral.DeferredCount})");
                if (bg.ProcessPriority.IsRunning) parts.Add("Priority");
                BgModeDetails = parts.Count > 0 ? string.Join(" \u00b7 ", parts) : "No services active";
            }
            else
            {
                BgModeStatus = "Disabled";
                BgModeDetails = "Enable in Settings \u2192 Background Mode";
            }

            // Game Profile status
            var gpm = App.Services.GameProfileMgr;
            if (gpm != null && gpm.HasActiveProfile)
            {
                var prof = gpm.ActiveProfile!;
                ActiveProfileName = prof.DisplayName;
                var details = new List<string>();
                details.Add($"Priority: {prof.GamePriority}");
                if (prof.IntelHybridPCoreOnly && IntelHybridDetector.IsHybridCpu)
                    details.Add("P-Core Only");
                if (prof.LauncherPriority != null)
                    details.Add($"Launcher: {prof.LauncherPriority}");
                ActiveProfileDetails = string.Join(" \u00b7 ", details);
            }
            else
            {
                ActiveProfileName = "No Profile";
                ActiveProfileDetails = "";
            }
        });
    }

    /// <summary>
    /// Rebuilds the optimization status list from current IOptimization states.
    /// Must run on UI thread (ObservableCollection is not thread-safe).
    /// Also updates OptimizationSummary and GpuOptimizationState.
    /// Populates IsEnabled from the default GameProfile.
    /// </summary>
    private void RefreshOptimizations()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Optimizations.Clear();

            int applied = 0;
            int total = _optimizations.Count;
            string gpuState = "Standby";

            var defaultProfile = App.Services.ProfileMgr?.GetDefaultProfile();

            // Before/after descriptors indexed to match Services.Optimizations array order (0-10)
            var beforeValues = new[]
            {
                "Services running",          // 0 ServiceSuppressor
                "Default power plan",        // 1 PowerPlanSwitcher
                "Default timer resolution",  // 2 TimerResolutionManager
                "Normal priority",           // 3 ProcessPriorityBooster
                "Default memory state",      // 4 MemoryOptimizer
                "Visual effects enabled",    // 5 VisualEffectReducer
                "Default network settings",  // 6 NetworkOptimizer
                "Default CPU scheduling",    // 7 HybridCpuDetector
                "MPO enabled",               // 8 MpoToggle
                "Standard mode",             // 9 CompetitiveMode
                "Default GPU settings"       // 10 GpuDriverOptimizer
            };

            var afterValues = new[]
            {
                "Non-essential services stopped",  // 0
                "Ultimate Performance plan",       // 1
                "0.5ms timer resolution",          // 2
                "High priority",                   // 3
                "Working set optimized",           // 4
                "Visual effects reduced",          // 5
                "Nagle disabled, TCP optimized",   // 6
                "P-cores prioritized for game",    // 7
                "MPO disabled",                    // 8
                "Competitive mode active",         // 9
                "GPU driver optimized"             // 10
            };

            int index = 0;
            foreach (var opt in _optimizations)
            {
                string status;
                Brush statusBrush;

                if (opt.IsApplied)
                {
                    status = "Applied";
                    statusBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // Green
                    applied++;
                }
                else if (!opt.IsAvailable)
                {
                    status = "Unavailable";
                    statusBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)); // Dark gray
                }
                else
                {
                    status = "Standby";
                    statusBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)); // Light gray
                }

                // Track GPU optimizer state
                if (opt.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                {
                    gpuState = opt.IsApplied ? "Applied" : "Standby";
                }

                bool isEnabled = defaultProfile?.IsOptimizationEnabled(opt.Name) ?? true;

                var item = new ExpandableOptimizationItem
                {
                    Name = opt.Name,
                    Description = opt.Description,
                    Status = status,
                    StatusBrush = statusBrush,
                    IsEnabled = isEnabled,
                    BeforeValue = index < beforeValues.Length ? beforeValues[index] : "Original state",
                    AfterValue = index < afterValues.Length ? afterValues[index] : "Optimized state"
                };

                // Wire toggle callback to persist profile changes
                item.OnToggled = OnOptimizationToggled;

                Optimizations.Add(item);
                index++;
            }

            OptimizationSummary = _sessionFailedCount > 0
                ? $"{applied}/{total} Active ({_sessionFailedCount} failed)"
                : $"{applied}/{total} Active";
            GpuOptimizationState = gpuState;
        });
    }

    /// <summary>
    /// Called when user toggles an optimization's enabled/disabled switch.
    /// Updates the default GameProfile and persists via ProfileManager.
    /// </summary>
    private void OnOptimizationToggled(ExpandableOptimizationItem item)
    {
        if (App.Services.ProfileMgr == null) return;

        var profile = App.Services.ProfileMgr.GetDefaultProfile();

        switch (item.Name)
        {
            case ServiceSuppressor.OptimizationId:
                profile.SuppressServices = item.IsEnabled;
                break;
            case PowerPlanSwitcher.OptimizationId:
                profile.SwitchPowerPlan = item.IsEnabled;
                break;
            case TimerResolutionManager.OptimizationId:
                profile.SetTimerResolution = item.IsEnabled;
                break;
            case ProcessPriorityBooster.OptimizationId:
                profile.BoostProcessPriority = item.IsEnabled;
                break;
            case MemoryOptimizer.OptimizationId:
                profile.OptimizeMemory = item.IsEnabled;
                break;
            case VisualEffectReducer.OptimizationId:
                profile.ReduceVisualEffects = item.IsEnabled;
                break;
            case NetworkOptimizer.OptimizationId:
                profile.OptimizeNetwork = item.IsEnabled;
                break;
            case HybridCpuDetector.OptimizationId:
                profile.UsePerformanceCoresOnly = item.IsEnabled;
                break;
            case MpoToggle.OptimizationId:
                profile.DisableMpo = item.IsEnabled;
                break;
            case CompetitiveMode.OptimizationId:
                profile.EnableCompetitiveMode = item.IsEnabled;
                break;
            case GpuDriverOptimizer.OptimizationId:
                profile.EnableGpuOptimization = item.IsEnabled;
                break;
            case ScheduledTaskSuppressor.OptimizationId:
                profile.SuppressScheduledTasks = item.IsEnabled;
                break;
            case CpuParkingManager.OptimizationId:
                profile.UnparkCpuCores = item.IsEnabled;
                break;
            case IoPriorityManager.OptimizationId:
                profile.ManageIoPriority = item.IsEnabled;
                break;
            case EfficiencyModeController.OptimizationId:
                profile.EnableEfficiencyMode = item.IsEnabled;
                break;
            case CpuSchedulingOptimizer.OptimizationId:
                // CpuSchedulingOptimizer is always-on when available, no per-profile toggle
                break;
            case SessionSystemTweaksOptimizer.OptimizationId:
                // SessionSystemTweaksOptimizer is always-on when available, no per-profile toggle
                break;
        }

        App.Services.ProfileMgr.SaveProfile(profile);
    }

    private void OnOptimizationApplied(object? sender, OptimizationAppliedEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            AddActivity(new ActivityEntry
            {
                Timestamp = DateTime.Now,
                Type = "OptimizationApplied",
                Description = $"{e.Optimization.Name} applied",
                TypeIcon = "\u2699"
            });
        });
        RefreshStatus();
        RefreshOptimizations();
        Hero.RefreshHeroState();
    }

    private void OnOptimizationFailed(object? sender, OptimizationAppliedEventArgs e)
    {
        _sessionFailedCount++;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            AddActivity(new ActivityEntry
            {
                Timestamp = DateTime.Now,
                Type = "OptimizationFailed",
                Description = $"{e.Optimization.Name} failed",
                TypeIcon = "\u26A0"
            });
        });
        RefreshOptimizations();
    }

    private void OnOptimizationReverted(object? sender, OptimizationRevertedEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            AddActivity(new ActivityEntry
            {
                Timestamp = DateTime.Now,
                Type = "OptimizationReverted",
                Description = $"{e.Optimization.Name} reverted",
                TypeIcon = "\u2699"
            });
        });
        RefreshStatus();
        RefreshOptimizations();
        Hero.RefreshHeroState();
    }

    private void OnGameStarted(object? sender, GameDetectedEventArgs e)
    {
        _sessionFailedCount = 0;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            AddActivity(new ActivityEntry
            {
                Timestamp = DateTime.Now,
                Type = "GameStarted",
                Description = $"{e.GameName} detected",
                TypeIcon = "[G]"
            });
        });
        RefreshStatus();
    }

    private void OnGameStopped(object? sender, GameDetectedEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            AddActivity(new ActivityEntry
            {
                Timestamp = DateTime.Now,
                Type = "GameStopped",
                Description = $"{e.GameName} stopped",
                TypeIcon = "[G]"
            });
        });
        RefreshStatus();
    }

    /// <summary>
    /// Stops the sparkline data feed by unsubscribing from all events.
    /// Called when DashboardPage navigates away to reduce idle RAM/CPU usage.
    /// Events are re-subscribed via StartTimers() when the page becomes visible again.
    /// </summary>
    public void StopTimers()
    {
        _engine.OptimizationApplied -= OnOptimizationApplied;
        _engine.OptimizationReverted -= OnOptimizationReverted;
        _engine.OptimizationFailed -= OnOptimizationFailed;
        _detector.GameStarted -= OnGameStarted;
        _detector.GameStopped -= OnGameStopped;

        Dpc.Stop();
        Perf.Stop();
        Ping.Stop();
    }

    /// <summary>
    /// Restarts the sparkline data feed by re-subscribing to all events.
    /// Called when DashboardPage navigates back.
    /// Refreshes status immediately to show current state after navigate-back.
    /// </summary>
    public void StartTimers()
    {
        // Unsubscribe first to prevent duplicate handlers accumulating on navigate-back
        // (C# does NOT deduplicate event handlers — each += adds another invocation)
        StopTimers();

        _engine.OptimizationApplied += OnOptimizationApplied;
        _engine.OptimizationReverted += OnOptimizationReverted;
        _engine.OptimizationFailed += OnOptimizationFailed;
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;

        Dpc.Start();
        Perf.Start();
        Ping.Start();
        LoadRecentSessions();

        // Refresh immediately to show current state after navigate-back
        RefreshStatus();
        RefreshOptimizations();
    }

    /// <summary>
    /// Unsubscribes from all events. Called when the window is closing.
    /// </summary>
    public void Cleanup()
    {
        _engine.OptimizationApplied -= OnOptimizationApplied;
        _engine.OptimizationReverted -= OnOptimizationReverted;
        _engine.OptimizationFailed -= OnOptimizationFailed;
        _detector.GameStarted -= OnGameStarted;
        _detector.GameStopped -= OnGameStopped;

        if (_sessionTracker != null) _sessionTracker.SessionEnded -= OnSessionEnded;

        Update.Cleanup();
        Hero.Cleanup();
        Dpc.Cleanup();
        Perf.Cleanup();
        Ping.Cleanup();
        Vbs.Cleanup();

        AllActivities.CollectionChanged -= _activitiesChangedHandler;
    }

    // ── GPU Feature Status (8D HAGS / 8E ReBAR) ─────────────────────────

    /// <summary>
    /// Reads HAGS and ReBAR state from HardwareScanResult (if available) and populates
    /// the dashboard status properties + advisory messages.
    /// </summary>
    private void UpdateGpuFeatureStatus()
    {
        try
        {
            var scan = App.Services.HardwareScan;
            if (scan == null) return;

            // ── HAGS ──
            HagsStatus = scan.IsHagsEnabled ? "Enabled" : "Disabled";

            if (!scan.IsHagsEnabled)
            {
                // Recommend HAGS for modern GPUs (RTX 30/40/50, RX 7000+, RDNA4)
                bool shouldRecommend = scan.GpuGeneration is
                    GpuGeneration.NvidiaRtx30 or GpuGeneration.NvidiaRtx40 or GpuGeneration.NvidiaRtx50 or
                    GpuGeneration.AmdRdna3 or GpuGeneration.AmdRdna4;

                if (shouldRecommend)
                {
                    ShowHagsAdvisory = true;
                    HagsAdvisory = "HAGS is off but recommended for your GPU. " +
                        "Required for DLSS 3.5+ Frame Generation and AMD Fluid Motion. " +
                        "Enable in Settings > Display > Graphics > Change default graphics settings.";
                }
            }

            // ── ReBAR ──
            ReBarStatus = scan.IsReBarEnabled ? "Enabled" : "Not detected";

            if (!scan.IsReBarEnabled && scan.GpuVendor != GpuVendor.Unknown)
            {
                bool gpuSupportsReBar = scan.GpuGeneration is
                    GpuGeneration.NvidiaRtx30 or GpuGeneration.NvidiaRtx40 or GpuGeneration.NvidiaRtx50 or
                    GpuGeneration.AmdRdna2 or GpuGeneration.AmdRdna3 or GpuGeneration.AmdRdna4;

                if (gpuSupportsReBar)
                {
                    ShowReBarAdvisory = true;
                    ReBarAdvisory = "Resizable BAR / Smart Access Memory is supported by your GPU but not detected. " +
                        "Enable in BIOS (may require Above 4G Decoding + Re-Size BAR Support). " +
                        "Can improve performance 5-10% in some titles.";
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DashboardViewModel] Failed to update GPU feature status");
        }
    }

    // ── Advanced Mode ────────────────────────────────────────────────

    /// <summary>
    /// Fired when Advanced Mode is toggled so MainWindow can rebuild navigation.
    /// </summary>
    public event Action<bool>? AdvancedModeChanged;

    public bool IsEasyMode
    {
        get => !_isAdvancedMode;
        set
        {
            IsAdvancedMode = !value;
            OnPropertyChanged();
        }
    }

    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (_isAdvancedMode == value) return;
            _isAdvancedMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEasyMode));

            var settings = SettingsManager.Load();
            settings.AdvancedMode = value;
            SettingsManager.Save(settings);

            AdvancedModeChanged?.Invoke(value);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Represents a single activity event entry (game event or optimization action).
/// Used in both the dashboard activity feed and the full ActivityLogPage.
/// </summary>
public class ActivityEntry
{
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Event category: "GameStarted", "GameStopped", "OptimizationApplied", "OptimizationReverted"
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// Human-readable description, e.g. "Valorant detected" or "Power Plan Switcher applied".
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Icon character for the event type.
    /// Uses simple ASCII fallback ("[G]" for games, gear symbol for optimizations).
    /// </summary>
    public string TypeIcon { get; set; } = "";

    /// <summary>
    /// Human-friendly relative time string, e.g. "just now", "2m ago".
    /// Computed from Timestamp on each access.
    /// </summary>
    public string TimeAgo
    {
        get
        {
            var elapsed = DateTime.Now - Timestamp;
            if (elapsed.TotalSeconds < 60) return "just now";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            return Timestamp.ToString("MMM d");
        }
    }

    /// <summary>
    /// Formatted timestamp for the full ActivityLogPage display.
    /// </summary>
    public string FormattedTime => Timestamp.ToString("HH:mm:ss");
}

/// <summary>
/// Wraps an optimization's status with expand/collapse state and per-profile enable toggle.
/// Each optimization row is togglable and shows description with before/after state.
/// </summary>
public class ExpandableOptimizationItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public Brush StatusBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

    /// <summary>Descriptive text for the system state before this optimization is applied.</summary>
    public string BeforeValue { get; set; } = "";

    /// <summary>Descriptive text for the system state after this optimization is applied.</summary>
    public string AfterValue { get; set; } = "";

    private bool _isExpanded = false;

    /// <summary>
    /// Whether the detail panel (description + before/after) is visible.
    /// Toggled by clicking the row.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    private bool _isEnabled = true;

    /// <summary>
    /// Whether this optimization is enabled in the default GameProfile.
    /// When toggled, OnToggled is invoked to persist the change.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Callback invoked when the user toggles IsEnabled. Wired by DashboardViewModel.
    /// The callback persists the change to the default GameProfile via ProfileManager.
    /// </summary>
    public Action<ExpandableOptimizationItem>? OnToggled { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Display model for a single optimization's status in the dashboard list.
/// Kept for backward compatibility — new code uses ExpandableOptimizationItem.
/// </summary>
public class OptimizationStatus
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public Brush StatusBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
}

internal class RelayCommand : ICommand
{
    private readonly Action _execute;
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}
