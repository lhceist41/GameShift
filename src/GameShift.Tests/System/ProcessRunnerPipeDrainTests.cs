using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using GameShift.Core.System;
using Xunit;

namespace GameShift.Tests.SystemRunner;

/// <summary>
/// Drives <see cref="ProcessRunner.PipeDrain"/> with in-process anonymous pipes, so a stream that
/// ends and one that stays open can be set up exactly, without a child process.
/// </summary>
public class ProcessRunnerPipeDrainTests
{
    [Fact]
    public void JoinBoth_OneStreamEndedOneStillOpen_KeepsTheEndedTextAndReportsTheOpenOneAsEmpty()
    {
        using var open = new TestPipe("partial");
        using var ended = new TestPipe("done");
        ended.CloseWriter();

        var sw = Stopwatch.StartNew();
        ProcessRunner.PipeDrain.JoinBoth(open.Drain, ended.Drain, 1000);
        sw.Stop();

        Assert.Equal(string.Empty, open.Drain.TextIfFinished);
        Assert.Equal("partial", open.Drain.Text); // what the timeout path reports
        Assert.Equal("done", ended.Drain.TextIfFinished);
        Assert.True(sw.ElapsedMilliseconds < 1700,
            $"joins took {sw.ElapsedMilliseconds} ms - longer than one shared grace of 1000 ms");
    }

    [Fact]
    public void JoinBoth_ClockAlreadyPastTheGrace_ReturnsAtOnceWithoutThrowing()
    {
        using var first = new TestPipe("a");
        using var second = new TestPipe("b");
        const int grace = 5000;

        var sw = Stopwatch.StartNew();
        ProcessRunner.PipeDrain.JoinBoth(first.Drain, second.Drain, grace, () => grace + 10);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"joins took {sw.ElapsedMilliseconds} ms although the grace was already used up");
    }

    /// <summary>A pipe with text written into it, drained by a <see cref="ProcessRunner.PipeDrain"/>.</summary>
    private sealed class TestPipe : IDisposable
    {
        private readonly AnonymousPipeServerStream _server = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream _writer;

        public TestPipe(string text)
        {
            _writer = new AnonymousPipeClientStream(PipeDirection.Out, _server.ClientSafePipeHandle);
            _writer.Write(Encoding.UTF8.GetBytes(text));
            _writer.Flush();
            Drain = new ProcessRunner.PipeDrain(new StreamReader(_server, Encoding.UTF8), "test drain");
        }

        public ProcessRunner.PipeDrain Drain { get; }

        public void CloseWriter() => _writer.Dispose();

        public void Dispose()
        {
            // Closing the writer ends the drain thread before its reader goes away.
            _writer.Dispose();
            ProcessRunner.PipeDrain.JoinBoth(Drain, Drain, 5000);
            _server.Dispose();
        }
    }
}
