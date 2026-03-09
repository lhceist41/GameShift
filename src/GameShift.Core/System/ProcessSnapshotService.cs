using System.Diagnostics;

namespace GameShift.Core.System;

/// <summary>
/// Shared cached process snapshot to avoid redundant Process.GetProcesses() calls.
/// Three periodic timers (MemoryOptimizer 5s, EfficiencyModeController 30s, IoPriorityManager 30s)
/// were each independently enumerating all processes. This service caches the result
/// with a 2-second TTL and a dirty flag triggered by GameDetector.ProcessSpawned.
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
    /// Otherwise refreshes from Process.GetProcesses().
    /// Callers are responsible for disposing each Process object after use.
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

            _cached = Process.GetProcesses();
            _lastRefresh = now;
            _dirty = false;
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
