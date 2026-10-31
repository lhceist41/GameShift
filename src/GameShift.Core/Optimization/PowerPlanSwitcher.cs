using System.Runtime.InteropServices;
using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Switches from Balanced to the High Performance plan during a game and back afterwards. Any other
/// active plan is the user's choice and is left alone, and so is Balanced on a Ryzen X3D with two
/// CCDs, where AMD's chipset driver needs Balanced to keep games on the V-Cache CCD.
///
/// Earlier versions also overrode plan sub-settings through powercfg. RevertFromRecord still restores
/// them from a journal record that carries them.
/// </summary>
public class PowerPlanSwitcher : IOptimization, IJournaledOptimization
{
    private static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public const string OptimizationId = "Ultimate Performance Power Plan";

    private readonly ILogger _logger = SettingsManager.Logger;

    public string Name => OptimizationId;
    public string Description => "Switches from Balanced to the High Performance power plan during a game";
    public bool IsApplied { get; private set; }
    public bool IsAvailable => true;

    // Original plan GUID captured at apply time (used by in-process Revert()).
    private Guid _originalPlanGuid = Guid.Empty;

    // The active plan when CanApply() allowed the switch, Guid.Empty when it did not.
    private Guid _planToLeave = Guid.Empty;

    // Seams so apply and revert can be tested without powercfg or the real power scheme.
    internal Func<string, (bool success, string output)> Powercfg { get; init; } = RunPowercfgSync;
    internal Func<Guid, uint> SetActiveScheme { get; init; } = SetActiveSchemeNative;
    internal Func<Guid> GetActiveScheme { get; init; } = () => SystemStateSnapshot.Capture().OriginalPowerPlan;
    internal Func<Guid, bool> SchemeExists { get; init; } = SchemeExistsNative;
    internal Func<bool> IsDualCcdX3D { get; init; } = () => HybridCpuDetector.DetectTopology().VCacheCcdIndex != null;

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
    /// Allows the switch only from Balanced, not on a dual-CCD X3D, and only when Windows has the
    /// High Performance plan.
    /// </summary>
    public bool CanApply(SystemContext context)
    {
        _planToLeave = Guid.Empty;
        var active = context.Snapshot.OriginalPowerPlan != Guid.Empty
            ? context.Snapshot.OriginalPowerPlan
            : GetActiveScheme();

        if (!ShouldSwitch(active, IsDualCcdX3D))
        {
            _logger.Information(
                "[PowerPlanSwitcher] Leaving power plan {Plan} active: only Balanced is switched, and not on a dual-CCD X3D CPU",
                active);
            return false;
        }

        if (!SchemeExists(HighPerformanceGuid))
        {
            _logger.Information("[PowerPlanSwitcher] Windows has no High Performance plan; leaving Balanced active");
            return false;
        }

        _planToLeave = active;
        return true;
    }

    /// <summary>
    /// Switch only from Balanced and not on a dual-CCD X3D. The CPU is checked only for Balanced,
    /// because reading the topology is not free.
    /// </summary>
    internal static bool ShouldSwitch(Guid activePlan, Func<bool> isDualCcdX3D) =>
        activePlan == BalancedGuid && !isDualCcdX3D();

    /// <summary>
    /// Activates High Performance and journals the plan it replaced.
    /// </summary>
    public OptimizationResult Apply()
    {
        if (_planToLeave == Guid.Empty)
            return Fail("CanApply did not allow a plan switch");

        try
        {
            uint result = SetActiveScheme(HighPerformanceGuid);
            if (result != 0)
            {
                _logger.Error(
                    "[PowerPlanSwitcher] Failed to activate High Performance (error {ErrorCode})", result);
                return Fail($"PowerSetActiveScheme failed with error {result}");
            }

            _originalPlanGuid = _planToLeave;
            IsApplied = true;
            _logger.Information(
                "[PowerPlanSwitcher] Switched from {OriginalPlan} to High Performance", _originalPlanGuid);

            var serialized = JsonSerializer.Serialize(new
            {
                originalPlanGuid = _originalPlanGuid.ToString(),
                targetPlanGuid = HighPerformanceGuid.ToString()
            });

            var applied = JsonSerializer.Serialize(new
            {
                activatedPlanGuid = HighPerformanceGuid.ToString()
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
    /// Switches back to the plan that was active before Apply().
    /// </summary>
    public OptimizationResult Revert()
    {
        try
        {
            if (_originalPlanGuid == Guid.Empty)
            {
                _logger.Warning(
                    "[PowerPlanSwitcher] No original power plan recorded, skipping plan revert");
                IsApplied = false;
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
            }

            uint result = SetActiveScheme(_originalPlanGuid);
            IsApplied = false;

            if (result != 0)
            {
                _logger.Error(
                    "[PowerPlanSwitcher] Failed to reactivate power plan {OriginalPlan} (error {ErrorCode})",
                    _originalPlanGuid, result);
                return RevertFail($"Failed to reactivate the original power plan (error {result})");
            }

            _logger.Information(
                "[PowerPlanSwitcher] Reverted to original power plan {OriginalPlan}", _originalPlanGuid);
            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
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
    /// </summary>
    public bool Verify()
    {
        if (!IsApplied)
            return false;

        try
        {
            var current = GetActiveScheme();
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
            if (state.ValueKind != JsonValueKind.Object)
                return RevertFail("Failed to parse originalValueJson");

            // Restore each sub-setting's AC/DC values on the plan they came from. Records written
            // before the target plan was journaled fall back to the active plan.
            string scheme = SchemeArgument(Guid.Empty);
            if (state.TryGetProperty("targetPlanGuid", out var targetElement) && targetElement.ValueKind != JsonValueKind.Null)
            {
                if (targetElement.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(targetElement.GetString(), out var targetPlan))
                    return RevertFail("Refused a journal record whose target plan is not a GUID");
                scheme = SchemeArgument(targetPlan);
            }

            var problems = new List<string>();
            if (state.TryGetProperty("subSettings", out var subSettingsElement) &&
                subSettingsElement.ValueKind == JsonValueKind.Array)
            {
                int written = 0;
                int refused = 0;
                foreach (var sub in subSettingsElement.EnumerateArray())
                {
                    // powercfg only ever receives canonical GUIDs and parsed numbers from a record.
                    if (!RecoveryInputRules.TryGuidProperty(sub, "subGroup", out var subGroup) ||
                        !RecoveryInputRules.TryGuidProperty(sub, "setting", out var setting) ||
                        !RecoveryInputRules.TryInt32Property(sub, "acValue", out int ac) ||
                        !RecoveryInputRules.TryInt32Property(sub, "dcValue", out int dc))
                    {
                        _logger.Warning("[PowerPlanSwitcher] Refused a sub-setting GameShift did not write: {Entry}", sub.GetRawText());
                        refused++;
                        continue;
                    }

                    written++;
                    bool acRestored = Powercfg($"/setacvalueindex {scheme} {subGroup} {setting} {ac}").success;
                    bool dcRestored = Powercfg($"/setdcvalueindex {scheme} {subGroup} {setting} {dc}").success;
                    if (!acRestored || !dcRestored)
                    {
                        _logger.Warning("[PowerPlanSwitcher] Failed to restore sub-setting {SubGroup}/{Setting}", subGroup, setting);
                        problems.Add($"power setting {setting}");
                    }
                }

                // Activate the restored sub-setting changes.
                if (written > 0 && !Powercfg("/setactive SCHEME_CURRENT").success)
                    problems.Add("reactivating the restored power settings");
                if (refused > 0)
                    problems.Add($"{refused} refused power setting entr{(refused == 1 ? "y" : "ies")}");
            }

            // Switch back to the original plan GUID.
            if (state.TryGetProperty("originalPlanGuid", out var guidElement) &&
                guidElement.ValueKind == JsonValueKind.String &&
                Guid.TryParse(guidElement.GetString(), out var originalGuid) &&
                originalGuid != Guid.Empty)
            {
                uint result = SetActiveScheme(originalGuid);
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
                    uint fallback = SetActiveScheme(BalancedGuid);
                    problems.Add(fallback == 0
                        ? $"original power plan {originalGuid} (Balanced is active instead)"
                        : $"original power plan {originalGuid} (Balanced could not be activated either)");
                }
            }
            else
            {
                _logger.Warning(
                    "[PowerPlanSwitcher] No originalPlanGuid in journal record, skipping plan revert");
                problems.Add("original power plan (not in the journal record)");
            }

            IsApplied = false;
            return problems.Count == 0
                ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
                : RevertFail($"Not restored: {string.Join(", ", problems)}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[PowerPlanSwitcher] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
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
            var result = ProcessRunner.Run(NativeInterop.SystemExePath("powercfg.exe"), arguments, 10_000);
            if (!result.Exited)
                return (false, result.TimedOut ? "powercfg timed out" : "Failed to start powercfg");

            return (result.ExitCode == 0, result.StdOut.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// True when Windows lists the scheme. Modern Standby PCs often have only Balanced.
    /// </summary>
    private static bool SchemeExistsNative(Guid scheme)
    {
        for (uint index = 0; ; index++)
        {
            var listed = Guid.Empty;
            uint size = (uint)Marshal.SizeOf<Guid>();
            if (NativeInterop.PowerEnumerate(
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeInterop.ACCESS_SCHEME, index, ref listed, ref size) != 0)
                return false;
            if (listed == scheme)
                return true;
        }
    }

    private static string SchemeArgument(Guid plan) =>
        plan == Guid.Empty ? "SCHEME_CURRENT" : plan.ToString("D");

    private static uint SetActiveSchemeNative(Guid scheme) =>
        NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref scheme);

    // ── Result helpers ────────────────────────────────────────────────────────

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);
}
