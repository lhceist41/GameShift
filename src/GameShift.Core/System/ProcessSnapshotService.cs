using System.Diagnostics;

namespace GameShift.Core.System;

/// <summary>
/// Shared cached process snapshot to avoid redundant Process.GetProcesses() calls.
/// Three periodic timers (MemoryOptimizer 5s, EfficiencyModeController 30s, IoPriorityManager 30s)
/// were each independently enumerating all processes. This service caches the result
/// with a 2-second TTL and a dirty flag triggered by GameDetector.ProcessSpawned.
///
/// OWNERSHIP: This service owns the cached Process[] and disposes old snapshots on refresh.
/// Callers must NOT dispose the returned Process objects — they belong to the cache.
/// Copy any data you need (ProcessName, Id, WorkingSet64, etc.) before releasing the lock.
/// </summary>
public static class ProcessSnapshotService
{
    private static Process[]? _cached;
    private static DateTime _lastRefresh = DateTime.MinValue;
    private static volatile bool _dirty = true;
    private static readonly object _lock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns a cached process snapshot if still fresh (under 2 seconds old and not dirty).
    /// Otherwise refreshes from Process.GetProcesses(), disposing the previous snapshot.
    ///
    /// IMPORTANT: Callers must NOT dispose the returned Process objects.
    /// The cache owns them and will dispose them on the next refresh.
    /// </summary>
    public static Process[] GetProcesses()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!_dirty && _cached != null && (now - _lastRefresh) < CacheTtl)
            {
                return _cached;
            }

            var previous = _cached;
            _cached = Process.GetProcesses();
            _lastRefresh = now;
            _dirty = false;

            // Dispose the previous snapshot now that it's been replaced
            if (previous != null)
            {
                foreach (var p in previous)
                {
                    try { p.Dispose(); }
                    catch { /* Process may already be disposed or exited */ }
                }
            }

            return _cached;
        }
    }

    /// <summary>
    /// Marks the cache as dirty so the next GetProcesses() call will refresh.
    /// Called when GameDetector.ProcessSpawned fires (a new process has started).
    /// </summary>
    public static void MarkDirty()
    {
        _dirty = true;
    }
}
