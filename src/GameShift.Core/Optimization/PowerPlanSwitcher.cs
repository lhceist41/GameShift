using System.Diagnostics;
using GameShift.Core.BackgroundMode;
using GameShift.Core.Config;
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
/// Crash recovery: static CleanupStalePowerPlan restores a saved GUID from the snapshot.
/// </summary>
public class PowerPlanSwitcher : IOptimization
{
    private static readonly Guid UltimatePerformanceGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public const string OptimizationId = "Ultimate Performance Power Plan";

    public string Name => OptimizationId;
    public string Description => "Switches to Ultimate Performance power plan for maximum CPU/GPU performance";
    public bool IsApplied { get; private set; }
    public bool IsAvailable => true;

    private readonly List<(string subGroup, string setting, string? previousValue)> _appliedOverrides = new();

    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            SettingsManager.Logger.Information(
                "[PowerPlanSwitcher] Original power plan GUID: {OriginalPlan}",
                snapshot.OriginalPowerPlan);

            var targetGuidNullable = await FindOrCreatePerformancePlan();
            if (targetGuidNullable == null)
            {
                SettingsManager.Logger.Error(
                    "[PowerPlanSwitcher] Could not find or create any high-performance power plan");
                return false;
            }

            var targetGuid = targetGuidNullable.Value;
            uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref targetGuid);
            if (result != 0)
            {
                SettingsManager.Logger.Error(
                    "[PowerPlanSwitcher] Failed to activate plan {Plan} (error {ErrorCode})",
                    targetGuid, result);
                return false;
            }

            SettingsManager.Logger.Information(
                "[PowerPlanSwitcher] Activated performance plan {Plan}", targetGuid);

            // Apply key sub-settings to the active plan for maximum gaming performance
            ApplySessionOverrides();

            IsApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[PowerPlanSwitcher] Failed to apply power plan");
            return false;
        }
    }

    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        try
        {
            // Revert session sub-setting overrides first
            RevertSessionOverrides();

            if (snapshot.OriginalPowerPlan == Guid.Empty)
            {
                SettingsManager.Logger.Warning(
                    "[PowerPlanSwitcher] No original power plan recorded, skipping revert");
                IsApplied = false;
                return Task.FromResult(true);
            }

            var originalGuid = snapshot.OriginalPowerPlan;
            uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref originalGuid);

            if (result == 0)
            {
                SettingsManager.Logger.Information(
                    "[PowerPlanSwitcher] Reverted to original power plan {OriginalPlan}",
                    snapshot.OriginalPowerPlan);
                IsApplied = false;
                return Task.FromResult(true);
            }

            // Original plan was deleted - fall back to Balanced
            SettingsManager.Logger.Warning(
                "[PowerPlanSwitcher] Original plan {OriginalPlan} no longer exists (error {ErrorCode}), falling back to Balanced",
                snapshot.OriginalPowerPlan, result);

            var balanced = BalancedGuid;
            result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref balanced);

            if (result == 0)
            {
                SettingsManager.Logger.Information("[PowerPlanSwitcher] Fell back to Balanced power plan");
            }
            else
            {
                SettingsManager.Logger.Error(
                    "[PowerPlanSwitcher] Failed to activate Balanced plan (error {ErrorCode})", result);
            }

            IsApplied = false;
            return Task.FromResult(result == 0);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[PowerPlanSwitcher] Failed to revert power plan");
            IsApplied = false;
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Static crash recovery: restores the original power plan from a saved snapshot GUID.
    /// Called from App.xaml.cs crash recovery path and can also be used by the watchdog.
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
    private static async Task<Guid?> FindOrCreatePerformancePlan()
    {
        // Step 1: Try Ultimate Performance (query only — no side effects)
        if (await PlanExistsAsync(UltimatePerformanceGuid))
            return UltimatePerformanceGuid;

        // Step 2: Try High Performance (query only — no side effects)
        if (await PlanExistsAsync(HighPerformanceGuid))
        {
            SettingsManager.Logger.Information(
                "[PowerPlanSwitcher] Ultimate Performance unavailable, using High Performance");
            return HighPerformanceGuid;
        }

        // Step 3: Scan for any existing performance plan
        var (listOk, listOutput) = await RunPowercfg("-list");
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
                        SettingsManager.Logger.Information(
                            "[PowerPlanSwitcher] Found existing performance plan {Guid}", parsed.Value);
                        return parsed;
                    }
                }
            }
        }

        // Step 4: Duplicate from Ultimate Performance template
        var (dupOk, dupOutput) = await RunPowercfg(
            $"-duplicatescheme {UltimatePerformanceGuid:D}");
        if (dupOk)
        {
            var newGuid = ExtractGuidFromLine(dupOutput);
            if (newGuid != null)
            {
                SettingsManager.Logger.Information(
                    "[PowerPlanSwitcher] Created Ultimate Performance plan {Guid}", newGuid.Value);
                return newGuid;
            }
        }

        // Step 5: Duplicate from High Performance template
        var (dup2Ok, dup2Output) = await RunPowercfg(
            $"-duplicatescheme {HighPerformanceGuid:D}");
        if (dup2Ok)
        {
            var newGuid = ExtractGuidFromLine(dup2Output);
            if (newGuid != null)
            {
                SettingsManager.Logger.Information(
                    "[PowerPlanSwitcher] Created High Performance plan {Guid}", newGuid.Value);
                return newGuid;
            }
        }

        return null;
    }

    // ── Session sub-setting overrides ─────────────────────────────────

    /// <summary>
    /// Applies key power sub-settings to SCHEME_CURRENT for the gaming session.
    /// These are a subset of PowerPlanConfigurator's full list - the most impactful
    /// settings that competitive gamers need regardless of Background Mode status.
    /// </summary>
    private void ApplySessionOverrides()
    {
        const string subProcessor = "54533251-82be-4824-96c1-47b60b740d00";
        const string subUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
        const string subPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
        const string subDisk = "0012ee47-9041-4b5d-9b77-535fba8b1442";
        const string subWireless = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";

        var overrides = new (string subGroup, string setting, int value, string desc)[]
        {
            // EPP = 0 (maximum performance)
            (subProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", 0, "EPP"),
            (subProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6864", 0, "EPP P-cores"),
            // Boost policy = 100%
            (subProcessor, "45bcc044-d885-43e2-8605-ee0ec6e96b59", 100, "Boost policy"),
            // USB selective suspend = disabled
            (subUsb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, "USB selective suspend"),
            // USB 3 link power = off
            (subUsb, "d4e98f31-5ffe-4ce1-be31-1b38b384c009", 0, "USB 3 link power"),
            // PCI Express ASPM = off
            (subPciExpress, "ee12f906-d277-404b-b6da-e5fa1a576df5", 0, "PCIe ASPM"),
            // NVMe primary idle timeout = 0
            (subDisk, "d639518a-e56d-4345-8af2-b9f32fb26109", 0, "NVMe idle timeout"),
            // Wireless power saving = max performance
            (subWireless, "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0, "Wireless power saving"),
        };

        _appliedOverrides.Clear();

        foreach (var (subGroup, setting, value, desc) in overrides)
        {
            try
            {
                // Read current value for revert
                var (qOk, qOut) = RunPowercfgSync($"/query SCHEME_CURRENT {subGroup} {setting}");
                string? prevVal = null;
                if (qOk)
                {
                    foreach (var line in qOut.Split('\n'))
                    {
                        if (line.Contains("Current AC Power Setting Index"))
                        {
                            var hexPart = line.Split(':').LastOrDefault()?.Trim();
                            if (hexPart != null && hexPart.StartsWith("0x"))
                                prevVal = Convert.ToInt32(hexPart, 16).ToString();
                            break;
                        }
                    }
                }

                // Apply override
                var (sOk, _) = RunPowercfgSync(
                    $"/setacvalueindex SCHEME_CURRENT {subGroup} {setting} {value}");
                if (sOk)
                    _appliedOverrides.Add((subGroup, setting, prevVal));
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Warning(ex,
                    "[PowerPlanSwitcher] Failed to apply session override: {Desc}", desc);
            }
        }

        // Activate the changes
        RunPowercfgSync("/setactive SCHEME_CURRENT");

        SettingsManager.Logger.Information(
            "[PowerPlanSwitcher] Applied {Count} session power sub-settings", _appliedOverrides.Count);
    }

    private void RevertSessionOverrides()
    {
        if (_appliedOverrides.Count == 0) return;

        foreach (var (subGroup, setting, prevVal) in _appliedOverrides)
        {
            if (prevVal == null) continue;
            try
            {
                RunPowercfgSync($"/setacvalueindex SCHEME_CURRENT {subGroup} {setting} {prevVal}");
            }
            catch { }
        }

        RunPowercfgSync("/setactive SCHEME_CURRENT");
        SettingsManager.Logger.Information(
            "[PowerPlanSwitcher] Reverted {Count} session power sub-settings", _appliedOverrides.Count);
        _appliedOverrides.Clear();
    }

    // ── Process helpers ───────────────────────────────────────────────

    private static async Task<(bool success, string output)> RunPowercfg(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "Failed to start powercfg");

            // Read stdout and stderr concurrently to avoid pipe deadlock
            string stderr = "";
            var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await stderrTask;

            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                return (false, "powercfg timed out");
            }

            return (process.ExitCode == 0, stdout.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool success, string output) RunPowercfgSync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
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
    private static async Task<bool> PlanExistsAsync(Guid planGuid)
    {
        var (ok, _) = await RunPowercfg($"/query {planGuid:D}");
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
}
