using System.Diagnostics;
using System.Text.Json;
using GameShift.Core.BackgroundMode;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Switches Windows power plan to a high-performance plan during gaming sessions
/// and applies key power sub-settings (EPP, NVMe, USB, wireless, boost policy).
///
/// Fallback chain: Ultimate Performance -> High Performance -> any existing perf plan -> duplicate from template.
/// On revert: restores original plan. Falls back to Balanced if original was deleted.
///
/// Implements IJournaledOptimization so the watchdog can restore the original plan GUID
/// and every overridden sub-setting (AC and DC values) from the serialized journal record,
/// even after a main-app crash wipes the in-memory instance fields.
/// Legacy crash recovery: the static CleanupStalePowerPlan method is preserved so the
/// snapshot-based recovery path (active_session.json lockfile) still restores the active plan.
/// </summary>
public class PowerPlanSwitcher : IOptimization, IJournaledOptimization
{
    private static readonly Guid UltimatePerformanceGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public const string OptimizationId = "Ultimate Performance Power Plan";

    private readonly ILogger _logger = SettingsManager.Logger;

    public string Name => OptimizationId;
    public string Description => "Switches to Ultimate Performance power plan for maximum CPU/GPU performance";
    public bool IsApplied { get; private set; }
    public bool IsAvailable => true;

    // Tracks applied session sub-setting overrides for in-process revert (AC + DC).
    private readonly List<SubSettingState> _appliedOverrides = new();

    // Original plan GUID captured at apply time (used by in-process Revert()).
    private Guid _originalPlanGuid = Guid.Empty;

    // Context stored by CanApply() for use by Apply().
    private SystemContext? _context;

    // ── Session sub-setting definitions (single source of truth) ──────────────

    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string SubDisk = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string SubWireless = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";

    /// <summary>
    /// The eight session power sub-settings GameShift overrides during a gaming session.
    /// Each entry is applied to SCHEME_CURRENT via powercfg after switching plans.
    /// </summary>
    private static readonly (string SubGroup, string Setting, int Value, string Description)[] SessionOverrides =
    {
        // EPP = 0 (maximum performance)
        (SubProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", 0, "EPP"),
        (SubProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6864", 0, "EPP P-cores"),
        // Boost policy = 100%
        (SubProcessor, "45bcc044-d885-43e2-8605-ee0ec6e96b59", 100, "Boost policy"),
        // USB selective suspend = disabled
        (SubUsb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, "USB selective suspend"),
        // USB 3 link power = off
        (SubUsb, "d4e98f31-5ffe-4ce1-be31-1b38b384c009", 0, "USB 3 link power"),
        // PCI Express ASPM = off
        (SubPciExpress, "ee12f906-d277-404b-b6da-e5fa1a576df5", 0, "PCIe ASPM"),
        // NVMe primary idle timeout = 0
        (SubDisk, "d639518a-e56d-4345-8af2-b9f32fb26109", 0, "NVMe idle timeout"),
        // Wireless power saving = max performance
        (SubWireless, "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0, "Wireless power saving"),
    };

    /// <summary>
    /// Captured original AC/DC values for a single sub-setting. Persisted in the journal
    /// so the watchdog can restore the exact pre-session values.
    /// </summary>
    private readonly record struct SubSettingState(string SubGroup, string Setting, int AcValue, int DcValue);

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
    /// Pre-flight check. Stores context for use in Apply(). Power plan switching applies
    /// at all intensity tiers, so always returns true.
    /// </summary>
    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    /// <summary>
    /// Captures the original plan GUID and every session sub-setting's AC/DC values,
    /// then activates a high-performance plan and applies the session overrides.
    /// Returns an OptimizationResult carrying serialized original state for deterministic revert.
    /// </summary>
    public OptimizationResult Apply()
    {
        var snapshot = _context?.Snapshot;
        _appliedOverrides.Clear();

        try
        {
            // Resolve the original active plan GUID (snapshot value takes precedence;
            // fall back to a fresh query so this works when called without a snapshot).
            if (snapshot != null && snapshot.OriginalPowerPlan != Guid.Empty)
            {
                _originalPlanGuid = snapshot.OriginalPowerPlan;
            }
            else
            {
                _originalPlanGuid = GetActivePowerPlanGuid();
            }

            _logger.Information(
                "[PowerPlanSwitcher] Original power plan GUID: {OriginalPlan}", _originalPlanGuid);

            // Capture AC/DC originals for every session override BEFORE making any changes.
            foreach (var (subGroup, setting, _, desc) in SessionOverrides)
            {
                try
                {
                    var (ac, dc) = ReadSubSettingValues(subGroup, setting);
                    _appliedOverrides.Add(new SubSettingState(subGroup, setting, ac, dc));
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "[PowerPlanSwitcher] Failed to read current values for sub-setting {Desc}; " +
                        "will skip revert for this entry", desc);
                }
            }

            // Find or create a high-performance plan and activate it.
            var targetGuidNullable = FindOrCreatePerformancePlanSync();
            if (targetGuidNullable == null)
            {
                _logger.Error(
                    "[PowerPlanSwitcher] Could not find or create any high-performance power plan");
                return Fail("No high-performance power plan could be found or created");
            }

            var targetGuid = targetGuidNullable.Value;
            uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref targetGuid);
            if (result != 0)
            {
                _logger.Error(
                    "[PowerPlanSwitcher] Failed to activate plan {Plan} (error {ErrorCode})",
                    targetGuid, result);
                return Fail($"PowerSetActiveScheme failed with error {result}");
            }

            _logger.Information(
                "[PowerPlanSwitcher] Activated performance plan {Plan}", targetGuid);

            // Apply session sub-settings to the now-active plan (SCHEME_CURRENT).
            ApplySessionOverrides();

            IsApplied = true;

            // Build serialized original state for the journal.
            var serialized = JsonSerializer.Serialize(new
            {
                originalPlanGuid = _originalPlanGuid.ToString(),
                subSettings = _appliedOverrides.Select(s => new
                {
                    subGroup = s.SubGroup,
                    setting = s.Setting,
                    acValue = s.AcValue,
                    dcValue = s.DcValue
                }).ToArray()
            });

            var applied = JsonSerializer.Serialize(new
            {
                activatedPlanGuid = targetGuid.ToString()
            });

            return new OptimizationResult(
                Name: OptimizationId,
                OriginalValue: serialized,
                AppliedValue: applied,
                State: OptimizationState.Applied);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[PowerPlanSwitcher] Failed to apply power plan");
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Restores the session sub-settings to their pre-apply AC/DC values and
    /// switches back to the original power plan (falling back to Balanced if the
    /// original plan no longer exists).
    /// </summary>
    public OptimizationResult Revert()
    {
        try
        {
            // Restore each captured sub-setting's AC/DC values on SCHEME_CURRENT.
            RevertSessionOverrides();

            if (_originalPlanGuid == Guid.Empty)
            {
                _logger.Warning(
                    "[PowerPlanSwitcher] No original power plan recorded, skipping plan revert");
                IsApplied = false;
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
            }

            var originalGuid = _originalPlanGuid;
            uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref originalGuid);

            if (result == 0)
            {
                _logger.Information(
                    "[PowerPlanSwitcher] Reverted to original power plan {OriginalPlan}",
                    _originalPlanGuid);
                IsApplied = false;
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
            }

            // Original plan was deleted - fall back to Balanced
            _logger.Warning(
                "[PowerPlanSwitcher] Original plan {OriginalPlan} no longer exists (error {ErrorCode}), falling back to Balanced",
                _originalPlanGuid, result);

            var balanced = BalancedGuid;
            result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref balanced);

            IsApplied = false;
            if (result == 0)
            {
                _logger.Information("[PowerPlanSwitcher] Fell back to Balanced power plan");
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
            }

            _logger.Error(
                "[PowerPlanSwitcher] Failed to activate Balanced plan (error {ErrorCode})", result);
            return RevertFail($"Failed to activate Balanced plan (error {result})");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[PowerPlanSwitcher] Failed to revert power plan");
            IsApplied = false;
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Confirms the active power plan is no longer the original plan.
    /// When Apply() switched to any high-performance plan, Verify() returns true as long as
    /// the currently active plan is not the captured original.
    /// </summary>
    public bool Verify()
    {
        if (!IsApplied)
            return false;

        try
        {
            var current = GetActivePowerPlanGuid();
            if (current == Guid.Empty)
                return false;

            // If we're still sitting on the original plan, something reverted us.
            if (_originalPlanGuid != Guid.Empty && current == _originalPlanGuid)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Watchdog recovery path: parses the serialized original state from the journal and
    /// restores every sub-setting's AC/DC values, then switches back to the original plan
    /// GUID, all without relying on any live instance state.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            _logger.Information("[PowerPlanSwitcher] Reverting from journal record (watchdog recovery)");

            var state = JsonSerializer.Deserialize<JsonElement>(originalValueJson);

            // Restore each sub-setting's AC/DC values on SCHEME_CURRENT.
            if (state.TryGetProperty("subSettings", out var subSettingsElement) &&
                subSettingsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var sub in subSettingsElement.EnumerateArray())
                {
                    try
                    {
                        var subGroup = sub.GetProperty("subGroup").GetString();
                        var setting = sub.GetProperty("setting").GetString();
                        var ac = sub.GetProperty("acValue").GetInt32();
                        var dc = sub.GetProperty("dcValue").GetInt32();

                        if (string.IsNullOrEmpty(subGroup) || string.IsNullOrEmpty(setting))
                            continue;

                        RunPowercfgSync(
                            $"/setacvalueindex SCHEME_CURRENT {subGroup} {setting} {ac}");
                        RunPowercfgSync(
                            $"/setdcvalueindex SCHEME_CURRENT {subGroup} {setting} {dc}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex,
                            "[PowerPlanSwitcher] Failed to restore a sub-setting during watchdog revert");
                    }
                }

                // Activate the restored sub-setting changes.
                RunPowercfgSync("/setactive SCHEME_CURRENT");
            }

            // Switch back to the original plan GUID.
            if (state.TryGetProperty("originalPlanGuid", out var guidElement) &&
                guidElement.ValueKind == JsonValueKind.String &&
                Guid.TryParse(guidElement.GetString(), out var originalGuid) &&
                originalGuid != Guid.Empty)
            {
                var target = originalGuid;
                uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref target);
                if (result == 0)
                {
                    _logger.Information(
                        "[PowerPlanSwitcher] Restored original power plan {Plan}", originalGuid);
                }
                else
                {
                    _logger.Warning(
                        "[PowerPlanSwitcher] Original plan {Plan} missing during watchdog revert (error {Code}), falling back to Balanced",
                        originalGuid, result);
                    var balanced = BalancedGuid;
                    NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref balanced);
                }
            }
            else
            {
                _logger.Warning(
                    "[PowerPlanSwitcher] No originalPlanGuid in journal record, skipping plan revert");
            }

            IsApplied = false;
            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[PowerPlanSwitcher] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Static crash recovery: restores the original power plan from a saved snapshot GUID.
    /// Called from App.xaml.cs crash recovery path (snapshot-based, separate from the journal).
    /// Idempotent — safe to call even if the journal-driven watchdog revert has already run.
    /// </summary>
    public static void CleanupStalePowerPlan(Guid originalPlanGuid)
    {
        if (originalPlanGuid == Guid.Empty) return;

        try
        {
            var guid = originalPlanGuid;
            uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref guid);

            if (result == 0)
            {
                Log.Information("[PowerPlanSwitcher] Crash recovery: restored power plan {Plan}", originalPlanGuid);
                return;
            }

            // Original plan gone - fall back to Balanced
            Log.Warning("[PowerPlanSwitcher] Crash recovery: original plan {Plan} missing, falling back to Balanced",
                originalPlanGuid);
            var balanced = BalancedGuid;
            NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref balanced);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PowerPlanSwitcher] Crash recovery: failed to restore power plan");
        }
    }

    // ── Performance plan discovery ────────────────────────────────────

    /// <summary>
    /// Finds or creates a high-performance power plan using a multi-step fallback:
    /// 1. Try Ultimate Performance GUID directly
    /// 2. Try High Performance GUID directly
    /// 3. Scan powercfg list for any plan with "Performance" in the name
    /// 4. Duplicate from Ultimate Performance template
    /// 5. Duplicate from High Performance template
    /// </summary>
    private Guid? FindOrCreatePerformancePlanSync()
    {
        // Step 1: Try Ultimate Performance (query only — no side effects)
        if (PlanExistsSync(UltimatePerformanceGuid))
            return UltimatePerformanceGuid;

        // Step 2: Try High Performance (query only — no side effects)
        if (PlanExistsSync(HighPerformanceGuid))
        {
            _logger.Information(
                "[PowerPlanSwitcher] Ultimate Performance unavailable, using High Performance");
            return HighPerformanceGuid;
        }

        // Step 3: Scan for any existing performance plan
        var (listOk, listOutput) = RunPowercfgSync("-list");
        if (listOk)
        {
            foreach (var line in listOutput.Split('\n'))
            {
                if (line.Contains("Performance", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("*")) // Skip the currently active plan marker
                {
                    var parsed = ExtractGuidFromLine(line);
                    if (parsed != null)
                    {
                        _logger.Information(
                            "[PowerPlanSwitcher] Found existing performance plan {Guid}", parsed.Value);
                        return parsed;
                    }
                }
            }
        }

        // Step 4: Duplicate from Ultimate Performance template
        var (dupOk, dupOutput) = RunPowercfgSync(
            $"-duplicatescheme {UltimatePerformanceGuid:D}");
        if (dupOk)
        {
            var newGuid = ExtractGuidFromLine(dupOutput);
            if (newGuid != null)
            {
                _logger.Information(
                    "[PowerPlanSwitcher] Created Ultimate Performance plan {Guid}", newGuid.Value);
                return newGuid;
            }
        }

        // Step 5: Duplicate from High Performance template
        var (dup2Ok, dup2Output) = RunPowercfgSync(
            $"-duplicatescheme {HighPerformanceGuid:D}");
        if (dup2Ok)
        {
            var newGuid = ExtractGuidFromLine(dup2Output);
            if (newGuid != null)
            {
                _logger.Information(
                    "[PowerPlanSwitcher] Created High Performance plan {Guid}", newGuid.Value);
                return newGuid;
            }
        }

        return null;
    }

    // ── Session sub-setting overrides ─────────────────────────────────

    /// <summary>
    /// Applies each session sub-setting's target value to the currently active plan.
    /// Assumes _appliedOverrides already contains the captured AC/DC original values.
    /// </summary>
    private void ApplySessionOverrides()
    {
        int applied = 0;

        foreach (var (subGroup, setting, value, desc) in SessionOverrides)
        {
            try
            {
                var (sOk, _) = RunPowercfgSync(
                    $"/setacvalueindex SCHEME_CURRENT {subGroup} {setting} {value}");
                if (sOk)
                    applied++;
                else
                    _logger.Warning(
                        "[PowerPlanSwitcher] powercfg /setacvalueindex failed for {Desc}", desc);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "[PowerPlanSwitcher] Failed to apply session override: {Desc}", desc);
            }
        }

        // Activate the changes
        RunPowercfgSync("/setactive SCHEME_CURRENT");

        _logger.Information(
            "[PowerPlanSwitcher] Applied {Count} session power sub-settings", applied);
    }

    /// <summary>
    /// Restores every captured sub-setting to its pre-apply AC and DC values on SCHEME_CURRENT.
    /// </summary>
    private void RevertSessionOverrides()
    {
        if (_appliedOverrides.Count == 0) return;

        foreach (var entry in _appliedOverrides)
        {
            try
            {
                RunPowercfgSync(
                    $"/setacvalueindex SCHEME_CURRENT {entry.SubGroup} {entry.Setting} {entry.AcValue}");
                RunPowercfgSync(
                    $"/setdcvalueindex SCHEME_CURRENT {entry.SubGroup} {entry.Setting} {entry.DcValue}");
            }
            catch (Exception ex)
            {
                _logger.Debug(ex,
                    "[PowerPlanSwitcher] Failed to revert sub-setting {SubGroup}/{Setting}",
                    entry.SubGroup, entry.Setting);
            }
        }

        RunPowercfgSync("/setactive SCHEME_CURRENT");
        _logger.Information(
            "[PowerPlanSwitcher] Reverted {Count} session power sub-settings", _appliedOverrides.Count);
        _appliedOverrides.Clear();
    }

    /// <summary>
    /// Reads the current AC and DC power setting indexes for a sub-setting on the active plan.
    /// Parses the 'powercfg /query SCHEME_CURRENT subGroup setting' output, which contains
    /// both 'Current AC Power Setting Index' and 'Current DC Power Setting Index' lines.
    /// </summary>
    private static (int acValue, int dcValue) ReadSubSettingValues(string subGroup, string setting)
    {
        int ac = 0;
        int dc = 0;

        var (ok, output) = RunPowercfgSync($"/query SCHEME_CURRENT {subGroup} {setting}");
        if (!ok)
            return (ac, dc);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("Current AC Power Setting Index"))
            {
                var hex = line.Split(':').LastOrDefault()?.Trim();
                if (hex != null && hex.StartsWith("0x") &&
                    int.TryParse(hex[2..], global::System.Globalization.NumberStyles.HexNumber,
                        global::System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    ac = parsed;
                }
            }
            else if (line.Contains("Current DC Power Setting Index"))
            {
                var hex = line.Split(':').LastOrDefault()?.Trim();
                if (hex != null && hex.StartsWith("0x") &&
                    int.TryParse(hex[2..], global::System.Globalization.NumberStyles.HexNumber,
                        global::System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    dc = parsed;
                }
            }
        }

        return (ac, dc);
    }

    /// <summary>
    /// Queries the currently active power plan GUID by shelling out to 'powercfg /getactivescheme'.
    /// Returns Guid.Empty on failure. Used when a SystemStateSnapshot is not available (e.g.,
    /// tests or watchdog paths that construct a fresh PowerPlanSwitcher).
    /// </summary>
    private static Guid GetActivePowerPlanGuid()
    {
        var (ok, output) = RunPowercfgSync("/getactivescheme");
        if (!ok) return Guid.Empty;

        var parsed = ExtractGuidFromLine(output);
        return parsed ?? Guid.Empty;
    }

    // ── Process helpers ───────────────────────────────────────────────

    /// <summary>
    /// Synchronously invokes powercfg.exe from %SystemRoot%\System32 (absolute path to
    /// prevent PATH hijacking) and returns (success, stdout). stderr is drained on a
    /// worker thread to avoid the classic pipe-deadlock.
    /// </summary>
    private static (bool success, string output) RunPowercfgSync(string arguments)
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
            if (process == null)
                return (false, "Failed to start powercfg");

            string stderr = "";
            var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
            string stdout = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(10_000);
            process.WaitForExit(10_000);

            return (process.ExitCode == 0, stdout.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Checks if a power plan GUID exists without activating it.
    /// Uses 'powercfg /query {GUID}' which returns exit code 0 if the plan exists.
    /// </summary>
    private static bool PlanExistsSync(Guid planGuid)
    {
        var (ok, _) = RunPowercfgSync($"/query {planGuid:D}");
        return ok;
    }

    private static Guid? ExtractGuidFromLine(string line)
    {
        // powercfg output format: "Power Scheme GUID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx  (Name)"
        var parts = line.Split(' ');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length >= 36 && trimmed.Contains('-') &&
                Guid.TryParse(trimmed[..36], out var guid))
            {
                return guid;
            }
        }
        return null;
    }

    // ── Result helpers ────────────────────────────────────────────────────────

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);
}
