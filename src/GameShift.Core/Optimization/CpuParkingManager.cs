using System.Diagnostics;
using System.Text.RegularExpressions;
using GameShift.Core.BackgroundMode;
using GameShift.Core.Config;
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
/// </summary>
public class CpuParkingManager : IOptimization
{
    /// <summary>
    /// Processor Power Management sub-group GUID.
    /// </summary>
    private const string ProcessorSubGroupGuid = "54533251-82be-4824-96c1-47b60b740d00";

    /// <summary>
    /// IDLEDISABLE setting GUID — forces all cores to C0 state when set to 1.
    /// </summary>
    private const string IdleDisableGuid = "5d76a2ca-e8c0-402f-a133-2158492d58ad";

    /// <summary>
    /// Performance time check interval setting GUID.
    /// </summary>
    private const string TimeCheckIntervalGuid = "4d2b0152-7d5c-498b-88e2-34345392a2c5";

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

    public string Name => "CPU Core Unparking";

    public string Description => "Keeps all CPU cores active during gaming to prevent unparking latency";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // powercfg is always available on Windows

    /// <summary>
    /// Unparks all CPU cores by setting aggressive parking values on the active power plan.
    /// Captures original values for both AC and DC power, sets both to gaming-optimal values.
    /// Records the active scheme GUID and original values in snapshot for crash recovery.
    /// Also disables processor idle (forces C0 state) if the game profile allows it.
    /// Uses vendor-aware parking values for AMD dual-CCD X3D processors.
    /// </summary>
    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        await Task.CompletedTask;

        try
        {
            // Get the currently active power scheme GUID
            _activeSchemeGuid = GetActiveSchemeGuid();
            if (_activeSchemeGuid == null)
            {
                SettingsManager.Logger.Error("CpuParkingManager: Failed to get active power scheme GUID");
                return false;
            }

            SettingsManager.Logger.Information(
                "CpuParkingManager: Active power scheme: {SchemeGuid}", _activeSchemeGuid);

            // Detect CPU profile for vendor-aware parking values
            var cpuProfile = PowerPlanConfigurator.DetectCpuProfile();
            var (cpMinCores, concurrencyThreshold) = PowerPlanConfigurator.GetParkingValuesForProfile(cpuProfile);

            SettingsManager.Logger.Information(
                "CpuParkingManager: CPU profile={Profile}, CPMINCORES={MinCores}, ConcurrencyThreshold={Threshold}",
                cpuProfile, cpMinCores, concurrencyThreshold);

            // Build vendor-aware parking settings
            var parkingSettings = new (string Guid, string Name, int TargetValue)[]
            {
                ("0cc5b647-c1df-4637-891a-dec35c318583", "CPMINCORES", cpMinCores),
                ("ea062031-0e34-4ff1-9b6d-eb1059334028", "CPMAXCORES", 100),
                ("893dee8e-2bef-41e0-89c6-b55d0929964c", "MinProcessorState", 100),
                ("2430ab6f-a520-44a2-9601-f7f23b5134b1", "ConcurrencyThreshold", concurrencyThreshold),
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
                        "CpuParkingManager: Set {Setting}={Value} (original AC={OrigAc}, DC={OrigDc})",
                        settingName, targetValue, originalAc ?? "null", originalDc ?? "null");
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(ex,
                        "CpuParkingManager: Failed to set {Setting}", settingName);
                }
            }

            // === Processor Idle Disable (session toggle) ===
            if (profile.DisableProcessorIdle)
            {
                ApplyIdleDisable();
                snapshot.RecordIdleDisableState(_activeSchemeGuid);
            }

            // Apply changes to the active scheme
            RunPowercfg($"/setactive {_activeSchemeGuid}");

            // Record in snapshot for crash recovery
            snapshot.RecordCpuParkingState(
                _activeSchemeGuid,
                _originalStates.Select(s => new CpuParkingSnapshotEntry(
                    s.SettingGuid, s.OriginalAcValue, s.OriginalDcValue)).ToList());

            SettingsManager.Logger.Information(
                "CpuParkingManager: All CPU cores unparked — {Count} settings applied, idle disable={IdleDisable}",
                _originalStates.Count, _idleDisableApplied);

            IsApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "CpuParkingManager: Apply failed");
            return false;
        }
    }

    /// <summary>
    /// Restores original parking settings for both AC and DC power.
    /// Re-enables processor idle and restores time check interval to responsive value.
    /// </summary>
    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        await Task.CompletedTask;

        try
        {
            if (_activeSchemeGuid == null)
            {
                SettingsManager.Logger.Warning(
                    "CpuParkingManager: No active scheme GUID recorded, skipping revert");
                IsApplied = false;
                return true;
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
                        "CpuParkingManager: Restored {Setting} to AC={AcValue}, DC={DcValue}",
                        state.SettingName, state.OriginalAcValue ?? "null", state.OriginalDcValue ?? "null");
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(ex,
                        "CpuParkingManager: Failed to restore {Setting}", state.SettingName);
                }
            }

            // === Revert Processor Idle Disable ===
            if (_idleDisableApplied)
            {
                RevertIdleDisable();
            }

            // Apply restored settings
            RunPowercfg($"/setactive {_activeSchemeGuid}");

            SettingsManager.Logger.Information(
                "CpuParkingManager: CPU parking settings restored ({Count} settings)",
                _originalStates.Count);

            _originalStates.Clear();
            IsApplied = false;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "CpuParkingManager: Revert failed");
            IsApplied = false;
            return false;
        }
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
            // Re-enable processor idle (set IDLEDISABLE to 0)
            RunPowercfg($"/setacvalueindex {schemeGuid} {ProcessorSubGroupGuid} {IdleDisableGuid} 0");
            RunPowercfg($"/setdcvalueindex {schemeGuid} {ProcessorSubGroupGuid} {IdleDisableGuid} 0");

            // Restore time check interval to responsive value
            RunPowercfg($"/setacvalueindex {schemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 15");
            RunPowercfg($"/setdcvalueindex {schemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 15");

            RunPowercfg($"/setactive {schemeGuid}");

            SettingsManager.Logger.Information(
                "CpuParkingManager: Cleaned up stale idle disable state for scheme {Guid}", schemeGuid);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex,
                "CpuParkingManager: Failed to clean up stale idle disable state");
        }
    }

    // ── Processor Idle Disable Helpers ─────────────────────────────────

    /// <summary>
    /// Disables processor idle (forces C0 state) and sets time check interval to 5000ms.
    /// Called during gaming session start.
    /// </summary>
    private void ApplyIdleDisable()
    {
        if (_activeSchemeGuid == null) return;

        try
        {
            // Disable processor idle (IDLEDISABLE = 1)
            RunPowercfg($"/setacvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {IdleDisableGuid} 1");
            RunPowercfg($"/setdcvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {IdleDisableGuid} 1");

            // Set time check interval to 5000ms (CPU locked at max, no need for frequent checks)
            RunPowercfg($"/setacvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 5000");
            RunPowercfg($"/setdcvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 5000");

            _idleDisableApplied = true;
            SettingsManager.Logger.Information("CpuParkingManager: Processor idle disabled (C0 forced) for gaming session");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "CpuParkingManager: Failed to disable processor idle");
        }
    }

    /// <summary>
    /// Re-enables processor idle and restores time check interval to 15ms.
    /// Called when gaming session ends.
    /// </summary>
    private void RevertIdleDisable()
    {
        if (_activeSchemeGuid == null) return;

        try
        {
            // Re-enable processor idle (IDLEDISABLE = 0)
            RunPowercfg($"/setacvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {IdleDisableGuid} 0");
            RunPowercfg($"/setdcvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {IdleDisableGuid} 0");

            // Restore time check interval to 15ms (responsive frequency scaling in desktop mode)
            RunPowercfg($"/setacvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 15");
            RunPowercfg($"/setdcvalueindex {_activeSchemeGuid} {ProcessorSubGroupGuid} {TimeCheckIntervalGuid} 15");

            _idleDisableApplied = false;
            SettingsManager.Logger.Information("CpuParkingManager: Processor idle re-enabled (C-states restored)");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "CpuParkingManager: Failed to re-enable processor idle");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

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
                FileName = "powercfg",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
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
