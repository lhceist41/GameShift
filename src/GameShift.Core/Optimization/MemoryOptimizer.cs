using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Core.Config;
using Timer = System.Threading.Timer;

namespace GameShift.Core.Optimization;

/// <summary>
/// Memory Optimizer — targeted per-session memory management.
/// On session start: demotes background processes to MEMORY_PRIORITY_VERY_LOW, trims their
/// working sets via EmptyWorkingSet, and sets a hard minimum working set on the game process.
/// During the session: rescans for new background processes every 5 seconds and applies the
/// same demotions. Purges the standby list only when both the standby threshold and the free
/// memory minimum are breached simultaneously.
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
    private bool _manageMemoryPriority;
    private bool _emptyWorkingSets;
    private int _hardMinWorkingSetMB;
    private string[] _activeGameProcessNames = Array.Empty<string>();

    private readonly List<MemPriorityOriginalState> _demotedProcesses = new();
    private readonly HashSet<int> _demotedPids = new();
    private readonly object _memPriorityLock = new();

    // Hard-min working set tracking
    private int _hardMinGamePid;
    private bool _hardMinApplied;

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
    /// Applies memory optimization: demotes background processes, trims their working sets,
    /// sets a hard minimum on the game process, then starts periodic monitoring.
    /// </summary>
    /// <param name="snapshot">Snapshot to record original state (not used — memory ops are non-destructive)</param>
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
            _manageMemoryPriority = profile.ManageMemoryPriority;
            _emptyWorkingSets = profile.EmptyWorkingSets;
            _hardMinWorkingSetMB = profile.HardMinWorkingSetMB;
            _activeGameProcessNames = ResolveGameProcessNames(profile);

            SettingsManager.Logger.Information("[MemoryOptimizer] Applying memory optimization");

            // Perform immediate standby list purge
            bool purgeSuccess = PurgeStandbyList();
            if (!purgeSuccess)
            {
                SettingsManager.Logger.Warning("[MemoryOptimizer] Initial standby list purge failed, but continuing with monitoring");
            }

            // Demote background process memory priority + empty their working sets
            if (_manageMemoryPriority)
            {
                DemoteBackgroundMemoryPriority();
                SettingsManager.Logger.Information(
                    "[MemoryOptimizer] Memory priority demoted on {Count} background processes",
                    _demotedProcesses.Count);
            }

            // Set hard minimum working set on the game process
            if (_hardMinWorkingSetMB > 0)
            {
                ApplyHardMinWorkingSet(profile.ProcessId);
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

            // Release the hard minimum working set on the game process
            if (_hardMinApplied)
            {
                ReleaseHardMinWorkingSet();
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
    /// Periodic callback: rescans for new background processes to demote, then checks memory
    /// and purges the standby list if below threshold. Called every 5 seconds.
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

    // ── Memory Priority Management ─────────────────────────────────────

    /// <summary>
    /// Lowers memory priority of targeted background processes to VERY_LOW (1) and,
    /// when EmptyWorkingSets is enabled, trims their working sets to the standby list.
    ///
    /// MEMORY_PRIORITY_VERY_LOW (1): pages become first candidates for reclamation under
    /// memory pressure. Lower than Low (2) — the old setting — so game pages are protected
    /// more aggressively.
    ///
    /// EmptyWorkingSet: moves background process pages to the standby list immediately,
    /// freeing physical RAM for the game WITHOUT destroying the game's own cached assets.
    /// Pages refault cheaply from standby on next access; this is a targeted trim, not a purge.
    ///
    /// Thread-safe via _memPriorityLock.
    /// </summary>
    private void DemoteBackgroundMemoryPriority()
    {
        const uint MemPriorityVeryLow = 1;
        int newlyDemoted = 0;
        int emptyCount = 0;

        try
        {
            foreach (var process in ProcessSnapshotService.GetProcesses())
            {
                try
                {
                    string name = process.ProcessName;

                    // Use shared targeting logic
                    if (!BackgroundProcessTargets.ShouldDemote(name, _activeGameProcessNames))
                        continue;

                    bool alreadyDemoted;
                    lock (_memPriorityLock)
                    {
                        alreadyDemoted = _demotedPids.Contains(process.Id);
                    }

                    // Technique 1: EmptyWorkingSet — runs every scan, not just on first demote,
                    // because background processes spawn new threads that fault in new pages.
                    if (_emptyWorkingSets)
                    {
                        IntPtr hEmpty = NativeInterop.OpenProcess(
                            NativeInterop.PROCESS_QUERY_INFORMATION | NativeInterop.PROCESS_SET_QUOTA,
                            false, process.Id);
                        if (hEmpty != IntPtr.Zero)
                        {
                            try
                            {
                                if (NativeInterop.EmptyWorkingSet(hEmpty))
                                    emptyCount++;
                            }
                            finally
                            {
                                NativeInterop.CloseHandle(hEmpty);
                            }
                        }
                    }

                    if (alreadyDemoted) continue; // Priority already set for this PID

                    // Technique 2: MEMORY_PRIORITY_VERY_LOW (1)
                    // Open our own handle — ProcessSnapshot no longer carries OS handles
                    IntPtr hMemPri = NativeInterop.OpenProcess(
                        NativeInterop.PROCESS_QUERY_INFORMATION | NativeInterop.PROCESS_SET_INFORMATION,
                        false, process.Id);
                    if (hMemPri == IntPtr.Zero) continue;
                    try
                    {
                        var currentInfo = new NativeInterop.MEMORY_PRIORITY_INFORMATION();
                        int size = Marshal.SizeOf<NativeInterop.MEMORY_PRIORITY_INFORMATION>();
                        IntPtr ptr = Marshal.AllocHGlobal(size);

                        try
                        {
                            Marshal.StructureToPtr(currentInfo, ptr, false);
                            bool querySuccess = NativeInterop.GetProcessInformation(
                                hMemPri,
                                NativeInterop.ProcessMemoryPriority,
                                ptr,
                                size);

                            if (!querySuccess) continue;

                            currentInfo = Marshal.PtrToStructure<NativeInterop.MEMORY_PRIORITY_INFORMATION>(ptr);
                            uint originalPriority = currentInfo.MemoryPriority;

                            if (originalPriority <= MemPriorityVeryLow) continue; // Already at or below target

                            var newInfo = new NativeInterop.MEMORY_PRIORITY_INFORMATION { MemoryPriority = MemPriorityVeryLow };
                            Marshal.StructureToPtr(newInfo, ptr, false);

                            if (NativeInterop.SetProcessInformation(
                                hMemPri,
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
                                    "[MemoryOptimizer] Memory priority VERY_LOW: {Name} (PID {Pid}) {From} → 1",
                                    name, process.Id, originalPriority);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(ptr);
                        }
                    }
                    finally
                    {
                        NativeInterop.CloseHandle(hMemPri);
                    }
                }
                catch (Exception ex)
                {
                    // Access denied on system processes is expected — skip silently
                    if (ex is not global::System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
                    {
                        SettingsManager.Logger.Debug(
                            "[MemoryOptimizer] Could not demote {Name}: {Error}",
                            process.ProcessName, ex.Message);
                    }
                }
                finally
                {
                    // Process objects are owned by ProcessSnapshotService cache — do not dispose
                }
            }

            if (newlyDemoted > 0 || emptyCount > 0)
            {
                SettingsManager.Logger.Debug(
                    "[MemoryOptimizer] Background process demote — {Demoted} priority set to VERY_LOW, {Empty} working sets trimmed",
                    newlyDemoted, emptyCount);
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

    // ── Hard minimum working set ─────────────────────────────────────────

    /// <summary>
    /// Sets a hard minimum working set on the game process.
    /// The memory manager will not trim game pages below HardMinWorkingSetMB.
    /// Uses QUOTA_LIMITS_HARDWS_MIN_ENABLE so the limit is enforced even under pressure.
    /// No-ops if the process handle cannot be opened (e.g., process already exited).
    /// </summary>
    private void ApplyHardMinWorkingSet(int gamePid)
    {
        if (gamePid <= 0) return;

        IntPtr handle = NativeInterop.OpenProcess(
            NativeInterop.PROCESS_QUERY_INFORMATION | NativeInterop.PROCESS_SET_QUOTA,
            false, gamePid);

        if (handle == IntPtr.Zero)
        {
            SettingsManager.Logger.Debug(
                "[MemoryOptimizer] Could not open game process PID {Pid} for hard-min working set", gamePid);
            return;
        }

        try
        {
            long minBytes = (long)_hardMinWorkingSetMB * 1024 * 1024;

            bool ok = NativeInterop.SetProcessWorkingSetSizeEx(
                handle,
                (IntPtr)minBytes,
                (IntPtr)(-1),                          // no upper limit
                NativeInterop.QUOTA_LIMITS_HARDWS_MIN_ENABLE);

            if (ok)
            {
                _hardMinGamePid = gamePid;
                _hardMinApplied = true;
                SettingsManager.Logger.Information(
                    "[MemoryOptimizer] Hard minimum working set set to {MB}MB on game PID {Pid}",
                    _hardMinWorkingSetMB, gamePid);
            }
            else
            {
                SettingsManager.Logger.Warning(
                    "[MemoryOptimizer] SetProcessWorkingSetSizeEx failed for PID {Pid} — error {Error}",
                    gamePid, Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            NativeInterop.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Clears the hard minimum working set on the game process.
    /// Passes QUOTA_LIMITS_HARDWS_MIN_DISABLE so the OS can resume normal trimming.
    /// </summary>
    private void ReleaseHardMinWorkingSet()
    {
        if (!_hardMinApplied || _hardMinGamePid <= 0) return;

        IntPtr handle = NativeInterop.OpenProcess(
            NativeInterop.PROCESS_QUERY_INFORMATION | NativeInterop.PROCESS_SET_QUOTA,
            false, _hardMinGamePid);

        if (handle == IntPtr.Zero)
        {
            // Process already exited — nothing to revert
            _hardMinApplied = false;
            _hardMinGamePid = 0;
            return;
        }

        try
        {
            NativeInterop.SetProcessWorkingSetSizeEx(
                handle,
                (IntPtr)(-1),
                (IntPtr)(-1),
                NativeInterop.QUOTA_LIMITS_HARDWS_MIN_DISABLE);

            SettingsManager.Logger.Information(
                "[MemoryOptimizer] Hard minimum working set cleared on game PID {Pid}", _hardMinGamePid);
        }
        finally
        {
            NativeInterop.CloseHandle(handle);
            _hardMinApplied = false;
            _hardMinGamePid = 0;
        }
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
