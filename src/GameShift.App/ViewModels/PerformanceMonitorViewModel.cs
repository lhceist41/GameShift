using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using GameShift.Core.Monitoring;

namespace GameShift.App.ViewModels;

/// <summary>
/// Manages CPU, RAM, and GPU utilization display with sparkline graphs.
/// Subscribes to SystemPerformanceMonitor.SampleUpdated events.
/// </summary>
public class PerformanceMonitorViewModel : INotifyPropertyChanged
{
    private readonly SystemPerformanceMonitor? _perfMonitor;
    private readonly Queue<double> _cpuSparklineSamples = new();
    private readonly Queue<double> _ramSparklineSamples = new();
    private readonly Queue<double> _gpuSparklineSamples = new();

    private string _cpuText = "0%";
    private string _ramText = "0%";
    private string _gpuUtilText = "N/A";
    private PointCollection _cpuSparklinePoints = new();
    private PointCollection _ramSparklinePoints = new();
    private PointCollection _gpuSparklinePoints = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CpuText { get => _cpuText; private set { _cpuText = value; OnPropertyChanged(); } }
    public string RamText { get => _ramText; private set { _ramText = value; OnPropertyChanged(); } }
    public string GpuUtilText { get => _gpuUtilText; private set { _gpuUtilText = value; OnPropertyChanged(); } }

    public PointCollection CpuSparklinePoints { get => _cpuSparklinePoints; private set { _cpuSparklinePoints = value; OnPropertyChanged(); } }
    public PointCollection RamSparklinePoints { get => _ramSparklinePoints; private set { _ramSparklinePoints = value; OnPropertyChanged(); } }
    public PointCollection GpuSparklinePoints { get => _gpuSparklinePoints; private set { _gpuSparklinePoints = value; OnPropertyChanged(); } }

    public PerformanceMonitorViewModel(SystemPerformanceMonitor? perfMonitor)
    {
        _perfMonitor = perfMonitor;
    }

    private void OnPerformanceSampled(object? sender, PerformanceSample e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CpuText = $"{e.CpuPercent:F0}%";
            RamText = $"{e.RamPercent:F0}%";
            GpuUtilText = e.GpuPercent >= 0 ? $"{e.GpuPercent:F0}%" : "N/A";

            EnqueueAndUpdateSparkline(_cpuSparklineSamples, e.CpuPercent, 100, v => CpuSparklinePoints = v);
            EnqueueAndUpdateSparkline(_ramSparklineSamples, e.RamPercent, 100, v => RamSparklinePoints = v);
            if (e.GpuPercent >= 0)
                EnqueueAndUpdateSparkline(_gpuSparklineSamples, e.GpuPercent, 100, v => GpuSparklinePoints = v);
        });
    }

    private void EnqueueAndUpdateSparkline(Queue<double> queue, double value, double maxValue, Action<PointCollection> setter)
    {
        queue.Enqueue(value);
        while (queue.Count > 60) queue.Dequeue();

        var samples = queue.ToArray();
        var points = new PointCollection(samples.Length);
        double max = Math.Max(maxValue, samples.Max());
        if (max < 1) max = 1;

        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples.Length == 1 ? 0 : i * (120.0 / (samples.Length - 1));
            double y = 48.0 - (samples[i] / max) * 48.0;
            points.Add(new System.Windows.Point(x, y));
        }
        setter(points);
    }

    public void Start()
    {
        if (_perfMonitor != null)
        {
            _perfMonitor.SampleUpdated += OnPerformanceSampled;
            _perfMonitor.Start();
        }
    }

    public void Stop()
    {
        if (_perfMonitor != null)
        {
            _perfMonitor.SampleUpdated -= OnPerformanceSampled;
            _perfMonitor.Stop();
        }
    }

    public void Cleanup()
    {
        if (_perfMonitor != null)
            _perfMonitor.SampleUpdated -= OnPerformanceSampled;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
