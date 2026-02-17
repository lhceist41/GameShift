using System.Diagnostics;
using GameShift.Core.Profiles;
using GameShift.Core.Config;
using GameShift.Core.System;

namespace GameShift.Core.Optimization;

/// <summary>
/// Sets game process priority to High for better CPU scheduling.
/// Boosts game process priority for better CPU scheduling during gameplay.
/// </summary>
public class ProcessPriorityBooster : IOptimization
{
    private int _boostedProcessId;

    public string Name => "Process Priority Booster";

    public string Description => "Sets game process priority to High for better CPU scheduling";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // Any process can have its priority changed

    /// <summary>
    /// Applies High priority to the game process.
    /// Records original priority in snapshot before changing.
    /// </summary>
    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        await Task.CompletedTask; // Make async to satisfy interface

        try
        {
            if (profile.ProcessId <= 0)
            {
                SettingsManager.Logger.Warning("ProcessPriorityBooster: No valid process ID in profile");
                return false;
            }

            Process? process;
            try
            {
                process = Process.GetProcessById(profile.ProcessId);
            }
            catch (ArgumentException)
            {
                SettingsManager.Logger.Warning(
                    "ProcessPriorityBooster: Game process {ProcessId} not found — may have exited",
                    profile.ProcessId);
                return false;
            }

            // Record original priority before changing
            var originalPriority = process.PriorityClass;
            snapshot.RecordProcessPriority(profile.ProcessId, originalPriority);

            // Set to High priority (NOT Realtime - per PRD decision)
            process.PriorityClass = ProcessPriorityClass.High;

            SettingsManager.Logger.Information(
                "ProcessPriorityBooster: Set process {ProcessName} (PID: {ProcessId}) priority from {Original} to High",
                process.ProcessName,
                profile.ProcessId,
                originalPriority);

            _boostedProcessId = profile.ProcessId;
            IsApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "ProcessPriorityBooster: Failed to apply priority boost");
            return false;
        }
    }

    /// <summary>
    /// Reverts process priority to original value.
    /// Handles case where process has already exited gracefully.
    /// </summary>
    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        await Task.CompletedTask; // Make async to satisfy interface

        try
        {
            if (_boostedProcessId <= 0)
            {
                return true; // Nothing to revert
            }

            // Check if we have the recorded original priority
            if (!snapshot.ProcessPriorities.TryGetValue(_boostedProcessId, out var originalPriority))
            {
                SettingsManager.Logger.Warning(
                    "ProcessPriorityBooster: No recorded priority for PID {ProcessId}",
                    _boostedProcessId);
                return true; // Not a fatal error
            }

            Process? process;
            try
            {
                process = Process.GetProcessById(_boostedProcessId);
            }
            catch (ArgumentException)
            {
                SettingsManager.Logger.Information(
                    "ProcessPriorityBooster: Process {ProcessId} already exited, no revert needed",
                    _boostedProcessId);
                IsApplied = false;
                return true; // Clean exit - process is gone, nothing to revert
            }

            // Restore original priority
            process.PriorityClass = originalPriority;

            SettingsManager.Logger.Information(
                "ProcessPriorityBooster: Reverted process {ProcessName} priority to {Original}",
                process.ProcessName,
                originalPriority);

            IsApplied = false;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "ProcessPriorityBooster: Failed to revert priority");
            IsApplied = false; // Mark as not applied even on failure
            return false;
        }
    }
}
