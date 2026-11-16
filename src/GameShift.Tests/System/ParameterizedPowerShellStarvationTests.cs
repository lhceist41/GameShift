using System.Diagnostics;
using System.IO;
using System.Threading;
using GameShift.Core.System;
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
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        // Not disposed: queued work items may still touch these after the test returns.
        var release = new ManualResetEventSlim(false);
        var pending = new CountdownEvent(1);
        void Queue(Action work)
        {
            pending.AddCount();
            ThreadPool.UnsafeQueueUserWorkItem(_ => { try { work(); } finally { pending.Signal(); } }, null);
        }
        try
        {
            // Cap the pool so it cannot inject more workers, then occupy every free worker.
            var cap = Math.Max(global::System.Environment.ProcessorCount, minWorkers);
            Assert.True(ThreadPool.SetMaxThreads(cap, maxIo), "could not cap the thread pool");
            ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
            var started = new CountdownEvent(freeWorkers);
            for (var i = 0; i < freeWorkers; i++)
                Queue(() => { started.Signal(); release.Wait(); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "the blockers never occupied the pool");
            // Spares queued ahead of the probe: a worker still busy with earlier tests' work takes
            // one of these when it frees up, so only a new worker beyond the cap could run the probe.
            for (var i = 0; i < cap; i++)
                Queue(() => release.Wait());

            var probe = new ManualResetEventSlim(false);
            Queue(() => probe.Set());

            var r = ParameterizedPowerShell.Run(psi, 10000);

            Assert.False(probe.IsSet, "the pool still ran queued work, so this run proves nothing");
            Assert.True(r.Exited);
            Assert.Equal("hello", r.StdOut.Trim());
            Assert.Equal("oops", r.StdErr.Trim());
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
            // The next starvation test counts free workers, so none of ours may still be running.
            pending.Signal();
            pending.Wait(TimeSpan.FromSeconds(30));
        }
    }
}
