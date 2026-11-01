using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameShift.App.Views;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using Serilog;

namespace GameShift.App.Services;

/// <summary>
/// Manages the system tray icon lifecycle, context menu, icon state transitions,
/// and toast notifications. Uses Hardcodet.NotifyIcon.Wpf TaskbarIcon which works
/// reliably on both Windows 10 and Windows 11.
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly DetectionOrchestrator _orchestrator;
    private readonly OptimizationEngine _engine;
    private readonly GameDetector _detector;
    private readonly AppSettings _settings;
    private readonly DpcLatencyMonitor? _dpcMonitor;
    private readonly ILogger _logger;
    private readonly TrayStatusTracker _status;
    private readonly DispatcherTimer _refreshTimer;
    private TrayIconTheme _iconTheme;
    private TrayIconShape _shownShape;
    private bool _disposed;

    // Session tracking for post-session toast
    private DateTime? _sessionStartTime;
    private string? _sessionGameName;
    private double _sessionPeakDpc;
    private double _sessionDpcSum;
    private int _sessionDpcSampleCount;
    private int _sessionOptimizationCount;
    private int _sessionFailedCount;

    // Session-stat fields above are written from the DPC-sample timer, the optimization-engine
    // thread, and the detector thread, and read when building the summary toast. Guard them all
    // with this lock so the toast figures are consistent (these are cosmetic stats only).
    private readonly object _statsLock = new();

    /// <summary>
    /// Gets whether game monitoring is currently paused by the user.
    /// </summary>
    public bool IsPaused => _orchestrator.IsPaused;

    /// <summary>Raised when the user requests the dashboard window (double-click or menu).</summary>
    public event Action? DashboardRequested;

    /// <summary>Raised when the user requests the game library window.</summary>
    public event Action? GameLibraryRequested;

    /// <summary>Raised when the user requests the profile editor window.</summary>
    public event Action? ProfileEditorRequested;

    /// <summary>Raised when the user requests the settings window.</summary>
    public event Action? SettingsRequested;

    public TrayIconManager(
        DetectionOrchestrator orchestrator,
        OptimizationEngine engine,
        GameDetector detector,
        AppSettings settings,
        DpcLatencyMonitor? dpcMonitor = null)
    {
        _orchestrator = orchestrator;
        _engine = engine;
        _detector = detector;
        _settings = settings;
        _dpcMonitor = dpcMonitor;
        _logger = SettingsManager.Logger;

        _status = new TrayStatusTracker(
            () => _orchestrator.IsOptimizing,
            () => _orchestrator.IsPaused,
            () => _engine.AppliedCount,
            () => _engine.LastOutcome,
            TimeProvider.System);
        _iconTheme = ReadIconTheme();

        // The engine raises its last events just before the orchestrator records that the session
        // ended, so a session can look active for a moment after it is over. Re-resolving on a
        // timer while one is seen running corrects that and expires the DPC line.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += OnRefreshTimerTick;

        // Create TaskbarIcon using Hardcodet.NotifyIcon.Wpf (works on Win10 + Win11)
        _taskbarIcon = new TaskbarIcon();
        _taskbarIcon.ToolTipText = "GameShift - Idle";
        _shownShape = TrayIconShape.Idle;
        _taskbarIcon.Icon = LoadIcon(TrayIconSets.ResourceName(_shownShape, _iconTheme)); // initial assignment - no prior icon to dispose
        _taskbarIcon.ContextMenu = BuildContextMenu();

        // Single-click opens dashboard (same as double-click)
        _taskbarIcon.TrayLeftMouseUp += OnTrayLeftDoubleClick;

        // Subscribe to engine events for icon state changes
        _engine.OptimizationApplied += OnOptimizationApplied;
        _engine.OptimizationReverted += OnOptimizationReverted;
        _engine.OptimizationFailed += OnOptimizationFailed;
        _engine.ActivationCompleted += OnActivationCompleted;

        // Subscribe to detector events for notifications
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;
        _detector.AllGamesStopped += OnAllGamesStopped;

        // A DPC spike adds a line to the tooltip and the status item
        if (_dpcMonitor != null)
        {
            _dpcMonitor.DpcSpikeDetected += OnDpcSpikeForIcon;
        }

        // Track peak DPC during session for post-session toast
        if (_dpcMonitor != null)
        {
            _dpcMonitor.LatencySampled += OnLatencySampled;
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;

        _logger.Information("TrayIconManager initialized with Hardcodet TaskbarIcon");
    }

    /// <summary>
    /// Loads a System.Drawing.Icon from the embedded .ico resource. The underlying
    /// resource stream is disposed before returning - Icon copies the pixel data
    /// during construction, so the stream is no longer needed. Hardcodet TaskbarIcon
    /// requires System.Drawing.Icon, not BitmapImage.
    /// </summary>
    private static Icon LoadIcon(string iconName)
    {
        var uri = new Uri($"pack://application:,,,/Assets/Icons/{iconName}.ico");
        var streamInfo = Application.GetResourceStream(uri);
        if (streamInfo == null)
            throw new FileNotFoundException($"Tray icon resource not found: {iconName}.ico");
        using var stream = streamInfo.Stream;

        // The notification area draws the small icon size for the current scaling (16 px at 100%,
        // 24 px at 150%); without a size, Icon takes the large frame and Windows shrinks it blurry.
        var size = GetSystemMetrics(SM_CXSMICON);
        return size > 0 ? new Icon(stream, size, size) : new Icon(stream);
    }

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// The icon set for the taskbar: from the Windows light-theme setting, or in high contrast from
    /// the theme's window background. A missing setting means the dark taskbar Windows 10 started with.
    /// </summary>
    private TrayIconTheme ReadIconTheme()
    {
        var lightTheme = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTheme = key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not read the taskbar theme; using the dark taskbar icons");
        }

        var background = System.Windows.SystemColors.WindowColor;
        return TrayIconSets.Choose(lightTheme, SystemParameters.HighContrast, background.R, background.G, background.B);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(RefreshIconTheme);
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Application.Current?.Dispatcher.InvokeAsync(RefreshIconTheme);
    }

    private void RefreshIconTheme()
    {
        if (_disposed) return;
        try
        {
            var theme = ReadIconTheme();
            if (theme == _iconTheme) return;
            _iconTheme = theme;
            SetTaskbarIcon(TrayIconSets.ResourceName(_shownShape, _iconTheme));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to switch the tray icons to the taskbar theme");
        }
    }

    /// <summary>
    /// Assigns a new icon to the taskbar icon and disposes the previous one.
    /// Each LoadIcon call allocates a GDI handle; without disposing the old icon
    /// every state transition would leak a handle.
    /// </summary>
    private void SetTaskbarIcon(string iconName)
    {
        var oldIcon = _taskbarIcon.Icon;
        _taskbarIcon.Icon = LoadIcon(iconName);
        oldIcon?.Dispose();
    }

    private void OnTrayLeftDoubleClick(object sender, RoutedEventArgs e)
    {
        DashboardRequested?.Invoke();
    }

    /// <summary>
    /// Builds the right-click context menu.
    /// Items: Status (disabled header), Dashboard, Game Library, Pause/Resume, Settings, Exit.
    /// </summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        // Status header (disabled, shows current state)
        var statusItem = new MenuItem { Header = "GameShift: Idle", IsEnabled = false };
        statusItem.Tag = "status";
        menu.Items.Add(statusItem);

        menu.Items.Add(new Separator());

        // Open Dashboard
        var dashboardItem = new MenuItem { Header = "Open Dashboard" };
        dashboardItem.Click += (s, e) => DashboardRequested?.Invoke();
        menu.Items.Add(dashboardItem);

        // Game Library
        var libraryItem = new MenuItem { Header = "Game Library" };
        libraryItem.Click += (s, e) => GameLibraryRequested?.Invoke();
        menu.Items.Add(libraryItem);

        // Profile Editor
        var profileItem = new MenuItem { Header = "Profile Editor" };
        profileItem.Click += (s, e) => ProfileEditorRequested?.Invoke();
        menu.Items.Add(profileItem);

        menu.Items.Add(new Separator());

        // Pause/Resume toggle
        var pauseItem = new MenuItem { Header = "Pause Monitoring" };
        pauseItem.Tag = "pause";
        pauseItem.Click += OnPauseClicked;
        menu.Items.Add(pauseItem);

        // Settings
        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (s, e) => SettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        // Exit
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += OnExitClicked;
        menu.Items.Add(exitItem);

        return menu;
    }

    /// <summary>
    /// Updates the tray icon, tooltip, and status menu item from <see cref="TrayState"/>.
    /// Must be called on the UI thread.
    /// </summary>
    private void UpdateTrayState()
    {
        var state = _status.Resolve();

        if (state.Shape != _shownShape)
        {
            SetTaskbarIcon(TrayIconSets.ResourceName(state.Shape, _iconTheme));
            _shownShape = state.Shape;
        }
        if (_taskbarIcon.ToolTipText != state.ToolTip)
            _taskbarIcon.ToolTipText = state.ToolTip;
        UpdateStatusMenuItem(state.StatusText);

        if (_orchestrator.IsOptimizing)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            UpdateTrayState();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update tray state");
        }
    }

    private void UpdateStatusMenuItem(string text)
    {
        if (_taskbarIcon.ContextMenu == null) return;

        foreach (var item in _taskbarIcon.ContextMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Tag as string == "status")
            {
                menuItem.Header = text;
                break;
            }
        }
    }

    private void OnOptimizationApplied(object? sender, OptimizationAppliedEventArgs e)
    {
        try
        {
            lock (_statsLock) { _sessionOptimizationCount++; }
            QueueTrayStateUpdate();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update tray state on optimization applied");
        }
    }

    private void OnOptimizationFailed(object? sender, OptimizationAppliedEventArgs e)
    {
        try
        {
            lock (_statsLock) { _sessionFailedCount++; }
            QueueTrayStateUpdate();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to track optimization failure");
        }
    }

    // A run where nothing applied or failed raises no module event, and the session may have been
    // recorded as started only after OnGameStarted's update ran.
    private void OnActivationCompleted(object? sender, SessionOutcomeEventArgs e)
    {
        try
        {
            QueueTrayStateUpdate();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update tray state on activation completed");
        }
    }

    private void OnOptimizationReverted(object? sender, OptimizationRevertedEventArgs e)
    {
        try
        {
            QueueTrayStateUpdate();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update tray state on optimization reverted");
        }
    }

    /// <summary>
    /// Updates the tray state on the UI thread without waiting for it. The engine raises its events
    /// on whatever thread ends a session, including while App.OnExit blocks the UI thread waiting for
    /// that session to end, where a synchronous Invoke would deadlock.
    /// </summary>
    private void QueueTrayStateUpdate()
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            try
            {
                UpdateTrayState();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to update tray state");
            }
        });
    }

    private void OnGameStarted(object? sender, GameDetectedEventArgs e)
    {
        try
        {
            // Track session start for post-session summary
            lock (_statsLock)
            {
                if (_sessionStartTime == null)
                {
                    _sessionStartTime = DateTime.Now;
                    _sessionGameName = e.GameName;
                    _sessionPeakDpc = 0;
                    _sessionDpcSum = 0;
                    _sessionDpcSampleCount = 0;
                    _sessionOptimizationCount = 0;
                    _sessionFailedCount = 0;
                }
            }

            QueueTrayStateUpdate();

            if (_settings.ShowNotifications && _settings.ShowGameDetectedToast)
            {
                _logger.Information(
                    "Notification: Game Detected - {GameName} detected. Optimizations activating...",
                    e.GameName);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to log game started notification for {GameName}", e.GameName);
        }
    }

    private void OnGameStopped(object? sender, GameDetectedEventArgs e)
    {
        try
        {
            if (_settings.ShowNotifications)
            {
                var remaining = _detector.GetActiveGames().Count;
                var message = remaining == 0
                    ? "All games exited. Optimizations reverted."
                    : $"{e.GameName} exited. {remaining} game(s) still running.";

                _logger.Information("Notification: Game Exited - {Message}", message);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to log game stopped notification for {GameName}", e.GameName);
        }
    }

    private void OnDpcSpikeForIcon(object? sender, DpcSpikeEventArgs e)
    {
        try
        {
            _status.RecordDpcSpike();
            QueueTrayStateUpdate();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update tray icon on DPC spike");
        }
    }

    private void OnLatencySampled(object? sender, double latencyMicroseconds)
    {
        if (_orchestrator.IsOptimizing)
        {
            lock (_statsLock)
            {
                if (latencyMicroseconds > _sessionPeakDpc)
                    _sessionPeakDpc = latencyMicroseconds;

                _sessionDpcSum += latencyMicroseconds;
                _sessionDpcSampleCount++;
            }
        }
    }

    private void ShowSessionSummaryToast(
        string gameName, TimeSpan duration, int optCount, int failedCount, double avgDpc, double peakDpc)
    {
        try
        {
            if (_disposed) return;

            if (!_settings.ShowNotifications || !_settings.ShowSessionSummaryToast)
            {
                _logger.Debug("Post-session toast suppressed by settings");
                return;
            }

            var toast = new ToastNotificationWindow();
            toast.SetSessionData(gameName, duration, optCount, failedCount, avgDpc, peakDpc);
            toast.Show();

            _logger.Information(
                "Post-session toast shown for {GameName}: duration={Duration}, opts={OptCount}, failed={FailedCount}, avgDpcLoad={AvgDpc}, peakDpcLoad={PeakDpc}",
                gameName, duration, optCount, failedCount, DpcLoad.Format(avgDpc), DpcLoad.Format(peakDpc));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to show post-session summary toast");
        }
    }

    /// <summary>
    /// Shows a DPC load alert notification, checking the per-type ShowDpcAlertToast setting.
    /// </summary>
    public void ShowDpcNotification(string title, string message)
    {
        if (!_settings.ShowNotifications || !_settings.ShowDpcAlertToast)
            return;

        ShowNotification(title, message);
    }

    private void OnAllGamesStopped(object? sender, EventArgs e)
    {
        try
        {
            // Resuming raises this while the orchestrator holds its session lock, so the toast and the
            // tray update are queued, not waited for (see QueueTrayStateUpdate). The toast gets the
            // session's figures as they are now, before they are cleared below.
            lock (_statsLock)
            {
                if (_sessionStartTime is DateTime startTime)
                {
                    var gameName = _sessionGameName ?? "Unknown Game";
                    var duration = DateTime.Now - startTime;
                    var optCount = _sessionOptimizationCount;
                    var failedCount = _sessionFailedCount;
                    var avgDpc = _sessionDpcSampleCount > 0 ? _sessionDpcSum / _sessionDpcSampleCount : 0;
                    var peakDpc = _sessionPeakDpc;
                    Application.Current.Dispatcher.InvokeAsync(() =>
                        ShowSessionSummaryToast(gameName, duration, optCount, failedCount, avgDpc, peakDpc));
                }

                _sessionStartTime = null;
                _sessionGameName = null;
                _sessionPeakDpc = 0;
                _sessionDpcSum = 0;
                _sessionDpcSampleCount = 0;
                _sessionOptimizationCount = 0;
                _sessionFailedCount = 0;
            }

            QueueTrayStateUpdate();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to handle all games stopped event");
        }
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            if (!_settings.ShowNotifications)
                return;

            if (_settings.SuppressNotificationsDuringGaming && _orchestrator.IsOptimizing)
            {
                _logger.Debug("Notification suppressed during gaming: {Title}", title);
                return;
            }

            _logger.Information("Notification: {Title} - {Message}", title, message);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to process notification: {Title}", title);
        }
    }

    /// <summary>
    /// Toggles game monitoring pause state. Can be called from the context menu or global hotkey.
    /// Pausing ends an active game session through the orchestrator; resuming optimizes a game
    /// that is still running. Completes once the change has taken effect and returns whether
    /// monitoring is paused after this toggle. Must be called on the UI thread; never throws.
    /// </summary>
    public async Task<bool> TogglePauseAsync()
    {
        var paused = _orchestrator.IsPaused;
        try
        {
            paused = await _orchestrator.TogglePauseAsync();
            if (paused)
                _logger.Information("Game monitoring paused by user");
            else
                _logger.Information("Game monitoring resumed by user");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to toggle pause state");
        }

        try
        {
            UpdatePauseMenuItem(_orchestrator.IsPaused ? "Resume Monitoring" : "Pause Monitoring");
            UpdateTrayState();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update tray state after toggling pause");
        }

        return paused;
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e)
    {
        _ = TogglePauseAsync();
    }

    private void UpdatePauseMenuItem(string text)
    {
        if (_taskbarIcon.ContextMenu == null) return;

        foreach (var item in _taskbarIcon.ContextMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Tag as string == "pause")
            {
                menuItem.Header = text;
                break;
            }
        }
    }

    private async void OnExitClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.Information("Ending the optimization session before exit");
            await _orchestrator.ShutdownAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to end the optimization session during exit");
        }

        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _engine.OptimizationApplied -= OnOptimizationApplied;
        _engine.OptimizationReverted -= OnOptimizationReverted;
        _engine.OptimizationFailed -= OnOptimizationFailed;
        _engine.ActivationCompleted -= OnActivationCompleted;

        _detector.GameStarted -= OnGameStarted;
        _detector.GameStopped -= OnGameStopped;
        _detector.AllGamesStopped -= OnAllGamesStopped;

        if (_dpcMonitor != null)
        {
            _dpcMonitor.DpcSpikeDetected -= OnDpcSpikeForIcon;
            _dpcMonitor.LatencySampled -= OnLatencySampled;
        }

        _taskbarIcon.TrayLeftMouseUp -= OnTrayLeftDoubleClick;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;

        // Capture the current icon BEFORE TaskbarIcon.Dispose() so we can free its GDI
        // handle. TaskbarIcon.Dispose() does not dispose the assigned Icon.
        var finalIcon = _taskbarIcon.Icon;
        _taskbarIcon.Dispose();
        finalIcon?.Dispose();

        _disposed = true;
        _logger.Information("TrayIconManager disposed");
    }
}
