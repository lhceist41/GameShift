using System.Runtime.InteropServices;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Core.Config;
using Timer = System.Threading.Timer;

namespace GameShift.Core.Optimization;

/// <summary>
/// Memory Optimizer - Purges standby list when free memory drops below threshold.
/// Monitors available physical memory every 5 seconds during gaming and purges cached memory
/// from the standby list when available RAM falls below configured threshold.
/// </summary>
public class MemoryOptimizer : IOptimization
{
    private Timer? _monitorTimer;
    private int _thresholdMB = 1024; // Default, overridden by profile in ApplyAsync
    private volatile bool _isMonitoring;
    private bool _isApplied;

    /// <inheritdoc/>
    public string Name => "Memory Optimizer";

    /// <inheritdoc/>
    public string Description => "Purges standby list when free memory drops below threshold";

    /// <inheritdoc/>
    public bool IsApplied => _isApplied;

    /// <inheritdoc/>
    public bool IsAvailable => true; // Memory purge works on all Windows versions with admin rights

    /// <summary>
    /// Applies memory optimization by immediately purging standby list and starting periodic monitoring.
    /// Monitors available RAM every 5 seconds and purges when below threshold.
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

            // Check if Background Mode is handling memory management
            var bgSettings = SettingsManager.Load();
            if (bgSettings.BackgroundMode?.Enabled == true && bgSettings.BackgroundMode.StandbyListCleanerEnabled)
            {
                SettingsManager.Logger.Information(
                    "[MemoryOptimizer] Background Mode active — recording snapshot but skipping start");
                _isApplied = true;
                return Task.FromResult(true);
            }

            SettingsManager.Logger.Information("[MemoryOptimizer] Applying memory optimization");

            // Perform immediate standby list purge
            bool purgeSuccess = PurgeStandbyList();
            if (!purgeSuccess)
            {
                SettingsManager.Logger.Warning("[MemoryOptimizer] Initial standby list purge failed, but continuing with monitoring");
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
    /// Reverts memory optimization by stopping the monitoring timer.
    /// No system state to revert - standby list purge is non-destructive (refills naturally).
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
                    "[MemoryOptimizer] Available memory ({AvailableMB}MB) below threshold ({ThresholdMB}MB), purging standby list",
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
}
