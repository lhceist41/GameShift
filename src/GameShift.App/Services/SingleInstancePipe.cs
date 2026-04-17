using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace GameShift.App.Services;

/// <summary>
/// Named pipe server/client for single-instance bring-to-front behavior.
/// The first instance starts a pipe server listening for "show" messages.
/// The second instance connects as a client, sends "show", and exits.
/// </summary>
public class SingleInstancePipe : IDisposable
{
    private const string PipeName = "GameShift_SingleInstance_Pipe";
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Raised on the first instance when a second instance requests focus.</summary>
    public event Action? ShowRequested;

    /// <summary>
    /// Starts the named pipe server on a background thread.
    /// Call this from the first (surviving) instance after startup.
    /// </summary>
    public void StartServer()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => ServerLoop(_cts.Token));
    }

    private async Task ServerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipeSecurity = CreatePipeSecurity();
                using var server = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    pipeSecurity);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(5000); // 5 second timeout per read
                try
                {
                    var message = await reader.ReadLineAsync(readCts.Token);

                    if (message == "show")
                    {
                        Log.Debug("SingleInstancePipe: Received 'show' from second instance");
                        ShowRequested?.Invoke();
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Individual read timed out, continue listening for next connection
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "SingleInstancePipe: Server loop error (non-fatal)");
                // Brief delay before retrying to avoid tight error loop
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    /// <summary>
    /// Sends a "show" message to the running first instance via the named pipe.
    /// Called from the second instance before it exits.
    /// Returns true if the message was sent successfully.
    /// </summary>
    public static bool SendShowMessage()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000); // 2 second timeout

            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("show");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a PipeSecurity that restricts access to Administrators and SYSTEM only.
    /// </summary>
    private static PipeSecurity CreatePipeSecurity()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return pipeSecurity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
