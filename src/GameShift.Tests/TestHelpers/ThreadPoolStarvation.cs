using System;
using System.Threading;
using Xunit;

namespace GameShift.Tests.TestHelpers;

/// <summary>
/// Runs work while the thread pool has no free worker, for tests that prove a code path does not
/// depend on pool threads. Saturation is process-wide, so callers belong to a collection that runs
/// alone (<c>ThreadPoolStarvationCollection</c>).
/// </summary>
public static class ThreadPoolStarvation
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Caps the pool, occupies every free worker, runs <paramref name="work"/> on the calling thread
    /// and fails if a probe queued behind the blockers ran meanwhile (the run then proves nothing).
    /// The pool maximum is restored and this method waits up to 30 s for every queued item to return.
    /// </summary>
    public static T Run<T>(Func<T> work)
    {
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        // Not disposed: a queued item that outlives the bounded wait below may still touch these.
        var release = new ManualResetEventSlim(false);
        var pending = new CountdownEvent(1);
        void Queue(Action item)
        {
            pending.AddCount();
            ThreadPool.UnsafeQueueUserWorkItem(_ => { try { item(); } finally { pending.Signal(); } }, null);
        }

        try
        {
            // Cap the pool so it cannot inject more workers, then occupy every free worker.
            var cap = Math.Max(Environment.ProcessorCount, minWorkers);
            Assert.True(ThreadPool.SetMaxThreads(cap, maxIo), "could not cap the thread pool");
            ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
            var started = new CountdownEvent(freeWorkers);
            for (var i = 0; i < freeWorkers; i++)
                Queue(() => { started.Signal(); release.Wait(); });
            Assert.True(started.Wait(Bound), "the blockers never occupied the pool");
            // Spares queued ahead of the probe: a worker still busy with earlier tests' work takes
            // one of these when it frees up, so only a new worker beyond the cap could run the probe.
            for (var i = 0; i < cap; i++)
                Queue(() => release.Wait());

            var probe = new ManualResetEventSlim(false);
            Queue(() => probe.Set());

            var result = work();

            Assert.False(probe.IsSet, "the pool still ran queued work, so this run proves nothing");
            return result;
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
            // The next starvation test counts free workers, so none of ours may still be running.
            pending.Signal();
            pending.Wait(Bound);
        }
    }
}
