using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using GameShift.Core.Detection;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;

namespace GameShift.App.ViewModels;

/// <summary>
/// ViewModel for the tray flyout panel. Provides live status data
/// including optimization state, DPC latency, and active game info.
/// Refreshes on a 1-second DispatcherTimer for live updates.
/// </summary>
public class TrayFlyoutViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DetectionOrchestrator _orchestrator;
    private readonly OptimizationEngine _engine;
    private readonly DpcLatencyMonitor? _dpcMonitor;
    private readonly bool _isPaused;
    private readonly DispatcherTimer _refreshTimer;

    private string _statusText = "Idle";
    private SolidColorBrush _statusColor = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)); // GS.Status.Disabled
    private string _dpcText = "-- \u00B5s";
    private string _optimizationCount = "0 active";
    private string _sessionInfo = "No active session";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public SolidColorBrush StatusColor
    {
        get => _statusColor;
        private set { _statusColor = value; OnPropertyChanged(); }
    }

    public string DpcText
    {
        get => _dpcText;
        private set { _dpcText = value; OnPropertyChanged(); }
    }

    public string OptimizationCount
    {
        get => _optimizationCount;
        private set { _optimizationCount = value; OnPropertyChanged(); }
    }

    public string SessionInfo
    {
        get => _sessionInfo;
        private set { _sessionInfo = value; OnPropertyChanged(); }
    }

    public TrayFlyoutViewModel(
        DetectionOrchestrator orchestrator,
        OptimizationEngine engine,
        DpcLatencyMonitor? dpcMonitor,
        bool isPaused = false)
    {
        _orchestrator = orchestrator;
        _engine = engine;
        _dpcMonitor = dpcMonitor;
        _isPaused = isPaused;

        // Refresh every 1 second for live DPC and status updates
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        // Initial refresh
        Refresh();
    }

    /// <summary>
    /// Pulls latest data from core services and updates all properties.
    /// </summary>
    public void Refresh()
    {
        // Status — paused state takes priority over normal status display
        if (_isPaused)
        {
            StatusText = "Paused";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // GS.Status.Warning amber (#FBBF24)
        }
        else if (_orchestrator.IsOptimizing)
        {
            StatusText = "Optimizing";
            StatusColor = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); // GS.Status.Active
        }
        else
        {
            StatusText = "Idle";
            StatusColor = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)); // GS.Status.Disabled
        }

        // DPC latency
        if (_dpcMonitor != null && _dpcMonitor.IsMonitoring)
        {
            DpcText = $"{_dpcMonitor.CurrentLatencyMicroseconds:F0} \u00B5s";
        }
        else
        {
            DpcText = "-- \u00B5s";
        }

        // Optimization count — count available (IsAvailable) optimizations when active
        var optimizations = App.Optimizations;
        if (_orchestrator.IsOptimizing && optimizations != null)
        {
            int availableCount = optimizations.Count(opt => opt.IsAvailable);
            OptimizationCount = $"{availableCount} active";
        }
        else
        {
            OptimizationCount = "0 active";
        }

        // Session info
        var activeGames = _orchestrator.GetActiveGames();
        if (activeGames.Count > 0)
        {
            var firstGame = activeGames.Values.First();
            if (activeGames.Count == 1)
                SessionInfo = firstGame.GameName;
            else
                SessionInfo = $"{firstGame.GameName} +{activeGames.Count - 1} more";
        }
        else
        {
            SessionInfo = "No active session";
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
