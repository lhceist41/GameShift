using GameShift.Core.Journal;
using GameShift.Core.Watchdog;
using Serilog;

namespace GameShift.Watchdog;

/// <summary>
/// BackgroundService that runs the named-pipe heartbeat server.
/// Hosted inside the Windows Service host; manages the pipe server lifecycle.
/// </summary>
public sealed class WatchdogWorker : BackgroundService
{
    private readonly ILogger<WatchdogWorker> _msLogger;

    public WatchdogWorker(ILogger<WatchdogWorker> msLogger)
    {
        _msLogger = msLogger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _msLogger.LogInformation("GameShift Watchdog service started");

        var journal = new JournalManager();
        var revertEngine = new WatchdogRevertEngine(Log.Logger);
        var pipeServer = new WatchdogPipeServer(journal, revertEngine, Log.Logger);

        try
        {
            await pipeServer.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on service stop — not an error
        }
        catch (Exception ex)
        {
            _msLogger.LogCritical(ex, "Watchdog pipe server crashed unexpectedly");
        }

        _msLogger.LogInformation("GameShift Watchdog service stopped");
    }
}
