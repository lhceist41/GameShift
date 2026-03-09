using System.Diagnostics;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Switches Windows power plan to Ultimate Performance for maximum gaming performance.
/// Switches to Ultimate Performance power plan during gaming sessions.
/// </summary>
public class PowerPlanSwitcher : IOptimization
{
    /// <summary>
    /// GUID for the Ultimate Performance power plan.
    /// This is a hidden plan in Windows 10/11 that must be activated via powercfg.
    /// </summary>
    private static readonly Guid UltimatePerformanceGuid = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public const string OptimizationId = "Ultimate Performance Power Plan";

    public string Name => OptimizationId;

    public string Description => "Switches to Ultimate Performance power plan for maximum CPU/GPU performance";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // Will create plan if missing

    /// <summary>
    /// Activates the Ultimate Performance power plan.
    /// Creates the plan via powercfg if it doesn't exist.
    /// Records original plan in snapshot before switching.
    /// </summary>
    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            // Log the original power plan (already captured during snapshot creation)
            SettingsManager.Logger.Information(
                "[PowerPlanSwitcher] Original power plan GUID: {OriginalPlan}",
                snapshot.OriginalPowerPlan);

            // Try to activate Ultimate Performance plan
            var ultimateGuid = UltimatePerformanceGuid;
            uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref ultimateGuid);

            if (result == 0)
            {
                // Success
                SettingsManager.Logger.Information(
                    "[PowerPlanSwitcher] Activated Ultimate Performance plan");
                IsApplied = true;
                return true;
            }

            // Plan doesn't exist - create it via powercfg
            SettingsManager.Logger.Information(
                "[PowerPlanSwitcher] Ultimate Performance plan not found (error {ErrorCode}), creating it via powercfg",
                result);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = "/duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    SettingsManager.Logger.Error("[PowerPlanSwitcher] Failed to start powercfg process");
                    return false;
                }

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    SettingsManager.Logger.Error(
                        "[PowerPlanSwitcher] powercfg failed with exit code {ExitCode}: {Error}",
                        process.ExitCode,
                        error);
                    return false;
                }

                SettingsManager.Logger.Information("[PowerPlanSwitcher] Successfully created Ultimate Performance plan");
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Error(
                    ex,
                    "[PowerPlanSwitcher] Failed to execute powercfg to create plan");
                return false;
            }

            // Retry activation after creating the plan
            result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref ultimateGuid);
            if (result == 0)
            {
                SettingsManager.Logger.Information(
                    "[PowerPlanSwitcher] Activated newly created Ultimate Performance plan");
                IsApplied = true;
                return true;
            }
            else
            {
                SettingsManager.Logger.Error(
                    "[PowerPlanSwitcher] Failed to activate plan after creation (error {ErrorCode})",
                    result);
                return false;
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[PowerPlanSwitcher] Failed to apply power plan");
            return false;
        }
    }

    /// <summary>
    /// Reverts to the original power plan captured in the snapshot.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        try
        {
            if (snapshot.OriginalPowerPlan == Guid.Empty)
            {
                SettingsManager.Logger.Warning(
                    "[PowerPlanSwitcher] No original power plan recorded in snapshot, skipping revert");
                IsApplied = false;
                return Task.FromResult(true); // Not a fatal error
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
            else
            {
                SettingsManager.Logger.Error(
                    "[PowerPlanSwitcher] Failed to revert to original plan (error {ErrorCode})",
                    result);
                IsApplied = false; // Mark as not applied even on failure
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[PowerPlanSwitcher] Failed to revert power plan");
            IsApplied = false;
            return Task.FromResult(false);
        }
    }
}
