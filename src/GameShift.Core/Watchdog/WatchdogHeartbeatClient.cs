using System.IO;
using System.IO.Pipes;
using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.Watchdog;

/// <summary>
/// Sends a heartbeat byte to the watchdog service every 5 seconds via
/// \\.\pipe\GameShiftWatchdog. Run in the main GameShift.App process.
///
/// If the watchdog is not installed or the pipe is unavailable, the client keeps retrying and
/// logs the failure once until it connects again - the heartbeat is best-effort and must not
/// affect app stability. Dispose to stop the heartbeat loop cleanly.
/// </summary>
public sealed class WatchdogHeartbeatClient : IDisposable
{
    private const int ConnectTimeoutMs = 1_000;

    private static readonly byte[] HeartbeatByte = [0x01];

    private readonly CancellationTokenSource _cts = new();
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly ILogger _logger;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _retryDelay;
    private Task? _loopTask;

    public WatchdogHeartbeatClient()
        : this(ConnectPipeAsync, SettingsManager.Logger, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2))
    {
    }

    internal WatchdogHeartbeatClient(
        Func<CancellationToken, Task<Stream>> connect, ILogger logger, TimeSpan heartbeatInterval, TimeSpan retryDelay)
    {
        _connect = connect;
        _logger = logger;
        _heartbeatInterval = heartbeatInterval;
        _retryDelay = retryDelay;
    }

    /// <summary>Starts the background heartbeat loop.</summary>
    public void Start()
    {
        _loopTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        _logger.Information("[WatchdogHeartbeatClient] Heartbeat loop started");
    }

    private static async Task<Stream> ConnectPipeAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: WatchdogPipeServer.PipeName,
            direction: PipeDirection.Out,
            options: PipeOptions.Asynchronous,
            impersonationLevel: global::System.Security.Principal.TokenImpersonationLevel.Anonymous,
            inheritability: HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, ct);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        Stream? pipe = null;

        // Releases do not ship the watchdog, so a missing pipe is the normal case: log the first
        // failure of each outage, not every retry.
        bool failureLogged = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // (Re)connect if not connected
                if (pipe == null || pipe is PipeStream { IsConnected: false })
                {
                    pipe?.Dispose();
                    pipe = null;
                    pipe = await _connect(ct);
                    _logger.Information("[WatchdogHeartbeatClient] Connected to watchdog pipe");
                    failureLogged = false;
                }

                await pipe.WriteAsync(HeartbeatByte, ct);
                _logger.Debug("[WatchdogHeartbeatClient] Heartbeat sent");
                await Task.Delay(_heartbeatInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Watchdog not installed or not running - retry after a backoff
                if (!failureLogged)
                {
                    _logger.Debug(ex, "[WatchdogHeartbeatClient] Watchdog pipe unavailable, retrying quietly until it connects");
                    failureLogged = true;
                }
                pipe?.Dispose();
                pipe = null;

                try { await Task.Delay(_retryDelay, ct); }
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
