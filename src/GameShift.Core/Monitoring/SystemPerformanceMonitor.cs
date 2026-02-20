using System.Diagnostics;
using System.Management;
using Serilog;
using Timer = global::System.Timers.Timer;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Event data for a performance sample (CPU, RAM, GPU percentages).
/// </summary>
public class PerformanceSample
{
    public double CpuPercent { get; init; }
    public double RamPercent { get; init; }
    public double GpuPercent { get; init; }
}

/// <summary>
/// Real-time system performance monitor that samples CPU, RAM, and GPU usage
/// via PerformanceCounter every 1 second. Follows the DpcLatencyMonitor pattern:
/// Timer-based polling, rolling Queue, IDisposable, events.
/// GPU monitoring uses the "GPU Engine" counter category (Win10 1709+).
/// </summary>
public class SystemPerformanceMonitor : IDisposable
{
    // -- Private fields ─────────────────────────────────────────────────────
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramCounter;
    private Timer _timer;
    private readonly Queue<double> _cpuSamples = new();
    private readonly Queue<double> _ramSamples = new();
    private readonly Queue<double> _gpuSamples = new();
    private readonly object _lock = new();
    private bool _cpuAvailable;
    private bool _ramAvailable;
    private bool _gpuAvailable;
    private double _totalRamMB;
    private bool _isMonitoring;
    private bool _disposed;
    private readonly ILogger _logger;

    // GPU via PerformanceCounter — sum ALL engtype_3D instances for total utilization
    private PerformanceCounterCategory? _gpuEngineCategory;
    private List<PerformanceCounter> _gpu3DCounters = new();
    private int _gpuRefreshTickCount;
    private const int GpuRefreshIntervalTicks = 15; // Refresh counter instance list every 15 seconds

    // -- Public properties ──────────────────────────────────────────────────

    /// <summary>Current CPU usage percentage (0-100).</summary>
    public double CurrentCpuPercent { get; private set; }

    /// <summary>Current RAM usage percentage (0-100).</summary>
    public double CurrentRamPercent { get; private set; }

    /// <summary>Current GPU usage percentage (0-100). -1 if unavailable.</summary>
    public double CurrentGpuPercent { get; private set; } = -1;

    /// <summary>Whether the monitor is currently sampling.</summary>
    public bool IsMonitoring => _isMonitoring;

    // -- Events ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired every 1 second with the latest performance sample.
    /// </summary>
    public event EventHandler<PerformanceSample>? SampleUpdated;

    // -- Constructor ────────────────────────────────────────────────────────

    public SystemPerformanceMonitor()
    {
        _logger = Config.SettingsManager.Logger;

        // CPU counter
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // Prime the counter (first read is always 0)
            _cpuAvailable = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "CPU PerformanceCounter not available. CPU monitoring disabled.");
            _cpuAvailable = false;
        }

        // RAM counter + total RAM via WMI
        try
        {
            _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            _ramAvailable = true;

            // Cache total physical RAM once
            _totalRamMB = GetTotalPhysicalMemoryMB();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "RAM PerformanceCounter not available. RAM monitoring disabled.");
            _ramAvailable = false;
        }

        // GPU counter (Win10 1709+ "GPU Engine" category)
        // We must sum ALL engtype_3D instances across all processes to get total
        // GPU utilization. Each instance is per-process (e.g., pid_1234_..._engtype_3D),
        // so a single instance only tracks one process — usually showing 0%.
        try
        {
            _gpuEngineCategory = new PerformanceCounterCategory("GPU Engine");
            RefreshGpuCounters();
            _gpuAvailable = _gpu3DCounters.Count > 0;
            if (!_gpuAvailable)
                _logger.Information("No GPU Engine 3D instances found. GPU monitoring unavailable.");
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "GPU PerformanceCounter category not available. GPU monitoring disabled.");
            _gpuAvailable = false;
        }

        _timer = new Timer(1000);
        _timer.Elapsed += OnTimerElapsed;
        _timer.AutoReset = true;
    }

    // -- Public methods ─────────────────────────────────────────────────────

    /// <summary>Start performance monitoring.</summary>
    public void Start()
    {
        lock (_lock)
        {
            _cpuSamples.Clear();
            _ramSamples.Clear();
            _gpuSamples.Clear();
        }

        CurrentCpuPercent = 0;
        CurrentRamPercent = 0;
        CurrentGpuPercent = _gpuAvailable ? 0 : -1;
        _isMonitoring = true;
        _timer.Enabled = true;

        _logger.Information("System performance monitor started");
    }

    /// <summary>Stop performance monitoring.</summary>
    public void Stop()
    {
        _timer.Enabled = false;
        _isMonitoring = false;

        lock (_lock)
        {
            _cpuSamples.Clear();
            _ramSamples.Clear();
            _gpuSamples.Clear();
        }

        _logger.Information("System performance monitor stopped");
    }

    // -- Private methods ────────────────────────────────────────────────────

    private void OnTimerElapsed(object? sender, global::System.Timers.ElapsedEventArgs e)
    {
        try
        {
            double cpu = 0;
            double ram = 0;
            double gpu = -1;

            if (_cpuAvailable && _cpuCounter != null)
            {
                cpu = Math.Round(_cpuCounter.NextValue(), 1);
                cpu = Math.Clamp(cpu, 0, 100);
            }

            if (_ramAvailable && _ramCounter != null && _totalRamMB > 0)
            {
                double availableMB = _ramCounter.NextValue();
                double usedMB = _totalRamMB - availableMB;
                ram = Math.Round((usedMB / _totalRamMB) * 100, 1);
                ram = Math.Clamp(ram, 0, 100);
            }

            if (_gpuAvailable && _gpu3DCounters.Count > 0)
            {
                try
                {
                    // Periodically refresh instance list (processes start/stop GPU engines)
                    _gpuRefreshTickCount++;
                    if (_gpuRefreshTickCount >= GpuRefreshIntervalTicks)
                    {
                        _gpuRefreshTickCount = 0;
                        RefreshGpuCounters();
                    }

                    // Sum utilization across ALL 3D engine instances (all processes)
                    double totalGpu = 0;
                    foreach (var counter in _gpu3DCounters)
                    {
                        try
                        {
                            totalGpu += counter.NextValue();
                        }
                        catch
                        {
                            // Instance may have gone stale — cleaned up on next refresh
                        }
                    }
                    gpu = Math.Clamp(Math.Round(totalGpu, 1), 0, 100);
                }
                catch
                {
                    gpu = -1;
                }
            }

            CurrentCpuPercent = cpu;
            CurrentRamPercent = ram;
            CurrentGpuPercent = gpu;

            lock (_lock)
            {
                EnqueueSample(_cpuSamples, cpu);
                EnqueueSample(_ramSamples, ram);
                if (gpu >= 0) EnqueueSample(_gpuSamples, gpu);
            }

            SampleUpdated?.Invoke(this, new PerformanceSample
            {
                CpuPercent = cpu,
                RamPercent = ram,
                GpuPercent = gpu
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error reading performance counters");
        }
    }

    /// <summary>
    /// Rebuild the list of GPU 3D engine PerformanceCounters by enumerating
    /// current instances. Called on init and every GpuRefreshIntervalTicks seconds
    /// to handle processes that start/stop using the GPU.
    /// </summary>
    private void RefreshGpuCounters()
    {
        foreach (var c in _gpu3DCounters)
        {
            try { c.Dispose(); } catch { /* best effort */ }
        }
        _gpu3DCounters.Clear();

        if (_gpuEngineCategory == null) return;

        try
        {
            var instanceNames = _gpuEngineCategory.GetInstanceNames();
            foreach (var name in instanceNames)
            {
                if (name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name);
                    counter.NextValue(); // Prime (first read is always 0)
                    _gpu3DCounters.Add(counter);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to refresh GPU Engine counter instances");
        }
    }

    private static void EnqueueSample(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > 60)
            queue.Dequeue();
    }

    private double GetTotalPhysicalMemoryMB()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                var totalBytes = Convert.ToDouble(obj["TotalPhysicalMemory"]);
                return totalBytes / (1024 * 1024);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to query total RAM via WMI, falling back to GC info");
        }

        // Fallback: use GCMemoryInfo (less accurate but always available)
        var gcInfo = GC.GetGCMemoryInfo();
        return gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0);
    }

    // -- IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Enabled = false;
        _timer.Dispose();
        _cpuCounter?.Dispose();
        _ramCounter?.Dispose();
        foreach (var c in _gpu3DCounters)
        {
            try { c.Dispose(); } catch { /* best effort */ }
        }
        _gpu3DCounters.Clear();

        GC.SuppressFinalize(this);
    }
}
