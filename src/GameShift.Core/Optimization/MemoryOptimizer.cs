using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Core.Config;
using Timer = System.Threading.Timer;

namespace GameShift.Core.Optimization;

/// <summary>
/// Memory Optimizer - Purges standby list and modified page list, manages memory priority.
/// Monitors available physical memory every 5 seconds during gaming and purges cached memory
/// from the standby list when available RAM falls below configured threshold.
/// Additionally flushes modified pages to prevent I/O storm buildup and lowers memory priority
/// of background processes so the OS preferentially evicts their pages under memory pressure.
/// </summary>
public class MemoryOptimizer : IOptimization
{
    /// <summary>
    /// Records original memory priority state for a demoted process.
    /// </summary>
    private readonly record struct MemPriorityOriginalState(int ProcessId, string ProcessName, uint OriginalPriority);

    private Timer? _monitorTimer;
    private int _thresholdMB = 1024; // Default, overridden by profile in ApplyAsync
    private volatile bool _isMonitoring;
    private bool _isApplied;
    private bool _flushModifiedPages;
    private bool _manageMemoryPriority;
    private string[] _activeGameProcessNames = Array.Empty<string>();

    private readonly List<MemPriorityOriginalState> _demotedProcesses = new();
    private readonly HashSet<int> _demotedPids = new();
    private readonly object _memPriorityLock = new();

    public const string OptimizationId = "Memory Optimizer";

    /// <inheritdoc/>
    public string Name => OptimizationId;

    /// <inheritdoc/>
    public string Description => "Purges standby list when free memory drops below threshold";

    /// <inheritdoc/>
    public bool IsApplied => _isApplied;

    /// <inheritdoc/>
    public bool IsAvailable => true; // Memory purge works on all Windows versions with admin rights

    /// <summary>
    /// Applies memory optimization by immediately purging standby list and starting periodic monitoring.
    /// Also flushes modified pages and demotes background process memory priority if enabled.
    /// </summary>
    /// <param name="snapshot">Snapshot to record original state (not used - memory purge is non-destructive)</param>
    /// <param name="profile">Game profile containing process info and settings</param>
    /// <returns>True if optimization applied successfully, false otherwise</returns>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            // Use per-profile memory threshold (already merged with AppSettings default by DetectionOrchestrator)
            if (profile.MemoryThresholdMB > 0)
            {
                _thresholdMB = profile.MemoryThresholdMB;
            }

            // Read sub-toggles from profile
            _flushModifiedPages = profile.FlushModifiedPages;
            _manageMemoryPriority = profile.ManageMemoryPriority;
            _activeGameProcessNames = ResolveGameProcessNames(profile);

            SettingsManager.Logger.Information("[MemoryOptimizer] Applying memory optimization");

            // Initial flush of modified pages at session start (clears accumulated dirty pages)
            if (_flushModifiedPages)
            {
                FlushModifiedPages();
            }

            // Perform immediate standby list purge
            bool purgeSuccess = PurgeStandbyList();
            if (!purgeSuccess)
            {
                SettingsManager.Logger.Warning("[MemoryOptimizer] Initial standby list purge failed, but continuing with monitoring");
            }

            // Demote background process memory priority
            if (_manageMemoryPriority)
            {
                DemoteBackgroundMemoryPriority();
                SettingsManager.Logger.Information(
                    "[MemoryOptimizer] Memory priority demoted on {Count} background processes",
                    _demotedProcesses.Count);
            }

            // Start periodic memory monitoring (check every 5 seconds)
            _isMonitoring = true;
            _monitorTimer = new Timer(_ => CheckAndPurge(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            _isApplied = true;
            SettingsManager.Logger.Information("[MemoryOptimizer] Memory optimizer started with {ThresholdMB}MB threshold", _thresholdMB);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[MemoryOptimizer] Failed to apply memory optimization");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Reverts memory optimization by stopping the monitoring timer and restoring memory priorities.
    /// Standby list purge is non-destructive (refills naturally).
    /// </summary>
    /// <param name="snapshot">Snapshot containing original state (not used for this optimization)</param>
    /// <returns>True if revert succeeded, false otherwise</returns>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        try
        {
            SettingsManager.Logger.Information("[MemoryOptimizer] Reverting memory optimization");

            _isMonitoring = false;

            // Stop the monitoring timer
            if (_monitorTimer != null)
            {
                _monitorTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _monitorTimer.Dispose();
                _monitorTimer = null;
            }

            // Restore background process memory priorities
            if (_manageMemoryPriority)
            {
                RestoreMemoryPriorities();
            }

            _isApplied = false;
            SettingsManager.Logger.Information("[MemoryOptimizer] Memory optimizer stopped");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[MemoryOptimizer] Failed to revert memory optimization");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Periodic callback that checks available memory and purges standby list if below threshold.
    /// Also flushes modified pages and rescans for newly spawned background processes.
    /// Called every 5 seconds by the monitoring timer.
    /// </summary>
    private void CheckAndPurge()
    {
        if (!_isMonitoring)
        {
            return;
        }

        try
        {
            // Rescan for newly spawned background processes and demote their memory priority
            if (_manageMemoryPriority)
            {
                DemoteBackgroundMemoryPriority();
            }

            // Query current memory status
            var memInfo = new NativeInterop.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeInterop.MEMORYSTATUSEX>()
            };

            if (!NativeInterop.GlobalMemoryStatusEx(ref memInfo))
            {
                SettingsManager.Logger.Warning("[MemoryOptimizer] Failed to query memory status");
                return;
            }

            // Convert available physical memory to MB
            ulong availableMB = memInfo.ullAvailPhys / (1024 * 1024);

            // Check if below threshold
            if (availableMB < (ulong)_thresholdMB)
            {
                SettingsManager.Logger.Information(
                    "[MemoryOptimizer] Available memory ({AvailableMB}MB) below threshold ({ThresholdMB}MB), purging",
                    availableMB, _thresholdMB);

                PurgeStandbyList();

                // Also flush modified pages to prevent I/O storm buildup
                if (_flushModifiedPages)
                {
                    FlushModifiedPages();
                }
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[MemoryOptimizer] Error during memory check");
        }
    }

    /// <summary>
    /// Purges the Windows standby list by calling NtSetSystemInformation.
    /// Standby list contains cached file data that can be freed to increase available RAM.
    /// Requires administrator privileges.
    /// </summary>
    /// <returns>True if purge succeeded, false otherwise</returns>
    private bool PurgeStandbyList()
    {
        IntPtr bufferPtr = IntPtr.Zero;

        try
        {
            // Allocate buffer for the purge command
            bufferPtr = Marshal.AllocHGlobal(sizeof(int));

            // Write the MemoryPurgeStandbyList command to the buffer
            Marshal.WriteInt32(bufferPtr, NativeInterop.MemoryPurgeStandbyList);

            // Call NtSetSystemInformation to purge standby list
            int status = NativeInterop.NtSetSystemInformation(
                NativeInterop.SystemMemoryListInformation,
                bufferPtr,
                sizeof(int));

            if (status == 0) // NTSTATUS 0 = STATUS_SUCCESS
            {
                SettingsManager.Logger.Debug("[MemoryOptimizer] Standby list purged successfully");
                return true;
            }
            else
            {
                SettingsManager.Logger.Warning("[MemoryOptimizer] Standby list purge failed with NTSTATUS: 0x{Status:X8}", status);
                return false;
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[MemoryOptimizer] Exception during standby list purge");
            return false;
        }
        finally
        {
            // Always free allocated memory
            if (bufferPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(bufferPtr);
            }
        }
    }

    /// <summary>
    /// Flushes the Windows modified page list by calling NtSetSystemInformation.
    /// Modified pages are dirty memory pages waiting to be written to disk.
    /// Flushing proactively prevents I/O storms from coinciding with gameplay.
    /// </summary>
    private void FlushModifiedPages()
    {
        IntPtr bufferPtr = IntPtr.Zero;

        try
        {
            bufferPtr = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(bufferPtr, NativeInterop.MemoryFlushModifiedList);

            int status = NativeInterop.NtSetSystemInformation(
                NativeInterop.SystemMemoryListInformation,
                bufferPtr,
                sizeof(int));

            if (status == 0)
            {
                SettingsManager.Logger.Debug("[MemoryOptimizer] Modified page list flushed successfully");
            }
            else
            {
                SettingsManager.Logger.Warning("[MemoryOptimizer] Modified page flush failed with NTSTATUS: 0x{Status:X8}", status);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[MemoryOptimizer] Exception during modified page flush");
        }
        finally
        {
            if (bufferPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(bufferPtr);
            }
        }
    }

    // ── Memory Priority Management ─────────────────────────────────────

    /// <summary>
    /// Lowers memory priority of targeted background processes to Low (2).
    /// Lower-priority pages are reclaimed first under memory pressure, protecting game pages.
    /// Thread-safe via _memPriorityLock.
    /// </summary>
    private void DemoteBackgroundMemoryPriority()
    {
        int newlyDemoted = 0;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    string name = process.ProcessName;

                    // Use shared targeting logic
                    if (!BackgroundProcessTargets.ShouldDemote(name, _activeGameProcessNames))
                        continue;

                    lock (_memPriorityLock)
                    {
                        // Skip if already demoted in this session
                        if (_demotedPids.Contains(process.Id))
                            continue;
                    }

                    // Query current memory priority
                    var currentInfo = new NativeInterop.MEMORY_PRIORITY_INFORMATION();
                    int size = Marshal.SizeOf<NativeInterop.MEMORY_PRIORITY_INFORMATION>();
                    IntPtr ptr = Marshal.AllocHGlobal(size);

                    try
                    {
                        Marshal.StructureToPtr(currentInfo, ptr, false);
                        bool querySuccess = NativeInterop.GetProcessInformation(
                            process.Handle,
                            NativeInterop.ProcessMemoryPriority,
                            ptr,
                            size);

                        if (!querySuccess) continue;

                        currentInfo = Marshal.PtrToStructure<NativeInterop.MEMORY_PRIORITY_INFORMATION>(ptr);
                        uint originalPriority = currentInfo.MemoryPriority;

                        if (originalPriority <= 2) continue; // Already low, skip

                        // Set to Low (2)
                        var newInfo = new NativeInterop.MEMORY_PRIORITY_INFORMATION { MemoryPriority = 2 };
                        Marshal.StructureToPtr(newInfo, ptr, false);

                        if (NativeInterop.SetProcessInformation(
                            process.Handle,
                            NativeInterop.ProcessMemoryPriority,
                            ptr,
                            size))
                        {
                            lock (_memPriorityLock)
                            {
                                _demotedProcesses.Add(new MemPriorityOriginalState(
                                    process.Id, name, originalPriority));
                                _demotedPids.Add(process.Id);
                            }

                            newlyDemoted++;
                            SettingsManager.Logger.Debug(
                                "[MemoryOptimizer] Memory priority lowered: {Name} (PID {Pid}) {From} → Low",
                                name, process.Id, originalPriority);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
                catch (Exception ex)
                {
                    // Access denied on system processes is expected — skip silently
                    if (ex is not global::System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
                    {
                        SettingsManager.Logger.Debug(
                            "[MemoryOptimizer] Could not adjust memory priority for {Name}: {Error}",
                            process.ProcessName, ex.Message);
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (newlyDemoted > 0)
            {
                SettingsManager.Logger.Debug(
                    "[MemoryOptimizer] Memory priority demoted on {Count} new processes", newlyDemoted);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[MemoryOptimizer] DemoteBackgroundMemoryPriority failed");
        }
    }

    /// <summary>
    /// Restores original memory priority for all processes still running.
    /// Checks PID reuse before restoring.
    /// </summary>
    private void RestoreMemoryPriorities()
    {
        int restoredCount = 0;
        int skippedCount = 0;

        lock (_memPriorityLock)
        {
            foreach (var state in _demotedProcesses)
            {
                try
                {
                    using var process = Process.GetProcessById(state.ProcessId);

                    // PID reuse check — verify it's the same process
                    if (!process.ProcessName.Equals(state.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        skippedCount++;
                        continue;
                    }

                    var info = new NativeInterop.MEMORY_PRIORITY_INFORMATION { MemoryPriority = state.OriginalPriority };
                    int size = Marshal.SizeOf<NativeInterop.MEMORY_PRIORITY_INFORMATION>();
                    IntPtr ptr = Marshal.AllocHGlobal(size);

                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);

                        if (NativeInterop.SetProcessInformation(
                            process.Handle,
                            NativeInterop.ProcessMemoryPriority,
                            ptr,
                            size))
                        {
                            restoredCount++;
                            SettingsManager.Logger.Debug(
                                "[MemoryOptimizer] Memory priority restored: {Name} (PID {Pid}) → {Priority}",
                                state.ProcessName, state.ProcessId, state.OriginalPriority);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
                catch (ArgumentException)
                {
                    // Process no longer running — nothing to revert
                    skippedCount++;
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Debug(
                        "[MemoryOptimizer] Failed to restore memory priority for {Name}: {Error}",
                        state.ProcessName, ex.Message);
                }
            }

            _demotedProcesses.Clear();
            _demotedPids.Clear();
        }

        SettingsManager.Logger.Information(
            "[MemoryOptimizer] Memory priority revert — {Restored} restored, {Skipped} skipped",
            restoredCount, skippedCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the active game's process names from the profile.
    /// </summary>
    private static string[] ResolveGameProcessNames(GameProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.ExecutableName))
        {
            var name = Path.GetFileNameWithoutExtension(profile.ExecutableName);
            return new[] { name };
        }

        return Array.Empty<string>();
    }
}
