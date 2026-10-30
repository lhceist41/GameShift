using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Core.Optimization;

/// <summary>
/// GameShift no longer disables scheduled tasks during a game. The class stays so a session journal
/// written by an earlier version still re-enables the tasks it recorded.
/// </summary>
public class ScheduledTaskSuppressor : IOptimization, IJournaledOptimization
{
    private bool _isApplied;

    public const string OptimizationId = "Scheduled Task Suppression";

    public string Name => OptimizationId;
    public string Description => "Revert-only: re-enables scheduled tasks recorded by earlier versions";
    public bool IsApplied => _isApplied;
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
    /// Watchdog recovery path: parses the serialized list of disabled task paths from the
    /// journal and re-enables each one without requiring any live instance fields.
    /// schtasks /enable is idempotent - running it against an already-enabled task is a no-op.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            SettingsManager.Logger.Information(
                "[ScheduledTaskSuppressor] Reverting from journal record (watchdog recovery)");

            var stateElement = JsonSerializer.Deserialize<JsonElement>(originalValueJson);
            if (!stateElement.TryGetProperty("disabledTasks", out var tasksElement) ||
                tasksElement.ValueKind != JsonValueKind.Array)
            {
                return RevertFail("Missing disabledTasks field");
            }

            int restoredCount = 0;
            int errorCount = 0;

            foreach (var element in tasksElement.EnumerateArray())
            {
                var taskPath = element.GetString();
                if (string.IsNullOrWhiteSpace(taskPath))
                    continue;

                try
                {
                    var result = RunSchtasks($"/change /tn \"{taskPath}\" /enable");
                    if (result != null && !result.Contains("ERROR"))
                    {
                        restoredCount++;
                        SettingsManager.Logger.Debug(
                            "[ScheduledTaskSuppressor] Re-enabled task: {TaskPath}", taskPath);
                    }
                    else
                    {
                        SettingsManager.Logger.Warning(
                            "[ScheduledTaskSuppressor] Failed to re-enable task: {TaskPath}", taskPath);
                        errorCount++;
                    }
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(ex,
                        "[ScheduledTaskSuppressor] Error re-enabling task {TaskPath}", taskPath);
                    errorCount++;
                }
            }

            SettingsManager.Logger.Information(
                "[ScheduledTaskSuppressor] Watchdog revert completed - {Restored} re-enabled, {Errors} errors",
                restoredCount, errorCount);

            _isApplied = false;
            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[ScheduledTaskSuppressor] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static string? RunSchtasks(string arguments)
    {
        try
        {
            var result = ProcessRunner.Run(NativeInterop.SystemExePath("schtasks.exe"), arguments, 10000);
            if (result.TimedOut)
                return "ERROR: schtasks timed out";
            if (!result.Exited)
                return null; // failed to start

            // stderr text turns the whole result into an ERROR sentinel (callers grep for "ERROR").
            return string.IsNullOrEmpty(result.StdErr) ? result.StdOut : $"ERROR: {result.StdErr}";
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex,
                "[ScheduledTaskSuppressor] schtasks failed: {Args}", arguments);
            return null;
        }
    }
}
