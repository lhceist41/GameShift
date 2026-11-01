using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading.Tasks;
using GameShift.Core.Presentation;
using GameShift.Core.System;

namespace GameShift.App.ViewModels;

/// <summary>
/// ViewModel for the System Overview page.
/// Gathers hardware info, Windows features, and top processes via SystemInfoGatherer.
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
    public ObservableCollection<ProcessItem> TopProcesses { get; } = new();

    // ── Startup Apps ───────────────────────────────────────────────────────
    public ObservableCollection<StartupAppItem> StartupApps { get; } = new();

    private bool _isRefreshing;
    public bool IsRefreshing { get => _isRefreshing; set { _isRefreshing = value; OnPropertyChanged(); } }

    // ── Constructor ────────────────────────────────────────────────────────

    public SystemViewModel()
    {
        _ = RefreshAsync();
    }

    // ── Public Methods ─────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        // Prevent overlapping refreshes (the constructor and the Refresh button both call this).
        if (IsRefreshing) return;
        IsRefreshing = true;

        try
        {
            await Task.Run(() =>
        {
            // OS
            var os = SystemInfoGatherer.GetOsInfo();
            OsCaption = os.Caption;
            OsVersion = SystemText.OsVersion(os);

            // CPU
            var cpu = SystemInfoGatherer.GetCpuInfo();
            CpuName = cpu.Name;
            CpuDetails = SystemText.CpuDetails(cpu);

            // GPU
            var gpu = SystemInfoGatherer.GetGpuInfo();
            GpuName = gpu.Name;
            GpuDetails = SystemText.GpuDetails(gpu);

            // RAM
            var ram = SystemInfoGatherer.GetRamInfo();
            RamTotal = SystemText.RamTotal(ram);
            RamDetails = SystemText.RamDetails(ram);

            // Storage: the first drive Windows lists
            var drive = SystemInfoGatherer.GetStorageDrives().FirstOrDefault();
            if (drive != null)
            {
                StorageSummary = drive.Model;
                StorageDetails = SystemText.StorageDetails(drive);
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
                DisplayResolution = SystemText.DisplayResolution(display);
                DisplayDetails = SystemText.DisplayDetails(display);
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
                    TopProcesses.Add(new ProcessItem { Name = p.Name, Details = SystemText.ProcessDetails(p) });
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
                        Source = app.Source
                    });
                }
            });
        });
        }
        catch (Exception ex)
        {
            GameShift.Core.Config.SettingsManager.Logger.Warning(ex, "[SystemViewModel] System info refresh failed");
        }
        finally
        {
            // Always clear the busy flag so the System page can't get stuck on "Loading...".
            IsRefreshing = false;
        }
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Model for startup app items displayed in the System Overview page.
/// </summary>
public class StartupAppItem
{
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>
/// A process in the System Overview page's top processes list.
/// </summary>
public class ProcessItem
{
    public string Name { get; set; } = "";
    public string Details { get; set; } = "";
}
