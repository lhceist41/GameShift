using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
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
public class TimerResolutionManager : IOptimization, IJournaledOptimization
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

    /// <summary>
    /// Win11 dedicated timer thread - keeps NtSetTimerResolution off the UI thread
    /// to prevent resolution revert when the GameShift window is minimized.
    /// </summary>
    private Thread? _timerThread;
    private CancellationTokenSource? _timerCts;
    private int _appliedResolution;
    private bool _usingDedicatedThread;

    // Context stored by CanApply() for the journaled Apply() path.
    private SystemContext? _context;

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
    /// On Win11: sets GlobalTimerResolutionRequests BEFORE calling NtSetTimerResolution,
    /// then keeps the resolution active on a dedicated windowless background thread.
    /// On Win10: skips GlobalTimerResolutionRequests entirely (not needed - Win10 always uses global).
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        CanApply(new SystemContext { Profile = profile, Snapshot = snapshot });
        var result = Apply();
        return Task.FromResult(result.State == OptimizationState.Applied);
    }

    // ── IJournaledOptimization ────────────────────────────────────────────────

    /// <summary>Pre-flight check; stores context for Apply(). Always applies when available.</summary>
    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    /// <summary>
    /// Sets the system timer resolution and (on Win11) GlobalTimerResolutionRequests. The returned
    /// OriginalValue JSON carries ONLY the persistent registry state - the timer-resolution request
    /// is process-scoped and self-resets if the process dies, so the watchdog never restores it.
    /// </summary>
    public OptimizationResult Apply()
    {
        var snapshot = _context?.Snapshot;
        var profile = _context?.Profile;

        try
        {
            int queryResult = NativeInterop.NtQueryTimerResolution(
                out int minResolution,
                out int maxResolution,
                out int currentResolution);

            if (queryResult != 0)
            {
                SettingsManager.Logger.Error(
                    "[TimerResolutionManager] NtQueryTimerResolution failed with NTSTATUS {Status}", queryResult);
                return Fail($"NtQueryTimerResolution failed ({queryResult})");
            }

            SettingsManager.Logger.Information(
                "[TimerResolutionManager] Current timer resolution: {Current} (min: {Min}, max: {Max}) in 100ns units",
                currentResolution, minResolution, maxResolution);

            snapshot?.RecordTimerResolution(currentResolution);

            // Win11 GlobalTimerResolutionRequests - the only persistent (reboot-surviving) state.
            ApplyGlobalTimerRegistryKey();

            var originalState = new Dictionary<string, object?>();
            if (_globalTimerKeyWasSet)
                originalState[GlobalTimerResolutionValueName] =
                    _globalTimerKeyExistedBefore ? _globalTimerOriginalValue : null;

            int desiredResolution = profile?.Intensity == OptimizationIntensity.Competitive ? 5000 : 10000;
            int build = GetWindowsBuildNumber();
            bool isWin11 = build >= 22000;

            bool success = isWin11
                ? ApplyOnDedicatedThread(desiredResolution).Result
                : ApplyDirect(desiredResolution).Result;

            if (!success)
                return Fail("NtSetTimerResolution failed");

            return new OptimizationResult(
                OptimizationId,
                JsonSerializer.Serialize(originalState),
                JsonSerializer.Serialize(new { applied = desiredResolution }),
                OptimizationState.Applied);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[TimerResolutionManager] Failed to apply timer resolution");
            return Fail(ex.Message);
        }
    }

    private Task<bool> ApplyOnDedicatedThread(int desiredResolution)
    {
        var cts = new CancellationTokenSource();
        _timerCts = cts;
        var token = cts.Token;

        var setDone = new ManualResetEventSlim(false);
        bool setSuccess = false;
        int actualResolution = 0;

        _timerThread = new Thread(() =>
        {
            int setResult = NativeInterop.NtSetTimerResolution(desiredResolution, true, out int actual);
            actualResolution = actual;
            setSuccess = setResult == 0;
            setDone.Set();

            if (setSuccess)
            {
                while (!token.IsCancellationRequested)
                    Thread.Sleep(500);

                // Release the resolution request from the same thread that made it
                NativeInterop.NtSetTimerResolution(desiredResolution, false, out _);
            }
        })
        {
            IsBackground = true,
            Name = "GameShift_TimerResolution",
            Priority = ThreadPriority.AboveNormal
        };

        _timerThread.Start();
        setDone.Wait();

        if (!setSuccess)
        {
            SettingsManager.Logger.Error(
                "[TimerResolutionManager] NtSetTimerResolution failed on dedicated thread");
            cts.Cancel();
            return Task.FromResult(false);
        }

        _appliedResolution = desiredResolution;
        _usingDedicatedThread = true;

        VerifyTimerResolution(desiredResolution, actualResolution);

        IsApplied = true;
        return Task.FromResult(true);
    }

    private Task<bool> ApplyDirect(int desiredResolution)
    {
        int setResult = NativeInterop.NtSetTimerResolution(desiredResolution, true, out int actualResolution);

        if (setResult != 0)
        {
            SettingsManager.Logger.Error(
                "[TimerResolutionManager] NtSetTimerResolution failed with NTSTATUS {Status}",
                setResult);
            return Task.FromResult(false);
        }

        _appliedResolution = desiredResolution;
        _usingDedicatedThread = false;

        VerifyTimerResolution(desiredResolution, actualResolution);

        IsApplied = true;
        return Task.FromResult(true);
    }

    private void VerifyTimerResolution(int desiredResolution, int actualResolution)
    {
        SettingsManager.Logger.Information(
            "[TimerResolutionManager] Set timer resolution to {Actual} (requested {Desired}) in 100ns units ({ActualMs}ms)",
            actualResolution,
            desiredResolution,
            actualResolution / 10000.0);

        // Post-apply verification via NtQueryTimerResolution
        int verifyResult = NativeInterop.NtQueryTimerResolution(out _, out _, out int currentResolution);
        if (verifyResult == 0)
        {
            if (currentResolution <= desiredResolution)
            {
                SettingsManager.Logger.Debug(
                    "[TimerResolutionManager] Verified system timer resolution: {Current} in 100ns units",
                    currentResolution);
            }
            else
            {
                SettingsManager.Logger.Warning(
                    "[TimerResolutionManager] System timer resolution {Current} is coarser than requested {Desired} - " +
                    "another process may be holding a higher (coarser) request.",
                    currentResolution,
                    desiredResolution);
            }
        }
    }

    /// <summary>
    /// Reverts timer resolution to the original value captured in the snapshot.
    /// On Win11: cancels the dedicated thread (which releases the timer request) then restores the registry key.
    /// On Win10: releases the timer request directly then restores the registry key.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        if (!IsApplied) return Task.FromResult(true);
        var result = Revert();
        return Task.FromResult(result.State == OptimizationState.Reverted);
    }

    /// <summary>
    /// Reverts the live timer-resolution request (releasing the dedicated thread / direct request)
    /// and restores the Win11 GlobalTimerResolutionRequests registry value.
    /// </summary>
    public OptimizationResult Revert()
    {
        try
        {
            if (_usingDedicatedThread)
            {
                _timerCts?.Cancel();
                _timerThread?.Join(TimeSpan.FromSeconds(2));
                _timerThread = null;
                _timerCts?.Dispose();
                _timerCts = null;
                _usingDedicatedThread = false;

                SettingsManager.Logger.Information(
                    "[TimerResolutionManager] Reverted timer resolution - released {Resolution} via dedicated thread",
                    _appliedResolution);
            }
            else
            {
                int setResult = NativeInterop.NtSetTimerResolution(_appliedResolution, false, out int actualResolution);
                if (setResult == 0)
                    SettingsManager.Logger.Information(
                        "[TimerResolutionManager] Reverted timer resolution (released {Applied}, actual: {Actual}) in 100ns units",
                        _appliedResolution, actualResolution);
                else
                    SettingsManager.Logger.Warning(
                        "[TimerResolutionManager] NtSetTimerResolution revert returned NTSTATUS {Status}", setResult);
            }

            RevertGlobalTimerRegistryKey();

            IsApplied = false;
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[TimerResolutionManager] Failed to revert timer resolution");
            IsApplied = false;
            return RevertFail(ex.Message);
        }
    }

    /// <summary>Confirms the timer resolution request is still in effect.</summary>
    public bool Verify()
    {
        if (!IsApplied) return false;
        try
        {
            int r = NativeInterop.NtQueryTimerResolution(out _, out _, out int current);
            return r == 0 && current <= _appliedResolution;
        }
        catch { return false; }
    }

    /// <summary>
    /// Watchdog recovery: the process-scoped timer request is already gone with the crashed
    /// process; only the persistent GlobalTimerResolutionRequests registry value is restored here.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalValueJson);
            if (values == null)
                return RevertFail("Failed to parse originalValueJson");

            if (values.TryGetValue(GlobalTimerResolutionValueName, out var el))
            {
                using var key = Registry.LocalMachine.OpenSubKey(GlobalTimerResolutionKeyPath, writable: true);
                if (key != null)
                {
                    if (el.ValueKind == JsonValueKind.Null)
                    {
                        key.DeleteValue(GlobalTimerResolutionValueName, throwOnMissingValue: false);
                        SettingsManager.Logger.Information(
                            "[TimerResolutionManager] Deleted GlobalTimerResolutionRequests (was absent before session)");
                    }
                    else if (el.ValueKind == JsonValueKind.Number)
                    {
                        key.SetValue(GlobalTimerResolutionValueName, el.GetInt32(), RegistryValueKind.DWord);
                        SettingsManager.Logger.Information(
                            "[TimerResolutionManager] Restored GlobalTimerResolutionRequests = {Value}", el.GetInt32());
                    }
                }
            }

            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[TimerResolutionManager] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    // ── Win11 GlobalTimerResolutionRequests helpers ──────────────────────────

    /// <summary>
    /// Sets GlobalTimerResolutionRequests=1 on Win11 to enable system-wide timer resolution.
    /// On Win10, this is a no-op (Win10 always uses global timer resolution).
    /// Records whether the key existed before for proper cleanup on revert.
    /// </summary>
    private void ApplyGlobalTimerRegistryKey()
    {
        int build = GetWindowsBuildNumber();
        bool isWin11 = build >= 22000;

        if (!isWin11)
        {
            // Win10 always uses global timer resolution - no registry key needed
            SettingsManager.Logger.Debug(
                "[TimerResolutionManager] Win10 detected (build {Build}) - skipping GlobalTimerResolutionRequests",
                build);
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GlobalTimerResolutionKeyPath, writable: true);
            if (key == null)
            {
                SettingsManager.Logger.Debug(
                    "[TimerResolutionManager] Registry key for GlobalTimerResolutionRequests not accessible");
                return;
            }

            // Record original state
            var existingValue = key.GetValue(GlobalTimerResolutionValueName);
            _globalTimerKeyExistedBefore = existingValue != null;
            _globalTimerOriginalValue = existingValue;

            // Set to 1 (enabled - global timer resolution behavior)
            key.SetValue(GlobalTimerResolutionValueName, 1, RegistryValueKind.DWord);
            _globalTimerKeyWasSet = true;

            SettingsManager.Logger.Information(
                "[TimerResolutionManager] Set GlobalTimerResolutionRequests=1 (original: {Original})",
                existingValue ?? "not present");

            // Log reboot advisory for older Win11 builds
            bool is24H2OrNewer = build >= 26100;
            if (!is24H2OrNewer)
            {
                SettingsManager.Logger.Information(
                    "[TimerResolutionManager] GlobalTimerResolutionRequests set. " +
                    "A reboot may be required for system-wide timer resolution on this Windows version (build {Build}).",
                    build);
            }
        }
        catch (UnauthorizedAccessException)
        {
            SettingsManager.Logger.Debug(
                "[TimerResolutionManager] Could not access GlobalTimerResolutionRequests registry key (access denied)");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex,
                "[TimerResolutionManager] Failed to set GlobalTimerResolutionRequests");
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
                // Key didn't exist before - delete it entirely
                key.DeleteValue(GlobalTimerResolutionValueName, throwOnMissingValue: false);
                SettingsManager.Logger.Information(
                    "[TimerResolutionManager] Deleted GlobalTimerResolutionRequests (was not present before)");
            }
            else
            {
                // Key existed - restore original value
                key.SetValue(GlobalTimerResolutionValueName, _globalTimerOriginalValue!, RegistryValueKind.DWord);
                SettingsManager.Logger.Information(
                    "[TimerResolutionManager] Restored GlobalTimerResolutionRequests to original value: {Value}",
                    _globalTimerOriginalValue);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex,
                "[TimerResolutionManager] Failed to revert GlobalTimerResolutionRequests");
        }

        _globalTimerKeyWasSet = false;
    }

    // ── Build detection helper ───────────────────────────────────────────────

    /// <summary>
    /// Reads the Windows build number from the registry.
    /// More reliable than Environment.OSVersion.Version.Build on systems without an app manifest.
    /// </summary>
    private static int GetWindowsBuildNumber()
    {
        try
        {
            var val = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "CurrentBuildNumber",
                "0");
            return int.TryParse(val?.ToString(), out var build) ? build : 0;
        }
        catch
        {
            return 0;
        }
    }
}
