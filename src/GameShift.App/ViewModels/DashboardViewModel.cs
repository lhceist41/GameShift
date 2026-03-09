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
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
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
    private readonly VbsHvciToggle? _vbsHvciToggle;
    private readonly DpcLatencyMonitor? _dpcMonitor;

    // Sparkline data: max 120 samples = 60 seconds at 500ms intervals
    private readonly Queue<double> _sparklineSamples = new();
    private const int MaxSparklineSamples = 120;
    private const double SparklineWidth = 120.0;
    private const double SparklineHeight = 32.0;

    // Activity entries: max 50 in memory
    private const int MaxActivityEntries = 50;

    private string _statusText = "Idle";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private int _activeGameCount;
    private string _activeGameNames = "None";
    private string _dpcLatencyText = "";
    private Brush _dpcLatencyBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private bool _showDpcIndicator = false;
    private bool _showVbsBanner = false;
    private string _vbsBannerMessage = "";
    private bool _isVbsConflict = false;
    private string _vbsBannerSeverity = "warning";

    // New Phase 13 properties
    private PointCollection _sparklinePoints = new();
    private Brush _sparklineBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private string _optimizationSummary = "0/0 Active";
    private string _gpuInfo = "Unknown";
    private string _gpuOptimizationState = "Standby";
    private double _averageDpcLatency = 0.0;
    private bool _showDpcSpikeAlert = false;
    private string _dpcSpikeAlertMessage = "";

    // Update checker state
    private bool _showUpdateBanner = false;
    private string _updateMessage = "";
    private string _updateUrl = "";

    // Update download state
    private bool _isDownloading = false;
    private double _downloadProgress = 0.0;
    private string _downloadStatusText = "";
    private bool _isUpdateReady = false;
    private CancellationTokenSource? _downloadCts;
    private UpdateInfo? _pendingUpdate;

    // DPC Troubleshooter state
    private bool _showTroubleshooter = false;
    private bool _isAnalyzing = false;
    private string _analysisSummary = "";

    // Performance monitor state
    private readonly SystemPerformanceMonitor? _perfMonitor;
    private readonly Queue<double> _cpuSparklineSamples = new();
    private readonly Queue<double> _ramSparklineSamples = new();
    private readonly Queue<double> _gpuSparklineSamples = new();

    // Ping monitor state
    private readonly PingMonitor? _pingMonitor;
    private readonly Queue<long> _pingSparklineSamples = new();

    // Background Mode status
    private string _bgModeStatus = "Disabled";
    private string _bgModeDetails = "";

    // Game Profile status
    private string _activeProfileName = "No Profile";
    private string _activeProfileDetails = "";

    // Session history state
    private readonly SessionHistoryStore? _sessionStore;
    private readonly SessionTracker? _sessionTracker;

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
    /// DPC latency display text, e.g. "250us (Good)".
    /// Updated in real-time from LatencySampled events.
    /// </summary>
    public string DpcLatencyText
    {
        get => _dpcLatencyText;
        private set { _dpcLatencyText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Brush for the DPC indicator dot: green &lt; 500us, yellow 500-1000us, red &gt; 1000us.
    /// </summary>
    public Brush DpcLatencyBrush
    {
        get => _dpcLatencyBrush;
        private set { _dpcLatencyBrush = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether to show the DPC latency indicator. True when DPC monitor is actively monitoring.
    /// </summary>
    public bool ShowDpcIndicator
    {
        get => _showDpcIndicator;
        private set { _showDpcIndicator = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether to show the VBS/HVCI warning banner. True when VBS is enabled and not dismissed.
    /// </summary>
    public bool ShowVbsBanner
    {
        get => _showVbsBanner;
        private set { _showVbsBanner = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// VBS/HVCI warning banner message text.
    /// </summary>
    public string VbsBannerMessage
    {
        get => _vbsBannerMessage;
        private set { _vbsBannerMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether there is a VBS conflict (VBS disabled but VBS-requiring anti-cheat detected).
    /// When true, the banner should be red/error level with a "Re-enable" action.
    /// </summary>
    public bool IsVbsConflict
    {
        get => _isVbsConflict;
        private set { _isVbsConflict = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Banner severity: "error" when VBS is disabled + AC requires it, "warning" otherwise.
    /// Used by the UI to switch between red and amber banner styles.
    /// </summary>
    public string VbsBannerSeverity
    {
        get => _vbsBannerSeverity;
        private set { _vbsBannerSeverity = value; OnPropertyChanged(); }
    }

    // ── Phase 13 new properties ──────────────────────────────────────────

    /// <summary>
    /// Sparkline points collection for the DPC latency chart (120x32 Polyline).
    /// Computed from _sparklineSamples on each LatencySampled event.
    /// </summary>
    public PointCollection SparklinePoints
    {
        get => _sparklinePoints;
        private set { _sparklinePoints = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Brush for the sparkline line: green &lt; 500µs, yellow 500-1000µs, red &gt; 1000µs.
    /// </summary>
    public Brush SparklineBrush
    {
        get => _sparklineBrush;
        private set { _sparklineBrush = value; OnPropertyChanged(); }
    }

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

    /// <summary>
    /// Average DPC latency in microseconds from the monitor.
    /// Updated in real-time from LatencySampled events.
    /// </summary>
    public double AverageDpcLatency
    {
        get => _averageDpcLatency;
        private set { _averageDpcLatency = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether to show the DPC spike alert banner.
    /// True when a DPC spike has been detected and not dismissed.
    /// </summary>
    public bool ShowDpcSpikeAlert
    {
        get => _showDpcSpikeAlert;
        private set { _showDpcSpikeAlert = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// DPC spike alert message with latency and driver info.
    /// </summary>
    public string DpcSpikeAlertMessage
    {
        get => _dpcSpikeAlertMessage;
        private set { _dpcSpikeAlertMessage = value; OnPropertyChanged(); }
    }

    /// <summary>Whether to show the update available banner.</summary>
    public bool ShowUpdateBanner
    {
        get => _showUpdateBanner;
        private set { _showUpdateBanner = value; OnPropertyChanged(); }
    }

    /// <summary>Update banner message text.</summary>
    public string UpdateMessage
    {
        get => _updateMessage;
        private set { _updateMessage = value; OnPropertyChanged(); }
    }

    /// <summary>URL to the latest release page.</summary>
    public string UpdateUrl
    {
        get => _updateUrl;
        private set { _updateUrl = value; OnPropertyChanged(); }
    }

    /// <summary>Whether an update download is currently in progress.</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        private set { _isDownloading = value; OnPropertyChanged(); }
    }

    /// <summary>Download progress from 0 to 100 (for ProgressBar.Value binding).</summary>
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set { _downloadProgress = value; OnPropertyChanged(); }
    }

    /// <summary>Status text during/after download.</summary>
    public string DownloadStatusText
    {
        get => _downloadStatusText;
        private set { _downloadStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>Whether the update has been downloaded and is ready to apply.</summary>
    public bool IsUpdateReady
    {
        get => _isUpdateReady;
        private set { _isUpdateReady = value; OnPropertyChanged(); }
    }

    // ── DPC Troubleshooter properties ────────────────────────────────────

    /// <summary>Whether the inline DPC troubleshooter results panel is visible.</summary>
    public bool ShowTroubleshooter
    {
        get => _showTroubleshooter;
        private set { _showTroubleshooter = value; OnPropertyChanged(); }
    }

    /// <summary>Whether a DPC analysis scan is in progress.</summary>
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set { _isAnalyzing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRescan)); }
    }

    /// <summary>Inverse of IsAnalyzing, for Re-scan button enable state.</summary>
    public bool CanRescan => !_isAnalyzing;

    /// <summary>Summary text: "Found 3 known DPC offenders" or "No known offenders found".</summary>
    public string AnalysisSummary
    {
        get => _analysisSummary;
        private set { _analysisSummary = value; OnPropertyChanged(); }
    }

    /// <summary>Collection of matched DPC offenders for the results panel ItemsControl.</summary>
    public ObservableCollection<DpcOffenderMatch> TroubleshooterResults { get; } = new();

    // ── Performance Monitor properties ─────────────────────────────────────
    private string _cpuText = "0%";
    public string CpuText { get => _cpuText; private set { _cpuText = value; OnPropertyChanged(); } }

    private string _ramText = "0%";
    public string RamText { get => _ramText; private set { _ramText = value; OnPropertyChanged(); } }

    private string _gpuUtilText = "N/A";
    public string GpuUtilText { get => _gpuUtilText; private set { _gpuUtilText = value; OnPropertyChanged(); } }

    private PointCollection _cpuSparklinePoints = new();
    public PointCollection CpuSparklinePoints { get => _cpuSparklinePoints; private set { _cpuSparklinePoints = value; OnPropertyChanged(); } }

    private PointCollection _ramSparklinePoints = new();
    public PointCollection RamSparklinePoints { get => _ramSparklinePoints; private set { _ramSparklinePoints = value; OnPropertyChanged(); } }

    private PointCollection _gpuSparklinePoints = new();
    public PointCollection GpuSparklinePoints { get => _gpuSparklinePoints; private set { _gpuSparklinePoints = value; OnPropertyChanged(); } }

    // ── Ping Monitor properties ──────────────────────────────────────────
    private string _pingText = "--";
    public string PingText { get => _pingText; private set { _pingText = value; OnPropertyChanged(); } }

    private Brush _pingBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    public Brush PingBrush { get => _pingBrush; private set { _pingBrush = value; OnPropertyChanged(); } }

    private PointCollection _pingSparklinePoints = new();
    public PointCollection PingSparklinePoints { get => _pingSparklinePoints; private set { _pingSparklinePoints = value; OnPropertyChanged(); } }

    private string _pingStats = "";
    public string PingStats { get => _pingStats; private set { _pingStats = value; OnPropertyChanged(); } }

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
        _vbsHvciToggle = vbsHvciToggle;
        _dpcMonitor = dpcMonitor;
        _perfMonitor = perfMonitor;
        _pingMonitor = pingMonitor;
        _sessionStore = sessionStore;
        _sessionTracker = sessionTracker;

        // Subscribe to engine events for optimization state changes
        _engine.OptimizationApplied += OnOptimizationApplied;
        _engine.OptimizationReverted += OnOptimizationReverted;

        // Subscribe to detector events for active game tracking
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;

        // Subscribe to DPC latency samples for real-time indicator and sparkline
        if (_dpcMonitor != null)
        {
            _dpcMonitor.LatencySampled += OnLatencySampled;
            _dpcMonitor.DpcSpikeDetected += OnDpcSpikeDetected;
        }

        // Subscribe to performance monitor
        if (_perfMonitor != null)
        {
            _perfMonitor.SampleUpdated += OnPerformanceSampled;
            _perfMonitor.Start();
        }

        // Subscribe to ping monitor
        if (_pingMonitor != null)
        {
            var settings = SettingsManager.Load();
            _pingMonitor.PingUpdated += OnPingUpdated;
            _pingMonitor.Start(settings.PingTarget);
        }

        // Subscribe to session tracker
        if (_sessionTracker != null)
        {
            _sessionTracker.SessionEnded += OnSessionEnded;
        }

        // Load recent sessions
        LoadRecentSessions();

        // Set initial VBS banner state — contextual red/amber logic
        if (_vbsHvciToggle != null)
        {
            var blockingACs = AntiCheatDetector.GetVbsRequiringAntiCheats();
            if (!_vbsHvciToggle.IsEitherEnabled && blockingACs.Count > 0)
            {
                // VBS disabled but anti-cheat requires it — RED error banner
                var acNames = string.Join(", ", blockingACs.Select(ac => ac.DisplayName));
                _isVbsConflict = true;
                _vbsBannerSeverity = "error";
                _showVbsBanner = true;
                _vbsBannerMessage = $"Memory Integrity is disabled but required by {acNames}. " +
                    "You may experience VAN:RESTRICTION errors or anti-cheat failures. " +
                    "Click Re-enable & Reboot to fix.";
            }
            else
            {
                // Normal amber warning (VBS enabled, no conflict)
                _isVbsConflict = false;
                _vbsBannerSeverity = "warning";
                _showVbsBanner = _vbsHvciToggle.ShouldShowBanner;
                _vbsBannerMessage = _vbsHvciToggle.BannerMessage;
            }
        }

        // Set GPU info from live WMI detection (single source of truth)
        _gpuInfo = GpuDetector.GetGpuName();

        // Set initial state
        RefreshStatus();
        RefreshOptimizations();

        // Subscribe to AllActivities changes to keep RecentActivities updated
        AllActivities.CollectionChanged += (s, e) => UpdateRecentActivities();
        UpdateRecentActivities();

        // Check for updates asynchronously (fire-and-forget, non-blocking)
        CheckForUpdatesAsync();
    }

    /// <summary>
    /// Checks GitHub for a newer release. Non-blocking, runs on background thread.
    /// Sets ShowUpdateBanner/UpdateMessage/UpdateUrl on the UI thread if an update exists.
    /// </summary>
    private async void CheckForUpdatesAsync()
    {
        try
        {
            // If update was already downloaded via startup popup, show "ready" state
            if (System.IO.File.Exists(UpdateApplier.GetUpdateStagingPath()))
            {
                var stagedUpdate = await UpdateChecker.CheckForUpdateAsync();
                if (stagedUpdate != null)
                {
                    _pendingUpdate = stagedUpdate;
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        IsUpdateReady = true;
                        ShowUpdateBanner = true;
                        UpdateMessage = $"GameShift v{stagedUpdate.LatestVersion} is downloaded and ready to install";
                        UpdateUrl = stagedUpdate.ReleaseUrl;
                    });
                    return;
                }
            }

            var update = await UpdateChecker.CheckForUpdateAsync();
            if (update != null)
            {
                _pendingUpdate = update;
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    UpdateMessage = $"GameShift v{update.LatestVersion} is available (you have v{update.CurrentVersion})";
                    UpdateUrl = update.ReleaseUrl;
                    ShowUpdateBanner = true;
                });
            }
        }
        catch
        {
            // Non-critical — silently ignore update check failures
        }
    }

    /// <summary>
    /// Opens the release URL in the default browser. Fallback when in-app download is unavailable.
    /// </summary>
    public void OpenUpdateUrl()
    {
        if (string.IsNullOrEmpty(UpdateUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// Downloads the update from GitHub and stages it for replacement.
    /// Falls back to opening the browser if no direct download URL is available.
    /// </summary>
    public async void DownloadAndApplyUpdateAsync()
    {
        if (_pendingUpdate == null) return;

        // Fallback: no direct download URL (asset not in release)
        if (string.IsNullOrEmpty(_pendingUpdate.DownloadUrl))
        {
            OpenUpdateUrl();
            return;
        }

        if (IsDownloading) return;

        IsDownloading = true;
        IsUpdateReady = false;
        DownloadProgress = 0;
        DownloadStatusText = "Starting download...";

        _downloadCts = new CancellationTokenSource();

        try
        {
            var targetPath = UpdateApplier.GetUpdateStagingPath();
            var progress = new Progress<double>(p =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    DownloadProgress = p * 100.0;
                    DownloadStatusText = $"Downloading... {p * 100.0:F0}%";
                });
            });

            var success = await Task.Run(() =>
                UpdateDownloader.DownloadAsync(
                    _pendingUpdate.DownloadUrl!,
                    targetPath,
                    _pendingUpdate.DownloadSize,
                    progress,
                    _downloadCts.Token));

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    IsDownloading = false;
                    IsUpdateReady = true;
                    DownloadProgress = 100;
                    DownloadStatusText = "Ready to install. Click Restart to apply.";
                }
                else
                {
                    IsDownloading = false;
                    DownloadStatusText = "Download failed. Click Download to retry.";
                }
            });
        }
        catch (OperationCanceledException)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsDownloading = false;
                DownloadProgress = 0;
                DownloadStatusText = "";
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Update download failed");
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsDownloading = false;
                DownloadStatusText = "Download failed. Click Download to retry.";
            });
        }
    }

    /// <summary>Cancels an in-progress download.</summary>
    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    /// <summary>
    /// Applies the staged update and shuts down the app for replacement.
    /// </summary>
    public void ApplyUpdateAndRestart()
    {
        if (!IsUpdateReady) return;

        if (UpdateApplier.ApplyUpdate())
        {
            Application.Current.Shutdown();
        }
        else
        {
            DownloadStatusText = "Failed to apply update. Try downloading again.";
            IsUpdateReady = false;
        }
    }

    // ── DPC Troubleshooter ─────────────────────────────────────────────────

    /// <summary>
    /// Runs DPC driver analysis in the background. Populates TroubleshooterResults
    /// and shows the inline results panel. Safe to call multiple times (re-scan).
    /// </summary>
    public async void RunDpcAnalysisAsync()
    {
        if (IsAnalyzing) return;

        IsAnalyzing = true;
        ShowTroubleshooter = true;
        AnalysisSummary = "Scanning drivers...";

        try
        {
            var troubleshooter = new DpcTroubleshooter(_dpcMonitor);
            var result = await troubleshooter.AnalyzeAsync();

            Application.Current.Dispatcher.Invoke(() =>
            {
                TroubleshooterResults.Clear();
                foreach (var match in result.Matches)
                {
                    TroubleshooterResults.Add(match);
                }

                if (result.Matches.Count > 0)
                {
                    AnalysisSummary = $"Found {result.Matches.Count} known DPC offender{(result.Matches.Count == 1 ? "" : "s")} " +
                                      $"({result.DriversScanned} drivers scanned)";
                }
                else
                {
                    AnalysisSummary = $"No known offenders detected ({result.DriversScanned} drivers scanned). Your drivers look clean.";
                }

                IsAnalyzing = false;
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "DPC analysis failed");
            Application.Current.Dispatcher.Invoke(() =>
            {
                AnalysisSummary = "Analysis failed. Try re-scanning.";
                IsAnalyzing = false;
            });
        }
    }

    // ── Performance / Ping / Session event handlers ─────────────────────────

    private void OnPerformanceSampled(object? sender, PerformanceSample e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CpuText = $"{e.CpuPercent:F0}%";
            RamText = $"{e.RamPercent:F0}%";
            GpuUtilText = e.GpuPercent >= 0 ? $"{e.GpuPercent:F0}%" : "N/A";

            EnqueueAndUpdateSparkline(_cpuSparklineSamples, e.CpuPercent, 100, v => CpuSparklinePoints = v);
            EnqueueAndUpdateSparkline(_ramSparklineSamples, e.RamPercent, 100, v => RamSparklinePoints = v);
            if (e.GpuPercent >= 0)
                EnqueueAndUpdateSparkline(_gpuSparklineSamples, e.GpuPercent, 100, v => GpuSparklinePoints = v);
        });
    }

    private void OnPingUpdated(object? sender, PingSample e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (e.Success)
            {
                PingText = $"{e.RttMilliseconds}ms";
                if (e.RttMilliseconds < 50)
                    PingBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); // green
                else if (e.RttMilliseconds <= 100)
                    PingBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // yellow
                else
                    PingBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // red
            }
            else
            {
                PingText = "Timeout";
                PingBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            }

            PingStats = $"Avg: {e.AverageRtt:F0}ms | Jitter: {e.JitterMs:F0}ms | Loss: {e.PacketLossPercent:F0}%";

            _pingSparklineSamples.Enqueue(e.Success ? e.RttMilliseconds : -1);
            while (_pingSparklineSamples.Count > 60) _pingSparklineSamples.Dequeue();
            UpdatePingSparkline();
        });
    }

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

    private void EnqueueAndUpdateSparkline(Queue<double> queue, double value, double maxValue, Action<PointCollection> setter)
    {
        queue.Enqueue(value);
        while (queue.Count > 60) queue.Dequeue();

        var samples = queue.ToArray();
        var points = new PointCollection(samples.Length);
        double max = Math.Max(maxValue, samples.Max());
        if (max < 1) max = 1;

        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples.Length == 1 ? 0 : i * (120.0 / (samples.Length - 1));
            double y = 48.0 - (samples[i] / max) * 48.0;
            points.Add(new System.Windows.Point(x, y));
        }
        setter(points);
    }

    private void UpdatePingSparkline()
    {
        var samples = _pingSparklineSamples.Where(s => s >= 0).ToArray();
        if (samples.Length == 0) { PingSparklinePoints = new PointCollection(); return; }

        var points = new PointCollection(samples.Length);
        double max = Math.Max(100, samples.Max());

        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples.Length == 1 ? 0 : i * (120.0 / (samples.Length - 1));
            double y = 32.0 - (samples[i] / max) * 32.0;
            points.Add(new System.Windows.Point(x, y));
        }
        PingSparklinePoints = points;
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

            // Update DPC indicator visibility based on monitoring state
            ShowDpcIndicator = _dpcMonitor?.IsMonitoring == true;
            if (!ShowDpcIndicator)
            {
                DpcLatencyText = "";
            }

            // Background Mode status
            var bg = App.BackgroundMode;
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
            var gpm = App.GameProfileMgr;
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

            var defaultProfile = App.ProfileMgr?.GetDefaultProfile();

            // Before/after descriptors indexed to match App.Optimizations array order (0-10)
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

            OptimizationSummary = $"{applied}/{total} Active";
            GpuOptimizationState = gpuState;
        });
    }

    /// <summary>
    /// Called when user toggles an optimization's enabled/disabled switch.
    /// Updates the default GameProfile and persists via ProfileManager.
    /// </summary>
    private void OnOptimizationToggled(ExpandableOptimizationItem item)
    {
        if (App.ProfileMgr == null) return;

        var profile = App.ProfileMgr.GetDefaultProfile();

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
        }

        App.ProfileMgr.SaveProfile(profile);
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
    /// Handles real-time DPC latency samples. Updates indicator text, color, visibility,
    /// sparkline data, sparkline brush, and average latency.
    /// Color thresholds: green &lt; 500us, yellow 500-1000us, red &gt; 1000us.
    /// </summary>
    private void OnLatencySampled(object? sender, double latencyUs)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ShowDpcIndicator = _dpcMonitor?.IsMonitoring == true;

            if (!ShowDpcIndicator) return;

            // Color thresholds: green < 500, yellow 500-1000, red > 1000
            string label;
            Brush brush;
            if (latencyUs < 500)
            {
                label = "Good";
                brush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); // #4ADE80 green
            }
            else if (latencyUs <= 1000)
            {
                label = "Warning";
                brush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // #FBBF24 yellow
            }
            else
            {
                label = "High";
                brush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // #F87171 red
            }

            DpcLatencyText = $"{latencyUs:F0}\u00B5s ({label})";
            DpcLatencyBrush = brush;
            SparklineBrush = brush;

            // Update average latency
            if (_dpcMonitor != null)
                AverageDpcLatency = Math.Round(_dpcMonitor.AverageLatencyMicroseconds, 0);

            // Update sparkline queue
            _sparklineSamples.Enqueue(latencyUs);
            while (_sparklineSamples.Count > MaxSparklineSamples)
                _sparklineSamples.Dequeue();

            UpdateSparklinePoints();
        });
    }

    /// <summary>
    /// Converts the sparkline sample queue into a PointCollection for the Polyline binding.
    /// X axis: evenly spaced across 120px. Y axis: scaled to fit 32px, Y=0 at top.
    /// </summary>
    private void UpdateSparklinePoints()
    {
        var samples = _sparklineSamples.ToArray();
        if (samples.Length == 0)
        {
            SparklinePoints = new PointCollection();
            return;
        }

        var maxSample = samples.Max();
        if (maxSample < 100.0) maxSample = 100.0; // avoid division by zero / flat line for small values

        var points = new PointCollection(samples.Length);
        int count = samples.Length;

        for (int i = 0; i < count; i++)
        {
            double x = count == 1 ? 0 : i * (SparklineWidth / (count - 1));
            double y = SparklineHeight - (samples[i] / maxSample) * SparklineHeight;
            points.Add(new System.Windows.Point(x, y));
        }

        SparklinePoints = points;
    }

    /// <summary>
    /// Handles DPC spike detection events. Shows the alert banner unless dismissed.
    /// </summary>
    private void OnDpcSpikeDetected(object? sender, DpcSpikeEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var settings = SettingsManager.Load();
            if (settings.DpcSpikeAlertDismissed) return;

            DpcSpikeAlertMessage = $"DPC spike detected: {e.LatencyMicroseconds:F0}µs" +
                (string.IsNullOrEmpty(e.DriverName) ? "" : $" — suspected driver: {e.DriverName}");
            ShowDpcSpikeAlert = true;
        });
    }

    /// <summary>
    /// Dismisses the DPC spike alert banner and troubleshooter panel.
    /// Persists dismissal via SettingsManager.
    /// </summary>
    public void DismissDpcSpikeAlert()
    {
        ShowDpcSpikeAlert = false;
        ShowTroubleshooter = false;
        var settings = SettingsManager.Load();
        settings.DpcSpikeAlertDismissed = true;
        SettingsManager.Save(settings);
    }

    /// <summary>
    /// Dismisses the VBS/HVCI banner. Persists dismissal state via VbsHvciToggle.
    /// </summary>
    public void DismissVbsBanner()
    {
        _vbsHvciToggle?.DismissBanner();
        ShowVbsBanner = false;
    }

    /// <summary>
    /// Attempts to disable VBS/HVCI. Returns true if successful.
    /// Caller should schedule a reboot on success.
    /// </summary>
    public bool DisableVbsHvci()
    {
        if (_vbsHvciToggle == null) return false;
        var result = _vbsHvciToggle.DisableVbsHvci();
        if (result)
        {
            ShowVbsBanner = false;
        }
        return result;
    }

    /// <summary>
    /// Re-enables VBS/HVCI when a conflict is detected (VBS disabled + AC requires it).
    /// Returns true if successful. Caller should schedule a reboot on success.
    /// </summary>
    public bool ReEnableVbsHvci()
    {
        if (_vbsHvciToggle == null) return false;
        var result = _vbsHvciToggle.ReEnableVbsHvci();
        if (result)
        {
            IsVbsConflict = false;
            VbsBannerSeverity = "warning";
            ShowVbsBanner = false;
        }
        return result;
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
        _detector.GameStarted -= OnGameStarted;
        _detector.GameStopped -= OnGameStopped;

        if (_dpcMonitor != null)
        {
            _dpcMonitor.LatencySampled -= OnLatencySampled;
            _dpcMonitor.DpcSpikeDetected -= OnDpcSpikeDetected;
        }

        if (_perfMonitor != null) _perfMonitor.SampleUpdated -= OnPerformanceSampled;
        if (_pingMonitor != null) _pingMonitor.PingUpdated -= OnPingUpdated;
    }

    /// <summary>
    /// Restarts the sparkline data feed by re-subscribing to all events.
    /// Called when DashboardPage navigates back.
    /// Refreshes status immediately to show current state after navigate-back.
    /// </summary>
    public void StartTimers()
    {
        // Re-subscribe — safe to call even if already subscribed since handlers are idempotent
        // (C# event handlers deduplicate by delegate reference)
        _engine.OptimizationApplied += OnOptimizationApplied;
        _engine.OptimizationReverted += OnOptimizationReverted;
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;

        if (_dpcMonitor != null)
        {
            _dpcMonitor.LatencySampled += OnLatencySampled;
            _dpcMonitor.DpcSpikeDetected += OnDpcSpikeDetected;
        }

        if (_perfMonitor != null) _perfMonitor.SampleUpdated += OnPerformanceSampled;
        if (_pingMonitor != null) _pingMonitor.PingUpdated += OnPingUpdated;
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
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();

        _engine.OptimizationApplied -= OnOptimizationApplied;
        _engine.OptimizationReverted -= OnOptimizationReverted;
        _detector.GameStarted -= OnGameStarted;
        _detector.GameStopped -= OnGameStopped;

        if (_dpcMonitor != null)
        {
            _dpcMonitor.LatencySampled -= OnLatencySampled;
            _dpcMonitor.DpcSpikeDetected -= OnDpcSpikeDetected;
        }

        if (_perfMonitor != null) _perfMonitor.SampleUpdated -= OnPerformanceSampled;
        if (_pingMonitor != null) _pingMonitor.PingUpdated -= OnPingUpdated;
        if (_sessionTracker != null) _sessionTracker.SessionEnded -= OnSessionEnded;
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
