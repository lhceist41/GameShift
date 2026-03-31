using System.Management;

namespace GameShift.Core.Detection;

/// <summary>
/// WMI-based process monitor using Win32_ProcessStartTrace / Win32_ProcessStopTrace.
/// Retained as a fallback when the ETW session cannot be created (e.g. 64-session limit hit).
///
/// WMI has higher latency (100-300 ms) and CPU overhead vs ETW, but requires no kernel
/// session and is universally available on Windows 10+.
/// </summary>
public sealed class WmiProcessMonitor : IProcessMonitor
{
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;

    public event Action<ProcessStartEventData>? ProcessStarted;
    public event Action<ProcessStopEventData>? ProcessStopped;

    public void Start()
    {
        _startWatcher = new ManagementEventWatcher(
            new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        _startWatcher.EventArrived += OnStart;
        _startWatcher.Start();

        _stopWatcher = new ManagementEventWatcher(
            new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
        _stopWatcher.EventArrived += OnStop;
        _stopWatcher.Start();
    }

    public void Stop()
    {
        _startWatcher?.Stop();
        _stopWatcher?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
        _startWatcher = null;
        _stopWatcher = null;
    }

    private void OnStart(object sender, EventArrivedEventArgs e)
    {
        var pid  = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
        var name = e.NewEvent.Properties["ProcessName"].Value?.ToString() ?? string.Empty;

        ProcessStarted?.Invoke(new ProcessStartEventData
        {
            ProcessId     = pid,
            ImageFileName = name,   // WMI provides filename only, not full path
            Timestamp     = DateTime.UtcNow
        });
    }

    private void OnStop(object sender, EventArrivedEventArgs e)
    {
        var pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);

        ProcessStopped?.Invoke(new ProcessStopEventData
        {
            ProcessId = pid,
            Timestamp = DateTime.UtcNow
        });
    }
}
