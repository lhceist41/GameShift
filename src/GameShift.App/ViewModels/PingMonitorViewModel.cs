using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Presentation;

namespace GameShift.App.ViewModels;

/// <summary>
/// Home's ping tile, fed by <see cref="PingMonitor.PingUpdated"/>.
/// The monitor only serves Home: it runs while Home is loaded and is paused during games.
/// </summary>
public class PingMonitorViewModel : INotifyPropertyChanged
{
    private readonly PingMonitor? _pingMonitor;

    private TileReading _reading = LiveTiles.Ping(monitoring: false, target: "", sample: null);
    private bool _feedOn;
    private DateTime _lastSampleUtc;
    private bool _stale;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TileReading Reading
    {
        get => _reading;
        private set { if (value != _reading) { _reading = value; OnPropertyChanged(); } }
    }

    /// <summary>Round-trip times; a ping without a reply is a gap in the line.</summary>
    public SparklineBuffer History { get; } = new();

    public PingMonitorViewModel(PingMonitor? pingMonitor)
    {
        _pingMonitor = pingMonitor;
    }

    private void OnPingUpdated(object? sender, PingSample e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_feedOn || _pingMonitor == null) return;

            if (_stale)
            {
                History.Clear();
                _stale = false;
            }
            _lastSampleUtc = DateTime.UtcNow;

            History.Push(e.Success ? e.RttMilliseconds : double.NaN);
            Reading = LiveTiles.Ping(_pingMonitor.IsMonitoring, _pingMonitor.Target, e, RepliesInLine());
        });
    }

    // The line holds at most the monitor's 60-sample window, so this never counts a reply the
    // monitor's jitter has already dropped.
    private int RepliesInLine() => History.Snapshot().Count(double.IsFinite);

    /// <summary>Called on Home's refresh: a tile with no sample for a while says so instead of keeping its last value.</summary>
    public void RefreshStale(DateTime utcNow, bool gameRunning)
    {
        if (!_feedOn || _pingMonitor?.IsMonitoring != true || !LiveTiles.IsStale(_lastSampleUtc, utcNow)) return;

        _stale = true;
        Reading = LiveTiles.Stale(gameRunning);
    }

    /// <summary>Starts the monitor on the saved target; Home loaded.</summary>
    public void Start()
    {
        _pingMonitor?.Start(SettingsManager.Load().PingTarget);
    }

    /// <summary>Stops the monitor; Home left. Nothing else uses it.</summary>
    public void Stop()
    {
        StopFeed();
        _pingMonitor?.Stop();
    }

    /// <summary>Starts the tile's sample feed with an empty line.</summary>
    public void StartFeed()
    {
        StopFeed();
        _feedOn = true;
        _stale = false;
        _lastSampleUtc = DateTime.UtcNow;
        History.Clear();

        if (_pingMonitor == null)
        {
            Reading = LiveTiles.Ping(monitoring: false, target: "", sample: null);
            return;
        }

        Reading = LiveTiles.Ping(_pingMonitor.IsMonitoring, _pingMonitor.Target, sample: null);
        _pingMonitor.PingUpdated += OnPingUpdated;
    }

    public void StopFeed()
    {
        _feedOn = false;
        if (_pingMonitor != null) _pingMonitor.PingUpdated -= OnPingUpdated;
    }

    public void Cleanup() => StopFeed();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
