using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;

namespace GameShift.Core.Optimization;

/// <summary>
/// Detects CPU topology (P-cores, E-cores, LP-E-cores, V-Cache CCDs) and pins game
/// processes to optimal core sets during gaming sessions.
///
/// Uses GetSystemCpuSetInformation() for topology enumeration and
/// SetProcessDefaultCpuSets() for Thread Director-aware CPU pinning.
///
/// Supports:
/// - Intel 12th-14th gen (Alder/Raptor Lake) — 2-tier P+E
/// - Intel Arrow Lake (Core Ultra 200S) — 2-tier P+E without HT on P-cores
/// - Intel Lunar Lake (Core Ultra 200V) — 2-tier mobile
/// - Intel Panther Lake (Core Ultra 300) — 3-tier P+E+LP
/// - AMD Zen 5c (Ryzen AI 300 "Strix Point") — 2-tier Zen 5 + Zen 5c
/// - AMD X3D (9800X3D, 9950X3D) — V-Cache CCD pinning
///
/// Falls back to IFEO CpuAffinityMask for anti-cheat games where
/// SetProcessDefaultCpuSets() is blocked.
///
/// Implements IJournaledOptimization. Only the IFEO fallback path is persistent —
/// CPU Sets and ProcessorAffinity are ephemeral (live-process state that vanishes
/// if the process exits). The journal state contains only IFEO subkey paths and
/// their original values so the watchdog can clean them up after a crash.
/// </summary>
public class HybridCpuDetector : IOptimization, IJournaledOptimization
{
    private bool _detectionComplete;
    private CpuTopology? _topology;
    private int _pinnedProcessId;
    private bool _usedIfeo;
    private bool _usedCpuSets;
    private string _ifeoExeName = string.Empty;
    private string _ifeoSubKeyPath = string.Empty;
    private bool _ifeoPerfOptionsPreviouslyExisted;
    private int? _ifeoOriginalAffinityMask;

    // Context stored by CanApply() for use by Apply()
    private SystemContext? _context;

    private const string IfeoBasePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public const string OptimizationId = "Hybrid CPU Optimizer";

    public string Name => OptimizationId;

    public string Description => "Detects CPU core topology and pins game process to optimal cores (P-cores, V-Cache CCD)";

    public bool IsApplied { get; private set; }

    /// <summary>
    /// Returns true only if this CPU has distinct core types (hybrid) or an X3D V-Cache CCD.
    /// Caches the detection result to avoid re-reading topology.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (!_detectionComplete)
            {
                _topology = DetectTopology();
                _detectionComplete = true;
            }
            return _topology != null && (_topology.IsHybrid || _topology.VCacheCcdIndex != null);
        }
    }

    // ── IOptimization ─────────────────────────────────────────────────────────

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
    /// Always returns true — topology gating and IFEO-vs-CPU-Sets routing happens in Apply().
    /// </summary>
    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    /// <summary>
    /// Pins game process to optimal CPU sets based on detected topology.
    /// Tries SetProcessDefaultCpuSets first (live, ephemeral — not journaled),
    /// then ProcessorAffinity (live, ephemeral — not journaled),
    /// falling back to IFEO CpuAffinityMask for anti-cheat titles (persistent — journaled).
    ///
    /// The returned OptimizationResult only carries IFEO state; if the live paths
    /// succeeded, the state is an empty ifeoKeys list (nothing for the watchdog to revert).
    /// </summary>
    public OptimizationResult Apply()
    {
        var profile = _context?.Profile;
        var snapshot = _context?.Snapshot;

        if (profile == null || snapshot == null)
            return Fail("Missing context — CanApply() must be called first");

        try
        {
            if (_topology == null)
            {
                SettingsManager.Logger.Warning("[HybridCpuDetector] No topology available");
                return Fail("No CPU topology detected");
            }

            // Determine target CPU sets based on profile and topology
            uint[] targetCpuSets = DetermineTargetCpuSets(_topology, profile);
            if (targetCpuSets.Length == 0)
            {
                SettingsManager.Logger.Information(
                    "[HybridCpuDetector] No CPU Set pinning recommended for current profile/topology");
                IsApplied = true;
                // Nothing persistent — journal carries an empty IFEO list.
                return AppliedWithIfeoState(originalJson: EmptyIfeoStateJson(), appliedJson: EmptyIfeoStateJson());
            }

            bool success;
            if (profile.RequiresIfeoFallback)
            {
                // Anti-cheat games: try SetProcessDefaultCpuSets first, fall back to IFEO
                success = ApplyWithFallback(snapshot, profile, targetCpuSets);
            }
            else
            {
                success = ApplyViaCpuSets(snapshot, profile, targetCpuSets);
            }

            if (!success)
                return Fail("Failed to apply CPU pinning on all code paths");

            // Build the journal payload. Only IFEO state needs persistence — live paths
            // (CPU Sets / ProcessorAffinity) are ephemeral.
            var stateJson = BuildIfeoStateJson();

            return new OptimizationResult(
                Name: OptimizationId,
                OriginalValue: stateJson,
                AppliedValue: stateJson,
                State: OptimizationState.Applied);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[HybridCpuDetector] Apply failed");
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Removes CPU Set restriction or reverts IFEO registry change.
    /// </summary>
    public OptimizationResult Revert()
    {
        try
        {
            bool success;
            if (_usedCpuSets)
            {
                success = RevertViaCpuSets();
            }
            else if (_usedIfeo)
            {
                success = RevertViaIfeo();
            }
            else
            {
                IsApplied = false;
                success = true;
            }

            if (!success)
                return RevertFail("Revert reported failure");

            return new OptimizationResult(
                Name: OptimizationId,
                OriginalValue: string.Empty,
                AppliedValue: string.Empty,
                State: OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[HybridCpuDetector] Revert failed");
            IsApplied = false;
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Confirms the applied change is still in effect. For the IFEO path this means the
    /// PerfOptions subkey still carries CpuAffinityMask. For the ephemeral paths there is
    /// nothing durable to verify — we report true while the in-memory flag is set.
    /// </summary>
    public bool Verify()
    {
        if (!IsApplied)
            return false;

        try
        {
            if (_usedIfeo && !string.IsNullOrEmpty(_ifeoSubKeyPath))
            {
                using var perfKey = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath);
                return perfKey?.GetValue("CpuAffinityMask") is int;
            }

            // Live paths: nothing persisted in the registry to verify — trust the in-memory state.
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Watchdog recovery path: parses the serialized IFEO state and cleans up each
    /// PerfOptions subkey without any live instance state. If the subkey did not exist
    /// before apply and has no other values, the subkey itself is deleted.
    ///
    /// Critical: the write handle to PerfOptions must be closed BEFORE attempting to
    /// delete the subkey — Windows refuses DeleteSubKey while a handle is still open.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            SettingsManager.Logger.Information(
                "[HybridCpuDetector] Reverting from journal record (watchdog recovery)");

            if (string.IsNullOrWhiteSpace(originalValueJson))
            {
                IsApplied = false;
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
            }

            var root = JsonSerializer.Deserialize<JsonElement>(originalValueJson);

            if (!root.TryGetProperty("ifeoKeys", out var ifeoKeysElement) ||
                ifeoKeysElement.ValueKind != JsonValueKind.Array)
            {
                // Empty/missing — nothing persistent to revert
                IsApplied = false;
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
            }

            foreach (var entry in ifeoKeysElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("keyPath", out var keyPathElement) ||
                    keyPathElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var keyPath = keyPathElement.GetString();
                if (string.IsNullOrWhiteSpace(keyPath))
                    continue;

                string? originalValuesJson = null;
                if (entry.TryGetProperty("originalValuesJson", out var originalValuesElement) &&
                    originalValuesElement.ValueKind == JsonValueKind.String)
                {
                    originalValuesJson = originalValuesElement.GetString();
                }

                RevertIfeoSubKeyFromRecord(keyPath, originalValuesJson);
            }

            IsApplied = false;
            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[HybridCpuDetector] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Cleans up a single IFEO PerfOptions subkey during watchdog recovery.
    /// If the original values JSON is non-null, restores them; otherwise deletes the
    /// CpuAffinityMask value, and if the subkey is now empty and was created by GameShift,
    /// deletes the PerfOptions subkey itself.
    /// </summary>
    private static void RevertIfeoSubKeyFromRecord(string perfOptionsPath, string? originalValuesJson)
    {
        try
        {
            if (!string.IsNullOrEmpty(originalValuesJson))
            {
                // Original values existed — restore them
                var originalValues = JsonSerializer.Deserialize<Dictionary<string, int>>(originalValuesJson);
                if (originalValues != null)
                {
                    using var perfKey = Registry.LocalMachine.OpenSubKey(perfOptionsPath, writable: true);
                    if (perfKey != null)
                    {
                        foreach (var (valueName, value) in originalValues)
                        {
                            perfKey.SetValue(valueName, value, RegistryValueKind.DWord);
                        }
                        SettingsManager.Logger.Information(
                            "[HybridCpuDetector] Restored IFEO values at {Path}", perfOptionsPath);
                    }
                }
                return;
            }

            // No original values — GameShift created the subkey (or CpuAffinityMask was fresh).
            // Delete the CpuAffinityMask value; delete the subkey too only if it's now empty.
            // Must close perfKey BEFORE attempting DeleteSubKey — Windows blocks DeleteSubKey
            // while a handle to it is still open in the same process.
            bool shouldDeleteSubKey;
            using (var perfKey = Registry.LocalMachine.OpenSubKey(perfOptionsPath, writable: true))
            {
                if (perfKey == null)
                    return;

                perfKey.DeleteValue("CpuAffinityMask", throwOnMissingValue: false);
                shouldDeleteSubKey = perfKey.ValueCount == 0 && perfKey.SubKeyCount == 0;
            } // perfKey closed here — safe to delete subkey

            if (shouldDeleteSubKey)
            {
                // Derive the parent IFEO\<exe> path from the PerfOptions subkey path
                const string perfOptionsSuffix = @"\PerfOptions";
                if (perfOptionsPath.EndsWith(perfOptionsSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    var parentPath = perfOptionsPath[..^perfOptionsSuffix.Length];
                    using var parentKey = Registry.LocalMachine.OpenSubKey(parentPath, writable: true);
                    parentKey?.DeleteSubKey("PerfOptions", throwOnMissingSubKey: false);

                    SettingsManager.Logger.Information(
                        "[HybridCpuDetector] Deleted empty IFEO PerfOptions subkey at {Path}", perfOptionsPath);
                }
            }
            else
            {
                SettingsManager.Logger.Information(
                    "[HybridCpuDetector] Deleted CpuAffinityMask at {Path} (PerfOptions retained)",
                    perfOptionsPath);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex,
                "[HybridCpuDetector] Failed to revert IFEO subkey {Path} from record", perfOptionsPath);
        }
    }

    // ── Journal Payload Builders ──────────────────────────────────────────────

    /// <summary>
    /// Builds the JSON state payload stored in the journal. Contains only IFEO subkeys
    /// since the live CPU Sets / ProcessorAffinity paths are ephemeral. Returns an
    /// empty ifeoKeys list when the live path succeeded.
    /// </summary>
    private string BuildIfeoStateJson()
    {
        var ifeoKeys = new List<object>();

        if (_usedIfeo && !string.IsNullOrEmpty(_ifeoSubKeyPath))
        {
            string? originalValuesJson = null;
            if (_ifeoPerfOptionsPreviouslyExisted && _ifeoOriginalAffinityMask.HasValue)
            {
                originalValuesJson = JsonSerializer.Serialize(
                    new Dictionary<string, int> { ["CpuAffinityMask"] = _ifeoOriginalAffinityMask.Value });
            }

            ifeoKeys.Add(new
            {
                keyPath = _ifeoSubKeyPath,
                originalValuesJson
            });
        }

        return JsonSerializer.Serialize(new { ifeoKeys });
    }

    private static string EmptyIfeoStateJson() =>
        JsonSerializer.Serialize(new { ifeoKeys = Array.Empty<object>() });

    /// <summary>
    /// Helper that constructs a success OptimizationResult and sets IsApplied.
    /// </summary>
    private OptimizationResult AppliedWithIfeoState(string originalJson, string appliedJson)
    {
        IsApplied = true;
        return new OptimizationResult(
            Name: OptimizationId,
            OriginalValue: originalJson,
            AppliedValue: appliedJson,
            State: OptimizationState.Applied);
    }

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    // ── Topology Detection ───────────────────────────────────────────

    /// <summary>
    /// Enumerates CPU topology via GetSystemCpuSetInformation.
    /// Categorizes logical processors by EfficiencyClass into P/E/LP tiers.
    /// Also detects AMD X3D V-Cache CCD via processor name check and CCD grouping.
    /// </summary>
    private CpuTopology DetectTopology()
    {
        var topology = new CpuTopology();

        try
        {
            // First call to get required buffer size
            NativeInterop.GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero, 0);

            if (requiredSize == 0)
            {
                SettingsManager.Logger.Warning("[HybridCpuDetector] GetSystemCpuSetInformation returned 0 size");
                return topology;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                if (!NativeInterop.GetSystemCpuSetInformation(buffer, requiredSize, out _, IntPtr.Zero, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    SettingsManager.Logger.Warning(
                        "[HybridCpuDetector] GetSystemCpuSetInformation failed with error {Error}", error);
                    return topology;
                }

                // Parse variable-length array of SYSTEM_CPU_SET_INFORMATION
                int offset = 0;
                while (offset < (int)requiredSize)
                {
                    var info = Marshal.PtrToStructure<NativeInterop.SYSTEM_CPU_SET_INFORMATION>(buffer + offset);

                    if (info.Type == 0) // CpuSetInformation
                    {
                        var core = new CpuCore
                        {
                            CpuSetId = info.Id,
                            EfficiencyClass = info.EfficiencyClass,
                            CoreIndex = info.CoreIndex,
                            LastLevelCacheIndex = info.LastLevelCacheIndex,
                            Group = info.Group,
                            LogicalProcessorIndex = info.LogicalProcessorIndex
                        };

                        switch (info.EfficiencyClass)
                        {
                            case 0:
                                topology.PerformanceCores.Add(core);
                                break;
                            case 1:
                                topology.EfficiencyCores.Add(core);
                                break;
                            default: // >= 2 (Panther Lake LP-E-cores)
                                topology.LowPowerCores.Add(core);
                                break;
                        }
                    }

                    // Walk to next structure using its reported size
                    offset += (int)info.Size;

                    // Safety: prevent infinite loop if Size is 0 or corrupted
                    if (info.Size == 0)
                        break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // Detect V-Cache CCD (AMD X3D)
            DetectVCacheCcd(topology);

            SettingsManager.Logger.Information(
                "[HybridCpuDetector] Topology detected — P-cores: {P}, E-cores: {E}, LP-cores: {LP}, " +
                "Hybrid: {IsHybrid}, 3-tier: {HasThree}, V-Cache CCD: {VCache}",
                topology.PerformanceCores.Count,
                topology.EfficiencyCores.Count,
                topology.LowPowerCores.Count,
                topology.IsHybrid,
                topology.HasThreeTiers,
                topology.VCacheCcdIndex?.ToString() ?? "none");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[HybridCpuDetector] Failed to detect CPU topology");
        }

        return topology;
    }

    /// <summary>
    /// Detects AMD X3D V-Cache CCD by checking CPU name for "X3D" suffix
    /// and grouping cores by LastLevelCacheIndex (CCD boundary).
    /// On current X3D SKUs, CCD0 (the first LLC group) contains V-Cache.
    /// </summary>
    private void DetectVCacheCcd(CpuTopology topology)
    {
        // Group P-cores by their L3 cache group (CCD)
        var ccdGroups = topology.PerformanceCores
            .GroupBy(c => c.LastLevelCacheIndex)
            .ToList();

        // Need at least 2 CCDs for V-Cache to be beneficial
        if (ccdGroups.Count < 2)
            return;

        // Check if this is an AMD X3D processor
        string cpuName = GetCpuName();
        if (!cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase))
            return;

        // CCD0 has V-Cache on all current X3D SKUs (5800X3D, 7800X3D, 9800X3D, 9950X3D)
        topology.VCacheCcdIndex = 0;

        SettingsManager.Logger.Information(
            "[HybridCpuDetector] AMD X3D detected — V-Cache CCD index: {CcdIndex} ({CoreCount} cores)",
            topology.VCacheCcdIndex,
            ccdGroups[0].Count());
    }

    /// <summary>
    /// Gets the CPU name via WMI Win32_Processor.Name.
    /// </summary>
    private static string GetCpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    return obj["Name"]?.ToString() ?? string.Empty;
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch
        {
            // WMI failure — return empty
        }
        return string.Empty;
    }

    // ── CPU Set Pinning ──────────────────────────────────────────────

    /// <summary>
    /// Determines which CPU Set IDs to pin the game to based on profile and topology.
    /// </summary>
    private uint[] DetermineTargetCpuSets(CpuTopology topology, GameProfile profile)
    {
        // Case 1: AMD X3D V-Cache CCD pinning (when enabled in profile)
        if (topology.VCacheCcdIndex != null && profile.PinToVCacheCcd)
        {
            var vCacheSets = topology.PerformanceCores
                .Where(c => c.LastLevelCacheIndex == topology.VCacheCcdIndex)
                .Select(c => c.CpuSetId)
                .ToArray();

            if (vCacheSets.Length > 0)
            {
                SettingsManager.Logger.Information(
                    "[HybridCpuDetector] V-Cache CCD pinning — {Count} cores on CCD {Ccd}",
                    vCacheSets.Length, topology.VCacheCcdIndex);
                return vCacheSets;
            }
        }

        // Case 2: 3-tier hybrid (Panther Lake) — pin to P+E, exclude LP-cores
        if (topology.HasThreeTiers)
        {
            var pePlusSets = topology.PerformanceCores
                .Concat(topology.EfficiencyCores)
                .Select(c => c.CpuSetId)
                .ToArray();

            SettingsManager.Logger.Information(
                "[HybridCpuDetector] 3-tier hybrid — pinning to P+E cores ({Count}), excluding {LpCount} LP-cores",
                pePlusSets.Length, topology.LowPowerCores.Count);
            return pePlusSets;
        }

        // Case 3: 2-tier hybrid — pin to P-cores only (if profile says so)
        if (topology.IsHybrid && profile.UsePerformanceCoresOnly)
        {
            var pCoreSets = topology.PerformanceCores
                .Select(c => c.CpuSetId)
                .ToArray();

            SettingsManager.Logger.Information(
                "[HybridCpuDetector] P-core only pinning — {Count} P-cores",
                pCoreSets.Length);
            return pCoreSets;
        }

        // Case 4: No pinning beneficial
        return Array.Empty<uint>();
    }

    /// <summary>
    /// Applies CPU Set pinning via SetProcessDefaultCpuSets (runtime API path).
    /// </summary>
    private bool ApplyViaCpuSets(SystemStateSnapshot snapshot, GameProfile profile, uint[] targetCpuSets)
    {
        if (profile.ProcessId <= 0)
        {
            SettingsManager.Logger.Warning("[HybridCpuDetector] No valid process ID in profile");
            return false;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(profile.ProcessId);
        }
        catch (ArgumentException)
        {
            SettingsManager.Logger.Warning(
                "[HybridCpuDetector] Game process {ProcessId} not found — may have exited",
                profile.ProcessId);
            return false;
        }

        using (process)
        {
            // Record original affinity for snapshot (legacy format for crash recovery compatibility)
            snapshot.RecordProcessAffinity(profile.ProcessId, process.ProcessorAffinity);

            bool success = NativeInterop.SetProcessDefaultCpuSets(
                process.Handle,
                targetCpuSets,
                (uint)targetCpuSets.Length);

            if (success)
            {
                _pinnedProcessId = profile.ProcessId;
                _usedCpuSets = true;
                _usedIfeo = false;
                IsApplied = true;

                SettingsManager.Logger.Information(
                    "[HybridCpuDetector] Pinned {ProcessName} (PID {Pid}) to {Count} CPU Sets via SetProcessDefaultCpuSets",
                    process.ProcessName, profile.ProcessId, targetCpuSets.Length);
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                SettingsManager.Logger.Warning(
                    "[HybridCpuDetector] SetProcessDefaultCpuSets failed with error {Error}", error);
            }

            return success;
        }
    }

    /// <summary>
    /// Tries SetProcessDefaultCpuSets first; if blocked (anti-cheat), falls back to IFEO CpuAffinityMask.
    /// </summary>
    private bool ApplyWithFallback(SystemStateSnapshot snapshot, GameProfile profile, uint[] targetCpuSets)
    {
        // Try CPU Sets first — may work even for some anti-cheat games
        try
        {
            if (profile.ProcessId > 0)
            {
                Process process;
                try
                {
                    process = Process.GetProcessById(profile.ProcessId);
                }
                catch (ArgumentException)
                {
                    SettingsManager.Logger.Warning(
                        "[HybridCpuDetector] Game process {ProcessId} not found for CPU Sets attempt",
                        profile.ProcessId);
                    // Fall through to IFEO
                    return ApplyViaIfeo(snapshot, profile, targetCpuSets);
                }

                using (process)
                {
                    // Capture original affinity BEFORE attempting CPU-Sets so we can record
                    // it only if the call succeeds — avoids a spurious snapshot entry if
                    // we fall through to the IFEO path.
                    var originalAffinity = process.ProcessorAffinity;

                    bool success = NativeInterop.SetProcessDefaultCpuSets(
                        process.Handle,
                        targetCpuSets,
                        (uint)targetCpuSets.Length);

                    if (success)
                    {
                        snapshot.RecordProcessAffinity(profile.ProcessId, originalAffinity);

                        _pinnedProcessId = profile.ProcessId;
                        _usedCpuSets = true;
                        _usedIfeo = false;
                        IsApplied = true;

                        SettingsManager.Logger.Information(
                            "[HybridCpuDetector] Pinned anti-cheat game {ProcessName} via SetProcessDefaultCpuSets (succeeded despite anti-cheat)",
                            process.ProcessName);
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Debug(
                "[HybridCpuDetector] SetProcessDefaultCpuSets blocked by anti-cheat, falling back to IFEO: {Error}",
                ex.Message);
        }

        // Fall back to IFEO registry path
        return ApplyViaIfeo(snapshot, profile, targetCpuSets);
    }

    /// <summary>
    /// Removes CPU Set restriction by calling SetProcessDefaultCpuSets with count=0.
    /// </summary>
    private bool RevertViaCpuSets()
    {
        if (_pinnedProcessId <= 0)
        {
            IsApplied = false;
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(_pinnedProcessId);

            NativeInterop.SetProcessDefaultCpuSets(process.Handle, null, 0);

            SettingsManager.Logger.Information(
                "[HybridCpuDetector] CPU Set pinning removed for {ProcessName} (PID {Pid})",
                process.ProcessName, _pinnedProcessId);
        }
        catch (ArgumentException)
        {
            SettingsManager.Logger.Information(
                "[HybridCpuDetector] Process {ProcessId} already exited, no CPU Set revert needed",
                _pinnedProcessId);
        }

        _usedCpuSets = false;
        IsApplied = false;
        return true;
    }

    // ── IFEO Fallback (anti-cheat games) ─────────────────────────────

    /// <summary>
    /// IFEO registry fallback — writes CpuAffinityMask to PerfOptions.
    /// Used when SetProcessDefaultCpuSets is blocked by kernel anti-cheat.
    /// Converts CPU Set IDs to a legacy affinity bitmask.
    /// </summary>
    private bool ApplyViaIfeo(SystemStateSnapshot snapshot, GameProfile profile, uint[] targetCpuSets)
    {
        var exeName = profile.ExecutableName;
        if (string.IsNullOrEmpty(exeName))
        {
            SettingsManager.Logger.Warning(
                "[HybridCpuDetector] No executable name in profile for IFEO fallback");
            return false;
        }

        _ifeoExeName = exeName;
        _ifeoSubKeyPath = $@"{IfeoBasePath}\{exeName}\PerfOptions";

        try
        {
            // Convert CPU Set IDs to legacy affinity mask
            ulong affinityMask = CpuSetsToAffinityMask(targetCpuSets);
            if (affinityMask == 0)
            {
                SettingsManager.Logger.Warning(
                    "[HybridCpuDetector] Could not convert CPU Sets to affinity mask for IFEO");
                return false;
            }

            // Check if PerfOptions subkey already exists
            using (var existingKey = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath))
            {
                if (existingKey != null)
                {
                    _ifeoPerfOptionsPreviouslyExisted = true;
                    var existingAffinity = existingKey.GetValue("CpuAffinityMask");
                    if (existingAffinity is int affinityValue)
                    {
                        _ifeoOriginalAffinityMask = affinityValue;
                    }
                }
                else
                {
                    _ifeoPerfOptionsPreviouslyExisted = false;
                    _ifeoOriginalAffinityMask = null;
                }
            }

            // Create or open the PerfOptions subkey
            var ifeoExeKeyPath = $@"{IfeoBasePath}\{exeName}";
            using var ifeoExeKey = Registry.LocalMachine.CreateSubKey(ifeoExeKeyPath);
            if (ifeoExeKey == null)
            {
                SettingsManager.Logger.Error(
                    "[HybridCpuDetector] Failed to create IFEO key for {ExeName}", exeName);
                return false;
            }

            using var perfOptionsKey = ifeoExeKey.CreateSubKey("PerfOptions");
            if (perfOptionsKey == null)
            {
                SettingsManager.Logger.Error(
                    "[HybridCpuDetector] Failed to create PerfOptions subkey for {ExeName}", exeName);
                return false;
            }

            var affinityMaskInt = (int)affinityMask;
            perfOptionsKey.SetValue("CpuAffinityMask", affinityMaskInt, RegistryValueKind.DWord);

            // Use distinct snapshot key to avoid collision with ProcessPriorityBooster
            var snapshotKey = _ifeoSubKeyPath + "::Affinity";
            var originalJson = _ifeoOriginalAffinityMask.HasValue
                ? JsonSerializer.Serialize(new Dictionary<string, int> { ["CpuAffinityMask"] = _ifeoOriginalAffinityMask.Value })
                : null;
            snapshot.RecordIfeoEntry(snapshotKey, originalJson);

            SettingsManager.Logger.Information(
                "[HybridCpuDetector] Set IFEO CpuAffinityMask=0x{Mask:X} for {ExeName} (anti-cheat: {AntiCheat})",
                affinityMaskInt, exeName, profile.AntiCheat);

            _usedIfeo = true;
            _usedCpuSets = false;
            IsApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex,
                "[HybridCpuDetector] Failed to apply IFEO affinity for {ExeName}", exeName);
            return false;
        }
    }

    /// <summary>
    /// Reverts IFEO registry affinity change.
    /// Only deletes CpuAffinityMask — does NOT delete the PerfOptions subkey
    /// (ProcessPriorityBooster's CpuPriorityClass may still be there).
    /// </summary>
    private bool RevertViaIfeo()
    {
        if (string.IsNullOrEmpty(_ifeoExeName))
        {
            IsApplied = false;
            return true;
        }

        try
        {
            var ifeoExeKeyPath = $@"{IfeoBasePath}\{_ifeoExeName}";

            if (_ifeoOriginalAffinityMask.HasValue)
            {
                using var perfKey = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath, writable: true);
                if (perfKey != null)
                {
                    perfKey.SetValue("CpuAffinityMask", _ifeoOriginalAffinityMask.Value,
                        RegistryValueKind.DWord);
                    SettingsManager.Logger.Information(
                        "[HybridCpuDetector] Restored IFEO CpuAffinityMask=0x{Value:X} for {ExeName}",
                        _ifeoOriginalAffinityMask.Value, _ifeoExeName);
                }
            }
            else
            {
                // Must close perfKey BEFORE attempting to delete the subkey — Windows registry
                // won't delete a subkey while a handle to it is still open in the same process.
                bool shouldDeleteSubKey = false;
                using (var perfKey = Registry.LocalMachine.OpenSubKey(_ifeoSubKeyPath, writable: true))
                {
                    if (perfKey != null)
                    {
                        perfKey.DeleteValue("CpuAffinityMask", throwOnMissingValue: false);

                        shouldDeleteSubKey = !_ifeoPerfOptionsPreviouslyExisted && perfKey.ValueCount == 0;
                    }
                } // perfKey closed here — safe to delete subkey

                if (shouldDeleteSubKey)
                {
                    using var parentKey = Registry.LocalMachine.OpenSubKey(ifeoExeKeyPath, writable: true);
                    parentKey?.DeleteSubKey("PerfOptions", throwOnMissingSubKey: false);

                    SettingsManager.Logger.Information(
                        "[HybridCpuDetector] Deleted empty IFEO PerfOptions subkey for {ExeName}",
                        _ifeoExeName);
                }
                else
                {
                    SettingsManager.Logger.Information(
                        "[HybridCpuDetector] Deleted IFEO CpuAffinityMask for {ExeName} (PerfOptions retained)",
                        _ifeoExeName);
                }
            }

            _usedIfeo = false;
            IsApplied = false;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex,
                "[HybridCpuDetector] Failed to revert IFEO affinity for {ExeName}", _ifeoExeName);
            IsApplied = false;
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Converts an array of CPU Set IDs to a legacy affinity bitmask.
    /// Only includes cores in processor group 0 (legacy affinity supports only group 0).
    /// </summary>
    private ulong CpuSetsToAffinityMask(uint[] cpuSetIds)
    {
        if (_topology == null) return 0;

        ulong mask = 0;
        foreach (uint id in cpuSetIds)
        {
            var core = _topology.AllCores.FirstOrDefault(c => c.CpuSetId == id);
            if (core != null && core.Group == 0 && core.LogicalProcessorIndex < 64)
            {
                mask |= 1UL << core.LogicalProcessorIndex;
            }
        }
        return mask;
    }
}
