namespace GameShift.Core.Detection;

/// <summary>
/// Event data for a process start notification.
/// ETW populates all fields. WMI populates ProcessId and ImageFileName (filename only).
/// </summary>
public class ProcessStartEventData
{
    public int ProcessId { get; init; }

    /// <summary>
    /// Executable image path. ETW provides the full path (e.g. <c>C:\Windows\notepad.exe</c>).
    /// WMI provides the filename only (e.g. <c>notepad.exe</c>).
    /// </summary>
    public string ImageFileName { get; init; } = string.Empty;

    public int ParentProcessId { get; init; }
    public string? CommandLine { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Event data for a process stop notification.
/// </summary>
public class ProcessStopEventData
{
    public int ProcessId { get; init; }
    public string ImageFileName { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Abstracts process creation/termination monitoring.
/// Implemented by <see cref="EtwProcessMonitor"/> (primary, sub-ms latency)
/// and <see cref="WmiProcessMonitor"/> (fallback if ETW unavailable).
///
/// GameDetector subscribes to these events instead of owning WMI watchers directly.
/// </summary>
public interface IProcessMonitor : IDisposable
{
    /// <summary>Fired for every process start. Handlers must be fast and non-throwing.</summary>
    event Action<ProcessStartEventData>? ProcessStarted;

    /// <summary>Fired for every process stop.</summary>
    event Action<ProcessStopEventData>? ProcessStopped;

    /// <summary>Begin monitoring. Call once; call <see cref="Stop"/> to end.</summary>
    void Start();

    /// <summary>Stop monitoring and release the underlying session/watcher.</summary>
    void Stop();
}
