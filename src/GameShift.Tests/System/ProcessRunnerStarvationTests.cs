using System.IO;
using System.Threading;
using GameShift.Core.System;
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
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        // Not disposed: queued work items may still touch these after the test returns.
        var release = new ManualResetEventSlim(false);
        try
        {
            // Cap the pool so it cannot inject more workers, then occupy every free worker.
            var cap = Math.Max(global::System.Environment.ProcessorCount, minWorkers);
            Assert.True(ThreadPool.SetMaxThreads(cap, maxIo), "could not cap the thread pool");
            ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
            var started = new CountdownEvent(freeWorkers);
            for (var i = 0; i < freeWorkers; i++)
                ThreadPool.UnsafeQueueUserWorkItem(_ => { started.Signal(); release.Wait(); }, null);
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "the blockers never occupied the pool");

            var probe = new ManualResetEventSlim(false);
            ThreadPool.UnsafeQueueUserWorkItem(_ => probe.Set(), null);

            var r = ProcessRunner.Run(Cmd, "/c echo hello& echo oops 1>&2", 10000);

            Assert.False(probe.IsSet, "the pool still ran queued work, so this run proves nothing");
            Assert.True(r.Exited);
            Assert.Equal("hello", r.StdOut.Trim());
            Assert.Equal("oops", r.StdErr.Trim());
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
        }
    }
}
