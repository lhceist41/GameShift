using System.Diagnostics;
using Serilog;
using Timer = global::System.Timers.Timer;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Event arguments for a DPC load spike. The value is in the stored <see cref="DpcLoad"/> scale.
/// </summary>
public class DpcSpikeEventArgs : EventArgs
{
    public double LatencyMicroseconds { get; }

    public DpcSpikeEventArgs(double latencyMicroseconds)
    {
        LatencyMicroseconds = latencyMicroseconds;
    }
}

/// <summary>
/// Passive DPC load monitor: samples the aggregate "% DPC Time" counter every 500ms.
/// Values and thresholds use the stored <see cref="DpcLoad"/> scale (percent x 10000);
/// the "Microseconds" member names are kept from earlier versions and are not a latency.
///
/// NOT an IOptimization - observes DPC load without changing system state.
/// Fires DpcSpikeDetected when a sample exceeds a configurable threshold (with 30s cooldown).
/// </summary>
public class DpcLatencyMonitor : IDisposable
{
    // -- Private fields ─────────────────────────────────────────────────────
    private PerformanceCounter? _counter;
    private Timer _timer;
    private readonly Queue<double> _samples = new();
    private readonly object _lock = new();
    private int _thresholdMicroseconds;
    private DateTime _lastAlertTime;
    private volatile bool _isMonitoring;
    private bool _counterAvailable;
    private bool _disposed;
    private readonly ILogger _logger;

    // -- Public properties ──────────────────────────────────────────────────

    /// <summary>
    /// Latest DPC load sample (stored scale).
    /// </summary>
    public double CurrentLatencyMicroseconds { get; private set; }

    /// <summary>
    /// Average DPC load over the rolling 60-second window (120 samples at 500ms).
    /// </summary>
    public double AverageLatencyMicroseconds
    {
        get
        {
            lock (_lock)
            {
                return _samples.Count > 0 ? _samples.Average() : 0;
            }
        }
    }

    /// <summary>
    /// Peak (maximum) DPC load over the rolling 60-second window.
    /// </summary>
    public double PeakLatencyMicroseconds
    {
        get
        {
            lock (_lock)
            {
                return _samples.Count > 0 ? _samples.Max() : 0;
            }
        }
    }

    /// <summary>
    /// Whether the monitor is currently sampling.
    /// </summary>
    public bool IsMonitoring => _isMonitoring;

    // -- Events ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when a DPC load sample exceeds the configured threshold.
    /// Subject to 30-second cooldown to avoid alert spam.
    /// </summary>
    public event EventHandler<DpcSpikeEventArgs>? DpcSpikeDetected;

    /// <summary>
    /// Fired every 500ms with the current DPC load (stored scale), for UI polling.
    /// </summary>
    public event EventHandler<double>? LatencySampled;

    // -- Constructor ────────────────────────────────────────────────────────

    public DpcLatencyMonitor()
    {
        _logger = Config.SettingsManager.Logger;

        try
        {
            _counter = new PerformanceCounter("Processor", "% DPC Time", "_Total");
            _counterAvailable = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PerformanceCounter 'Processor/% DPC Time' not available on this system. DPC monitoring disabled.");
            _counterAvailable = false;
        }

        _timer = new Timer(500);
        _timer.Elapsed += OnTimerElapsed;
        _timer.AutoReset = true;
    }

    // -- Public methods ─────────────────────────────────────────────────────

    /// <summary>
    /// Start DPC load monitoring with the specified threshold.
    /// </summary>
    /// <param name="thresholdMicroseconds">Spike threshold in the stored <see cref="DpcLoad"/> scale.</param>
    public void Start(int thresholdMicroseconds)
    {
        _thresholdMicroseconds = thresholdMicroseconds;
        _lastAlertTime = DateTime.MinValue;

        lock (_lock)
        {
            _samples.Clear();
        }

        CurrentLatencyMicroseconds = 0;
        _isMonitoring = true;
        _timer.Enabled = true;

        _logger.Information("DPC load monitor started with threshold {Threshold}", DpcLoad.Format(thresholdMicroseconds));
    }

    /// <summary>
    /// Stop DPC load monitoring.
    /// </summary>
    public void Stop()
    {
        _timer.Enabled = false;
        _isMonitoring = false;

        lock (_lock)
        {
            _samples.Clear();
        }

        _logger.Information("DPC load monitor stopped");
    }

    // -- Private methods ────────────────────────────────────────────────────

    private void OnTimerElapsed(object? sender, global::System.Timers.ElapsedEventArgs e)
    {
        if (!_counterAvailable || _counter == null)
            return;

        try
        {
            double value = DpcLoad.FromCounterPercent(_counter.NextValue());

            CurrentLatencyMicroseconds = value;

            lock (_lock)
            {
                _samples.Enqueue(value);
                while (_samples.Count > 120)
                    _samples.Dequeue();
            }

            LatencySampled?.Invoke(this, value);

            bool shouldAlert = false;
            lock (_lock)
            {
                if (DpcLoad.ExceedsThreshold(value, _thresholdMicroseconds) &&
                    (DateTime.UtcNow - _lastAlertTime).TotalSeconds >= 30)
                {
                    _lastAlertTime = DateTime.UtcNow;
                    shouldAlert = true;
                }
            }
            if (shouldAlert)
                DpcSpikeDetected?.Invoke(this, new DpcSpikeEventArgs(value));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error reading DPC load counter");
        }
    }

    // -- IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _timer.Enabled = false;
        _timer.Dispose();
        _counter?.Dispose();

        GC.SuppressFinalize(this);
    }
}
