using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using GameShift.Core.Monitoring;
using GameShift.Core.System;

namespace GameShift.App.ViewModels;

/// <summary>
/// ViewModel for the System Overview page.
/// Gathers hardware info, Windows features, and top processes via SystemInfoGatherer.
/// Temperature and Startup sections are populated in Phase F.
/// </summary>
public class SystemViewModel : INotifyPropertyChanged
{
    // ── OS ─────────────────────────────────────────────────────────────────
    private string _osCaption = "Loading...";
    public string OsCaption { get => _osCaption; set { _osCaption = value; OnPropertyChanged(); } }

    private string _osVersion = "";
    public string OsVersion { get => _osVersion; set { _osVersion = value; OnPropertyChanged(); } }

    // ── CPU ────────────────────────────────────────────────────────────────
    private string _cpuName = "Loading...";
    public string CpuName { get => _cpuName; set { _cpuName = value; OnPropertyChanged(); } }

    private string _cpuDetails = "";
    public string CpuDetails { get => _cpuDetails; set { _cpuDetails = value; OnPropertyChanged(); } }

    // ── GPU ────────────────────────────────────────────────────────────────
    private string _gpuName = "Loading...";
    public string GpuName { get => _gpuName; set { _gpuName = value; OnPropertyChanged(); } }

    private string _gpuDetails = "";
    public string GpuDetails { get => _gpuDetails; set { _gpuDetails = value; OnPropertyChanged(); } }

    // ── RAM ────────────────────────────────────────────────────────────────
    private string _ramTotal = "Loading...";
    public string RamTotal { get => _ramTotal; set { _ramTotal = value; OnPropertyChanged(); } }

    private string _ramDetails = "";
    public string RamDetails { get => _ramDetails; set { _ramDetails = value; OnPropertyChanged(); } }

    // ── Storage ────────────────────────────────────────────────────────────
    private string _storageSummary = "Loading...";
    public string StorageSummary { get => _storageSummary; set { _storageSummary = value; OnPropertyChanged(); } }

    private string _storageDetails = "";
    public string StorageDetails { get => _storageDetails; set { _storageDetails = value; OnPropertyChanged(); } }

    // ── Display ────────────────────────────────────────────────────────────
    private string _displayResolution = "Loading...";
    public string DisplayResolution { get => _displayResolution; set { _displayResolution = value; OnPropertyChanged(); } }

    private string _displayDetails = "";
    public string DisplayDetails { get => _displayDetails; set { _displayDetails = value; OnPropertyChanged(); } }

    // ── Windows Features ───────────────────────────────────────────────────
    private bool _gameModeEnabled;
    public bool GameModeEnabled { get => _gameModeEnabled; set { _gameModeEnabled = value; OnPropertyChanged(); } }

    private bool _gameBarEnabled;
    public bool GameBarEnabled { get => _gameBarEnabled; set { _gameBarEnabled = value; OnPropertyChanged(); } }

    // ── Top Processes ──────────────────────────────────────────────────────
    public ObservableCollection<SystemInfoGatherer.ProcessInfo> TopProcesses { get; } = new();

    // ── Temperature placeholders (Phase F) ─────────────────────────────────
    private string _cpuTemp = "N/A";
    public string CpuTemp { get => _cpuTemp; set { _cpuTemp = value; OnPropertyChanged(); } }

    private string _gpuTemp = "N/A";
    public string GpuTemp { get => _gpuTemp; set { _gpuTemp = value; OnPropertyChanged(); } }

    private bool _tempAvailable;
    public bool TempAvailable { get => _tempAvailable; set { _tempAvailable = value; OnPropertyChanged(); } }

    private Brush _cpuTempBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    public Brush CpuTempBrush { get => _cpuTempBrush; set { _cpuTempBrush = value; OnPropertyChanged(); } }

    private Brush _gpuTempBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    public Brush GpuTempBrush { get => _gpuTempBrush; set { _gpuTempBrush = value; OnPropertyChanged(); } }

    // ── Startup Apps placeholders (Phase F) ────────────────────────────────
    public ObservableCollection<StartupAppItem> StartupApps { get; } = new();

    private bool _isRefreshing;
    public bool IsRefreshing { get => _isRefreshing; set { _isRefreshing = value; OnPropertyChanged(); } }

    private readonly TemperatureMonitor? _tempMonitor;

    // ── Constructor ────────────────────────────────────────────────────────

    public SystemViewModel()
    {
        _ = RefreshAsync();

        _tempMonitor = App.TempMon;
        if (_tempMonitor != null && _tempMonitor.IsAvailable)
        {
            TempAvailable = true;
            _tempMonitor.TemperatureUpdated += OnTemperatureUpdated;
            _tempMonitor.Start();
        }
    }

    // ── Temperature Event Handler ───────────────────────────────────────────

    private void OnTemperatureUpdated(object? sender, TemperatureSample e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CpuTemp = e.CpuTempCelsius > 0 ? $"{e.CpuTempCelsius:F0}\u00B0C" : "N/A";
            GpuTemp = e.GpuTempCelsius > 0 ? $"{e.GpuTempCelsius:F0}\u00B0C" : "N/A";

            // Update brushes based on thresholds
            CpuTempBrush = GetTempBrush(e.CpuTempCelsius);
            GpuTempBrush = GetTempBrush(e.GpuTempCelsius);
        });
    }

    private static Brush GetTempBrush(float temp)
    {
        if (temp <= 0) return new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        if (temp < 60) return new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); // green
        if (temp < 80) return new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // yellow
        return new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // red
    }

    // ── Public Methods ─────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        IsRefreshing = true;

        await Task.Run(() =>
        {
            // OS
            var os = SystemInfoGatherer.GetOsInfo();
            OsCaption = os.Caption;
            OsVersion = $"Version {os.Version} (Build {os.BuildNumber})";

            // CPU
            var cpu = SystemInfoGatherer.GetCpuInfo();
            CpuName = cpu.Name;
            CpuDetails = $"{cpu.Cores} cores / {cpu.LogicalProcessors} threads @ {cpu.MaxClockSpeedMHz} MHz";

            // GPU
            var gpu = SystemInfoGatherer.GetGpuInfo();
            GpuName = gpu.Name;
            var vramGB = gpu.AdapterRamBytes / (1024.0 * 1024 * 1024);
            GpuDetails = vramGB > 0
                ? $"Driver {gpu.DriverVersion} | {vramGB:F1} GB VRAM"
                : $"Driver {gpu.DriverVersion}";

            // RAM
            var ram = SystemInfoGatherer.GetRamInfo();
            var totalGB = ram.TotalBytes / (1024.0 * 1024 * 1024);
            RamTotal = $"{totalGB:F1} GB";
            RamDetails = ram.SpeedMHz > 0
                ? $"{ram.ModuleCount} module(s) @ {ram.SpeedMHz} MHz"
                : $"{ram.ModuleCount} module(s)";

            // Storage
            var drives = SystemInfoGatherer.GetStorageDrives();
            if (drives.Count > 0)
            {
                var d = drives[0];
                var sizeGB = d.SizeBytes / (1024.0 * 1024 * 1024);
                StorageSummary = $"{d.Model}";
                var freeGB = d.FreeSpaceBytes / (1024.0 * 1024 * 1024);
                StorageDetails = freeGB > 0
                    ? $"{sizeGB:F0} GB total | {freeGB:F0} GB free"
                    : $"{sizeGB:F0} GB | {d.MediaType}";
            }
            else
            {
                StorageSummary = "Unavailable";
                StorageDetails = "";
            }

            // Display
            var display = SystemInfoGatherer.GetDisplayInfo();
            if (display.Width > 0)
            {
                DisplayResolution = $"{display.Width} x {display.Height}";
                DisplayDetails = $"{display.RefreshRate} Hz refresh rate";
            }
            else
            {
                DisplayResolution = "Unavailable";
                DisplayDetails = "";
            }

            // Windows Features
            GameModeEnabled = SystemInfoGatherer.IsGameModeEnabled();
            GameBarEnabled = SystemInfoGatherer.IsGameBarEnabled();

            // Top Processes
            var processes = SystemInfoGatherer.GetTopProcessesByCpu(5);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                TopProcesses.Clear();
                foreach (var p in processes)
                    TopProcesses.Add(p);
            });

            // Startup Apps
            var startupApps = StartupAppManager.GetStartupApps();
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                StartupApps.Clear();
                foreach (var app in startupApps)
                {
                    StartupApps.Add(new StartupAppItem
                    {
                        Name = app.Name,
                        Publisher = app.Source,
                        Source = app.Source,
                        IsRecommendedDisable = app.IsRecommendedDisable,
                        IsEnabled = app.IsEnabled,
                        AppInfo = app
                    });
                }
            });
        });

        IsRefreshing = false;
    }

    public void ToggleStartupApp(StartupAppItem item)
    {
        if (item.AppInfo == null) return;
        StartupAppManager.ToggleStartupApp(item.AppInfo, item.IsEnabled);
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Model for startup app items displayed in the System Overview page.
/// </summary>
public class StartupAppItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsRecommendedDisable { get; set; }
    public StartupAppInfo? AppInfo { get; set; }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
