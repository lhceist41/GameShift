using System.Diagnostics;
using System.IO;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.SystemRunner;

/// <summary>
/// <see cref="ParameterizedPowerShell.Run"/> under a saturated thread pool, with a cmd.exe child so
/// no PowerShell runs. Shares the collection that keeps pool saturation away from other tests.
/// </summary>
[Collection(nameof(ThreadPoolStarvationCollection))]
public class ParameterizedPowerShellStarvationTests
{
    private static string Cmd => Path.Combine(global::System.Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void Run_WhileTheThreadPoolHasNoFreeWorker_StillReturnsTheCompleteOutput()
    {
        var psi = new ProcessStartInfo(Cmd, "/c echo hello& echo oops 1>&2")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var r = ThreadPoolStarvation.Run(() => ParameterizedPowerShell.Run(psi, 10000));

        Assert.True(r.Exited);
        Assert.Equal("hello", r.StdOut.Trim());
        Assert.Equal("oops", r.StdErr.Trim());
    }
}
