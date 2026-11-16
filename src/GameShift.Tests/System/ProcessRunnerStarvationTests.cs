using System.IO;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.SystemRunner;

/// <summary>
/// Saturating the thread pool is process-wide, so these tests run alone, never beside other tests.
/// </summary>
[CollectionDefinition(nameof(ThreadPoolStarvationCollection), DisableParallelization = true)]
public class ThreadPoolStarvationCollection { }

[Collection(nameof(ThreadPoolStarvationCollection))]
public class ProcessRunnerStarvationTests
{
    private static string Cmd => Path.Combine(global::System.Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void Run_WhileTheThreadPoolHasNoFreeWorker_StillReturnsTheCompleteOutput()
    {
        var r = ThreadPoolStarvation.Run(() => ProcessRunner.Run(Cmd, "/c echo hello& echo oops 1>&2", 10000));

        Assert.True(r.Exited);
        Assert.Equal("hello", r.StdOut.Trim());
        Assert.Equal("oops", r.StdErr.Trim());
    }
}
