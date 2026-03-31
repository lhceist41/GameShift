using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GameShift.Core.Config;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Core.SystemTweaks;
using Serilog;

namespace GameShift.App.ViewModels;

/// <summary>
/// Manages the settings view: loads/saves global application preferences.
/// Uses SettingsManager static methods (no constructor injection needed).
/// Each property setter marks IsDirty=true so the Save button enables on any change.
/// </summary>
public class SettingsViewModel : INotifyPropertyChanged
{
    private bool _startWithWindows;
    private bool _startMinimized;
    private bool _showNotifications;
    private bool _enableLogging;
    private string _logLevel = "Information";
    private int _memoryThresholdMB;
    private int _timerResolution100ns;
    // v2 fields
    private string _gpuVendorOverride = "Auto";
    private int _defaultDpcThresholdMicroseconds = 1000;
    private string _vbsHvciStatus = "Unknown";
    private bool _showReEnableButton = false;
    private VbsHvciToggle? _vbsHvciToggle;

    // v2.1 notification preference fields
    private bool _showGameDetectedToast;
    private bool _showSessionSummaryToast;
    private bool _showDpcAlertToast;
    private bool _suppressNotificationsDuringGaming;

    // v2.1 hotkey field
    private string _globalHotkeyBinding = "Ctrl+Shift+G";

    // v2.3 network field
    private string _pingTarget = "8.8.8.8";

    // Game Profiles fields
    private bool _gameProfilesEnabled;

    // Background Mode fields
    private bool _bgEnabled;
    private bool _bgStandbyListEnabled;
    private int _bgStandbyListStandbyThresholdMB;
    private int _bgStandbyListFreeMemoryMinMB;
    private int _bgStandbyListPollIntervalMs;
    private bool _bgStandbyListOnlyDuringGaming;
    private bool _bgTimerEnabled;
    private int _bgTimerResolution100ns;
    private bool _bgPowerPlanEnabled;
    private int _bgIdleTimeoutMinutes;
    private bool _bgTaskDeferralEnabled;
    private bool _bgProBalanceEnabled;
    private bool _bgProcessPriorityEnabled;

    private bool _isDirty;
    private string _statusMessage = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── AppSettings properties ────────────────────────────────────────

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { if (_startWithWindows != value) { _startWithWindows = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set { if (_startMinimized != value) { _startMinimized = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set { if (_showNotifications != value) { _showNotifications = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool ShowGameDetectedToast
    {
        get => _showGameDetectedToast;
        set { if (_showGameDetectedToast != value) { _showGameDetectedToast = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool ShowSessionSummaryToast
    {
        get => _showSessionSummaryToast;
        set { if (_showSessionSummaryToast != value) { _showSessionSummaryToast = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool ShowDpcAlertToast
    {
        get => _showDpcAlertToast;
        set { if (_showDpcAlertToast != value) { _showDpcAlertToast = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool SuppressNotificationsDuringGaming
    {
        get => _suppressNotificationsDuringGaming;
        set { if (_suppressNotificationsDuringGaming != value) { _suppressNotificationsDuringGaming = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public string GlobalHotkeyBinding
    {
        get => _globalHotkeyBinding;
        set { _globalHotkeyBinding = value; OnPropertyChanged(); IsDirty = true; }
    }

    public string PingTarget
    {
        get => _pingTarget;
        set { if (_pingTarget != value) { _pingTarget = value; IsDirty = true; OnPropertyChanged(); } }
    }

    // ── Background Mode properties ────────────────────────────────────
    public bool BgEnabled
    {
        get => _bgEnabled;
        set { if (_bgEnabled != value) { _bgEnabled = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool BgStandbyListEnabled
    {
        get => _bgStandbyListEnabled;
        set { if (_bgStandbyListEnabled != value) { _bgStandbyListEnabled = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int BgStandbyListStandbyThresholdMB
    {
        get => _bgStandbyListStandbyThresholdMB;
        set { if (_bgStandbyListStandbyThresholdMB != value) { _bgStandbyListStandbyThresholdMB = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int BgStandbyListFreeMemoryMinMB
    {
        get => _bgStandbyListFreeMemoryMinMB;
        set { if (_bgStandbyListFreeMemoryMinMB != value) { _bgStandbyListFreeMemoryMinMB = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int BgStandbyListPollIntervalMs
    {
        get => _bgStandbyListPollIntervalMs;
        set { if (_bgStandbyListPollIntervalMs != value) { _bgStandbyListPollIntervalMs = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool BgStandbyListOnlyDuringGaming
    {
        get => _bgStandbyListOnlyDuringGaming;
        set { if (_bgStandbyListOnlyDuringGaming != value) { _bgStandbyListOnlyDuringGaming = value; IsDirty = true; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Read-only hint showing auto-scaled defaults based on detected total RAM.
    /// Shown in the UI next to the threshold fields to guide manual overrides.
    /// </summary>
    public string BgStandbyListAutoHint { get; private set; } = "";

    public bool BgTimerEnabled
    {
        get => _bgTimerEnabled;
        set { if (_bgTimerEnabled != value) { _bgTimerEnabled = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int BgTimerResolution100ns
    {
        get => _bgTimerResolution100ns;
        set { if (_bgTimerResolution100ns != value) { _bgTimerResolution100ns = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool BgPowerPlanEnabled
    {
        get => _bgPowerPlanEnabled;
        set { if (_bgPowerPlanEnabled != value) { _bgPowerPlanEnabled = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int BgIdleTimeoutMinutes
    {
        get => _bgIdleTimeoutMinutes;
        set { if (_bgIdleTimeoutMinutes != value) { _bgIdleTimeoutMinutes = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool BgTaskDeferralEnabled
    {
        get => _bgTaskDeferralEnabled;
        set { if (_bgTaskDeferralEnabled != value) { _bgTaskDeferralEnabled = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool BgProBalanceEnabled
    {
        get => _bgProBalanceEnabled;
        set { if (_bgProBalanceEnabled != value) { _bgProBalanceEnabled = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool BgProcessPriorityEnabled
    {
        get => _bgProcessPriorityEnabled;
        set { if (_bgProcessPriorityEnabled != value) { _bgProcessPriorityEnabled = value; IsDirty = true; OnPropertyChanged(); } }
    }

    // ── Game Profiles properties ────────────────────────────────────
    public bool GameProfilesEnabled
    {
        get => _gameProfilesEnabled;
        set { if (_gameProfilesEnabled != value) { _gameProfilesEnabled = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool EnableLogging
    {
        get => _enableLogging;
        set { if (_enableLogging != value) { _enableLogging = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public string LogLevel
    {
        get => _logLevel;
        set { if (_logLevel != value) { _logLevel = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int MemoryThresholdMB
    {
        get => _memoryThresholdMB;
        set { if (_memoryThresholdMB != value) { _memoryThresholdMB = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int TimerResolution100ns
    {
        get => _timerResolution100ns;
        set { if (_timerResolution100ns != value) { _timerResolution100ns = value; IsDirty = true; OnPropertyChanged(); } }
    }

    // ── v2 GPU / DPC / VBS properties ────────────────────────────────

    public string GpuVendorOverride
    {
        get => _gpuVendorOverride;
        set { if (_gpuVendorOverride != value) { _gpuVendorOverride = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int DefaultDpcThresholdMicroseconds
    {
        get => _defaultDpcThresholdMicroseconds;
        set { if (_defaultDpcThresholdMicroseconds != value) { _defaultDpcThresholdMicroseconds = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public string VbsHvciStatus
    {
        get => _vbsHvciStatus;
        private set { _vbsHvciStatus = value; OnPropertyChanged(); }
    }

    public bool ShowReEnableButton
    {
        get => _showReEnableButton;
        private set { _showReEnableButton = value; OnPropertyChanged(); }
    }

    public List<string> GpuVendorOptions { get; } = new() { "Auto", "NVIDIA", "AMD" };

    // ── UI state ──────────────────────────────────────────────────────

    /// <summary>
    /// True when there are unsaved changes. Enables the Save button.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        set { _isDirty = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Status message shown next to the Save button (e.g. "Settings saved.").
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Available log levels for the ComboBox.
    /// </summary>
    public List<string> LogLevels { get; } = new() { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    /// <summary>
    /// Loads all settings from disk via SettingsManager.Load().
    /// Populates properties and resets IsDirty.
    /// </summary>
    public void LoadSettings()
    {
        var settings = SettingsManager.Load();
        // Sync with actual registry state (registry is source of truth for this setting)
        _startWithWindows = StartupManager.IsRegisteredForStartup();
        _startMinimized = settings.StartMinimized;
        _showNotifications = settings.ShowNotifications;
        _enableLogging = settings.EnableLogging;
        _logLevel = settings.LogLevel;
        _memoryThresholdMB = settings.MemoryThresholdMB;
        _timerResolution100ns = settings.TimerResolution100ns;
        _gpuVendorOverride = settings.GpuVendorOverride;
        _defaultDpcThresholdMicroseconds = settings.DefaultDpcThresholdMicroseconds;
        _showGameDetectedToast = settings.ShowGameDetectedToast;
        _showSessionSummaryToast = settings.ShowSessionSummaryToast;
        _showDpcAlertToast = settings.ShowDpcAlertToast;
        _suppressNotificationsDuringGaming = settings.SuppressNotificationsDuringGaming;
        _globalHotkeyBinding = settings.GlobalHotkeyBinding;
        _pingTarget = settings.PingTarget;

        // Background Mode
        var bg = settings.BackgroundMode ?? new BackgroundModeSettings();
        _bgEnabled = bg.Enabled;
        _bgStandbyListEnabled = bg.StandbyListCleanerEnabled;
        _bgStandbyListStandbyThresholdMB = bg.StandbyListStandbyThresholdMB;
        _bgStandbyListFreeMemoryMinMB = bg.StandbyListFreeMemoryMinMB;
        _bgStandbyListPollIntervalMs = bg.StandbyListPollIntervalMs > 0 ? bg.StandbyListPollIntervalMs : 1000;
        _bgStandbyListOnlyDuringGaming = bg.StandbyListOnlyDuringGaming;

        // Compute auto-scale hint for display
        var (autoStandby, autoFree) = GameShift.Core.BackgroundMode.StandbyListCleaner.ComputeDefaults();
        BgStandbyListAutoHint = $"Auto: Standby={autoStandby} MB, FreeMin={autoFree} MB  (0 = use auto)";

        _bgTimerEnabled = bg.TimerResolutionEnabled;
        _bgTimerResolution100ns = bg.TimerResolution100ns;
        _bgPowerPlanEnabled = bg.PowerPlanEnabled;
        _bgIdleTimeoutMinutes = bg.IdleTimeoutMinutes;
        _bgTaskDeferralEnabled = bg.TaskDeferralEnabled;
        _bgProBalanceEnabled = bg.ProBalanceEnabled;
        _bgProcessPriorityEnabled = bg.ProcessPriorityEnabled;

        // Game Profiles
        var gp = settings.GameProfiles ?? new GameProfileSettings();
        _gameProfilesEnabled = gp.Enabled;

        IsDirty = false;
        StatusMessage = "";

        // Notify all properties changed
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(StartMinimized));
        OnPropertyChanged(nameof(ShowNotifications));
        OnPropertyChanged(nameof(EnableLogging));
        OnPropertyChanged(nameof(LogLevel));
        OnPropertyChanged(nameof(MemoryThresholdMB));
        OnPropertyChanged(nameof(TimerResolution100ns));
        OnPropertyChanged(nameof(GpuVendorOverride));
        OnPropertyChanged(nameof(DefaultDpcThresholdMicroseconds));
        OnPropertyChanged(nameof(ShowGameDetectedToast));
        OnPropertyChanged(nameof(ShowSessionSummaryToast));
        OnPropertyChanged(nameof(ShowDpcAlertToast));
        OnPropertyChanged(nameof(SuppressNotificationsDuringGaming));
        OnPropertyChanged(nameof(GlobalHotkeyBinding));
        OnPropertyChanged(nameof(PingTarget));
        OnPropertyChanged(nameof(BgEnabled));
        OnPropertyChanged(nameof(BgStandbyListEnabled));
        OnPropertyChanged(nameof(BgStandbyListStandbyThresholdMB));
        OnPropertyChanged(nameof(BgStandbyListFreeMemoryMinMB));
        OnPropertyChanged(nameof(BgStandbyListPollIntervalMs));
        OnPropertyChanged(nameof(BgStandbyListOnlyDuringGaming));
        OnPropertyChanged(nameof(BgStandbyListAutoHint));
        OnPropertyChanged(nameof(BgTimerEnabled));
        OnPropertyChanged(nameof(BgTimerResolution100ns));
        OnPropertyChanged(nameof(BgPowerPlanEnabled));
        OnPropertyChanged(nameof(BgIdleTimeoutMinutes));
        OnPropertyChanged(nameof(BgTaskDeferralEnabled));
        OnPropertyChanged(nameof(BgProcessPriorityEnabled));
        OnPropertyChanged(nameof(GameProfilesEnabled));
    }

    /// <summary>
    /// Saves all settings to disk via SettingsManager.Save().
    /// Applies changes immediately (e.g. log level reconfigured).
    /// </summary>
    public void SaveSettings()
    {
        // Load existing settings to preserve fields not shown in UI
        var settings = SettingsManager.Load();

        // Overlay UI-managed fields
        settings.StartWithWindows = StartWithWindows;
        settings.StartMinimized = StartMinimized;
        settings.ShowNotifications = ShowNotifications;
        settings.EnableLogging = EnableLogging;
        settings.LogLevel = LogLevel;
        settings.MemoryThresholdMB = MemoryThresholdMB;
        settings.TimerResolution100ns = TimerResolution100ns;
        settings.GpuVendorOverride = GpuVendorOverride;
        settings.DefaultDpcThresholdMicroseconds = DefaultDpcThresholdMicroseconds;
        settings.ShowGameDetectedToast = ShowGameDetectedToast;
        settings.ShowSessionSummaryToast = ShowSessionSummaryToast;
        settings.ShowDpcAlertToast = ShowDpcAlertToast;
        settings.SuppressNotificationsDuringGaming = SuppressNotificationsDuringGaming;
        settings.GlobalHotkeyBinding = GlobalHotkeyBinding;
        settings.PingTarget = PingTarget;

        // Background Mode
        settings.BackgroundMode ??= new BackgroundModeSettings();
        settings.BackgroundMode.Enabled = BgEnabled;
        settings.BackgroundMode.StandbyListCleanerEnabled = BgStandbyListEnabled;
        settings.BackgroundMode.StandbyListStandbyThresholdMB = BgStandbyListStandbyThresholdMB;
        settings.BackgroundMode.StandbyListFreeMemoryMinMB = BgStandbyListFreeMemoryMinMB;
        settings.BackgroundMode.StandbyListPollIntervalMs = BgStandbyListPollIntervalMs;
        settings.BackgroundMode.StandbyListOnlyDuringGaming = BgStandbyListOnlyDuringGaming;
        settings.BackgroundMode.TimerResolutionEnabled = BgTimerEnabled;
        settings.BackgroundMode.TimerResolution100ns = BgTimerResolution100ns;
        settings.BackgroundMode.PowerPlanEnabled = BgPowerPlanEnabled;
        settings.BackgroundMode.IdleTimeoutMinutes = BgIdleTimeoutMinutes;
        settings.BackgroundMode.TaskDeferralEnabled = BgTaskDeferralEnabled;
        settings.BackgroundMode.ProBalanceEnabled = BgProBalanceEnabled;
        settings.BackgroundMode.ProcessPriorityEnabled = BgProcessPriorityEnabled;

        // Game Profiles
        settings.GameProfiles ??= new GameProfileSettings();
        settings.GameProfiles.Enabled = GameProfilesEnabled;

        SettingsManager.Save(settings);

        // Apply startup registration change to Windows registry
        StartupManager.SetStartWithWindows(settings.StartWithWindows);

        // Apply Background Mode changes live
        App.BackgroundMode?.ApplySettings();

        IsDirty = false;
        StatusMessage = "Settings saved.";
    }

    // ── Export / Import ──────────────────────────────────────────────

    /// <summary>
    /// Exports current settings and all profiles to a .gameshift JSON file.
    /// Opens a SaveFileDialog for the user to choose the destination.
    /// </summary>
    public void ExportSettings()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "GameShift Config (*.gameshift)|*.gameshift",
                DefaultExt = ".gameshift",
                FileName = $"GameShift-{DateTime.Now:yyyy-MM-dd}"
            };

            if (dialog.ShowDialog() != true) return;

            var settings = SettingsManager.Load();
            var profiles = App.ProfileMgr?.GetAllProfiles() ?? Array.Empty<GameProfile>();

            var exportData = new
            {
                version = "2.1",
                exportDate = DateTime.UtcNow.ToString("o"),
                settings = settings,
                profiles = profiles
            };

            var json = System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });

            System.IO.File.WriteAllText(dialog.FileName, json);
            StatusMessage = $"Settings exported to {System.IO.Path.GetFileName(dialog.FileName)}";
            Log.Information("Settings exported to {Path}", dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed — see logs";
            Log.Error(ex, "Failed to export settings");
        }
    }

    /// <summary>
    /// Imports settings and profiles from a .gameshift JSON file.
    /// Opens an OpenFileDialog, validates the file, shows a confirmation MessageBox,
    /// then applies settings via SettingsManager.Save and profiles via ProfileManager.SaveProfile.
    /// </summary>
    public void ImportSettings()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "GameShift Config (*.gameshift)|*.gameshift",
                DefaultExt = ".gameshift"
            };

            if (dialog.ShowDialog() != true) return;

            var json = System.IO.File.ReadAllText(dialog.FileName);

            // Parse and validate structure
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("version", out _) ||
                !root.TryGetProperty("settings", out _) ||
                !root.TryGetProperty("profiles", out _))
            {
                System.Windows.MessageBox.Show(
                    "Invalid GameShift config file. Missing required fields (version, settings, profiles).",
                    "Import Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return;
            }

            var version = root.GetProperty("version").GetString();
            var profileCount = root.GetProperty("profiles").GetArrayLength();

            // Confirm with user
            var result = System.Windows.MessageBox.Show(
                $"Import settings from this file?\n\nVersion: {version}\nProfiles: {profileCount}\n\nThis will replace your current settings and profiles.",
                "Confirm Import",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            // Deserialize and apply settings
            var settingsElement = root.GetProperty("settings");
            var importedSettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(settingsElement.GetRawText());

            if (importedSettings != null)
            {
                SettingsManager.Save(importedSettings);
            }

            // Deserialize and apply profiles
            var profilesElement = root.GetProperty("profiles");
            var importedProfiles = System.Text.Json.JsonSerializer.Deserialize<GameProfile[]>(profilesElement.GetRawText());

            if (importedProfiles != null && App.ProfileMgr != null)
            {
                foreach (var profile in importedProfiles)
                {
                    App.ProfileMgr.SaveProfile(profile);
                }
            }

            // Reload ViewModel to reflect imported settings
            LoadSettings();
            IsDirty = false;

            StatusMessage = $"Settings imported from {System.IO.Path.GetFileName(dialog.FileName)}";
            Log.Information("Settings imported from {Path} ({ProfileCount} profiles)", dialog.FileName, profileCount);
        }
        catch (System.Text.Json.JsonException ex)
        {
            System.Windows.MessageBox.Show(
                $"The selected file is not valid JSON.\n\n{ex.Message}",
                "Import Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Log.Warning(ex, "Failed to parse import file as JSON");
        }
        catch (Exception ex)
        {
            StatusMessage = "Import failed — see logs";
            Log.Error(ex, "Failed to import settings");
        }
    }

    // ── VBS/HVCI management ─────────────────────────────────────────

    /// <summary>
    /// Receives the VbsHvciToggle instance (called from App.xaml.cs OnSettingsRequested).
    /// </summary>
    public void SetVbsHvciToggle(VbsHvciToggle? toggle)
    {
        _vbsHvciToggle = toggle;
        UpdateVbsStatus();
    }

    private void UpdateVbsStatus()
    {
        if (_vbsHvciToggle == null)
        {
            VbsHvciStatus = "Unknown";
            ShowReEnableButton = false;
            return;
        }

        if (_vbsHvciToggle.IsEitherEnabled)
            VbsHvciStatus = "Enabled (5-15% FPS penalty)";
        else
            VbsHvciStatus = "Disabled";

        var settings = SettingsManager.Load();
        ShowReEnableButton = settings.VbsHvciDisabledByGameShift && !_vbsHvciToggle.IsEitherEnabled;
    }

    /// <summary>
    /// Re-enables VBS/HVCI via the toggle instance. Returns true on success.
    /// </summary>
    public bool ReEnableVbsHvci()
    {
        if (_vbsHvciToggle == null) return false;
        var result = _vbsHvciToggle.ReEnableVbsHvci();
        if (result) UpdateVbsStatus();
        return result;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
