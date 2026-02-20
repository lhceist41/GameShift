using System.Diagnostics;
using Serilog;
using Timer = global::System.Timers.Timer;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Event arguments for DPC latency spike detection.
/// DriverName is null for the PerformanceCounter approach (ETW needed for per-driver attribution).
/// </summary>
public class DpcSpikeEventArgs : EventArgs
{
    public double LatencyMicroseconds { get; }
    public string? DriverName { get; }

    public DpcSpikeEventArgs(double latencyMicroseconds, string? driverName)
    {
        LatencyMicroseconds = latencyMicroseconds;
        DriverName = driverName;
    }
}

/// <summary>
/// Passive DPC latency monitor that samples via PerformanceCounter every 500ms.
/// NOT an IOptimization — observes DPC latency without changing system state.
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
    private bool _isMonitoring;
    private bool _counterAvailable;
    private bool _disposed;
    private readonly ILogger _logger;

    // -- Public properties ──────────────────────────────────────────────────

    /// <summary>
    /// Latest DPC latency sample in microseconds.
    /// </summary>
    public double CurrentLatencyMicroseconds { get; private set; }

    /// <summary>
    /// Average DPC latency over the rolling 60-second window (120 samples at 500ms).
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
    /// Peak (maximum) DPC latency over the rolling 60-second window.
    /// Used by DpcTroubleshooter to report worst-case latency.
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
    /// Fired when a DPC latency sample exceeds the configured threshold.
    /// Subject to 30-second cooldown to avoid alert spam.
    /// </summary>
    public event EventHandler<DpcSpikeEventArgs>? DpcSpikeDetected;

    /// <summary>
    /// Fired every 500ms with the current latency value, for UI polling.
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
    /// Start DPC latency monitoring with the specified threshold.
    /// </summary>
    /// <param name="thresholdMicroseconds">Latency threshold in microseconds for spike alerts.</param>
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

        _logger.Information("DPC latency monitor started with threshold {Threshold}us", thresholdMicroseconds);
    }

    /// <summary>
    /// Stop DPC latency monitoring.
    /// </summary>
    public void Stop()
    {
        _timer.Enabled = false;
        _isMonitoring = false;

        lock (_lock)
        {
            _samples.Clear();
        }

        _logger.Information("DPC latency monitor stopped");
    }

    /// <summary>
    /// Look up a fix suggestion for a known problematic DPC driver.
    /// Delegates to KnownDpcOffenders for the expanded database.
    /// </summary>
    /// <param name="driverName">Driver file name (e.g. "nvlddmkm.sys").</param>
    /// <returns>Fix suggestion string, or null if driver not in the known list.</returns>
    public string? GetFixSuggestion(string driverName)
    {
        return KnownDpcOffenders.GetFixSuggestion(driverName);
    }

    // -- Private methods ────────────────────────────────────────────────────

    private void OnTimerElapsed(object? sender, global::System.Timers.ElapsedEventArgs e)
    {
        if (!_counterAvailable || _counter == null)
            return;

        try
        {
            // PerformanceCounter returns % DPC Time.
            // Convert to approximate microseconds: 1% of 1 second = 10,000us
            double dpcPercent = _counter.NextValue();
            double value = dpcPercent * 10000;

            CurrentLatencyMicroseconds = value;

            lock (_lock)
            {
                _samples.Enqueue(value);
                while (_samples.Count > 120)
                    _samples.Dequeue();
            }

            LatencySampled?.Invoke(this, value);

            if (value > _thresholdMicroseconds &&
                (DateTime.UtcNow - _lastAlertTime).TotalSeconds >= 30)
            {
                _lastAlertTime = DateTime.UtcNow;
                DpcSpikeDetected?.Invoke(this, new DpcSpikeEventArgs(value, null));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error reading DPC latency counter");
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
