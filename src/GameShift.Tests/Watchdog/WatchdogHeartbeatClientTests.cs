using System.Collections.Concurrent;
using GameShift.Core.Watchdog;
using Serilog;
using Serilog.Events;
using Xunit;

namespace GameShift.Tests.Watchdog;

/// <summary>
/// The heartbeat loop through its connect seam: no pipe is opened, and connect attempts and writes
/// follow a script.
/// </summary>
public class WatchdogHeartbeatClientTests
{
    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    /// <summary>Accepts a number of heartbeats, then fails as a pipe does when the server goes away.</summary>
    private sealed class BreakingStream : Stream
    {
        private int _writesLeft;
        public BreakingStream(int writes) => _writesLeft = writes;
        public int Writes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_writesLeft-- <= 0) throw new IOException("Pipe is broken.");
            Writes++;
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task NoWatchdog_LogsTheFailureOncePerOutage_AndStillConnectsWhenOneAppears()
    {
        var stream = new BreakingStream(writes: 2);
        var script = new Queue<Func<Stream>>();
        for (int i = 0; i < 5; i++) script.Enqueue(() => throw new TimeoutException("The operation has timed out."));
        script.Enqueue(() => stream);
        for (int i = 0; i < 5; i++) script.Enqueue(() => throw new TimeoutException("The operation has timed out."));

        var attempts = 0;
        var scriptDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Stream> Connect(CancellationToken ct)
        {
            attempts++;
            if (script.TryDequeue(out var next)) return next();
            scriptDone.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }

        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

        using (var client = new WatchdogHeartbeatClient(Connect, logger, TimeSpan.Zero, TimeSpan.Zero))
        {
            client.Start();
            await scriptDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(12, attempts);
        Assert.Equal(2, stream.Writes);
        Assert.Single(sink.Events, e => e.MessageTemplate.Text.Contains("Connected to watchdog pipe"));

        // Ten failed connects and one broken write, in two outages.
        var failures = sink.Events.Where(e => e.MessageTemplate.Text.Contains("Watchdog pipe unavailable")).ToList();
        Assert.Equal(2, failures.Count);
        Assert.IsType<TimeoutException>(failures[0].Exception);
        Assert.IsType<IOException>(failures[1].Exception);
    }
}
