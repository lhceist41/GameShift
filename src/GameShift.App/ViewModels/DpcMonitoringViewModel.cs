using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Presentation;

namespace GameShift.App.ViewModels;

/// <summary>
/// Home's DPC load tile, the spike notice and the inline troubleshooter results.
/// </summary>
public class DpcMonitoringViewModel : INotifyPropertyChanged
{
    private readonly DpcLatencyMonitor? _dpcMonitor;

    private TileReading _reading = LiveTiles.Dpc(counterReadable: true, monitoring: false, latest: null, average: 0);
    private double? _latest;
    private bool _feedOn;
    private DateTime _lastSampleUtc;
    private bool _stale;
    private bool _runningGameHasDpcOff;
    private bool _showDpcSpikeAlert;
    private string _dpcSpikeAlertMessage = "";
    private bool _showTroubleshooter;
    private bool _isAnalyzing;
    private string _analysisSummary = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The DPC load tile, from <see cref="LiveTiles.Dpc"/>.</summary>
    public TileReading Reading
    {
        get => _reading;
        private set
        {
            if (value == _reading) return;
            _reading = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The tile's line: one sample per reading while Home is seen; 120 half-second samples make a minute like the other tiles.</summary>
    public SparklineBuffer History { get; } = new(120);

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
    /// Re-reads whether the monitor samples. It runs only during a game session, so the tile says
    /// "Not measuring" between sessions instead of keeping the last value, and "No new reading" when
    /// samples stop while it still runs (the counter can start failing mid-session).
    /// </summary>
    /// <param name="runningGameHasDpcOff">Asked only while not measuring: a game runs and its profile has DPC monitoring off.</param>
    public void RefreshState(Func<bool>? runningGameHasDpcOff = null)
    {
        bool monitoring = _dpcMonitor?.IsMonitoring == true;
        var now = DateTime.UtcNow;
        if (!monitoring)
        {
            if (runningGameHasDpcOff != null) _runningGameHasDpcOff = runningGameHasDpcOff();
            _latest = null;
            // Staleness counts from when sampling could have started, not from an earlier session.
            _lastSampleUtc = now;
            if (History.Count > 0) History.Clear();
        }
        else if (_feedOn && _dpcMonitor?.IsCounterAvailable == true && LiveTiles.IsStale(_lastSampleUtc, now))
        {
            _stale = true;
            Reading = LiveTiles.Stale(gameRunning: false);
            return;
        }
        Reading = Read(monitoring);
    }

    private TileReading Read(bool monitoring) => LiveTiles.Dpc(
        counterReadable: _dpcMonitor?.IsCounterAvailable == true,
        monitoring: monitoring,
        latest: _latest,
        average: _latest == null ? 0 : _dpcMonitor?.AverageLatencyMicroseconds ?? 0,
        runningGameHasDpcOff: _runningGameHasDpcOff);

    private void OnLatencySampled(object? sender, double load)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // A sample queued before the feed stopped is dropped, so a hidden Home does no work.
            if (!_feedOn) return;
            if (_stale)
            {
                History.Clear();
                _stale = false;
            }
            _lastSampleUtc = DateTime.UtcNow;
            _latest = load;
            Reading = Read(_dpcMonitor?.IsMonitoring == true);
            History.Push(load);
        });
    }

    private void OnDpcSpikeDetected(object? sender, DpcSpikeEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var settings = SettingsManager.Load();
            if (settings.DpcSpikeAlertDismissed) return;

            DpcSpikeAlertMessage = $"Drivers used {DpcLoad.Format(e.LatencyMicroseconds)} of CPU time for deferred procedure calls (DPC load).";
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

    /// <summary>Listens for spikes, which set the Home notice even while Home is hidden.</summary>
    public void Start()
    {
        if (_dpcMonitor == null) return;
        _dpcMonitor.DpcSpikeDetected -= OnDpcSpikeDetected;
        _dpcMonitor.DpcSpikeDetected += OnDpcSpikeDetected;
    }

    public void Stop()
    {
        StopFeed();
        if (_dpcMonitor != null) _dpcMonitor.DpcSpikeDetected -= OnDpcSpikeDetected;
    }

    /// <summary>
    /// Starts the tile's sample feed with an empty line, so it never joins readings across the time
    /// Home was not seen. The monitor itself is the session's and is left as it is.
    /// </summary>
    public void StartFeed()
    {
        StopFeed();
        _feedOn = true;
        _latest = null;
        _stale = false;
        _lastSampleUtc = DateTime.UtcNow;
        History.Clear();
        RefreshState();
        if (_dpcMonitor != null) _dpcMonitor.LatencySampled += OnLatencySampled;
    }

    public void StopFeed()
    {
        _feedOn = false;
        if (_dpcMonitor != null) _dpcMonitor.LatencySampled -= OnLatencySampled;
    }

    public void Cleanup() => Stop();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
