using System.ComponentModel;
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
/// changes after a main-app crash. Only IFEO changes are journaled - the live
/// PriorityClass path is ephemeral (dies with the game process).
/// </summary>
public class ProcessPriorityBooster : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    private int _boostedProcessId;
    private string? _boostedProcessName;
    private bool _usedIfeo;
    private string _ifeoExeName = string.Empty;
    private string _ifeoSubKeyPath = string.Empty;
    private bool _ifeoPerfOptionsPreviouslyExisted;
    private Dictionary<string, int>? _ifeoOriginalValues;

    // Context stored by CanApply() for use by Apply().
    private SystemContext? _context;

    internal IProcessPriorityAccess Processes { get; init; } = SystemProcessPriorityAccess.Instance;

    private const string IfeoBasePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

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
        _boostedProcessId = 0;
        _boostedProcessName = null;

        try
        {
            bool pathSuccess;
            if (profile.RequiresIfeoFallback)
            {
                pathSuccess = ApplyViaIfeo(snapshot, profile);
            }
            else
            {
                pathSuccess = ApplyViaRuntimeApi(snapshot, profile);
            }

            // With no game to target (e.g. Quick Optimize) the per-process boost is not applicable,
            // so a no-op is success, not failure.
            bool hadGameToBoost = profile.ProcessId > 0;
            if (!pathSuccess && hadGameToBoost)
                return Fail("Failed to apply priority boost");

            IsApplied = true;
            return BuildAppliedResult();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ProcessPriorityBooster] Failed to apply priority boost");

            // Whatever failed, an IFEO change already made must stay tracked so the engine
            // reverts it; the engine only reverts optimizations that report Applied.
            if (_usedIfeo)
            {
                IsApplied = true;
                return BuildAppliedResult();
            }

            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Builds the Applied result. Only IFEO changes persist across reboots and therefore need
    /// watchdog recovery; the live-API path is ephemeral.
    /// </summary>
    private OptimizationResult BuildAppliedResult()
    {
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

    /// <summary>
    /// Standard runtime API path - sets priority via Process.PriorityClass.
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
            var processName = Processes.GetName(profile.ProcessId);

            // Guard against PID reuse: if the PID no longer belongs to the detected game (it exited
            // and Windows recycled the PID), don't boost an unrelated process.
            var expectedName = Path.GetFileNameWithoutExtension(profile.ExecutableName);
            if (!string.IsNullOrEmpty(expectedName) &&
                !string.Equals(processName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning(
                    "[ProcessPriorityBooster] PID {ProcessId} is now {Actual}, expected {Expected} - skipping priority boost",
                    profile.ProcessId, processName, expectedName);
                return false;
            }

            // Record original priority before changing
            var originalPriority = Processes.GetPriority(profile.ProcessId);
            snapshot.RecordProcessPriority(profile.ProcessId, originalPriority);

            // Set to High priority (NOT Realtime - per PRD decision)
            Processes.SetPriority(profile.ProcessId, ProcessPriorityClass.High);

            _logger.Information(
                "[ProcessPriorityBooster] Set process {ProcessName} (PID: {ProcessId}) priority from {Original} to High via runtime API",
                processName,
                profile.ProcessId,
                originalPriority);

            _boostedProcessId = profile.ProcessId;
            _boostedProcessName = processName;
            _usedIfeo = false;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // ArgumentException: no process with that PID. InvalidOperationException: the
            // process exited between GetProcessById and the property read (PID-reuse race).
            _logger.Warning(
                "[ProcessPriorityBooster] Game process {ProcessId} not found - may have exited",
                profile.ProcessId);
            return false;
        }
        catch (Win32Exception ex)
        {
            // Access denied, typically an anti-cheat protecting the game process.
            _logger.Warning(
                "[ProcessPriorityBooster] Cannot change the priority of game process {ProcessId}: {Message}",
                profile.ProcessId, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// IFEO registry fallback path - sets priority via Image File Execution Options.
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
            try
            {
                perfOptionsKey.SetValue("CpuPriorityClass", 3, RegistryValueKind.DWord);
            }
            catch when (!_ifeoPerfOptionsPreviouslyExisted)
            {
                // Nothing tracks the key yet, so a key created only for this value would stay behind.
                perfOptionsKey.Dispose();
                DeleteCreatedPerfOptions(ifeoExeKey, exeName);
                throw;
            }

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

    private void DeleteCreatedPerfOptions(RegistryKey ifeoExeKey, string exeName)
    {
        try
        {
            ifeoExeKey.DeleteSubKey("PerfOptions", throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "[ProcessPriorityBooster] Could not delete the PerfOptions subkey created for {ExeName}",
                exeName);
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
            // Restore original priority
            if (string.IsNullOrEmpty(_boostedProcessName))
            {
                Processes.SetPriority(_boostedProcessId, originalPriority);
            }
            else if (!Processes.RestoreIfSameProcess(_boostedProcessId, _boostedProcessName, originalPriority))
            {
                _logger.Information(
                    "[ProcessPriorityBooster] PID {ProcessId} now belongs to another process, skipping restore",
                    _boostedProcessId);
                return true;
            }

            _logger.Information(
                "[ProcessPriorityBooster] Reverted process {ProcessId} priority to {Original} via runtime API",
                _boostedProcessId,
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
                // Delete only the CpuPriorityClass value (not the whole subkey - affinity may still be there).
                // Must close perfKey BEFORE attempting to delete the subkey - Windows registry
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
                } // perfKey closed here - safe to delete subkey

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

    internal RecoveryRegistry RecoveryRegistry { get; init; } = RecoveryRegistry.Windows;

    private const string IfeoValueName = "CpuPriorityClass";

    /// <summary>
    /// Watchdog recovery path: parses the serialized IFEO state from the journal and
    /// restores each IFEO PerfOptions subkey without relying on any live instance state.
    /// For each entry, like the live revert:
    ///   - If a CpuPriorityClass original was recorded, restore it.
    ///   - Else delete CpuPriorityClass, and PerfOptions too when GameShift created it and it is now empty.
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
                // No IFEO changes were journaled - nothing to revert (live-only path or no apply).
                IsApplied = false;
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
            }

            var notRestored = RecoveryRegistry.RevertIfeoRecords(keys, IfeoValueName, OptimizationId, _logger);

            IsApplied = false;
            return notRestored.Count == 0
                ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
                : RevertFail($"Not restored: {string.Join(", ", notRestored)}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ProcessPriorityBooster] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    // ── Result helpers ────────────────────────────────────────────────────────

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);
}
