using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Core.Optimization;

/// <summary>
/// GameShift no longer changes core parking or processor idle settings during a game. The class stays
/// so a session journal written by an earlier version still restores the recorded power settings.
/// </summary>
public class CpuParkingManager : IOptimization, IJournaledOptimization
{
    /// <summary>
    /// Processor Power Management sub-group GUID.
    /// </summary>
    private const string ProcessorSubGroupGuid = "54533251-82be-4824-96c1-47b60b740d00";

    public const string OptimizationId = "CPU Core Unparking";

    public string Name => OptimizationId;
    public string Description => "Revert-only: restores core parking settings recorded by earlier versions";
    public bool IsApplied { get; private set; }
    public bool IsAvailable => false;

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile) => Task.FromResult(false);
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot) => Task.FromResult(true);

    public bool CanApply(SystemContext context) => false;

    public OptimizationResult Apply() =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, "Revert-only: no longer applied");

    public OptimizationResult Revert() =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);

    public bool Verify() => false;

    /// <summary>Runs powercfg; true on exit code 0. A seam so the revert runs without powercfg.</summary>
    internal Func<string, bool> Powercfg { get; init; } = RunPowercfg;

    /// <summary>
    /// Watchdog recovery path: parses the serialized original parking state from the journal
    /// and restores AC/DC values for every tracked setting via powercfg, without requiring
    /// any live instance fields. Idempotent - writing the same values twice is safe, so
    /// coexisting with the snapshot-based boot recovery path is fine.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            SettingsManager.Logger.Information(
                "[CpuParkingManager] Reverting from journal record (watchdog recovery)");

            var payload = JsonSerializer.Deserialize<JsonElement>(originalValueJson);

            // powercfg only ever receives canonical GUIDs and parsed numbers from a record.
            if (!RecoveryInputRules.TryGuidProperty(payload, "schemeGuid", out var schemeGuid))
                return RevertFail("Missing schemeGuid field");

            var problems = new List<string>();
            int refused = 0;

            // Revert core parking settings, then the C-state limiting originals if they were captured.
            foreach (var (property, rehide) in new[] { ("entries", false), ("cStateOriginals", true) })
            {
                if (!payload.TryGetProperty(property, out var listElement) || listElement.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in listElement.EnumerateArray())
                {
                    if (!RecoveryInputRules.TryGuidProperty(entry, "settingGuid", out var settingGuid) ||
                        !TryOriginalIndex(entry, "originalAc", out int? ac) ||
                        !TryOriginalIndex(entry, "originalDc", out int? dc))
                    {
                        SettingsManager.Logger.Warning(
                            "[CpuParkingManager] Refused a setting GameShift did not write: {Entry}", entry.GetRawText());
                        refused++;
                        continue;
                    }

                    bool restored = true;
                    if (ac.HasValue)
                        restored &= Powercfg($"/setacvalueindex {schemeGuid} {ProcessorSubGroupGuid} {settingGuid} {ac.Value}");
                    if (dc.HasValue)
                        restored &= Powercfg($"/setdcvalueindex {schemeGuid} {ProcessorSubGroupGuid} {settingGuid} {dc.Value}");

                    // Re-hide the setting so it goes back to being an advanced hidden option.
                    if (rehide)
                        restored &= Powercfg($"-attributes {ProcessorSubGroupGuid} {settingGuid} +ATTRIB_HIDE");

                    if (!restored)
                        problems.Add($"power setting {settingGuid}");
                }
            }

            // Activate restored settings.
            if (!Powercfg($"/setactive {schemeGuid}"))
                problems.Add($"reactivating power plan {schemeGuid}");
            if (refused > 0)
                problems.Add($"{refused} refused setting entr{(refused == 1 ? "y" : "ies")}");

            IsApplied = false;
            return problems.Count == 0
                ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
                : RevertFail($"Not restored: {string.Join(", ", problems)}");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[CpuParkingManager] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// A recorded original index: absent or null means it was not captured and is left alone.
    /// </summary>
    private static bool TryOriginalIndex(JsonElement entry, string name, out int? index)
    {
        index = null;
        if (!entry.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String || !RecoveryInputRules.TryPowerIndex(element.GetString(), out int value))
            return false;

        index = value;
        return true;
    }

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static bool RunPowercfg(string arguments)
    {
        try
        {
            var result = ProcessRunner.Run(NativeInterop.SystemExePath("powercfg.exe"), arguments, 5000);
            return result.Exited && result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
