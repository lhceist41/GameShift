using Serilog;

namespace GameShift.Core.Detection;

/// <summary>
/// Creates the best available <see cref="IProcessMonitor"/> for the current system.
/// Prefers <see cref="EtwProcessMonitor"/> (sub-ms latency, low CPU) and falls back to
/// <see cref="WmiProcessMonitor"/> when the ETW session cannot be created
/// (e.g. 64-session system-wide limit, insufficient privileges, TraceEvent DLL missing).
/// </summary>
public static class ProcessMonitorFactory
{
    public static IProcessMonitor Create(ILogger logger)
    {
        try
        {
            var etw = new EtwProcessMonitor();
            etw.Start();
            logger.Information(
                "Using ETW process monitoring (sub-millisecond latency, session '{Name}')",
                EtwProcessMonitor.SessionName);
            return etw;
        }
        catch (Exception ex)
        {
            logger.Warning(
                ex,
                "ETW session creation failed ({Message}), falling back to WMI process monitoring",
                ex.Message);

            try
            {
                var wmi = new WmiProcessMonitor();
                wmi.Start();
                logger.Information("Using WMI process monitoring (fallback)");
                return wmi;
            }
            catch (Exception wmiEx)
            {
                logger.Error(
                    wmiEx,
                    "WMI process monitoring also failed — process detection unavailable");
                throw;
            }
        }
    }
}
