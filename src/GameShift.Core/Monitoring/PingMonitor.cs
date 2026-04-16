using System.Net.NetworkInformation;
using Serilog;
using Timer = global::System.Timers.Timer;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Event data for a network ping sample.
/// </summary>
public class PingSample
{
    /// <summary>Round-trip time in milliseconds. -1 if request timed out.</summary>
    public long RttMilliseconds { get; init; }

    /// <summary>Average RTT over the rolling 60-second window.</summary>
    public double AverageRtt { get; init; }

    /// <summary>Jitter (mean absolute deviation from average) in milliseconds.</summary>
    public double JitterMs { get; init; }

    /// <summary>Packet loss percentage (0-100) over the rolling window.</summary>
    public double PacketLossPercent { get; init; }

    /// <summary>Whether the ping was successful.</summary>
    public bool Success { get; init; }
}

/// <summary>
/// Network latency monitor that pings a configurable target every 1 second.
/// Tracks RTT, average, jitter (mean absolute deviation), and packet loss %.
/// Follows the DpcLatencyMonitor pattern: Timer-based, rolling window, IDisposable, events.
/// Color thresholds: green &lt; 50ms, yellow 50-100ms, red &gt; 100ms.
/// </summary>
public class PingMonitor : IDisposable
{
    // -- Private fields ─────────────────────────────────────────────────────
    private Timer _timer;
    private Ping? _ping;
    private string _target = "8.8.8.8";
    private readonly Queue<long> _rttSamples = new();       // -1 for timeout/failure
    private readonly object _lock = new();
    private bool _isMonitoring;
    private bool _disposed;
    private volatile bool _stopping;
    private readonly ILogger _logger;
    private int _totalSent;
    private int _totalLost;

    // -- Public properties ──────────────────────────────────────────────────

    /// <summary>Latest RTT in milliseconds. -1 if last ping failed.</summary>
    public long CurrentRttMs { get; private set; } = -1;

    /// <summary>Average RTT over the rolling 60-second window (successful pings only).</summary>
    public double AverageRttMs
    {
        get
        {
            lock (_lock)
            {
                var successful = _rttSamples.Where(r => r >= 0).ToArray();
                return successful.Length > 0 ? successful.Average() : 0;
            }
        }
    }

    /// <summary>Jitter: mean absolute deviation from average (successful pings only).</summary>
    public double JitterMs
    {
        get
        {
            lock (_lock)
            {
                var successful = _rttSamples.Where(r => r >= 0).ToArray();
                if (successful.Length < 2) return 0;

                double avg = successful.Average();
                return successful.Select(r => Math.Abs(r - avg)).Average();
            }
        }
    }

    /// <summary>Packet loss percentage over the rolling window.</summary>
    public double PacketLossPercent
    {
        get
        {
            lock (_lock)
            {
                if (_rttSamples.Count == 0) return 0;
                int lost = _rttSamples.Count(r => r < 0);
                return (double)lost / _rttSamples.Count * 100;
            }
        }
    }

    /// <summary>Whether the monitor is currently pinging.</summary>
    public bool IsMonitoring => _isMonitoring;

    /// <summary>Current ping target address.</summary>
    public string Target => _target;

    // -- Events ─────────────────────────────────────────────────────────────

    /// <summary>Fired every 1 second with the latest ping sample data.</summary>
    public event EventHandler<PingSample>? PingUpdated;

    // -- Constructor ────────────────────────────────────────────────────────

    public PingMonitor()
    {
        _logger = Config.SettingsManager.Logger;

        _timer = new Timer(1000);
        _timer.Elapsed += OnTimerElapsed;
        _timer.AutoReset = true;
    }

    // -- Public methods ─────────────────────────────────────────────────────

    /// <summary>Start pinging the specified target every 1 second.</summary>
    /// <param name="target">IP address or hostname to ping (default: 8.8.8.8).</param>
    public void Start(string target = "8.8.8.8")
    {
        _target = target;
        _totalSent = 0;
        _totalLost = 0;

        lock (_lock)
        {
            _rttSamples.Clear();
        }

        _stopping = false;
        _ping?.Dispose();
        _ping = new Ping();

        CurrentRttMs = -1;
        _isMonitoring = true;
        _timer.Enabled = true;

        _logger.Information("Ping monitor started targeting {Target}", target);
    }

    /// <summary>Stop ping monitoring.</summary>
    public void Stop()
    {
        _stopping = true;
        _timer.Enabled = false;
        _isMonitoring = false;

        _ping?.Dispose();
        _ping = null;

        lock (_lock)
        {
            _rttSamples.Clear();
        }

        _logger.Information("Ping monitor stopped");
    }

    /// <summary>Pause monitoring (stop timer but keep state and samples).</summary>
    public void Pause()
    {
        if (!_isMonitoring) return;
        _timer.Enabled = false;
        _logger.Debug("Ping monitor paused");
    }

    /// <summary>Resume monitoring after a pause.</summary>
    public void Resume()
    {
        if (!_isMonitoring) return;
        _timer.Enabled = true;
        _logger.Debug("Ping monitor resumed");
    }

    // -- Private methods ────────────────────────────────────────────────────

    private async void OnTimerElapsed(object? sender, global::System.Timers.ElapsedEventArgs e)
    {
        if (_stopping) return;
        if (_ping == null || _disposed) return;

        long rtt = -1;
        bool success = false;

        try
        {
            _totalSent++;
            var reply = await _ping.SendPingAsync(_target, 1000);

            if (reply.Status == IPStatus.Success)
            {
                rtt = reply.RoundtripTime;
                success = true;
            }
            else
            {
                _totalLost++;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            _totalLost++;
            _logger.Debug(ex, "Ping to {Target} failed", _target);
        }

        CurrentRttMs = rtt;

        lock (_lock)
        {
            _rttSamples.Enqueue(rtt);
            while (_rttSamples.Count > 60)
                _rttSamples.Dequeue();
        }

        PingUpdated?.Invoke(this, new PingSample
        {
            RttMilliseconds = rtt,
            AverageRtt = AverageRttMs,
            JitterMs = JitterMs,
            PacketLossPercent = PacketLossPercent,
            Success = success
        });
    }

    // -- IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Enabled = false;
        _timer.Dispose();
        _ping?.Dispose();

        GC.SuppressFinalize(this);
    }
}
