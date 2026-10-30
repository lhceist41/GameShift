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
                }
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
    /// Missing values (null or absent) are skipped - nothing to restore.
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

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static string? RunPowercfg(string arguments)
    {
        try
        {
            var result = ProcessRunner.Run(NativeInterop.SystemExePath("powercfg.exe"), arguments, 5000);
            // null only when the process could not start (callers null-check); otherwise return the
            // captured stdout (callers regex-parse it). The runner also reaps a wedged child.
            return result.Exited || result.TimedOut ? result.StdOut : null;
        }
        catch
        {
            return null;
        }
    }
}
