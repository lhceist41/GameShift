using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using GameShift.Core.Monitoring;
using GameShift.Core.Presentation;

namespace GameShift.App.ViewModels;

/// <summary>
/// Home's CPU / GPU and memory tiles, fed by <see cref="SystemPerformanceMonitor.SampleUpdated"/>.
/// The monitor only serves Home: it runs while Home is loaded and is paused during games.
/// </summary>
public class PerformanceMonitorViewModel : INotifyPropertyChanged
{
    private readonly SystemPerformanceMonitor? _perfMonitor;

    private TileReading _cpuGpu = LiveTiles.Measuring("CPU / GPU");
    private TileReading _memory = LiveTiles.Measuring("in use");
    private bool _hasGpu = true;
    private bool _feedOn;
    private DateTime _lastSampleUtc;
    private bool _stale;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TileReading CpuGpu
    {
        get => _cpuGpu;
        private set { if (value != _cpuGpu) { _cpuGpu = value; OnPropertyChanged(); } }
    }

    public TileReading Memory
    {
        get => _memory;
        private set { if (value != _memory) { _memory = value; OnPropertyChanged(); } }
    }

    /// <summary>False once a sample shows the GPU counters are missing; the GPU line is then hidden.</summary>
    public bool HasGpu
    {
        get => _hasGpu;
        private set { if (value != _hasGpu) { _hasGpu = value; OnPropertyChanged(); } }
    }

    public SparklineBuffer CpuHistory { get; } = new();
    public SparklineBuffer GpuHistory { get; } = new();
    public SparklineBuffer MemoryHistory { get; } = new();

    public PerformanceMonitorViewModel(SystemPerformanceMonitor? perfMonitor)
    {
        _perfMonitor = perfMonitor;
    }

    private void OnPerformanceSampled(object? sender, PerformanceSample e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_feedOn) return;

            // The monitor reports 0 for a counter it cannot read; those are not shown as 0.
            double? cpu = _perfMonitor?.IsCpuAvailable == true ? e.CpuPercent : null;
            double? ram = _perfMonitor?.IsRamAvailable == true ? e.RamPercent : null;
            double? gpu = e.GpuPercent >= 0 ? e.GpuPercent : null;

            if (_stale)
            {
                ClearHistory();
                _stale = false;
            }
            _lastSampleUtc = DateTime.UtcNow;

            CpuGpu = LiveTiles.CpuGpu(cpu, gpu);
            Memory = LiveTiles.Memory(ram, _perfMonitor?.TotalRamMB ?? 0);
            HasGpu = gpu != null;

            CpuHistory.Push(cpu ?? double.NaN);
            GpuHistory.Push(gpu ?? double.NaN);
            MemoryHistory.Push(ram ?? double.NaN);
        });
    }

    /// <summary>Called on Home's refresh: a tile with no sample for a while says so instead of keeping its last value.</summary>
    public void RefreshStale(DateTime utcNow, bool gameRunning)
    {
        if (!_feedOn || _perfMonitor == null || !LiveTiles.IsStale(_lastSampleUtc, utcNow)) return;

        _stale = true;
        CpuGpu = LiveTiles.Stale(gameRunning);
        Memory = LiveTiles.Stale(gameRunning);
    }

    /// <summary>Starts the monitor; Home loaded.</summary>
    public void Start()
    {
        _perfMonitor?.Start();
    }

    /// <summary>Stops the monitor; Home left. Nothing else uses it.</summary>
    public void Stop()
    {
        StopFeed();
        _perfMonitor?.Stop();
    }

    /// <summary>Starts the tiles' sample feed with empty lines.</summary>
    public void StartFeed()
    {
        StopFeed();
        _feedOn = true;
        _stale = false;
        _lastSampleUtc = DateTime.UtcNow;
        ClearHistory();

        if (_perfMonitor == null)
        {
            CpuGpu = LiveTiles.CpuGpu(null, null);
            Memory = LiveTiles.Memory(null, 0);
            HasGpu = false;
            return;
        }

        CpuGpu = LiveTiles.Measuring("CPU / GPU");
        Memory = _perfMonitor.IsRamAvailable ? LiveTiles.Measuring("in use") : LiveTiles.Memory(null, 0);
        _perfMonitor.SampleUpdated += OnPerformanceSampled;
    }

    public void StopFeed()
    {
        _feedOn = false;
        if (_perfMonitor != null) _perfMonitor.SampleUpdated -= OnPerformanceSampled;
    }

    public void Cleanup() => StopFeed();

    private void ClearHistory()
    {
        CpuHistory.Clear();
        GpuHistory.Clear();
        MemoryHistory.Clear();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
