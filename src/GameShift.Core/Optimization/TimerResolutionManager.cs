using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Sets Windows system timer resolution to 0.5ms for reduced input latency.
/// Manages system timer resolution for reduced input latency during gameplay.
/// On Windows 11, also sets GlobalTimerResolutionRequests to ensure system-wide effect.
/// </summary>
public class TimerResolutionManager : IOptimization
{
    /// <summary>
    /// Registry key path for Windows 11 global timer resolution workaround.
    /// On Win11, NtSetTimerResolution only affects the calling process by default.
    /// Setting GlobalTimerResolutionRequests=1 restores Windows 10 behavior where
    /// the highest requested resolution applies system-wide.
    /// </summary>
    private const string GlobalTimerResolutionKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string GlobalTimerResolutionValueName = "GlobalTimerResolutionRequests";

    /// <summary>
    /// Tracks whether this instance set the GlobalTimerResolutionRequests key,
    /// and whether the key existed before we set it (for proper cleanup on revert).
    /// </summary>
    private bool _globalTimerKeyExistedBefore;
    private object? _globalTimerOriginalValue;
    private bool _globalTimerKeyWasSet;

    public const string OptimizationId = "System Timer Resolution Manager";

    public string Name => OptimizationId;

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
    /// On Win11: sets GlobalTimerResolutionRequests BEFORE calling NtSetTimerResolution.
    /// On Win10: skips GlobalTimerResolutionRequests entirely (not needed — Win10 always uses global).
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
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
                return Task.FromResult(false);
            }

            SettingsManager.Logger.Information(
                "TimerResolutionManager: Current timer resolution: {Current} (min: {Min}, max: {Max}) in 100ns units",
                currentResolution,
                minResolution,
                maxResolution);

            // Record original resolution
            snapshot.RecordTimerResolution(currentResolution);

            // Win11 GlobalTimerResolutionRequests — MUST be set BEFORE NtSetTimerResolution
            // This is a prerequisite for both session timer and Background Mode timer to work globally.
            // Only relevant on Win11 (build >= 22000) — Win10 always uses global timer resolution.
            ApplyGlobalTimerRegistryKey();

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
                return Task.FromResult(false);
            }

            SettingsManager.Logger.Information(
                "TimerResolutionManager: Set timer resolution to {Actual} (requested {Desired}) in 100ns units ({ActualMs}ms)",
                actualResolution,
                desiredResolution,
                actualResolution / 10000.0);

            IsApplied = true;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "TimerResolutionManager: Failed to apply timer resolution");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Reverts timer resolution to the original value captured in the snapshot.
    /// Also restores or deletes the Win11 GlobalTimerResolutionRequests key.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
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

            // Revert Win11 GlobalTimerResolutionRequests registry key
            RevertGlobalTimerRegistryKey();

            IsApplied = false;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "TimerResolutionManager: Failed to revert timer resolution");
            IsApplied = false;
            return Task.FromResult(false);
        }
    }

    // ── Win11 GlobalTimerResolutionRequests helpers ──────────────────────────

    /// <summary>
    /// Sets GlobalTimerResolutionRequests=1 on Win11 to enable system-wide timer resolution.
    /// On Win10, this is a no-op (Win10 always uses global timer resolution).
    /// Records whether the key existed before for proper cleanup on revert.
    /// </summary>
    private void ApplyGlobalTimerRegistryKey()
    {
        int build = Environment.OSVersion.Version.Build;
        bool isWin11 = build >= 22000;

        if (!isWin11)
        {
            // Win10 always uses global timer resolution — no registry key needed
            SettingsManager.Logger.Debug(
                "TimerResolutionManager: Win10 detected (build {Build}) — skipping GlobalTimerResolutionRequests",
                build);
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GlobalTimerResolutionKeyPath, writable: true);
            if (key == null)
            {
                SettingsManager.Logger.Debug(
                    "TimerResolutionManager: Registry key for GlobalTimerResolutionRequests not accessible");
                return;
            }

            // Record original state
            var existingValue = key.GetValue(GlobalTimerResolutionValueName);
            _globalTimerKeyExistedBefore = existingValue != null;
            _globalTimerOriginalValue = existingValue;

            // Set to 1 (enabled — global timer resolution behavior)
            key.SetValue(GlobalTimerResolutionValueName, 1, RegistryValueKind.DWord);
            _globalTimerKeyWasSet = true;

            SettingsManager.Logger.Information(
                "TimerResolutionManager: Set GlobalTimerResolutionRequests=1 (original: {Original})",
                existingValue ?? "not present");

            // Log reboot advisory for older Win11 builds
            bool is24H2OrNewer = build >= 26100;
            if (!is24H2OrNewer)
            {
                SettingsManager.Logger.Information(
                    "TimerResolutionManager: GlobalTimerResolutionRequests set. " +
                    "A reboot may be required for system-wide timer resolution on this Windows version (build {Build}).",
                    build);
            }
        }
        catch (UnauthorizedAccessException)
        {
            SettingsManager.Logger.Debug(
                "TimerResolutionManager: Could not access GlobalTimerResolutionRequests registry key (access denied)");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex,
                "TimerResolutionManager: Failed to set GlobalTimerResolutionRequests");
        }
    }

    /// <summary>
    /// Reverts GlobalTimerResolutionRequests to its original state.
    /// If the key didn't exist before → delete it (don't leave as 0).
    /// If the key existed → restore original value.
    /// </summary>
    private void RevertGlobalTimerRegistryKey()
    {
        if (!_globalTimerKeyWasSet) return;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GlobalTimerResolutionKeyPath, writable: true);
            if (key == null) return;

            if (!_globalTimerKeyExistedBefore)
            {
                // Key didn't exist before — delete it entirely
                key.DeleteValue(GlobalTimerResolutionValueName, throwOnMissingValue: false);
                SettingsManager.Logger.Information(
                    "TimerResolutionManager: Deleted GlobalTimerResolutionRequests (was not present before)");
            }
            else
            {
                // Key existed — restore original value
                key.SetValue(GlobalTimerResolutionValueName, _globalTimerOriginalValue!, RegistryValueKind.DWord);
                SettingsManager.Logger.Information(
                    "TimerResolutionManager: Restored GlobalTimerResolutionRequests to original value: {Value}",
                    _globalTimerOriginalValue);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex,
                "TimerResolutionManager: Failed to revert GlobalTimerResolutionRequests");
        }

        _globalTimerKeyWasSet = false;
    }
}
