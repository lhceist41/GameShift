using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GameShift.App.Views;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using Hardcodet.Wpf.TaskbarNotification;
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
    private readonly ProfileManager? _profileManager;
    private readonly ILogger _logger;
    private bool _isPaused;
    private bool _hasDpcWarning;
    private bool _disposed;

    // Session tracking for post-session toast
    private DateTime? _sessionStartTime;
    private string? _sessionGameName;
    private double _sessionPeakDpc;
    private double _sessionDpcSum;
    private int _sessionDpcSampleCount;
    private int _sessionOptimizationCount;
    private int _sessionFailedCount;

    /// <summary>
    /// Gets whether game monitoring is currently paused by the user.
    /// </summary>
    public bool IsPaused => _isPaused;

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
        DpcLatencyMonitor? dpcMonitor = null,
        ProfileManager? profileManager = null)
    {
        _orchestrator = orchestrator;
        _engine = engine;
        _detector = detector;
        _settings = settings;
        _dpcMonitor = dpcMonitor;
        _profileManager = profileManager;
        _logger = SettingsManager.Logger;
        _isPaused = false;

        // Create TaskbarIcon using Hardcodet.NotifyIcon.Wpf (works on Win10 + Win11)
        _taskbarIcon = new TaskbarIcon();
        _taskbarIcon.ToolTipText = "GameShift - Idle";
        _taskbarIcon.Icon = LoadIcon("tray-idle"); // initial assignment — no prior icon to dispose
        _taskbarIcon.ContextMenu = BuildContextMenu();

        // Single-click opens dashboard (same as double-click)
        _taskbarIcon.TrayLeftMouseUp += OnTrayLeftDoubleClick;

        // Subscribe to engine events for icon state changes
        _engine.OptimizationApplied += OnOptimizationApplied;
        _engine.OptimizationReverted += OnOptimizationReverted;
        _engine.OptimizationFailed += OnOptimizationFailed;

        // Subscribe to detector events for notifications
        _detector.GameStarted += OnGameStarted;
        _detector.GameStopped += OnGameStopped;
        _detector.AllGamesStopped += OnAllGamesStopped;

        // Subscribe to DPC spike events for warning icon
        if (_dpcMonitor != null)
        {
            _dpcMonitor.DpcSpikeDetected += OnDpcSpikeForIcon;
        }

        // Track peak DPC during session for post-session toast
        if (_dpcMonitor != null)
        {
            _dpcMonitor.LatencySampled += OnLatencySampled;
        }

        _logger.Information("TrayIconManager initialized with Hardcodet TaskbarIcon");
    }

    /// <summary>
    /// Loads a System.Drawing.Icon from the embedded .ico resource. The underlying
    /// resource stream is disposed before returning — Icon copies the pixel data
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
        return new Icon(stream);
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

        // Quick Profile Switch submenu
        var profileSwitchItem = new MenuItem { Header = "Quick Profile Switch" };
        profileSwitchItem.Tag = "profileSwitch";
        profileSwitchItem.SubmenuOpened += OnProfileSubmenuOpened;
        profileSwitchItem.Items.Add(new MenuItem { Header = "Loading...", IsEnabled = false });
        menu.Items.Add(profileSwitchItem);

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
    /// Updates the tray icon, tooltip, and status menu item based on current state.
    /// Priority order: error > warning (DPC during active session) > paused > active > idle.
    /// Must be called on the UI thread.
    /// </summary>
    private void UpdateTrayState()
    {
        string iconName;
        string tooltip;
        string statusText;

        if (_hasDpcWarning && _orchestrator.IsOptimizing)
        {
            iconName = "tray-warning";
            tooltip = "GameShift - Warning (DPC)";
            statusText = "GameShift: Warning";
        }
        else if (_isPaused)
        {
            iconName = "tray-idle";
            tooltip = "GameShift - Paused";
            statusText = "GameShift: Paused";
        }
        else if (_orchestrator.IsOptimizing)
        {
            iconName = "tray-active";
            tooltip = "GameShift - Optimizing";
            statusText = "GameShift: Optimizing";
        }
        else
        {
            iconName = "tray-idle";
            tooltip = "GameShift - Idle";
            statusText = "GameShift: Idle";
        }

        SetTaskbarIcon(iconName);
        _taskbarIcon.ToolTipText = tooltip;
        UpdateStatusMenuItem(statusText);
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
            _sessionOptimizationCount++;
            Application.Current.Dispatcher.Invoke(UpdateTrayState);
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
            _sessionFailedCount++;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to track optimization failure");
        }
    }

    private void OnOptimizationReverted(object? sender, OptimizationRevertedEventArgs e)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(UpdateTrayState);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update tray state on optimization reverted");
        }
    }

    private void OnGameStarted(object? sender, GameDetectedEventArgs e)
    {
        try
        {
            // Track session start for post-session summary
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

                _logger.Information("Notification: Game Exited — {Message}", message);
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
            _hasDpcWarning = true;
            Application.Current.Dispatcher.Invoke(UpdateTrayState);
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
            if (latencyMicroseconds > _sessionPeakDpc)
                _sessionPeakDpc = latencyMicroseconds;

            _sessionDpcSum += latencyMicroseconds;
            _sessionDpcSampleCount++;
        }
    }

    private void ShowSessionSummaryToast()
    {
        try
        {
            if (!_settings.ShowNotifications || !_settings.ShowSessionSummaryToast)
            {
                _logger.Debug("Post-session toast suppressed by settings");
                return;
            }

            var duration = DateTime.Now - (_sessionStartTime ?? DateTime.Now);
            var avgDpc = _sessionDpcSampleCount > 0 ? _sessionDpcSum / _sessionDpcSampleCount : 0;
            var peakDpc = _sessionPeakDpc;
            var gameName = _sessionGameName ?? "Unknown Game";
            var optCount = _sessionOptimizationCount;
            var failedCount = _sessionFailedCount;

            var toast = new ToastNotificationWindow();
            toast.SetSessionData(gameName, duration, optCount, failedCount, avgDpc, peakDpc);
            toast.Show();

            _logger.Information(
                "Post-session toast shown for {GameName}: duration={Duration}, opts={OptCount}, failed={FailedCount}, avgDpc={AvgDpc:F0}us, peakDpc={PeakDpc:F0}us",
                gameName, duration, optCount, failedCount, avgDpc, peakDpc);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to show post-session summary toast");
        }
    }

    /// <summary>
    /// Shows a DPC latency alert notification, checking the per-type ShowDpcAlertToast setting.
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
            // Show post-session toast BEFORE clearing session data
            if (_sessionStartTime.HasValue)
            {
                Application.Current.Dispatcher.Invoke(() => ShowSessionSummaryToast());
            }

            _hasDpcWarning = false;

            _sessionStartTime = null;
            _sessionGameName = null;
            _sessionPeakDpc = 0;
            _sessionDpcSum = 0;
            _sessionDpcSampleCount = 0;
            _sessionOptimizationCount = 0;
            _sessionFailedCount = 0;

            Application.Current.Dispatcher.Invoke(UpdateTrayState);
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

    private void OnProfileSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem submenu || _profileManager == null) return;

        try
        {
            submenu.Items.Clear();
            var profiles = _profileManager.GetAllProfiles();

            if (profiles.Count == 0)
            {
                submenu.Items.Add(new MenuItem { Header = "No profiles available", IsEnabled = false });
                return;
            }

            foreach (var profile in profiles)
            {
                var displayName = string.IsNullOrEmpty(profile.GameName)
                    ? (profile.Id == "default" ? "Default" : profile.Id)
                    : profile.GameName;

                var item = new MenuItem { Header = displayName };
                var capturedProfile = profile;
                item.Click += (_, _) => OnProfileSelected(capturedProfile);
                submenu.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to populate profile switch submenu");
        }
    }

    private void OnProfileSelected(GameProfile profile)
    {
        try
        {
            _settings.QuickSwitchProfileId = profile.Id;
            SettingsManager.Save(_settings);

            if (_orchestrator.IsOptimizing)
            {
                _logger.Information(
                    "Profile '{ProfileId}' selected via Quick Switch. Game is active — will apply on next session.",
                    profile.Id);
            }
            else
            {
                _logger.Information("Profile '{ProfileId}' selected via Quick Switch (no active game)", profile.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to handle profile selection for '{ProfileId}'", profile.Id);
        }
    }

    /// <summary>
    /// Toggles game monitoring pause state. Can be called from the context menu or global hotkey.
    /// When pausing during an active optimization session, force-deactivates optimizations first.
    /// Must be called on the UI thread.
    /// </summary>
    public async void TogglePause()
    {
        try
        {
            if (!_isPaused)
            {
                if (_orchestrator.IsOptimizing)
                {
                    _logger.Information("Force-deactivating optimizations before pausing monitoring");
                    await _engine.DeactivateProfileAsync();
                }

                _detector.StopMonitoring();
                _isPaused = true;
                UpdatePauseMenuItem("Resume Monitoring");
                _logger.Information("Game monitoring paused by user");
            }
            else
            {
                _detector.StartMonitoring();
                _isPaused = false;
                UpdatePauseMenuItem("Pause Monitoring");
                _logger.Information("Game monitoring resumed by user");
            }

            UpdateTrayState();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to toggle pause state");
        }
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e)
    {
        TogglePause();
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
            if (_orchestrator.IsOptimizing)
            {
                _logger.Information("Deactivating optimizations before exit");
                await _engine.DeactivateProfileAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to deactivate optimizations during exit");
        }

        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _engine.OptimizationApplied -= OnOptimizationApplied;
        _engine.OptimizationReverted -= OnOptimizationReverted;
        _engine.OptimizationFailed -= OnOptimizationFailed;

        _detector.GameStarted -= OnGameStarted;
        _detector.GameStopped -= OnGameStopped;
        _detector.AllGamesStopped -= OnAllGamesStopped;

        if (_dpcMonitor != null)
        {
            _dpcMonitor.DpcSpikeDetected -= OnDpcSpikeForIcon;
            _dpcMonitor.LatencySampled -= OnLatencySampled;
        }

        _taskbarIcon.TrayLeftMouseUp -= OnTrayLeftDoubleClick;

        // Capture the current icon BEFORE TaskbarIcon.Dispose() so we can free its GDI
        // handle. TaskbarIcon.Dispose() does not dispose the assigned Icon.
        var finalIcon = _taskbarIcon.Icon;
        _taskbarIcon.Dispose();
        finalIcon?.Dispose();

        _disposed = true;
        _logger.Information("TrayIconManager disposed");
    }
}
