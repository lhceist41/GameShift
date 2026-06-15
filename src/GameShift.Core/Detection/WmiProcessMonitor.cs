using System.Management;
using GameShift.Core.Config;

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
        // EventArrived runs on a WMI delivery thread; an exception escaping here is not caught by
        // any app-level handler and would terminate the process. Contain and log instead - a single
        // dropped process event is harmless. The event object is COM-backed and disposed promptly.
        try
        {
            using ManagementBaseObject ev = e.NewEvent;
            var pid = Convert.ToInt32(ev.Properties["ProcessID"].Value);
            var name = ev.Properties["ProcessName"].Value?.ToString() ?? string.Empty;

            ProcessStarted?.Invoke(new ProcessStartEventData
            {
                ProcessId = pid,
                ImageFileName = name,   // WMI provides filename only, not full path
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[WmiProcessMonitor] Failed to handle process-start event");
        }
    }

    private void OnStop(object sender, EventArrivedEventArgs e)
    {
        try
        {
            using ManagementBaseObject ev = e.NewEvent;
            var pid = Convert.ToInt32(ev.Properties["ProcessID"].Value);

            ProcessStopped?.Invoke(new ProcessStopEventData
            {
                ProcessId = pid,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[WmiProcessMonitor] Failed to handle process-stop event");
        }
    }
}
