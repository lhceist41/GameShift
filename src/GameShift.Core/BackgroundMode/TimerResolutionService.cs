using GameShift.Core.Config;
using GameShift.Core.System;
using Microsoft.Win32;

namespace GameShift.Core.BackgroundMode;

/// <summary>
/// Always-on timer resolution lock. Sets Windows system timer to 0.5ms
/// and keeps it locked 24/7. Includes Win11 24H2 registry workaround.
/// </summary>
public class TimerResolutionService : IDisposable
{
    private const string GlobalTimerResolutionKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string GlobalTimerResolutionValueName = "GlobalTimerResolutionRequests";

    private bool _isLocked;
    private int _originalResolution;
    private int _appliedResolution;

    public bool IsLocked => _isLocked;

    /// <summary>
    /// Locks the system timer resolution to the configured value.
    /// </summary>
    public void Start(BackgroundModeSettings settings)
    {
        if (_isLocked) return;

        try
        {
            // Query and save original resolution
            int queryResult = NativeInterop.NtQueryTimerResolution(
                out _, out _, out int currentResolution);
            if (queryResult == 0)
                _originalResolution = currentResolution;

            // Set desired resolution
            int desired = settings.TimerResolution100ns;
            int setResult = NativeInterop.NtSetTimerResolution(desired, true, out int actual);

            if (setResult != 0)
            {
                SettingsManager.Logger.Error(
                    "[TimerResolutionService] NtSetTimerResolution failed NTSTATUS {Status}", setResult);
                return;
            }

            _appliedResolution = actual;

            SettingsManager.Logger.Information(
                "[TimerResolutionService] Locked timer to {Actual} (requested {Desired}) = {Ms}ms",
                actual, desired, actual / 10000.0);

            // Win11 24H2 registry workaround
            ApplyRegistryWorkaround();

            _isLocked = true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[TimerResolutionService] Failed to start");
        }
    }

    /// <summary>
    /// Releases the timer resolution lock and restores the original value.
    /// </summary>
    public void Stop()
    {
        if (!_isLocked) return;

        try
        {
            NativeInterop.NtSetTimerResolution(_appliedResolution, false, out _);
            SettingsManager.Logger.Information("[TimerResolutionService] Released timer lock");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[TimerResolutionService] Failed to release timer lock");
        }

        _isLocked = false;
    }

    /// <summary>
    /// Gets the current actual timer resolution in 100ns units.
    /// </summary>
    public int GetCurrentResolution()
    {
        NativeInterop.NtQueryTimerResolution(out _, out _, out int current);
        return current;
    }

    private void ApplyRegistryWorkaround()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GlobalTimerResolutionKeyPath, writable: true);
            if (key != null)
            {
                key.SetValue(GlobalTimerResolutionValueName, 1, RegistryValueKind.DWord);
                SettingsManager.Logger.Debug("[TimerResolutionService] Set Win11 24H2 registry workaround");
            }
        }
        catch (UnauthorizedAccessException)
        {
            SettingsManager.Logger.Debug("[TimerResolutionService] Registry workaround access denied (may not be needed)");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[TimerResolutionService] Registry workaround failed");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
