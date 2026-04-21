using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameShift.Core.BackgroundMode;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Core.Optimization;

/// <summary>
/// Keeps all CPU cores active during gaming by disabling Windows core parking.
/// Overrides the active power plan's parking settings to prevent unparking latency.
/// Uses powercfg to set CPMINCORES, CPMAXCORES, MinProcessorState, and ConcurrencyThreshold.
/// Coordinates with PowerPlanSwitcher: captures values from whichever plan is active at Apply time
/// (which will be Ultimate Performance if PowerPlanSwitcher ran first per LIFO ordering).
/// Sets both AC and DC values for laptop support.
///
/// Also manages processor idle disable (IDLEDISABLE) as a session-scoped toggle —
/// forces all cores to C0 state during gaming, re-enables idle on game exit.
/// AMD dual-CCD X3D processors get special parking values (CPMINCORES=50, ConcurrencyThreshold=67).
///
/// Implements IJournaledOptimization so the watchdog can re-apply original parking values
/// after a crash without relying on live instance state. Both the snapshot-based recovery
/// path (via SystemStateSnapshot.CpuParkingEntries) and the journal-based watchdog path
/// coexist — running both is safe since powercfg writes are idempotent.
/// </summary>
public class CpuParkingManager : IOptimization, IJournaledOptimization
{
    /// <summary>
    /// Processor Power Management sub-group GUID.
    /// </summary>
    private const string ProcessorSubGroupGuid = "54533251-82be-4824-96c1-47b60b740d00";

    // Core parking setting GUIDs (used by both apply and revert paths).
    private const string CpMinCoresGuid          = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string CpMaxCoresGuid          = "ea062031-0e34-4ff1-9b6d-eb1059334028";
    private const string MinProcessorStateGuid   = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string ConcurrencyThresholdGuid = "2430ab6f-a520-44a2-9601-f7f23b5134b1";

    // ── C-state depth limiting GUIDs (replaces IDLEDISABLE=1) ──────────

    /// <summary>Max idle state depth: 1 = C1 only (2µs wake, HLT still runs so counters work).</summary>
    private const string IdleStateMaxGuid       = "9943e905-9a30-4ec1-9b99-44dd3b76f7a2";
    /// <summary>Idle promote threshold: 100 = never promote to deeper C-states.</summary>
    private const string IdlePromoteGuid        = "7b224883-b3cc-4d79-819f-8374152cbe7c";
    /// <summary>Idle demote threshold: 100 = aggressively return to shallowest state.</summary>
    private const string IdleDemoteGuid         = "4b92d758-5a24-4851-a470-815d78aee119";
    /// <summary>Idle scaling: 0 = disable auto threshold scaling.</summary>
    private const string IdleScalingGuid         = "6c2993b0-8f48-481f-bcc6-00dd2742aa06";
    /// <summary>C-state time check interval: ms between idle re-evaluation.</summary>
    private const string CsTimeCheckGuid         = "c4581c31-89ab-4597-8e2b-9c9cab440e6b";
    /// <summary>Latency hint perf response (primary): 100 = max CPU on input events.</summary>
    private const string LatencyHintPerfGuid     = "619b7505-003b-4e82-b7a6-4dd29c300971";
    /// <summary>Latency hint perf response (secondary).</summary>
    private const string LatencyHintPerf1Guid    = "619b7505-003b-4e82-b7a6-4dd29c300972";
    /// <summary>Latency-sensitive unparked cores hint: 100 = keep all cores unparked during hints.</summary>
    private const string LatencyUnparkedGuid     = "616cdaa5-695e-4545-97ad-97dc2d1bdd88";

    /// <summary>Performance time check interval setting GUID.</summary>
    private const string TimeCheckIntervalGuid   = "4d2b0152-7d5c-498b-88e2-34345392a2c5";

    /// <summary>All C-state limiting GUIDs in apply order.</summary>
    private static readonly (string Guid, string Name, int Value)[] CStateLimitSettings =
    [
        (IdleStateMaxGuid,    "IDLESTATEMAX (C1 max depth)",     1),
        (IdlePromoteGuid,     "IDLEPROMOTE (block deep sleep)", 100),
        (IdleDemoteGuid,      "IDLEDEMOTE (fast wake)",         100),
        (IdleScalingGuid,     "IDLESCALING (disable auto)",       0),
        (CsTimeCheckGuid,     "CS_TIME_CHECK (20ms eval)",    20000),
        (LatencyHintPerfGuid, "LATENCYHINTPERF (max on input)",  100),
        (LatencyHintPerf1Guid,"LATENCYHINTPERF1 (secondary)",    100),
        (LatencyUnparkedGuid, "Latency unparked cores hint",     100),
    ];

    /// <summary>
    /// Records original AC and DC values for each setting.
    /// </summary>
    private readonly record struct ParkingOriginalState(
        string SettingGuid,
        string SettingName,
        string? OriginalAcValue,
        string? OriginalDcValue);

    private readonly List<ParkingOriginalState> _originalStates = new();
    private string? _activeSchemeGuid;
    private bool _idleDisableApplied;

    // Target parking values applied during Apply() — captured so Verify() can re-read them.
    private int _appliedCpMinCores;
    private int _appliedConcurrencyThreshold;

    // Context stored by CanApply() for use by Apply().
    private SystemContext? _context;

    public const string OptimizationId = "CPU Core Unparking";

    public string Name => OptimizationId;

    public string Description => "Keeps all CPU cores active during gaming to prevent unparking latency";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // powercfg is always available on Windows

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
    /// Always returns true — powercfg is available on every supported Windows version.
    /// </summary>
    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    /// <summary>
    /// Unparks all CPU cores by setting aggressive parking values on the active power plan.
    /// Captures original values for both AC and DC power, sets both to gaming-optimal values.
    /// Records the active scheme GUID and original values in snapshot for crash recovery and
    /// also serializes them as JSON for the journal-based watchdog revert path.
    /// Also disables processor idle (forces C0 state) if the game profile allows it.
    /// Uses vendor-aware parking values for AMD dual-CCD X3D processors.
    /// </summary>
    public OptimizationResult Apply()
    {
        var profile = _context?.Profile;
        var snapshot = _context?.Snapshot;

        try
        {
            // Get the currently active power scheme GUID
            _activeSchemeGuid = GetActiveSchemeGuid();
            if (_activeSchemeGuid == null)
            {
                SettingsManager.Logger.Error("[CpuParkingManager] Failed to get active power scheme GUID");
                return Fail("Failed to get active power scheme GUID");
            }

            SettingsManager.Logger.Information(
                "[CpuParkingManager] Active power scheme: {SchemeGuid}", _activeSchemeGuid);

            // Detect CPU profile for vendor-aware parking values
            var cpuProfile = PowerPlanConfigurator.DetectCpuProfile();
            var (cpMinCores, concurrencyThreshold) = PowerPlanConfigurator.GetParkingValuesForProfile(cpuProfile);
            _appliedCpMinCores = cpMinCores;
            _appliedConcurrencyThreshold = concurrencyThreshold;

            SettingsManager.Logger.Information(
                "[CpuParkingManager] CPU profile={Profile}, CPMINCORES={MinCores}, ConcurrencyThreshold={Threshold}",
                cpuProfile, cpMinCores, concurrencyThreshold);

            // Build vendor-aware parking settings
            var parkingSettings = new (string Guid, string Name, int TargetValue)[]
            {
                (CpMinCoresGuid,          "CPMINCORES",           cpMinCores),
                (CpMaxCoresGuid,          "CPMAXCORES",           100),
                (MinProcessorStateGuid,   "MinProcessorState",    100),
                (ConcurrencyThresholdGuid, "ConcurrencyThreshold", concurrencyThreshold),
            };

            foreach (var (settingGuid, settingName, targetValue) in parkingSettings)
            {
                try
                {
                    // Capture original AC and DC values
                    string? originalAc = QueryPowerSettingValue(_activeSchemeGuid, settingGuid, "AC");
                    string? originalDc = QueryPowerSettingValue(_activeSchemeGuid, settingGuid, "DC");

                    _originalStates.Add(new ParkingOriginalState(
                        settingGuid, settingName, originalAc, originalDc));

                    // Set AC value
                    RunPowercfg(
                        $"/setacvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {settingGuid} {targetValue}");

                    // Set DC value (for laptop users)
                    RunPowercfg(
                        $"/setdcvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {settingGuid} {targetValue}");

                    SettingsManager.Logger.Debug(
                        "[CpuParkingManager] Set {Setting}={Value} (original AC={OrigAc}, DC={OrigDc})",
                        settingName, targetValue, originalAc ?? "null", originalDc ?? "null");
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(ex,
                        "[CpuParkingManager] Failed to set {Setting}", settingName);
                }
            }

            // === Low-Latency Idle Mode (C-state depth limiting) ===
            // When enabled in a Competitive profile, limits C-states to C1 max depth
            // instead of the old IDLEDISABLE=1 approach. C1 has 2µs wake latency (vs
            // C6's 100µs+) while keeping the idle thread running so WMI counters report
            // correct CPU utilization. Preserves thermal headroom for boost clocks.
            if (profile != null &&
                profile.DisableProcessorIdle &&
                profile.Intensity == OptimizationIntensity.Competitive)
            {
                ApplyCStateLimiting();
                snapshot?.RecordIdleDisableState(_activeSchemeGuid);
            }

            // Apply changes to the active scheme
            RunPowercfg($"/setactive {_activeSchemeGuid}");

            // Record in snapshot for crash recovery (legacy snapshot path)
            snapshot?.RecordCpuParkingState(
                _activeSchemeGuid,
                _originalStates.Select(s => new CpuParkingSnapshotEntry(
                    s.SettingGuid, s.OriginalAcValue, s.OriginalDcValue)).ToList());

            SettingsManager.Logger.Information(
                "[CpuParkingManager] All CPU cores unparked — {Count} settings applied, idle disable={IdleDisable}",
                _originalStates.Count, _idleDisableApplied);

            IsApplied = true;

            // Build JSON payload for the journal-based watchdog revert path.
            var originalJson = BuildOriginalStateJson();
            var appliedJson = BuildAppliedStateJson(cpMinCores, concurrencyThreshold);

            return new OptimizationResult(
                Name: OptimizationId,
                OriginalValue: originalJson,
                AppliedValue: appliedJson,
                State: OptimizationState.Applied);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[CpuParkingManager] Apply failed");
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Restores original parking settings for both AC and DC power.
    /// Re-enables processor idle and restores time check interval to responsive value.
    /// </summary>
    public OptimizationResult Revert()
    {
        try
        {
            if (_activeSchemeGuid == null)
            {
                SettingsManager.Logger.Warning(
                    "[CpuParkingManager] No active scheme GUID recorded, skipping revert");
                IsApplied = false;
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
            }

            foreach (var state in _originalStates)
            {
                try
                {
                    // Restore AC value
                    if (state.OriginalAcValue != null)
                    {
                        RunPowercfg(
                            $"/setacvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {state.SettingGuid} {state.OriginalAcValue}");
                    }

                    // Restore DC value
                    if (state.OriginalDcValue != null)
                    {
                        RunPowercfg(
                            $"/setdcvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {state.SettingGuid} {state.OriginalDcValue}");
                    }

                    SettingsManager.Logger.Debug(
                        "[CpuParkingManager] Restored {Setting} to AC={AcValue}, DC={DcValue}",
                        state.SettingName, state.OriginalAcValue ?? "null", state.OriginalDcValue ?? "null");
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(ex,
                        "[CpuParkingManager] Failed to restore {Setting}", state.SettingName);
                }
            }

            // === Revert C-State Limiting ===
            if (_idleDisableApplied)
            {
                RevertCStateLimiting();
            }

            // Apply restored settings
            RunPowercfg($"/setactive {_activeSchemeGuid}");

            SettingsManager.Logger.Information(
                "[CpuParkingManager] CPU parking settings restored ({Count} settings)",
                _originalStates.Count);

            _originalStates.Clear();
            IsApplied = false;
            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[CpuParkingManager] Revert failed");
            IsApplied = false;
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Confirms the applied CPMINCORES value is still in effect on the active scheme.
    /// Returns false if the value was reverted externally (e.g., by Windows Update or
    /// another process re-editing the power plan).
    /// </summary>
    public bool Verify()
    {
        if (!IsApplied || _activeSchemeGuid == null)
            return false;

        try
        {
            // Re-read CPMINCORES AC value — should match the value we applied.
            var current = QueryPowerSettingValue(_activeSchemeGuid, CpMinCoresGuid, "AC");
            if (current == null)
                return false;

            return int.TryParse(current, out var value) && value == _appliedCpMinCores;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Watchdog recovery path: parses the serialized original parking state from the journal
    /// and restores AC/DC values for every tracked setting via powercfg, without requiring
    /// any live instance fields. Idempotent — writing the same values twice is safe, so
    /// coexisting with the snapshot-based boot recovery path is fine.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            SettingsManager.Logger.Information(
                "[CpuParkingManager] Reverting from journal record (watchdog recovery)");

            var payload = JsonSerializer.Deserialize<JsonElement>(originalValueJson);

            if (!payload.TryGetProperty("schemeGuid", out var schemeElement) ||
                schemeElement.ValueKind != JsonValueKind.String)
            {
                return RevertFail("Missing schemeGuid field");
            }

            var schemeGuid = schemeElement.GetString();
            if (string.IsNullOrWhiteSpace(schemeGuid))
                return RevertFail("schemeGuid is empty");

            // Revert core parking settings.
            if (payload.TryGetProperty("entries", out var entriesElement) &&
                entriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entriesElement.EnumerateArray())
                {
                    RestoreParkingEntry(schemeGuid, entry);
                }
            }

            // Revert C-state limiting originals if they were captured.
            bool hadCStateLimiting = false;
            if (payload.TryGetProperty("cStateOriginals", out var cStateElement) &&
                cStateElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in cStateElement.EnumerateArray())
                {
                    if (!entry.TryGetProperty("settingGuid", out var guidElement) ||
                        guidElement.ValueKind != JsonValueKind.String)
                        continue;

                    var settingGuid = guidElement.GetString();
                    if (string.IsNullOrWhiteSpace(settingGuid))
                        continue;

                    if (entry.TryGetProperty("originalAc", out var acElement) &&
                        acElement.ValueKind == JsonValueKind.String)
                    {
                        var ac = acElement.GetString();
                        if (!string.IsNullOrWhiteSpace(ac))
                        {
                            RunPowercfg(
                                $"/setacvalueindex {schemeGuid} {ProcessorSubGroupGuid} {settingGuid} {ac}");
                        }
                    }

                    if (entry.TryGetProperty("originalDc", out var dcElement) &&
                        dcElement.ValueKind == JsonValueKind.String)
                    {
                        var dc = dcElement.GetString();
                        if (!string.IsNullOrWhiteSpace(dc))
                        {
                            RunPowercfg(
                                $"/setdcvalueindex {schemeGuid} {ProcessorSubGroupGuid} {settingGuid} {dc}");
                        }
                    }

                    // Re-hide the setting so it goes back to being an advanced hidden option.
                    RunPowercfg($"-attributes {ProcessorSubGroupGuid} {settingGuid} +ATTRIB_HIDE");
                    hadCStateLimiting = true;
                }
            }

            if (hadCStateLimiting)
            {
                // Restore time check interval to the safe default if the session touched it.
                RunPowercfg($"/setacvalueindex {schemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 15");
                RunPowercfg($"/setdcvalueindex {schemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 15");
            }

            // Activate restored settings.
            RunPowercfg($"/setactive {schemeGuid}");

            IsApplied = false;
            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[CpuParkingManager] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Restores AC/DC values for a single parking entry parsed from the journal JSON.
    /// Missing values (null or absent) are skipped — nothing to restore.
    /// </summary>
    private void RestoreParkingEntry(string schemeGuid, JsonElement entry)
    {
        if (!entry.TryGetProperty("settingGuid", out var guidElement) ||
            guidElement.ValueKind != JsonValueKind.String)
            return;

        var settingGuid = guidElement.GetString();
        if (string.IsNullOrWhiteSpace(settingGuid))
            return;

        if (entry.TryGetProperty("originalAc", out var acElement) &&
            acElement.ValueKind == JsonValueKind.String)
        {
            var ac = acElement.GetString();
            if (!string.IsNullOrWhiteSpace(ac))
            {
                RunPowercfg(
                    $"/setacvalueindex {schemeGuid} {ProcessorSubGroupGuid} {settingGuid} {ac}");
            }
        }

        if (entry.TryGetProperty("originalDc", out var dcElement) &&
            dcElement.ValueKind == JsonValueKind.String)
        {
            var dc = dcElement.GetString();
            if (!string.IsNullOrWhiteSpace(dc))
            {
                RunPowercfg(
                    $"/setdcvalueindex {schemeGuid} {ProcessorSubGroupGuid} {settingGuid} {dc}");
            }
        }
    }

    /// <summary>
    /// Serializes the captured original state into the JSON payload persisted by the journal.
    /// The payload must be self-contained so the watchdog can revert without live fields.
    /// </summary>
    private string BuildOriginalStateJson()
    {
        var payload = new
        {
            schemeGuid = _activeSchemeGuid,
            entries = _originalStates.Select(s => new
            {
                settingGuid = s.SettingGuid,
                settingName = s.SettingName,
                originalAc = s.OriginalAcValue,
                originalDc = s.OriginalDcValue
            }).ToArray(),
            idleDisableApplied = _idleDisableApplied,
            cStateOriginals = _cStateOriginals.Select(s => new
            {
                settingGuid = s.Guid,
                settingName = s.Name,
                originalAc = s.OrigAc,
                originalDc = s.OrigDc
            }).ToArray()
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Serializes the values that were actually applied. Used for logging / audit only.
    /// </summary>
    private string BuildAppliedStateJson(int cpMinCores, int concurrencyThreshold)
    {
        var payload = new
        {
            schemeGuid = _activeSchemeGuid,
            cpMinCores,
            cpMaxCores = 100,
            minProcessorState = 100,
            concurrencyThreshold,
            idleDisableApplied = _idleDisableApplied
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Crash recovery: restores parking settings from a previous crashed session.
    /// Called during app startup when a stale lockfile is found.
    /// </summary>
    public static void CleanupStaleParkingState(string schemeGuid, List<CpuParkingSnapshotEntry> entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                if (entry.OriginalAcValue != null)
                {
                    RunPowercfg(
                        $"/setacvalueindex {schemeGuid} {ProcessorSubGroupGuid} {entry.SettingGuid} {entry.OriginalAcValue}");
                }

                if (entry.OriginalDcValue != null)
                {
                    RunPowercfg(
                        $"/setdcvalueindex {schemeGuid} {ProcessorSubGroupGuid} {entry.SettingGuid} {entry.OriginalDcValue}");
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        RunPowercfg($"/setactive {schemeGuid}");
    }

    /// <summary>
    /// Crash recovery: restores processor idle state from a previous crashed session.
    /// If GameShift crashed while idle was disabled (IDLEDISABLE=1), re-enables it.
    /// Also restores the time check interval to 15ms for responsive frequency scaling.
    /// </summary>
    public static void CleanupStaleIdleDisable(string schemeGuid)
    {
        try
        {
            // Reset all C-state limiting settings to safe defaults
            foreach (var (guid, _, _) in CStateLimitSettings)
            {
                // Re-hide the setting
                RunPowercfg($"-attributes {ProcessorSubGroupGuid} {guid} +ATTRIB_HIDE");
            }

            // Restore time check interval
            RunPowercfg($"/setacvalueindex {schemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 15");
            RunPowercfg($"/setdcvalueindex {schemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 15");

            RunPowercfg($"/setactive {schemeGuid}");

            SettingsManager.Logger.Information(
                "[CpuParkingManager] Cleaned up stale C-state limit state for scheme {Guid}", schemeGuid);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex,
                "[CpuParkingManager] Failed to clean up stale C-state limit state");
        }
    }

    // ── C-State Depth Limiting Helpers ──────────────────────────────────

    /// <summary>Original values for C-state limiting settings, for revert.</summary>
    private readonly List<(string Guid, string Name, string? OrigAc, string? OrigDc)> _cStateOriginals = new();

    /// <summary>
    /// Limits C-state depth to C1 (2µs wake) and configures latency hints.
    /// Replaces the old IDLEDISABLE=1 approach which caused 100% utilization.
    /// Hidden power settings are unhidden before writing.
    /// </summary>
    private void ApplyCStateLimiting()
    {
        if (_activeSchemeGuid == null) return;

        try
        {
            _cStateOriginals.Clear();

            foreach (var (guid, name, targetValue) in CStateLimitSettings)
            {
                // Unhide the setting so powercfg can access it
                RunPowercfg($"-attributes {ProcessorSubGroupGuid} {guid} -ATTRIB_HIDE");

                // Read original values
                string? origAc = QueryPowerSettingValue(_activeSchemeGuid, guid, "AC");
                string? origDc = QueryPowerSettingValue(_activeSchemeGuid, guid, "DC");
                _cStateOriginals.Add((guid, name, origAc, origDc));

                // Apply gaming values
                RunPowercfg($"/setacvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {guid} {targetValue}");
                RunPowercfg($"/setdcvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {guid} {targetValue}");

                SettingsManager.Logger.Debug(
                    "[CpuParkingManager] C-state limit: {Name} = {Value} (was AC={Ac}, DC={Dc})",
                    name, targetValue, origAc ?? "null", origDc ?? "null");
            }

            RunPowercfg($"/setactive {_activeSchemeGuid}");
            _idleDisableApplied = true;

            SettingsManager.Logger.Information(
                "[CpuParkingManager] Low-latency idle mode applied (C1 max depth, {Count} settings)",
                CStateLimitSettings.Length);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[CpuParkingManager] Failed to apply C-state limiting");
        }
    }

    /// <summary>
    /// Restores all C-state limiting settings to their original values and re-hides them.
    /// </summary>
    private void RevertCStateLimiting()
    {
        if (_activeSchemeGuid == null) return;

        try
        {
            foreach (var (guid, name, origAc, origDc) in _cStateOriginals)
            {
                if (origAc != null)
                    RunPowercfg($"/setacvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {guid} {origAc}");
                if (origDc != null)
                    RunPowercfg($"/setdcvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {guid} {origDc}");

                // Re-hide the setting
                RunPowercfg($"-attributes {ProcessorSubGroupGuid} {guid} +ATTRIB_HIDE");
            }

            _cStateOriginals.Clear();
            RunPowercfg($"/setactive {_activeSchemeGuid}");
            _idleDisableApplied = false;

            SettingsManager.Logger.Information("[CpuParkingManager] C-state limits reverted, settings re-hidden");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[CpuParkingManager] Failed to revert C-state limiting");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static string? GetActiveSchemeGuid()
    {
        var output = RunPowercfg("/getactivescheme");
        if (output == null) return null;

        // "Power Scheme GUID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx  (name)"
        var match = Regex.Match(output,
            @"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    private static string? QueryPowerSettingValue(string schemeGuid, string settingGuid, string powerType)
    {
        var output = RunPowercfg(
            $"/query {schemeGuid} {ProcessorSubGroupGuid} {settingGuid}");
        if (output == null) return null;

        // Parse "Current AC Power Setting Index: 0x000000XX"
        var pattern = $@"Current {powerType} Power Setting Index:\s*0x([0-9a-fA-F]+)";
        var match = Regex.Match(output, pattern);
        return match.Success ? Convert.ToInt32(match.Groups[1].Value, 16).ToString() : null;
    }

    private static string? RunPowercfg(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = NativeInterop.SystemExePath("powercfg.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stderr = "";
            var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
            var output = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(5000);
            process.WaitForExit(5000);
            return output;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Serializable entry for CPU parking crash recovery in SystemStateSnapshot.
/// </summary>
public record CpuParkingSnapshotEntry(
    string SettingGuid,
    string? OriginalAcValue,
    string? OriginalDcValue);
