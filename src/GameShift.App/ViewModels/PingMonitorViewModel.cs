using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;

namespace GameShift.App.ViewModels;

/// <summary>
/// Manages network ping display with sparkline graph.
/// Subscribes to PingMonitor.PingUpdated events.
/// </summary>
public class PingMonitorViewModel : INotifyPropertyChanged
{
    private readonly PingMonitor? _pingMonitor;
    private readonly Queue<long> _pingSparklineSamples = new();

    private string _pingText = "--";
    private Brush _pingBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private PointCollection _pingSparklinePoints = new();
    private string _pingStats = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PingText { get => _pingText; private set { _pingText = value; OnPropertyChanged(); } }
    public Brush PingBrush { get => _pingBrush; private set { _pingBrush = value; OnPropertyChanged(); } }
    public PointCollection PingSparklinePoints { get => _pingSparklinePoints; private set { _pingSparklinePoints = value; OnPropertyChanged(); } }
    public string PingStats { get => _pingStats; private set { _pingStats = value; OnPropertyChanged(); } }

    public PingMonitorViewModel(PingMonitor? pingMonitor)
    {
        _pingMonitor = pingMonitor;
    }

    private void OnPingUpdated(object? sender, PingSample e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (e.Success)
            {
                PingText = $"{e.RttMilliseconds}ms";
                if (e.RttMilliseconds < 50)
                    PingBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                else if (e.RttMilliseconds <= 100)
                    PingBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                else
                    PingBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            }
            else
            {
                PingText = "Timeout";
                PingBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            }

            PingStats = $"Avg: {e.AverageRtt:F0}ms | Jitter: {e.JitterMs:F0}ms | Loss: {e.PacketLossPercent:F0}%";

            _pingSparklineSamples.Enqueue(e.Success ? e.RttMilliseconds : -1);
            while (_pingSparklineSamples.Count > 60) _pingSparklineSamples.Dequeue();
            UpdatePingSparkline();
        });
    }

    private void UpdatePingSparkline()
    {
        var samples = _pingSparklineSamples.Where(s => s >= 0).ToArray();
        if (samples.Length == 0) { PingSparklinePoints = new PointCollection(); return; }

        var points = new PointCollection(samples.Length);
        double max = System.Math.Max(100, samples.Max());

        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples.Length == 1 ? 0 : i * (120.0 / (samples.Length - 1));
            double y = 32.0 - (samples[i] / max) * 32.0;
            points.Add(new System.Windows.Point(x, y));
        }
        PingSparklinePoints = points;
    }

    public void Start()
    {
        if (_pingMonitor != null)
        {
            var settings = SettingsManager.Load();
            _pingMonitor.PingUpdated += OnPingUpdated;
            _pingMonitor.Start(settings.PingTarget);
        }
    }

    public void Stop()
    {
        if (_pingMonitor != null)
            _pingMonitor.PingUpdated -= OnPingUpdated;
    }

    public void Cleanup()
    {
        if (_pingMonitor != null)
            _pingMonitor.PingUpdated -= OnPingUpdated;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
