using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Sets Windows system timer resolution to 0.5ms for reduced input latency.
/// Manages system timer resolution for reduced input latency during gameplay.
/// </summary>
public class TimerResolutionManager : IOptimization
{
    /// <summary>
    /// Registry key path for Windows 11 24H2+ global timer resolution workaround.
    /// </summary>
    private const string GlobalTimerResolutionKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string GlobalTimerResolutionValueName = "GlobalTimerResolutionRequests";

    public string Name => "System Timer Resolution Manager";

    public string Description => "Sets system timer to 0.5ms for reduced input latency and smoother frame delivery";

    public bool IsApplied { get; private set; }

    /// <summary>
    /// Checks if timer resolution can be queried.
    /// Always returns true as NtQueryTimerResolution is available on all modern Windows versions.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                int result = NativeInterop.NtQueryTimerResolution(
                    out _,
                    out _,
                    out _);

                return result == 0; // STATUS_SUCCESS
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Sets system timer resolution to 0.5ms (5000 in 100-nanosecond units).
    /// Records original resolution in snapshot before changing.
    /// Also sets Win11 24H2+ registry workaround if needed.
    /// </summary>
    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        await Task.CompletedTask; // Make async to satisfy interface

        try
        {
            // Query current timer resolution
            int queryResult = NativeInterop.NtQueryTimerResolution(
                out int minResolution,
                out int maxResolution,
                out int currentResolution);

            if (queryResult != 0)
            {
                SettingsManager.Logger.Error(
                    "TimerResolutionManager: NtQueryTimerResolution failed with NTSTATUS {Status}",
                    queryResult);
                return false;
            }

            SettingsManager.Logger.Information(
                "TimerResolutionManager: Current timer resolution: {Current} (min: {Min}, max: {Max}) in 100ns units",
                currentResolution,
                minResolution,
                maxResolution);

            // Record original resolution
            snapshot.RecordTimerResolution(currentResolution);

            // Check if Background Mode is handling timer resolution
            var bgSettings = SettingsManager.Load();
            if (bgSettings.BackgroundMode?.Enabled == true && bgSettings.BackgroundMode.TimerResolutionEnabled)
            {
                SettingsManager.Logger.Information(
                    "TimerResolutionManager: Background Mode active — recording snapshot but skipping set");
                IsApplied = true;
                return true;
            }

            // TODO: Read from AppSettings.TimerResolution100ns when DI is set up
            // For now, hardcode to 5000 (0.5ms in 100-nanosecond units)
            int desiredResolution = 5000;

            // Set new timer resolution
            int setResult = NativeInterop.NtSetTimerResolution(
                desiredResolution,
                true, // Request the new resolution
                out int actualResolution);

            if (setResult != 0)
            {
                SettingsManager.Logger.Error(
                    "TimerResolutionManager: NtSetTimerResolution failed with NTSTATUS {Status}",
                    setResult);
                return false;
            }

            SettingsManager.Logger.Information(
                "TimerResolutionManager: Set timer resolution to {Actual} (requested {Desired}) in 100ns units ({ActualMs}ms)",
                actualResolution,
                desiredResolution,
                actualResolution / 10000.0);

            // Win11 24H2+ registry workaround
            // Some Windows 11 24H2 builds require a registry key to honor timer resolution changes
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(GlobalTimerResolutionKeyPath, writable: true);
                if (key != null)
                {
                    // Record original value if it exists
                    var originalValue = key.GetValue(GlobalTimerResolutionValueName);
                    if (originalValue != null)
                    {
                        snapshot.RecordRegistryValue(
                            $@"HKEY_LOCAL_MACHINE\{GlobalTimerResolutionKeyPath}",
                            GlobalTimerResolutionValueName,
                            originalValue);

                        SettingsManager.Logger.Debug(
                            "TimerResolutionManager: Recorded original GlobalTimerResolutionRequests value: {Value}",
                            originalValue);
                    }

                    // Set to 1 (enabled)
                    key.SetValue(GlobalTimerResolutionValueName, 1, RegistryValueKind.DWord);

                    SettingsManager.Logger.Information(
                        "TimerResolutionManager: Set Win11 24H2 GlobalTimerResolutionRequests registry key");
                }
                else
                {
                    SettingsManager.Logger.Debug(
                        "TimerResolutionManager: Registry key for Win11 24H2 workaround not accessible (may not be needed)");
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Registry access denied - not fatal, workaround only needed on some systems
                SettingsManager.Logger.Debug(
                    "TimerResolutionManager: Could not access GlobalTimerResolutionRequests registry key (access denied)");
            }
            catch (Exception ex)
            {
                // Other registry errors - not fatal
                SettingsManager.Logger.Warning(
                    ex,
                    "TimerResolutionManager: Failed to set Win11 24H2 registry workaround");
            }

            IsApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "TimerResolutionManager: Failed to apply timer resolution");
            return false;
        }
    }

    /// <summary>
    /// Reverts timer resolution to the original value captured in the snapshot.
    /// Also restores the Win11 24H2 registry key if it was modified.
    /// </summary>
    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        await Task.CompletedTask; // Make async to satisfy interface

        try
        {
            // Restore original timer resolution
            int setResult = NativeInterop.NtSetTimerResolution(
                snapshot.TimerResolution,
                false, // Release the resolution request
                out int actualResolution);

            if (setResult == 0)
            {
                SettingsManager.Logger.Information(
                    "TimerResolutionManager: Reverted timer resolution to {Original} (actual: {Actual}) in 100ns units",
                    snapshot.TimerResolution,
                    actualResolution);
            }
            else
            {
                SettingsManager.Logger.Warning(
                    "TimerResolutionManager: NtSetTimerResolution revert returned NTSTATUS {Status}",
                    setResult);
            }

            // Restore Win11 24H2 registry key if it was modified
            string registryKey = $@"HKEY_LOCAL_MACHINE\{GlobalTimerResolutionKeyPath}\{GlobalTimerResolutionValueName}";
            if (snapshot.RegistryValues.TryGetValue(registryKey, out var originalValue))
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(GlobalTimerResolutionKeyPath, writable: true);
                    if (key != null)
                    {
                        key.SetValue(GlobalTimerResolutionValueName, originalValue);

                        SettingsManager.Logger.Information(
                            "TimerResolutionManager: Restored GlobalTimerResolutionRequests to original value: {Value}",
                            originalValue);
                    }
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(
                        ex,
                        "TimerResolutionManager: Failed to restore Win11 24H2 registry key");
                }
            }
            else
            {
                // No original value recorded - try to delete the key we created
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(GlobalTimerResolutionKeyPath, writable: true);
                    if (key != null && key.GetValue(GlobalTimerResolutionValueName) != null)
                    {
                        key.DeleteValue(GlobalTimerResolutionValueName, throwOnMissingValue: false);

                        SettingsManager.Logger.Debug(
                            "TimerResolutionManager: Removed GlobalTimerResolutionRequests registry value");
                    }
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(
                        ex,
                        "TimerResolutionManager: Failed to remove Win11 24H2 registry key");
                }
            }

            IsApplied = false;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "TimerResolutionManager: Failed to revert timer resolution");
            IsApplied = false;
            return false;
        }
    }
}
