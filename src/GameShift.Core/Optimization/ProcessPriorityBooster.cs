using System.Diagnostics;
using System.Text.Json;
using GameShift.Core.Profiles;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Sets game process priority to High for better CPU scheduling.
/// Uses runtime SetPriorityClass() for most games, or IFEO registry fallback
/// for anti-cheat-protected games (EAC, BattlEye, RICOCHET, TencentACE).
///
/// Implements IJournaledOptimization so the watchdog can clean up IFEO registry
/// changes after a main-app crash. Only IFEO changes are journaled — the live
/// PriorityClass path is ephemeral (dies with the game process) and the
/// Win32PrioritySeparation value is captured in the snapshot-based recovery path.
/// </summary>
public class ProcessPriorityBooster : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    private int _boostedProcessId;
    private bool _usedIfeo;
    private string _ifeoExeName = string.Empty;
    private string _ifeoSubKeyPath = string.Empty;
    private bool _ifeoPerfOptionsPreviouslyExisted;
    private Dictionary<string, int>? _ifeoOriginalValues;

    // Win32PrioritySeparation session-scoped override
    private int? _originalPrioritySeparation;
    private bool _prioritySeparationApplied;

    // Context stored by CanApply() for use by Apply().
    private SystemContext? _context;

    private const string IfeoBasePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>
    /// Gaming-optimal Win32PrioritySeparation value.
    /// 0x2A = fixed quantum (0x20) | short length (0x08) | max foreground boost (0x02).
    /// </summary>
    private const int GamingPrioritySeparation = 0x2A;

    public const string OptimizationId = "Process Priority Booster";

    // ── IOptimization ─────────────────────────────────────────────────────────

    public string Name => OptimizationId;

    public string Description => "Sets game process priority to High for better CPU scheduling";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // Any process can have its priority changed

    /// <summary>
    /// Delegates to the journaled Apply() path. Stores context first via CanApply().
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        var context = new SystemContext { Profile = profile, Snapshot = snapshot };
        if (!CanApply(context))
            return Task.FromResult(true);

        var result = Apply();
        return Task.FromResult(result.State == OptimizationState.Applied);
    }

    /// <summary>
    /// Delegates to the journaled Revert() path.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        if (!IsApplied)
            return Task.FromResult(true);

        var result = Revert();
        return Task.FromResult(result.State == OptimizationState.Reverted);
    }

    // ── IJournaledOptimization ────────────────────────────────────────────────

    /// <summary>
    /// Pre-flight check. Stores context for use in Apply().
    /// Process priority boost applies at all intensity tiers.
    /// </summary>
    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    /// <summary>
    /// Applies High priority to the game process.
    /// Uses IFEO registry path for anti-cheat games, runtime API for others.
    /// Records original state in snapshot before changing.
    /// Returns OptimizationResult carrying the serialized IFEO state for journal-based revert.
    /// </summary>
    public OptimizationResult Apply()
    {
        var snapshot = _context?.Snapshot;
        var profile = _context?.Profile;

        if (snapshot == null || profile == null)
        {
            _logger.Warning("[ProcessPriorityBooster] Apply called without context; skipping");
            return Fail("No context available");
        }

        // Reset per-apply state
        _usedIfeo = false;
        _ifeoExeName = string.Empty;
        _ifeoSubKeyPath = string.Empty;
        _ifeoPerfOptionsPreviouslyExisted = false;
        _ifeoOriginalValues = null;

        try
        {
            // Apply session-scoped Win32PrioritySeparation (gaming-optimal scheduling quantum).
            // Captured via snapshot for crash recovery — not journaled here.
            ApplyPrioritySeparation(snapshot);

            bool pathSuccess;
            if (profile.RequiresIfeoFallback)
            {
                pathSuccess = ApplyViaIfeo(snapshot, profile);
            }
            else
            {
                pathSuccess = ApplyViaRuntimeApi(snapshot, profile);
            }

            if (!pathSuccess)
                return Fail("Failed to apply priority boost");

            IsApplied = true;

            // Build journaled state. Only IFEO changes persist across reboots and
            // therefore need watchdog recovery; the live-API path is ephemeral.
            var ifeoKeys = new List<object>();
            if (_usedIfeo && !string.IsNullOrEmpty(_ifeoSubKeyPath))
            {
                string? originalValuesJson = _ifeoPerfOptionsPreviouslyExisted && _ifeoOriginalValues != null
                    ? JsonSerializer.Serialize(_ifeoOriginalValues)
                    : null;

                ifeoKeys.Add(new
                {
                    keyPath = _ifeoSubKeyPath,
                    originalValuesJson
                });
            }

            var originalState = JsonSerializer.Serialize(new { ifeoKeys });
            var appliedState = JsonSerializer.Serialize(new
            {
                usedIfeo = _usedIfeo,
                ifeoKeyCount = ifeoKeys.Count
            });

            return new OptimizationResult(
                Name: OptimizationId,
                OriginalValue: originalState,
                AppliedValue: appliedState,
                State: OptimizationState.Applied);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ProcessPriorityBooster] Failed to apply priority boost");
            return Fail(ex.Message);
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
            _logger.Warning("[ProcessPriorityBooster] No valid process ID in profile");
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(profile.ProcessId);

            // Record original priority before changing
            var originalPriority = process.PriorityClass;
            snapshot.RecordProcessPriority(profile.ProcessId, originalPriority);

            // Set to High priority (NOT Realtime - per PRD decision)
            process.PriorityClass = ProcessPriorityClass.High;

            _logger.Information(
                "[ProcessPriorityBooster] Set process {ProcessName} (PID: {ProcessId}) priority from {Original} to High via runtime API",
                process.ProcessName,
                profile.ProcessId,
                originalPriority);

            _boostedProcessId = profile.ProcessId;
            _usedIfeo = false;
            return true;
        }
        catch (ArgumentException)
        {
            _logger.Warning(
                "[ProcessPriorityBooster] Game process {ProcessId} not found - may have exited",
                profile.ProcessId);
            return false;
        }
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
            _logger.Warning(
                "[ProcessPriorityBooster] No executable name in profile for IFEO fallback");
            return false;
        }

        _ifeoExeName = exeName;
        _ifeoSubKeyPath = $@"{IfeoBasePath}\{exeName}\PerfOptions";

        try
        {
            // Check if PerfOptions subkey already exists
            using (var existingKey = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath))
            {
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
            }

            // Create or open the PerfOptions subkey
            var ifeoExeKeyPath = $@"{IfeoBasePath}\{exeName}";
            using var ifeoExeKey = Registry.LocalMachine.CreateSubKey(ifeoExeKeyPath);
            if (ifeoExeKey == null)
            {
                _logger.Error(
                    "[ProcessPriorityBooster] Failed to create IFEO key for {ExeName}",
                    exeName);
                return false;
            }

            using var perfOptionsKey = ifeoExeKey.CreateSubKey("PerfOptions");
            if (perfOptionsKey == null)
            {
                _logger.Error(
                    "[ProcessPriorityBooster] Failed to create PerfOptions subkey for {ExeName}",
                    exeName);
                return false;
            }

            // Set CpuPriorityClass = 3 (High priority)
            perfOptionsKey.SetValue("CpuPriorityClass", 3, RegistryValueKind.DWord);

            // Record in snapshot for crash recovery (parallel to the journal path).
            string? originalJson = _ifeoOriginalValues != null
                ? JsonSerializer.Serialize(_ifeoOriginalValues)
                : null;
            snapshot.RecordIfeoEntry(_ifeoSubKeyPath, originalJson);

            _logger.Information(
                "[ProcessPriorityBooster] Set IFEO CpuPriorityClass=3 (High) for {ExeName} " +
                "(anti-cheat: {AntiCheat}, PerfOptions previously existed: {Existed})",
                exeName,
                profile.AntiCheat,
                _ifeoPerfOptionsPreviouslyExisted);

            _usedIfeo = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "[ProcessPriorityBooster] Failed to apply IFEO priority for {ExeName}",
                exeName);
            return false;
        }
    }

    /// <summary>
    /// Reverts process priority to original value.
    /// Handles both runtime API and IFEO registry paths.
    /// </summary>
    public OptimizationResult Revert()
    {
        var snapshot = _context?.Snapshot;

        try
        {
            // Revert session-scoped Win32PrioritySeparation
            RevertPrioritySeparation();

            bool pathSuccess;
            if (_usedIfeo)
            {
                pathSuccess = RevertViaIfeo();
            }
            else
            {
                pathSuccess = RevertViaRuntimeApi(snapshot);
            }

            IsApplied = false;

            if (!pathSuccess)
                return RevertFail("Failed to revert priority boost");

            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ProcessPriorityBooster] Failed to revert priority");
            IsApplied = false; // Mark as not applied even on failure
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Reverts runtime API priority change.
    /// </summary>
    private bool RevertViaRuntimeApi(SystemStateSnapshot? snapshot)
    {
        if (_boostedProcessId <= 0)
        {
            return true; // Nothing to revert
        }

        // Check if we have the recorded original priority
        if (snapshot == null || !snapshot.ProcessPriorities.TryGetValue(_boostedProcessId, out var originalPriority))
        {
            _logger.Warning(
                "[ProcessPriorityBooster] No recorded priority for PID {ProcessId}",
                _boostedProcessId);
            return true; // Not a fatal error
        }

        try
        {
            using var process = Process.GetProcessById(_boostedProcessId);

            // Restore original priority
            process.PriorityClass = originalPriority;

            _logger.Information(
                "[ProcessPriorityBooster] Reverted process {ProcessName} priority to {Original} via runtime API",
                process.ProcessName,
                originalPriority);

            return true;
        }
        catch (ArgumentException)
        {
            _logger.Information(
                "[ProcessPriorityBooster] Process {ProcessId} already exited, no revert needed",
                _boostedProcessId);
            return true;
        }
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
                    _logger.Information(
                        "[ProcessPriorityBooster] Restored IFEO CpuPriorityClass={Value} for {ExeName}",
                        _ifeoOriginalValues["CpuPriorityClass"],
                        _ifeoExeName);
                }
            }
            else
            {
                // Delete only the CpuPriorityClass value (not the whole subkey — affinity may still be there).
                // Must close perfKey BEFORE attempting to delete the subkey — Windows registry
                // won't delete a subkey while a handle to it is still open in the same process.
                bool shouldDeleteSubKey = false;
                using (var perfKey = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath, writable: true))
                {
                    if (perfKey != null)
                    {
                        perfKey.DeleteValue("CpuPriorityClass", throwOnMissingValue: false);

                        // If PerfOptions is now empty and we created it, mark for deletion
                        shouldDeleteSubKey = !_ifeoPerfOptionsPreviouslyExisted && perfKey.ValueCount == 0;
                    }
                } // perfKey closed here — safe to delete subkey

                if (shouldDeleteSubKey)
                {
                    using var parentKey = Registry.LocalMachine.OpenSubKey(ifeoExeKeyPath, writable: true);
                    parentKey?.DeleteSubKey("PerfOptions", throwOnMissingSubKey: false);

                    _logger.Information(
                        "[ProcessPriorityBooster] Deleted empty IFEO PerfOptions subkey for {ExeName}",
                        _ifeoExeName);
                }
                else
                {
                    _logger.Information(
                        "[ProcessPriorityBooster] Deleted IFEO CpuPriorityClass for {ExeName} (PerfOptions retained)",
                        _ifeoExeName);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "[ProcessPriorityBooster] Failed to revert IFEO priority for {ExeName}",
                _ifeoExeName);
            return false;
        }
    }

    /// <summary>
    /// Confirms the applied IFEO entries are still present with the expected CpuPriorityClass.
    /// For the live runtime-API path, the priority is ephemeral and cannot be verified reliably
    /// once the game process exits, so this path returns true if IsApplied and no IFEO was written.
    /// </summary>
    public bool Verify()
    {
        if (!IsApplied)
            return false;

        try
        {
            if (_usedIfeo && !string.IsNullOrEmpty(_ifeoSubKeyPath))
            {
                using var key = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath);
                if (key?.GetValue("CpuPriorityClass") is not int cpc || cpc != 3)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Watchdog recovery path: parses the serialized IFEO state from the journal and
    /// restores each IFEO PerfOptions subkey without relying on any live instance state.
    /// For each entry:
    ///   - If originalValuesJson is null, GameShift created the key → delete PerfOptions.
    ///   - Else, restore the original DWORD values.
    /// Live PriorityClass changes are ephemeral and never journaled, so there's nothing
    /// to do for the non-IFEO path.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            _logger.Information(
                "[ProcessPriorityBooster] Reverting from journal record (watchdog recovery)");

            var state = JsonSerializer.Deserialize<JsonElement>(originalValueJson);
            if (!state.TryGetProperty("ifeoKeys", out var keys) ||
                keys.ValueKind != JsonValueKind.Array)
            {
                // No IFEO changes were journaled — nothing to revert (live-only path or no apply).
                IsApplied = false;
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
            }

            foreach (var entry in keys.EnumerateArray())
            {
                try
                {
                    if (!entry.TryGetProperty("keyPath", out var keyPathElement) ||
                        keyPathElement.ValueKind != JsonValueKind.String)
                        continue;

                    var keyPath = keyPathElement.GetString();
                    if (string.IsNullOrEmpty(keyPath))
                        continue;

                    var hasOriginal = entry.TryGetProperty("originalValuesJson", out var origElement) &&
                                      origElement.ValueKind == JsonValueKind.String;

                    if (hasOriginal)
                    {
                        // Restore original values
                        var originals = JsonSerializer.Deserialize<Dictionary<string, int>>(
                            origElement.GetString()!);
                        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                        if (key != null && originals != null)
                        {
                            foreach (var (name, value) in originals)
                            {
                                key.SetValue(name, value, RegistryValueKind.DWord);
                            }

                            _logger.Information(
                                "[ProcessPriorityBooster] Restored {Count} original value(s) at {KeyPath}",
                                originals.Count, keyPath);
                        }
                    }
                    else
                    {
                        // GameShift created it — delete the PerfOptions subkey.
                        // CRITICAL: don't hold a handle to the subkey while deleting it.
                        var parentPath = keyPath.EndsWith(@"\PerfOptions", StringComparison.Ordinal)
                            ? keyPath[..keyPath.LastIndexOf(@"\PerfOptions", StringComparison.Ordinal)]
                            : keyPath;

                        using var parentKey = Registry.LocalMachine.OpenSubKey(parentPath, writable: true);
                        parentKey?.DeleteSubKey("PerfOptions", throwOnMissingSubKey: false);

                        _logger.Information(
                            "[ProcessPriorityBooster] Deleted PerfOptions subkey under {ParentPath} (created by GameShift)",
                            parentPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "[ProcessPriorityBooster] Failed to revert an IFEO entry during watchdog recovery");
                }
            }

            IsApplied = false;
            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ProcessPriorityBooster] RevertFromRecord failed");
            return RevertFail(ex.Message);
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
                _logger.Warning(
                    "[ProcessPriorityBooster] Cannot open PriorityControl registry key");
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
                _logger.Debug(
                    "[ProcessPriorityBooster] Win32PrioritySeparation already 0x{Value:X2}, skipping",
                    GamingPrioritySeparation);
                _prioritySeparationApplied = false;
                return;
            }

            key.SetValue("Win32PrioritySeparation", GamingPrioritySeparation, RegistryValueKind.DWord);
            _prioritySeparationApplied = true;

            _logger.Information(
                "[ProcessPriorityBooster] Win32PrioritySeparation set to 0x{New:X2} (was: 0x{Original:X2})",
                GamingPrioritySeparation,
                currentValue ?? 0);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[ProcessPriorityBooster] Failed to set Win32PrioritySeparation");
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
                _logger.Warning(
                    "[ProcessPriorityBooster] Cannot open PriorityControl registry key for revert");
                return;
            }

            key.SetValue("Win32PrioritySeparation", _originalPrioritySeparation.Value, RegistryValueKind.DWord);
            _prioritySeparationApplied = false;

            _logger.Information(
                "[ProcessPriorityBooster] Win32PrioritySeparation restored to 0x{Original:X2}",
                _originalPrioritySeparation.Value);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[ProcessPriorityBooster] Failed to restore Win32PrioritySeparation");
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
                "[ProcessPriorityBooster] Crash recovery — restored Win32PrioritySeparation to 0x{Value:X2}",
                snapshot.OriginalPrioritySeparation.Value);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(
                ex,
                "[ProcessPriorityBooster] Crash recovery — failed to restore Win32PrioritySeparation");
        }
    }

    // ── Result helpers ────────────────────────────────────────────────────────

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);
}
