using System.IO.Pipes;
using GameShift.Core.Journal;
using Serilog;

namespace GameShift.Core.Watchdog;

/// <summary>
/// Named-pipe server that monitors GameShift heartbeats.
/// Listens on \\.\pipe\GameShiftWatchdog. Expects a 1-byte heartbeat at least every 15 seconds.
///
/// On heartbeat timeout or on client disconnect with an active journal session, calls
/// <see cref="WatchdogRevertEngine.RevertFromJournal"/> to restore all applied optimizations.
/// After recovery the server loop restarts so it can monitor a future GameShift instance.
/// </summary>
public class WatchdogPipeServer
{
    public const string PipeName = "GameShiftWatchdog";

    private const int HeartbeatTimeoutMs = 15_000;

    private readonly JournalManager _journal;
    private readonly WatchdogRevertEngine _revertEngine;
    private readonly ILogger _logger;

    public WatchdogPipeServer(JournalManager journal, WatchdogRevertEngine revertEngine, ILogger logger)
    {
        _journal = journal;
        _revertEngine = revertEngine;
        _logger = logger.ForContext<WatchdogPipeServer>();
    }

    /// <summary>
    /// Runs the pipe server loop. Returns only when <paramref name="ct"/> is cancelled.
    /// Each connection cycle: wait for client → monitor heartbeats → handle disconnect/timeout.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.Information("[WatchdogPipeServer] Starting on pipe '{PipeName}'", PipeName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);

                _logger.Information("[WatchdogPipeServer] Waiting for GameShift connection...");
                await pipe.WaitForConnectionAsync(ct);
                _logger.Information("[WatchdogPipeServer] GameShift connected");

                await MonitorHeartbeatsAsync(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[WatchdogPipeServer] Error in server loop — restarting in 2s");
                await Task.Delay(2_000, ct).ConfigureAwait(false);
            }
        }

        _logger.Information("[WatchdogPipeServer] Stopped");
    }

    // ── Heartbeat monitoring ──────────────────────────────────────────────────

    private async Task MonitorHeartbeatsAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var buf = new byte[1];

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            using var heartbeatCts = new CancellationTokenSource(HeartbeatTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, heartbeatCts.Token);

            try
            {
                int read = await pipe.ReadAsync(buf, linked.Token);

                if (read == 0)
                {
                    // Pipe closed by the client (clean disconnect or crash)
                    _logger.Information("[WatchdogPipeServer] Client disconnected — checking session state");
                    await CheckSessionAndRecoverAsync();
                    return;
                }

                _logger.Debug("[WatchdogPipeServer] Heartbeat received (0x{Byte:X2})", buf[0]);
            }
            catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested
                                                     && !ct.IsCancellationRequested)
            {
                // 15-second timeout with no heartbeat — definite crash
                _logger.Warning(
                    "[WatchdogPipeServer] No heartbeat for {Timeout}s — triggering crash recovery",
                    HeartbeatTimeoutMs / 1_000);
                await TriggerRecoveryAsync();
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.Warning(ex, "[WatchdogPipeServer] Pipe read error — checking session state");
                await CheckSessionAndRecoverAsync();
                return;
            }
        }
    }

    // ── Recovery ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the journal has an active session. Triggers recovery if so;
    /// logs a clean-shutdown message if not.
    /// </summary>
    private async Task CheckSessionAndRecoverAsync()
    {
        await Task.Yield(); // Keep async signature without blocking spin

        var journalData = _journal.LoadJournal();

        if (journalData?.SessionActive == true)
        {
            _logger.Warning(
                "[WatchdogPipeServer] Session still active on disconnect — possible crash, reverting optimizations");
            _revertEngine.RevertFromJournal(journalData, _journal);
        }
        else
        {
            _logger.Information("[WatchdogPipeServer] Session inactive — clean shutdown, no recovery needed");
        }
    }

    /// <summary>
    /// Unconditional recovery triggered by a heartbeat timeout.
    /// Always reverts applied optimizations regardless of sessionActive flag.
    /// </summary>
    private async Task TriggerRecoveryAsync()
    {
        await Task.Yield();

        var journalData = _journal.LoadJournal();

        if (journalData == null)
        {
            _logger.Warning("[WatchdogPipeServer] Heartbeat timed out but no journal found");
            return;
        }

        _revertEngine.RevertFromJournal(journalData, _journal);
    }
}
