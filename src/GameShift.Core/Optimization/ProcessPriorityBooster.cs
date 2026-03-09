using System.Diagnostics;
using System.Text.Json;
using GameShift.Core.Profiles;
using GameShift.Core.Config;
using GameShift.Core.System;
using Microsoft.Win32;

namespace GameShift.Core.Optimization;

/// <summary>
/// Sets game process priority to High for better CPU scheduling.
/// Uses runtime SetPriorityClass() for most games, or IFEO registry fallback
/// for anti-cheat-protected games (EAC, BattlEye, RICOCHET, TencentACE).
/// </summary>
public class ProcessPriorityBooster : IOptimization
{
    private int _boostedProcessId;
    private bool _usedIfeo;
    private string _ifeoExeName = string.Empty;
    private string _ifeoSubKeyPath = string.Empty;
    private bool _ifeoPerfOptionsPreviouslyExisted;
    private Dictionary<string, int>? _ifeoOriginalValues;

    // Win32PrioritySeparation session-scoped override
    private int? _originalPrioritySeparation;
    private bool _prioritySeparationApplied;

    private const string IfeoBasePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>
    /// Gaming-optimal Win32PrioritySeparation value.
    /// 0x2A = fixed quantum (0x20) | short length (0x08) | max foreground boost (0x02).
    /// </summary>
    private const int GamingPrioritySeparation = 0x2A;

    public const string OptimizationId = "Process Priority Booster";

    public string Name => OptimizationId;

    public string Description => "Sets game process priority to High for better CPU scheduling";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // Any process can have its priority changed

    /// <summary>
    /// Applies High priority to the game process.
    /// Uses IFEO registry path for anti-cheat games, runtime API for others.
    /// Records original state in snapshot before changing.
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            // Apply session-scoped Win32PrioritySeparation (gaming-optimal scheduling quantum)
            ApplyPrioritySeparation(snapshot);

            if (profile.RequiresIfeoFallback)
            {
                return Task.FromResult(ApplyViaIfeo(snapshot, profile));
            }
            else
            {
                return Task.FromResult(ApplyViaRuntimeApi(snapshot, profile));
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "ProcessPriorityBooster: Failed to apply priority boost");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Standard runtime API path — sets priority via Process.PriorityClass.
    /// Used for games without kernel-level anti-cheat that blocks process manipulation.
    /// </summary>
    private bool ApplyViaRuntimeApi(SystemStateSnapshot snapshot, GameProfile profile)
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
            "ProcessPriorityBooster: Set process {ProcessName} (PID: {ProcessId}) priority from {Original} to High via runtime API",
            process.ProcessName,
            profile.ProcessId,
            originalPriority);

        _boostedProcessId = profile.ProcessId;
        _usedIfeo = false;
        IsApplied = true;
        return true;
    }

    /// <summary>
    /// IFEO registry fallback path — sets priority via Image File Execution Options.
    /// Used for anti-cheat games (EAC, BattlEye, RICOCHET, TencentACE) that block
    /// runtime SetPriorityClass() on protected game processes.
    /// Windows applies IFEO at process creation time, before anti-cheat locks the handle.
    /// </summary>
    private bool ApplyViaIfeo(SystemStateSnapshot snapshot, GameProfile profile)
    {
        var exeName = profile.ExecutableName;
        if (string.IsNullOrEmpty(exeName))
        {
            SettingsManager.Logger.Warning(
                "ProcessPriorityBooster: No executable name in profile for IFEO fallback");
            return false;
        }

        _ifeoExeName = exeName;
        _ifeoSubKeyPath = $@"{IfeoBasePath}\{exeName}\PerfOptions";

        try
        {
            // Check if PerfOptions subkey already exists
            using var existingKey = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath);
            if (existingKey != null)
            {
                _ifeoPerfOptionsPreviouslyExisted = true;
                _ifeoOriginalValues = new Dictionary<string, int>();

                // Record existing CpuPriorityClass if present
                var existingPriority = existingKey.GetValue("CpuPriorityClass");
                if (existingPriority is int priorityValue)
                {
                    _ifeoOriginalValues["CpuPriorityClass"] = priorityValue;
                }
            }
            else
            {
                _ifeoPerfOptionsPreviouslyExisted = false;
                _ifeoOriginalValues = null;
            }

            // Create or open the PerfOptions subkey
            var ifeoExeKeyPath = $@"{IfeoBasePath}\{exeName}";
            using var ifeoExeKey = Registry.LocalMachine.CreateSubKey(ifeoExeKeyPath);
            if (ifeoExeKey == null)
            {
                SettingsManager.Logger.Error(
                    "ProcessPriorityBooster: Failed to create IFEO key for {ExeName}",
                    exeName);
                return false;
            }

            using var perfOptionsKey = ifeoExeKey.CreateSubKey("PerfOptions");
            if (perfOptionsKey == null)
            {
                SettingsManager.Logger.Error(
                    "ProcessPriorityBooster: Failed to create PerfOptions subkey for {ExeName}",
                    exeName);
                return false;
            }

            // Set CpuPriorityClass = 3 (High priority)
            perfOptionsKey.SetValue("CpuPriorityClass", 3, RegistryValueKind.DWord);

            // Record in snapshot for crash recovery
            string? originalJson = _ifeoOriginalValues != null
                ? JsonSerializer.Serialize(_ifeoOriginalValues)
                : null;
            snapshot.RecordIfeoEntry(_ifeoSubKeyPath, originalJson);

            SettingsManager.Logger.Information(
                "ProcessPriorityBooster: Set IFEO CpuPriorityClass=3 (High) for {ExeName} " +
                "(anti-cheat: {AntiCheat}, PerfOptions previously existed: {Existed})",
                exeName,
                profile.AntiCheat,
                _ifeoPerfOptionsPreviouslyExisted);

            _usedIfeo = true;
            IsApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(
                ex,
                "ProcessPriorityBooster: Failed to apply IFEO priority for {ExeName}",
                exeName);
            return false;
        }
    }

    /// <summary>
    /// Reverts process priority to original value.
    /// Handles both runtime API and IFEO registry paths.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        try
        {
            // Revert session-scoped Win32PrioritySeparation
            RevertPrioritySeparation();

            if (_usedIfeo)
            {
                return Task.FromResult(RevertViaIfeo());
            }
            else
            {
                return Task.FromResult(RevertViaRuntimeApi(snapshot));
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "ProcessPriorityBooster: Failed to revert priority");
            IsApplied = false; // Mark as not applied even on failure
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Reverts runtime API priority change.
    /// </summary>
    private bool RevertViaRuntimeApi(SystemStateSnapshot snapshot)
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
            "ProcessPriorityBooster: Reverted process {ProcessName} priority to {Original} via runtime API",
            process.ProcessName,
            originalPriority);

        IsApplied = false;
        return true;
    }

    /// <summary>
    /// Reverts IFEO registry priority change.
    /// If GameShift created the PerfOptions subkey, deletes CpuPriorityClass value only
    /// (HybridCpuDetector may still have CpuAffinityMask in the same subkey).
    /// If the subkey is empty after value removal, deletes the subkey.
    /// </summary>
    private bool RevertViaIfeo()
    {
        if (string.IsNullOrEmpty(_ifeoExeName))
        {
            return true; // Nothing to revert
        }

        try
        {
            var ifeoExeKeyPath = $@"{IfeoBasePath}\{_ifeoExeName}";

            if (_ifeoPerfOptionsPreviouslyExisted && _ifeoOriginalValues?.ContainsKey("CpuPriorityClass") == true)
            {
                // Restore original CpuPriorityClass value
                using var perfKey = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath, writable: true);
                if (perfKey != null)
                {
                    perfKey.SetValue("CpuPriorityClass", _ifeoOriginalValues["CpuPriorityClass"],
                        RegistryValueKind.DWord);
                    SettingsManager.Logger.Information(
                        "ProcessPriorityBooster: Restored IFEO CpuPriorityClass={Value} for {ExeName}",
                        _ifeoOriginalValues["CpuPriorityClass"],
                        _ifeoExeName);
                }
            }
            else
            {
                // Delete only the CpuPriorityClass value (not the whole subkey — affinity may still be there)
                using var perfKey = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath, writable: true);
                if (perfKey != null)
                {
                    perfKey.DeleteValue("CpuPriorityClass", throwOnMissingValue: false);

                    // If PerfOptions is now empty and we created it, delete the subkey
                    if (!_ifeoPerfOptionsPreviouslyExisted && perfKey.ValueCount == 0)
                    {
                        using var parentKey = Registry.LocalMachine.OpenSubKey(ifeoExeKeyPath, writable: true);
                        parentKey?.DeleteSubKey("PerfOptions", throwOnMissingSubKey: false);

                        SettingsManager.Logger.Information(
                            "ProcessPriorityBooster: Deleted empty IFEO PerfOptions subkey for {ExeName}",
                            _ifeoExeName);
                    }
                    else
                    {
                        SettingsManager.Logger.Information(
                            "ProcessPriorityBooster: Deleted IFEO CpuPriorityClass for {ExeName} (PerfOptions retained)",
                            _ifeoExeName);
                    }
                }
            }

            IsApplied = false;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(
                ex,
                "ProcessPriorityBooster: Failed to revert IFEO priority for {ExeName}",
                _ifeoExeName);
            IsApplied = false;
            return false;
        }
    }

    // ── Win32PrioritySeparation ──────────────────────────────────────

    /// <summary>
    /// Sets Win32PrioritySeparation to gaming-optimal value (0x2A).
    /// 0x2A = fixed quantum, short length, maximum foreground boost.
    /// Records original value in snapshot for crash recovery.
    /// Takes effect immediately — no reboot required.
    /// </summary>
    private void ApplyPrioritySeparation(SystemStateSnapshot snapshot)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\PriorityControl", writable: true);

            if (key == null)
            {
                SettingsManager.Logger.Warning(
                    "ProcessPriorityBooster: Cannot open PriorityControl registry key");
                return;
            }

            var currentValue = key.GetValue("Win32PrioritySeparation") as int?;
            _originalPrioritySeparation = currentValue;

            // Record in snapshot for crash recovery
            if (currentValue.HasValue)
            {
                snapshot.RecordPrioritySeparation(currentValue.Value);
            }

            // Skip if already set to gaming-optimal value
            if (currentValue == GamingPrioritySeparation)
            {
                SettingsManager.Logger.Debug(
                    "ProcessPriorityBooster: Win32PrioritySeparation already 0x{Value:X2}, skipping",
                    GamingPrioritySeparation);
                _prioritySeparationApplied = false;
                return;
            }

            key.SetValue("Win32PrioritySeparation", GamingPrioritySeparation, RegistryValueKind.DWord);
            _prioritySeparationApplied = true;

            SettingsManager.Logger.Information(
                "ProcessPriorityBooster: Win32PrioritySeparation set to 0x{New:X2} (was: 0x{Original:X2})",
                GamingPrioritySeparation,
                currentValue ?? 0);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(
                ex,
                "ProcessPriorityBooster: Failed to set Win32PrioritySeparation");
        }
    }

    /// <summary>
    /// Restores the original Win32PrioritySeparation value.
    /// </summary>
    private void RevertPrioritySeparation()
    {
        if (!_prioritySeparationApplied || !_originalPrioritySeparation.HasValue)
            return;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\PriorityControl", writable: true);

            if (key == null)
            {
                SettingsManager.Logger.Warning(
                    "ProcessPriorityBooster: Cannot open PriorityControl registry key for revert");
                return;
            }

            key.SetValue("Win32PrioritySeparation", _originalPrioritySeparation.Value, RegistryValueKind.DWord);
            _prioritySeparationApplied = false;

            SettingsManager.Logger.Information(
                "ProcessPriorityBooster: Win32PrioritySeparation restored to 0x{Original:X2}",
                _originalPrioritySeparation.Value);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(
                ex,
                "ProcessPriorityBooster: Failed to restore Win32PrioritySeparation");
        }
    }

    /// <summary>
    /// Restores Win32PrioritySeparation from a stale snapshot during crash recovery.
    /// Called by startup logic when a stale lockfile is found.
    /// </summary>
    public static void RestorePrioritySeparationFromSnapshot(SystemStateSnapshot snapshot)
    {
        if (!snapshot.OriginalPrioritySeparation.HasValue)
            return;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\PriorityControl", writable: true);

            key?.SetValue("Win32PrioritySeparation", snapshot.OriginalPrioritySeparation.Value,
                RegistryValueKind.DWord);

            SettingsManager.Logger.Information(
                "ProcessPriorityBooster: Crash recovery — restored Win32PrioritySeparation to 0x{Value:X2}",
                snapshot.OriginalPrioritySeparation.Value);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(
                ex,
                "ProcessPriorityBooster: Crash recovery — failed to restore Win32PrioritySeparation");
        }
    }
}
