using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using GameShift.Core.System;
using Xunit;

// Namespace is deliberately NOT "GameShift.Tests.System": a "System" namespace under the test
// assembly would shadow the BCL System namespace for sibling test files that use unqualified
// System.* names.
namespace GameShift.Tests.SystemRunner;

/// <summary>
/// Tests for the shared <see cref="ProcessRunner"/> helper. These run real short-lived Windows
/// console children (cmd.exe / ping) - fast and side-effect-free - to prove the core contract:
/// output and exit-code capture, stderr/stdout separation without deadlock, and kill-on-timeout for
/// a wedged child (the hang the helper exists to prevent).
/// </summary>
public class ProcessRunnerTests
{
    private static string Cmd => Path.Combine(global::System.Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void Run_Echo_CapturesStdoutAndExitCodeZero()
    {
        var r = ProcessRunner.Run(Cmd, "/c echo hello", 5000);

        Assert.True(r.Exited);
        Assert.False(r.TimedOut);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("hello", r.StdOut.Trim());
    }

    [Fact]
    public void Run_NonZeroExit_ReportsExitCode()
    {
        var r = ProcessRunner.Run(Cmd, "/c exit 3", 5000);

        Assert.True(r.Exited);
        Assert.False(r.TimedOut);
        Assert.Equal(3, r.ExitCode);
    }

    [Fact]
    public void Run_StderrOnly_CapturedSeparatelyWithoutDeadlock()
    {
        var r = ProcessRunner.Run(Cmd, "/c echo oops 1>&2", 5000);

        Assert.True(r.Exited);
        Assert.Equal("oops", r.StdErr.Trim());
        Assert.True(string.IsNullOrWhiteSpace(r.StdOut));
    }

    [Fact]
    public void Run_WedgedChild_TimesOutAndKillsWithoutHanging()
    {
        // ping -n 20 runs ~19s; a 1s timeout must kill it and return promptly.
        var sw = Stopwatch.StartNew();
        var r = ProcessRunner.Run(Cmd, "/c ping -n 20 127.0.0.1", 1000);
        sw.Stop();

        Assert.False(r.Exited);
        Assert.True(r.TimedOut);
        Assert.Equal(0, r.ExitCode); // exit code is never read on the timeout path
        Assert.True(sw.Elapsed.TotalSeconds < 10,
            $"timeout path took {sw.Elapsed.TotalSeconds:F1}s - the child was not killed promptly");
    }

    [Fact]
    public void Run_NonexistentExecutable_PropagatesStartException()
    {
        // Process.Start with UseShellExecute=false throws for a missing file; the helper does not
        // swallow it, so each call site's existing try/catch sees the same exception it does today.
        Assert.Throws<Win32Exception>(() =>
            ProcessRunner.Run(@"C:\does-not-exist\gameshift-nope-12345.exe", "", 2000));
    }
}
