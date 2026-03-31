using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Serilog;

namespace GameShift.Core.Detection;

/// <summary>
/// ETW-based process monitor using the Microsoft-Windows-Kernel-Process provider.
/// Sub-millisecond event delivery with minimal CPU overhead compared to WMI.
///
/// The ETW session <c>GameShift-ProcessMonitor</c> is a kernel session that persists
/// system-wide until explicitly stopped. Crash recovery must call
/// <see cref="CleanupStaleSession"/> to avoid orphaned sessions (max 64 system-wide).
///
/// The <see cref="TraceEventSession.Source.Process"/> call is blocking — it runs on
/// a dedicated background thread named <c>GameShift_ETW_ProcessMonitor</c>.
/// </summary>
public sealed class EtwProcessMonitor : IProcessMonitor
{
    public const string SessionName = "GameShift-ProcessMonitor";

    private TraceEventSession? _session;
    private Thread? _processingThread;

    public event Action<ProcessStartEventData>? ProcessStarted;
    public event Action<ProcessStopEventData>? ProcessStopped;

    public void Start()
    {
        // Clean up any stale session from a previous crash before creating a new one
        CleanupStaleSession(null);

        _session = new TraceEventSession(SessionName);

        // Enable only kernel process events — minimal overhead
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);

        // Subscribe to process lifecycle events
        _session.Source.Kernel.ProcessStart += data =>
        {
            ProcessStarted?.Invoke(new ProcessStartEventData
            {
                ProcessId       = data.ProcessID,
                ImageFileName   = data.ImageFileName,
                ParentProcessId = data.ParentID,
                CommandLine     = data.CommandLine,
                Timestamp       = data.TimeStamp
            });
        };

        _session.Source.Kernel.ProcessStop += data =>
        {
            ProcessStopped?.Invoke(new ProcessStopEventData
            {
                ProcessId     = data.ProcessID,
                ImageFileName = data.ImageFileName,
                // ExitCode not available from the kernel session parser;
                // available if using the Microsoft-Windows-Kernel-Process provider directly.
                Timestamp     = data.TimeStamp
            });
        };

        // Process() is a blocking call — run it on a dedicated background thread
        _processingThread = new Thread(() =>
        {
            try { _session.Source.Process(); }
            catch { /* Expected when Stop() is called */ }
        })
        {
            IsBackground = true,
            Name = "GameShift_ETW_ProcessMonitor"
        };
        _processingThread.Start();
    }

    public void Stop()
    {
        _session?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _processingThread?.Join(3_000);
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// Stops and disposes any orphaned ETW session left over from a previous crash.
    /// Call from boot recovery and watchdog recovery paths.
    /// Safe to call at any time — no-ops if no stale session exists.
    /// </summary>
    public static void CleanupStaleSession(ILogger? logger)
    {
        try
        {
            var stale = TraceEventSession.GetActiveSession(SessionName);
            if (stale != null)
            {
                stale.Stop();
                stale.Dispose();
                logger?.Information(
                    "[EtwProcessMonitor] Cleaned up stale ETW session '{Name}'", SessionName);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(
                ex,
                "[EtwProcessMonitor] Failed to clean up stale ETW session '{Name}'", SessionName);
        }
    }
}
