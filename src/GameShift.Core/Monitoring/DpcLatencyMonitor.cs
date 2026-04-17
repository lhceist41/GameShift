using System.Diagnostics;
using Serilog;
using Timer = global::System.Timers.Timer;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Event arguments for DPC latency spike detection.
/// DriverName is populated when ETW-based monitoring is active (per-driver attribution).
/// DriverName is null for the PerformanceCounter fallback (aggregate only).
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
/// Passive DPC latency monitor with two backends:
/// 1. ETW (via DpcTraceEngine) — per-driver attribution, microsecond accuracy
/// 2. PerformanceCounter fallback — aggregate % DPC Time, no driver info
///
/// ETW is preferred when available (requires admin, no other kernel session active).
/// Falls back to PerformanceCounter automatically if ETW session creation fails.
///
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
    private volatile bool _isMonitoring;
    private bool _counterAvailable;
    private bool _disposed;
    private readonly ILogger _logger;

    // ETW backend (provides per-driver attribution)
    private DpcTraceEngine? _traceEngine;
    private bool _useEtwBackend;

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

    /// <summary>
    /// Whether per-driver DPC data is available (ETW backend active).
    /// When true, DriverStatistics contains per-driver breakdown.
    /// </summary>
    public bool HasPerDriverData => _useEtwBackend && _traceEngine?.IsCapturing == true;

    /// <summary>
    /// Per-driver DPC statistics when ETW backend is active. Null when using PerformanceCounter fallback.
    /// </summary>
    public IReadOnlyList<DriverDpcStats>? DriverStatistics =>
        HasPerDriverData ? _traceEngine?.GetTopDrivers(20) : null;

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
    /// Attempts to enable ETW-based monitoring for per-driver DPC attribution.
    /// Requires admin privileges and no other kernel trace session active.
    /// Falls back silently to PerformanceCounter if ETW is unavailable.
    /// </summary>
    /// <param name="traceEngine">DpcTraceEngine instance (shared with DPC Doctor page).</param>
    /// <returns>True if ETW backend was activated, false if using PerformanceCounter fallback.</returns>
    public bool EnableEtwBackend(DpcTraceEngine traceEngine)
    {
        try
        {
            _traceEngine = traceEngine;

            if (_traceEngine.IsCapturing || _traceEngine.StartCapture())
            {
                _useEtwBackend = true;

                // Subscribe to ETW driver updates for spike detection with attribution
                _traceEngine.DriversUpdated += OnEtwDriversUpdated;

                _logger.Information("DPC monitor: ETW backend activated (per-driver attribution available)");
                return true;
            }

            _logger.Information("DPC monitor: ETW session unavailable, using PerformanceCounter fallback");
            _traceEngine = null;
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "DPC monitor: Failed to enable ETW backend, using PerformanceCounter fallback");
            _traceEngine = null;
            _useEtwBackend = false;
            return false;
        }
    }

    /// <summary>
    /// Disables the ETW backend and reverts to PerformanceCounter monitoring.
    /// </summary>
    public void DisableEtwBackend()
    {
        if (_traceEngine != null)
        {
            _traceEngine.DriversUpdated -= OnEtwDriversUpdated;

            // Only stop capture if we started it (DPC Doctor may still be using it)
            // Don't stop — let the DPC Doctor page manage the trace engine lifecycle
        }

        _useEtwBackend = false;
        _traceEngine = null;

        _logger.Information("DPC monitor: ETW backend disabled, using PerformanceCounter");
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
        if (_useEtwBackend)
        {
            // When ETW is active, derive aggregate latency from the trace engine's system peak
            if (_traceEngine != null)
            {
                double value = _traceEngine.SystemPeakDpc;
                CurrentLatencyMicroseconds = value;

                lock (_lock)
                {
                    _samples.Enqueue(value);
                    while (_samples.Count > 120)
                        _samples.Dequeue();
                }

                LatencySampled?.Invoke(this, value);
            }
            return;
        }

        // PerformanceCounter fallback
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

            bool shouldAlert = false;
            lock (_lock)
            {
                if (value > _thresholdMicroseconds &&
                    (DateTime.UtcNow - _lastAlertTime).TotalSeconds >= 30)
                {
                    _lastAlertTime = DateTime.UtcNow;
                    shouldAlert = true;
                }
            }
            if (shouldAlert)
                DpcSpikeDetected?.Invoke(this, new DpcSpikeEventArgs(value, null));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error reading DPC latency counter");
        }
    }

    /// <summary>
    /// Called when DpcTraceEngine fires DriversUpdated (1-second intervals).
    /// Checks the top offender for spike detection with driver attribution.
    /// </summary>
    private void OnEtwDriversUpdated(IReadOnlyList<DriverDpcStats> drivers)
    {
        if (drivers.Count == 0 || _thresholdMicroseconds <= 0) return;

        // Check if any driver's highest DPC exceeds the threshold
        var topOffender = drivers[0]; // Already sorted by HighestExecutionMicroseconds
        bool shouldAlert = false;
        lock (_lock)
        {
            if (topOffender.HighestExecutionMicroseconds > _thresholdMicroseconds &&
                (DateTime.UtcNow - _lastAlertTime).TotalSeconds >= 30)
            {
                _lastAlertTime = DateTime.UtcNow;
                shouldAlert = true;
            }
        }
        if (shouldAlert)
        {
            DpcSpikeDetected?.Invoke(this, new DpcSpikeEventArgs(
                topOffender.HighestExecutionMicroseconds,
                topOffender.FriendlyName));
        }
    }

    // -- IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        DisableEtwBackend();

        _timer.Enabled = false;
        _timer.Dispose();
        _counter?.Dispose();

        GC.SuppressFinalize(this);
    }
}
