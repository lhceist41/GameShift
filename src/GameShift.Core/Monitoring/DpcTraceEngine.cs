using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Serilog;

namespace GameShift.Core.Monitoring;

/// <summary>
/// DPC severity based on peak execution time.
/// </summary>
public enum DpcHealthStatus
{
    Good,
    Warning,
    Critical
}

/// <summary>
/// Per-driver DPC statistics tracked by the trace engine.
/// </summary>
public class DriverDpcStats
{
    public readonly object SyncRoot = new();
    public string DriverFileName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string Category { get; set; } = "";
    public long DpcCount { get; set; }
    public double HighestExecutionMicroseconds { get; set; }
    public double TotalExecutionMicroseconds { get; set; }
    public double AverageExecutionMicroseconds =>
        DpcCount > 0 ? TotalExecutionMicroseconds / DpcCount : 0;

    /// <summary>Last 60 peak-per-second samples for sparkline rendering.</summary>
    public double[] RecentHistory { get; set; } = new double[60];
    public int HistoryIndex { get; set; }
    public int HistoryCount { get; set; }

    /// <summary>Peak value accumulated in the current 1-second window (reset each tick).</summary>
    public double CurrentWindowPeak { get; set; }

    public DpcHealthStatus Severity =>
        HighestExecutionMicroseconds > 2000 ? DpcHealthStatus.Critical :
        HighestExecutionMicroseconds > 500 ? DpcHealthStatus.Warning :
        DpcHealthStatus.Good;
}

/// <summary>
/// ETW-based per-driver DPC/ISR trace engine.
/// Uses Microsoft.Diagnostics.Tracing.TraceEvent to capture kernel DPC and interrupt events
/// with driver attribution and execution time in microseconds.
///
/// Resolves kernel routine addresses to driver filenames using EnumDeviceDrivers/GetDeviceDriverBaseName.
///
/// Requires administrator privileges. Only one kernel trace session can run system-wide.
/// If another tool (LatencyMon) is using the kernel session, StartCapture will fail gracefully.
/// </summary>
public class DpcTraceEngine : IDisposable
{
    private const string SessionName = "GameShift-DPC-Trace";

    private readonly KnownDriverDatabase _driverDb;
    private readonly ConcurrentDictionary<string, DriverDpcStats> _driverStats = new(StringComparer.OrdinalIgnoreCase);

    // Kernel module address-to-name resolution
    private readonly ConcurrentDictionary<ulong, string> _routineCache = new();
    private KernelModuleInfo[]? _kernelModules;

    private TraceEventSession? _session;
    private ETWTraceEventSource? _source;
    private Thread? _processingThread;
    private global::System.Timers.Timer? _tickTimer;
    private bool _isCapturing;
    private bool _disposed;
    private double _systemPeakDpc;

    /// <summary>Fires every 1 second with the latest driver stats snapshot.</summary>
    public event Action<IReadOnlyList<DriverDpcStats>>? DriversUpdated;

    /// <summary>Whether the trace session is actively capturing.</summary>
    public bool IsCapturing => _isCapturing;

    /// <summary>System-wide peak DPC value since capture started.</summary>
    public double SystemPeakDpc => Interlocked.CompareExchange(ref _systemPeakDpc, 0, 0);

    /// <summary>Overall system DPC health based on the highest driver peak.</summary>
    public DpcHealthStatus SystemHealth
    {
        get
        {
            if (_systemPeakDpc > 2000) return DpcHealthStatus.Critical;
            if (_systemPeakDpc > 500) return DpcHealthStatus.Warning;
            return DpcHealthStatus.Good;
        }
    }

    /// <summary>How many seconds the trace has been running.</summary>
    public int CaptureSeconds { get; private set; }

    public DpcTraceEngine(KnownDriverDatabase driverDb)
    {
        _driverDb = driverDb;
    }

    /// <summary>
    /// Starts the ETW kernel trace session for DPC/ISR capture.
    /// Returns false if another kernel trace is already running or we lack admin.
    /// </summary>
    public bool StartCapture()
    {
        if (_isCapturing) return true;

        try
        {
            // Build kernel module address map for routine-to-driver resolution
            _kernelModules = KernelModuleResolver.GetKernelModules();
            _routineCache.Clear();
            _driverStats.Clear();
            _systemPeakDpc = 0;

            Log.Debug("DpcTraceEngine: loaded {Count} kernel modules for address resolution",
                _kernelModules?.Length ?? 0);

            // Kill any stale session from a previous crash
            try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); }
            catch { /* ignore */ }

            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true
            };

            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                KernelTraceEventParser.Keywords.Interrupt);

            _source = _session.Source;

            // Subscribe to DPC events
            _source.Kernel.PerfInfoDPC += OnDpcEvent;
            _source.Kernel.PerfInfoISR += OnIsrEvent;

            // Start processing on background thread
            _processingThread = new Thread(() =>
            {
                try { _source.Process(); }
                catch (Exception ex) { Log.Warning(ex, "DpcTraceEngine: ETW processing stopped"); }
            })
            {
                IsBackground = true,
                Name = "DpcTraceEngine-ETW"
            };
            _processingThread.Start();

            // Start 1-second tick timer for UI updates
            CaptureSeconds = 0;
            _tickTimer = new global::System.Timers.Timer(1000);
            _tickTimer.Elapsed += OnTick;
            _tickTimer.Start();

            _isCapturing = true;
            Log.Information("DpcTraceEngine: capture started");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warning("DpcTraceEngine: requires administrator privileges");
            Cleanup();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DpcTraceEngine: failed to start capture (another trace session may be active)");
            Cleanup();
            return false;
        }
    }

    /// <summary>Stops the trace session and cleans up.</summary>
    public void StopCapture()
    {
        if (!_isCapturing) return;

        _isCapturing = false;
        Cleanup();
        Log.Information("DpcTraceEngine: capture stopped after {Seconds}s", CaptureSeconds);
    }

    /// <summary>Returns the top N drivers sorted by highest DPC execution time.</summary>
    public IReadOnlyList<DriverDpcStats> GetTopDrivers(int count = 10)
    {
        return _driverStats.Values
            .OrderByDescending(d => d.HighestExecutionMicroseconds)
            .Take(count)
            .ToList();
    }

    /// <summary>Returns a snapshot of per-driver peak values for persistence.</summary>
    public Dictionary<string, double> GetDriverPeaks()
    {
        return _driverStats.Values.ToDictionary(
            d => d.DriverFileName,
            d => d.HighestExecutionMicroseconds);
    }

    private void OnDpcEvent(DPCTraceData data)
    {
        var driverName = ResolveRoutineToDriver(data.Routine);
        RecordDriverEvent(driverName, data.ElapsedTimeMSec * 1000.0);
    }

    private void OnIsrEvent(ISRTraceData data)
    {
        var driverName = ResolveRoutineToDriver(data.Routine);
        RecordDriverEvent(driverName, data.ElapsedTimeMSec * 1000.0);
    }

    /// <summary>
    /// Resolves a kernel routine address to the driver filename that contains it.
    /// Uses a cached map of kernel module address ranges built from EnumDeviceDrivers.
    /// </summary>
    private string? ResolveRoutineToDriver(ulong routineAddress)
    {
        if (routineAddress == 0) return null;

        // Check cache first (hot path)
        if (_routineCache.TryGetValue(routineAddress, out var cached))
            return cached;

        if (_kernelModules == null || _kernelModules.Length == 0)
            return null;

        // Binary search through sorted kernel module addresses
        // Find the module whose base address is closest to (but not exceeding) the routine address
        string? driverName = null;
        ulong bestBase = 0;

        foreach (var module in _kernelModules)
        {
            if (module.BaseAddress <= routineAddress && module.BaseAddress > bestBase)
            {
                bestBase = module.BaseAddress;
                driverName = module.FileName;
            }
        }

        if (driverName != null)
        {
            _routineCache.TryAdd(routineAddress, driverName);
        }

        return driverName;
    }

    private void RecordDriverEvent(string? driverName, double microseconds)
    {
        if (string.IsNullOrEmpty(driverName) || microseconds <= 0) return;

        // Extract just the filename from a full path
        var fileName = Path.GetFileName(driverName);
        if (string.IsNullOrEmpty(fileName)) return;

        var stats = _driverStats.GetOrAdd(fileName, fn =>
        {
            var s = new DriverDpcStats { DriverFileName = fn };
            if (_driverDb.TryGetDriver(fn, out var info))
            {
                s.FriendlyName = info.FriendlyName;
                s.Category = info.Category;
            }
            else
            {
                s.FriendlyName = fn;
            }
            return s;
        });

        lock (stats.SyncRoot)
        {
            stats.DpcCount++;
            stats.TotalExecutionMicroseconds += microseconds;

            if (microseconds > stats.HighestExecutionMicroseconds)
                stats.HighestExecutionMicroseconds = microseconds;

            if (microseconds > stats.CurrentWindowPeak)
                stats.CurrentWindowPeak = microseconds;
        }

        if (microseconds > Interlocked.CompareExchange(ref _systemPeakDpc, 0, 0))
            Interlocked.Exchange(ref _systemPeakDpc, microseconds);
    }

    private void OnTick(object? sender, global::System.Timers.ElapsedEventArgs e)
    {
        CaptureSeconds++;

        // Roll sparkline history and reset current window peak
        foreach (var stats in _driverStats.Values)
        {
            lock (stats.SyncRoot)
            {
                var idx = stats.HistoryIndex % 60;
                stats.RecentHistory[idx] = stats.CurrentWindowPeak;
                stats.HistoryIndex++;
                if (stats.HistoryCount < 60) stats.HistoryCount++;
                stats.CurrentWindowPeak = 0;
            }
        }

        // Fire UI update event
        DriversUpdated?.Invoke(GetTopDrivers());
    }

    private void Cleanup()
    {
        _tickTimer?.Stop();
        _tickTimer?.Dispose();
        _tickTimer = null;

        try { _session?.Stop(); } catch { /* ignore */ }
        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;
        _source = null;

        // Wait for the ETW processing thread to exit (session.Stop() causes Process() to return)
        if (_processingThread != null && _processingThread.IsAlive)
        {
            _processingThread.Join(TimeSpan.FromSeconds(3));
        }
        _processingThread = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCapture();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents a loaded kernel module with its base address and filename.
/// </summary>
internal class KernelModuleInfo
{
    public ulong BaseAddress { get; init; }
    public string FileName { get; init; } = "";
}

/// <summary>
/// Resolves kernel routine addresses to driver filenames using
/// EnumDeviceDrivers and GetDeviceDriverBaseName Win32 APIs.
/// This is the same technique LatencyMon uses for DPC driver attribution.
/// </summary>
internal static class KernelModuleResolver
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumDeviceDrivers(
        [Out] IntPtr[] lpImageBase,
        int cb,
        out int lpcbNeeded);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetDeviceDriverBaseNameW(
        IntPtr ImageBase,
        [Out] char[] lpBaseName,
        int nSize);

    /// <summary>
    /// Enumerates all loaded kernel modules and returns their base addresses and filenames.
    /// </summary>
    public static KernelModuleInfo[] GetKernelModules()
    {
        try
        {
            // First call to get required buffer size
            EnumDeviceDrivers(Array.Empty<IntPtr>(), 0, out int needed);
            int count = needed / IntPtr.Size;

            if (count == 0) return Array.Empty<KernelModuleInfo>();

            var bases = new IntPtr[count];
            if (!EnumDeviceDrivers(bases, needed, out _))
                return Array.Empty<KernelModuleInfo>();

            var modules = new List<KernelModuleInfo>(count);
            var nameBuffer = new char[260];

            foreach (var baseAddr in bases)
            {
                if (baseAddr == IntPtr.Zero) continue;

                var nameLen = GetDeviceDriverBaseNameW(baseAddr, nameBuffer, nameBuffer.Length);
                if (nameLen > 0)
                {
                    modules.Add(new KernelModuleInfo
                    {
                        BaseAddress = (ulong)baseAddr,
                        FileName = new string(nameBuffer, 0, nameLen)
                    });
                }
            }

            // Sort by base address for efficient lookup
            modules.Sort((a, b) => a.BaseAddress.CompareTo(b.BaseAddress));
            return modules.ToArray();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "KernelModuleResolver: failed to enumerate kernel modules");
            return Array.Empty<KernelModuleInfo>();
        }
    }
}
