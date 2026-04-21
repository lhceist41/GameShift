using System.IO;
using System.IO.Pipes;
using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.Watchdog;

/// <summary>
/// Sends a heartbeat byte to the watchdog service every 5 seconds via
/// \\.\pipe\GameShiftWatchdog. Run in the main GameShift.App process.
///
/// If the watchdog is not installed or the pipe is unavailable, the client retries
/// silently — the heartbeat is best-effort and must not affect app stability.
/// Dispose to stop the heartbeat loop cleanly.
/// </summary>
public sealed class WatchdogHeartbeatClient : IDisposable
{
    private const int HeartbeatIntervalMs = 5_000;
    private const int ConnectTimeoutMs = 1_000;

    private static readonly byte[] HeartbeatByte = [0x01];

    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger = SettingsManager.Logger;
    private Task? _loopTask;

    /// <summary>Starts the background heartbeat loop.</summary>
    public void Start()
    {
        _loopTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        _logger.Information("[WatchdogHeartbeatClient] Heartbeat loop started");
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        NamedPipeClientStream? pipe = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // (Re)connect if not connected
                if (pipe?.IsConnected != true)
                {
                    pipe?.Dispose();
                    pipe = new NamedPipeClientStream(
                        serverName: ".",
                        pipeName: WatchdogPipeServer.PipeName,
                        direction: PipeDirection.Out,
                        options: PipeOptions.Asynchronous,
                        impersonationLevel: global::System.Security.Principal.TokenImpersonationLevel.Anonymous,
                        inheritability: HandleInheritability.None);

                    await pipe.ConnectAsync(ConnectTimeoutMs, ct);
                    _logger.Information("[WatchdogHeartbeatClient] Connected to watchdog pipe");
                }

                await pipe.WriteAsync(HeartbeatByte, ct);
                _logger.Debug("[WatchdogHeartbeatClient] Heartbeat sent");
                await Task.Delay(HeartbeatIntervalMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Watchdog not installed or not running — retry after a backoff
                _logger.Debug(ex, "[WatchdogHeartbeatClient] Pipe error, will retry");
                pipe?.Dispose();
                pipe = null;

                try { await Task.Delay(2_000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        pipe?.Dispose();
        _logger.Information("[WatchdogHeartbeatClient] Heartbeat loop stopped");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask?.Wait(3_000); } catch { /* Best-effort on shutdown */ }
        _cts.Dispose();
    }
}
