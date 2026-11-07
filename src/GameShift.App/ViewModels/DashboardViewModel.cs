using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using GameShift.Core.Profiles;
using GameShift.Core.Updates;

namespace GameShift.App.ViewModels;

/// <summary>
/// Home: the session status strip, this session's changes, recent sessions, notices and the live cards.
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

    private HomeStatus _homeStatus = new(HomeStatusKind.Idle, HomeStatusTone.Neutral, "Waiting for a game", null);
    private string _gpuInfo = "Unknown";

    // Session history state
    private readonly SessionHistoryStore? _sessionStore;
    private readonly SessionTracker? _sessionTracker;

    // Pausing raises no event, so Home reads the session state on a timer while it is shown.
    private readonly DispatcherTimer _homeTimer;

    // Home notices
    private HomeNotice? _lastingChanges;
    private double _windowWidth;
    private bool _noticesExpanded;
    private NoticeAreaView _noticeArea = ShellLayout.Notices([], 0, expanded: false);

    // ── Sub-ViewModels ──────────────────────────────────────────────────
    public UpdateManagementViewModel Update { get; }
    public HeroOptimizeViewModel Hero { get; }
    public DpcMonitoringViewModel Dpc { get; }
    public PerformanceMonitorViewModel Perf { get; }
    public PingMonitorViewModel Ping { get; }
    public VbsAdvisoryViewModel Vbs { get; }
    public AmdTimerAdvisoryViewModel AmdTimer { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Shared static collection of all activity entries, accessible by ActivityLogPage.
    /// Max 50 entries; newest entries are at index 0.
    /// </summary>
    public static ObservableCollection<ActivityEntry> AllActivities { get; } = new();

    /// <summary>The status strip, from <see cref="HomeStatus"/>.</summary>
    public HomeStatus HomeStatus
    {
        get => _homeStatus;
        private set { _homeStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCountsLine)); }
    }

    public bool HasCountsLine => !string.IsNullOrEmpty(_homeStatus.CountsLine);

    /// <summary>This session's changes, failed first.</summary>
    public ObservableCollection<HomeModuleRow> SessionModules { get; } = new();

    /// <summary>
    /// GPU name from <see cref="GpuDetector"/>, or "Unknown".
    /// </summary>
    public string GpuInfo
    {
        get => _gpuInfo;
        private set { _gpuInfo = value; OnPropertyChanged(); }
    }

    // ── Session History properties ────────────────────────────────────────
    public ObservableCollection<RecentSessionLine> RecentSessions { get; } = new();

    public bool HasNoRecentSessions => RecentSessions.Count == 0;

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
        Hero = new HeroOptimizeViewModel(optimizations, orchestrator, App.Services.ProfileMgr);
        Dpc = new DpcMonitoringViewModel(dpcMonitor);
        Perf = new PerformanceMonitorViewModel(perfMonitor);
        Ping = new PingMonitorViewModel(pingMonitor);
        Vbs = new VbsAdvisoryViewModel(vbsHvciToggle);
        AmdTimer = new AmdTimerAdvisoryViewModel();

        Vbs.PropertyChanged += OnNoticeSourceChanged;
        AmdTimer.PropertyChanged += OnNoticeSourceChanged;
        Dpc.PropertyChanged += OnNoticeSourceChanged;
        Update.PropertyChanged += OnNoticeSourceChanged;
        RefreshNoticeArea();

        // Subscribe to engine events for optimization state changes
        _engine.OptimizationApplied += OnOptimizationApplied;
        _engine.OptimizationReverted += OnOptimizationReverted;
        _engine.OptimizationFailed += OnOptimizationFailed;
        _engine.ActivationCompleted += OnSessionEvent;
        _engine.DeactivationCompleted += OnSessionEvent;

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

        _homeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _homeTimer.Tick += OnHomeTimerTick;
        _homeTimer.Start();

        RefreshStatus();
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
                Description = string.Create(CultureInfo.InvariantCulture,
                    $"Session ended: {e.GameName} ({e.Duration.TotalMinutes:F0} min)"),
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
                    RecentSessions.Add(RecentSessionLine.For(session));
            }
            OnPropertyChanged(nameof(HasNoRecentSessions));
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
    /// Reads the status strip and this session's changes from the session's own results. The
    /// engine raises its last events before the orchestrator clears the session marker, so the
    /// timer, not the events, settles the state after a session ends.
    /// </summary>
    private void RefreshStatus()
    {
        Application.Current.Dispatcher.BeginInvoke(RefreshHome);
    }

    private void RefreshHome()
    {
        var profile = App.Services.ProfileMgr?.GetDefaultProfile();
        var inputs = _optimizations
            .Select(o => new SessionModuleInput(o.Name, o.IsAvailable, profile?.IsOptimizationEnabled(o.Name) ?? true))
            .ToList();

        var snapshot = HomeSnapshot.Read(
            inputs, () => _orchestrator.SessionRequestedUtc, () => _engine.LastOutcome,
            _orchestrator.IsPaused, SingleActiveGameName(), _orchestrator.GameActionsInEffect, TimeZoneInfo.Local);

        if (snapshot.Status != HomeStatus)
            HomeStatus = snapshot.Status;
        ShowModules(snapshot.Modules);

        Dpc.RefreshIndicatorVisibility();
    }

    /// <summary>The detected game's name when exactly one runs; otherwise the session's own game name is shown.</summary>
    private string? SingleActiveGameName()
    {
        var names = _detector.GetActiveGames().Values.Select(g => g.GameName).Distinct().ToList();
        return names.Count == 1 ? names[0] : null;
    }

    private void ShowModules(IReadOnlyList<HomeModuleLine> lines)
    {
        bool same = lines.Count == SessionModules.Count
            && lines.Select((l, i) => SessionModules[i].Shows(l)).All(x => x);
        if (same) return;

        var byName = _optimizations.ToDictionary(o => o.Name);
        SessionModules.Clear();
        foreach (var line in lines)
        {
            var module = byName[line.ModuleName];
            SessionModules.Add(new HomeModuleRow(line, SessionModuleText.For(module.Name, module.Description).Title));
        }
    }

    private void OnHomeTimerTick(object? sender, EventArgs e)
    {
        RefreshHome();
        // A game session's flags clear only after the engine reports the deactivation, so the
        // button's refresh from that report can still see the session; this one catches up.
        Hero.RefreshHeroState();
    }

    private void OnSessionEvent(object? sender, EventArgs e)
    {
        RefreshStatus();
        Hero.RefreshHeroState();
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
        Hero.RefreshHeroState();
    }

    private void OnOptimizationFailed(object? sender, OptimizationAppliedEventArgs e)
    {
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
        RefreshStatus();
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
        Hero.RefreshHeroState();
    }

    private void OnGameStarted(object? sender, GameDetectedEventArgs e)
    {
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
        _homeTimer.Stop();
        _engine.OptimizationApplied -= OnOptimizationApplied;
        _engine.OptimizationReverted -= OnOptimizationReverted;
        _engine.OptimizationFailed -= OnOptimizationFailed;
        _engine.ActivationCompleted -= OnSessionEvent;
        _engine.DeactivationCompleted -= OnSessionEvent;
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
        // (C# does NOT deduplicate event handlers - each += adds another invocation)
        StopTimers();

        _engine.OptimizationApplied += OnOptimizationApplied;
        _engine.OptimizationReverted += OnOptimizationReverted;
        _engine.OptimizationFailed += OnOptimizationFailed;
        _engine.ActivationCompleted += OnSessionEvent;
        _engine.DeactivationCompleted += OnSessionEvent;
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;

        Dpc.Start();
        Perf.Start();
        Ping.Start();
        LoadRecentSessions();

        // Refresh immediately to show current state after navigate-back
        RefreshStatus();
        Hero.RefreshHeroState();
        _homeTimer.Start();
    }

    /// <summary>
    /// Unsubscribes from all events. Called when the window is closing.
    /// </summary>
    public void Cleanup()
    {
        _homeTimer.Stop();
        _homeTimer.Tick -= OnHomeTimerTick;
        _engine.OptimizationApplied -= OnOptimizationApplied;
        _engine.OptimizationReverted -= OnOptimizationReverted;
        _engine.OptimizationFailed -= OnOptimizationFailed;
        _engine.ActivationCompleted -= OnSessionEvent;
        _engine.DeactivationCompleted -= OnSessionEvent;
        _detector.GameStarted -= OnGameStarted;
        _detector.GameStopped -= OnGameStopped;

        if (_sessionTracker != null) _sessionTracker.SessionEnded -= OnSessionEnded;

        Update.Cleanup();
        Hero.Cleanup();
        Dpc.Cleanup();
        Perf.Cleanup();
        Ping.Cleanup();
        Vbs.Cleanup();

        Vbs.PropertyChanged -= OnNoticeSourceChanged;
        AmdTimer.PropertyChanged -= OnNoticeSourceChanged;
        Dpc.PropertyChanged -= OnNoticeSourceChanged;
        Update.PropertyChanged -= OnNoticeSourceChanged;
    }

    // ── Home notices ────────────────────────────────────────────────

    /// <summary>Whether the notices or the "N notices" bar show, from the notice count and the window width.</summary>
    public NoticeAreaView NoticeArea
    {
        get => _noticeArea;
        private set { _noticeArea = value; OnPropertyChanged(); }
    }

    public string NoticeToggleText => _noticesExpanded ? "Hide" : "Show";

    public bool ShowLastingChangesNotice => _lastingChanges != null;
    public string LastingChangesMessage => _lastingChanges?.Message ?? "";
    public NoticeSeverity LastingChangesSeverity => _lastingChanges?.Severity ?? NoticeSeverity.Info;

    /// <param name="someRecordsUnreadable">The listing reported records it could not read, or failed outright.</param>
    public void SetLastingChanges(int itemCount, bool someRecordsUnreadable)
    {
        _lastingChanges = ShellLayout.LastingChangesNotice(itemCount, someRecordsUnreadable);
        OnPropertyChanged(nameof(ShowLastingChangesNotice));
        OnPropertyChanged(nameof(LastingChangesMessage));
        OnPropertyChanged(nameof(LastingChangesSeverity));
        RefreshNoticeArea();
    }

    public void SetWindowWidth(double width)
    {
        _windowWidth = width;
        RefreshNoticeArea();
    }

    public void ToggleNotices()
    {
        _noticesExpanded = !_noticesExpanded;
        OnPropertyChanged(nameof(NoticeToggleText));
        RefreshNoticeArea();
    }

    private void OnNoticeSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VbsAdvisoryViewModel.ShowVbsBanner)
            or nameof(AmdTimerAdvisoryViewModel.ShowBanner)
            or nameof(DpcMonitoringViewModel.ShowDpcSpikeAlert)
            or nameof(UpdateManagementViewModel.ShowUpdateBanner))
        {
            // The AMD advisory sets its flag on a pool thread; refreshing there could race a UI-thread refresh.
            Application.Current.Dispatcher.BeginInvoke(RefreshNoticeArea);
        }
    }

    private void RefreshNoticeArea()
    {
        var shown = new List<NoticeSeverity>();
        if (Vbs.ShowVbsBanner) shown.Add(Vbs.VbsBannerSeverity == "error" ? NoticeSeverity.Error : NoticeSeverity.Warning);
        if (AmdTimer.ShowBanner) shown.Add(NoticeSeverity.Warning);
        if (Dpc.ShowDpcSpikeAlert) shown.Add(NoticeSeverity.Error);
        if (Update.ShowUpdateBanner) shown.Add(NoticeSeverity.Info);
        if (_lastingChanges != null) shown.Add(_lastingChanges.Severity);
        NoticeArea = ShellLayout.Notices(shown, _windowWidth, _noticesExpanded);
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
            return Timestamp.ToString("MMM d", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Formatted timestamp for the full ActivityLogPage display.
    /// </summary>
    public string FormattedTime => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>One module on Home's "This session" list.</summary>
public sealed class HomeModuleRow
{
    private readonly HomeModuleLine _line;

    public HomeModuleRow(HomeModuleLine line, string title)
    {
        _line = line;
        Title = title;
    }

    public string Title { get; }
    public string ModuleName => _line.ModuleName;
    public ModuleStatusKind StatusKind => _line.Status.Kind;
    public string StatusWord => _line.Status.Word;
    public string? StatusReason => _line.Status.Reason;
    public bool HasStatusReason => !string.IsNullOrEmpty(StatusReason);

    internal bool Shows(HomeModuleLine line) => _line == line;
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
