using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;

namespace GameShift.App.ViewModels;

/// <summary>
/// Manages DPC latency monitoring, sparkline display, spike alerts,
/// and inline troubleshooter results on the dashboard.
/// </summary>
public class DpcMonitoringViewModel : INotifyPropertyChanged
{
    private readonly DpcLatencyMonitor? _dpcMonitor;

    // Sparkline data: max 120 samples = 60 seconds at 500ms intervals
    private readonly System.Collections.Generic.Queue<double> _sparklineSamples = new();
    private const int MaxSparklineSamples = 120;
    private const double SparklineWidth = 120.0;
    private const double SparklineHeight = 32.0;

    private string _dpcLatencyText = "";
    private Brush _dpcLatencyBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private bool _showDpcIndicator;
    private PointCollection _sparklinePoints = new();
    private Brush _sparklineBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private double _averageDpcLatency;
    private bool _showDpcSpikeAlert;
    private string _dpcSpikeAlertMessage = "";
    private bool _showTroubleshooter;
    private bool _isAnalyzing;
    private string _analysisSummary = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DpcLatencyText
    {
        get => _dpcLatencyText;
        private set { _dpcLatencyText = value; OnPropertyChanged(); }
    }

    public Brush DpcLatencyBrush
    {
        get => _dpcLatencyBrush;
        private set { _dpcLatencyBrush = value; OnPropertyChanged(); }
    }

    public bool ShowDpcIndicator
    {
        get => _showDpcIndicator;
        private set { _showDpcIndicator = value; OnPropertyChanged(); }
    }

    public PointCollection SparklinePoints
    {
        get => _sparklinePoints;
        private set { _sparklinePoints = value; OnPropertyChanged(); }
    }

    public Brush SparklineBrush
    {
        get => _sparklineBrush;
        private set { _sparklineBrush = value; OnPropertyChanged(); }
    }

    public double AverageDpcLatency
    {
        get => _averageDpcLatency;
        private set { _averageDpcLatency = value; OnPropertyChanged(); }
    }

    public bool ShowDpcSpikeAlert
    {
        get => _showDpcSpikeAlert;
        private set { _showDpcSpikeAlert = value; OnPropertyChanged(); }
    }

    public string DpcSpikeAlertMessage
    {
        get => _dpcSpikeAlertMessage;
        private set { _dpcSpikeAlertMessage = value; OnPropertyChanged(); }
    }

    public bool ShowTroubleshooter
    {
        get => _showTroubleshooter;
        private set { _showTroubleshooter = value; OnPropertyChanged(); }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set { _isAnalyzing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRescan)); }
    }

    public bool CanRescan => !_isAnalyzing;

    public string AnalysisSummary
    {
        get => _analysisSummary;
        private set { _analysisSummary = value; OnPropertyChanged(); }
    }

    public ObservableCollection<DpcOffenderMatch> TroubleshooterResults { get; } = new();

    public DpcMonitoringViewModel(DpcLatencyMonitor? dpcMonitor)
    {
        _dpcMonitor = dpcMonitor;
    }

    /// <summary>
    /// Updates DPC indicator visibility. Called by parent VM on status refresh.
    /// </summary>
    public void RefreshIndicatorVisibility()
    {
        ShowDpcIndicator = _dpcMonitor?.IsMonitoring == true;
        if (!ShowDpcIndicator)
        {
            DpcLatencyText = "";
        }
    }

    private void OnLatencySampled(object? sender, double latencyUs)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ShowDpcIndicator = _dpcMonitor?.IsMonitoring == true;

            if (!ShowDpcIndicator) return;

            string label;
            Brush brush;
            if (latencyUs < 500)
            {
                label = "Good";
                brush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            }
            else if (latencyUs <= 1000)
            {
                label = "Warning";
                brush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            }
            else
            {
                label = "High";
                brush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            }

            DpcLatencyText = $"{latencyUs:F0}\u00B5s ({label})";
            DpcLatencyBrush = brush;
            SparklineBrush = brush;

            if (_dpcMonitor != null)
                AverageDpcLatency = Math.Round(_dpcMonitor.AverageLatencyMicroseconds, 0);

            _sparklineSamples.Enqueue(latencyUs);
            while (_sparklineSamples.Count > MaxSparklineSamples)
                _sparklineSamples.Dequeue();

            UpdateSparklinePoints();
        });
    }

    private void UpdateSparklinePoints()
    {
        var samples = _sparklineSamples.ToArray();
        if (samples.Length == 0)
        {
            SparklinePoints = new PointCollection();
            return;
        }

        var maxSample = samples.Max();
        if (maxSample < 100.0) maxSample = 100.0;

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

    private void OnDpcSpikeDetected(object? sender, DpcSpikeEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var settings = SettingsManager.Load();
            if (settings.DpcSpikeAlertDismissed) return;

            DpcSpikeAlertMessage = $"DPC spike detected: {e.LatencyMicroseconds:F0}\u00B5s" +
                (string.IsNullOrEmpty(e.DriverName) ? "" : $" \u2014 suspected driver: {e.DriverName}");
            ShowDpcSpikeAlert = true;
        });
    }

    public void DismissDpcSpikeAlert()
    {
        ShowDpcSpikeAlert = false;
        ShowTroubleshooter = false;
        var settings = SettingsManager.Load();
        settings.DpcSpikeAlertDismissed = true;
        SettingsManager.Save(settings);
    }

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

    public void Start()
    {
        if (_dpcMonitor != null)
        {
            _dpcMonitor.LatencySampled += OnLatencySampled;
            _dpcMonitor.DpcSpikeDetected += OnDpcSpikeDetected;
        }
    }

    public void Stop()
    {
        if (_dpcMonitor != null)
        {
            _dpcMonitor.LatencySampled -= OnLatencySampled;
            _dpcMonitor.DpcSpikeDetected -= OnDpcSpikeDetected;
        }
    }

    public void Cleanup()
    {
        if (_dpcMonitor != null)
        {
            _dpcMonitor.LatencySampled -= OnLatencySampled;
            _dpcMonitor.DpcSpikeDetected -= OnDpcSpikeDetected;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
