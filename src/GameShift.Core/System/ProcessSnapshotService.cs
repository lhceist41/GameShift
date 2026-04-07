using System.Diagnostics;

namespace GameShift.Core.System;

/// <summary>
/// Lightweight snapshot of a process for safe cross-thread use.
/// Callers work with these value objects instead of live Process handles.
/// </summary>
public class ProcessSnapshot
{
    public int Id { get; init; }
    public string ProcessName { get; init; } = "";
    public long WorkingSet64 { get; init; }
    public IntPtr ProcessHandle { get; init; }
}

/// <summary>
/// Shared cached process snapshot to avoid redundant Process.GetProcesses() calls.
/// Three periodic timers (MemoryOptimizer 5s, EfficiencyModeController 30s, IoPriorityManager 30s)
/// were each independently enumerating all processes. This service caches the result
/// with a 2-second TTL and a dirty flag triggered by GameDetector.ProcessSpawned.
///
/// Returns ProcessSnapshot[] (value objects) so callers can safely iterate without
/// risk of disposed Process handles from a concurrent cache refresh.
/// </summary>
public static class ProcessSnapshotService
{
    private static ProcessSnapshot[]? _cached;
    private static DateTime _lastRefresh = DateTime.MinValue;
    private static volatile bool _dirty = true;
    private static readonly object _lock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns a cached process snapshot if still fresh (under 2 seconds old and not dirty).
    /// Otherwise refreshes from Process.GetProcesses() and captures scalar data.
    /// The returned array contains value objects safe to use from any thread.
    /// </summary>
    public static ProcessSnapshot[] GetProcesses()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!_dirty && _cached != null && (now - _lastRefresh) < CacheTtl)
            {
                return _cached;
            }

            var live = Process.GetProcesses();
            var snapshots = new List<ProcessSnapshot>(live.Length);

            foreach (var p in live)
            {
                try
                {
                    snapshots.Add(new ProcessSnapshot
                    {
                        Id = p.Id,
                        ProcessName = p.ProcessName,
                        WorkingSet64 = p.WorkingSet64,
                        ProcessHandle = p.Handle
                    });
                }
                catch
                {
                    // Process may have exited between enumeration and property access
                }
                finally
                {
                    try { p.Dispose(); }
                    catch { }
                }
            }

            _cached = snapshots.ToArray();
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
