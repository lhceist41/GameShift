using System.Text.Json;
using GameShift.Core.BackgroundMode;
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

    internal const string ReEnabledPrefix = "Re-enabled scheduled tasks: ";
    internal const string NotReEnabledPrefix = "Could not re-enable scheduled tasks: ";
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

    internal IScheduledTaskStore Tasks { get; init; } = new WindowsScheduledTaskStore();

    /// <summary>
    /// Watchdog recovery path: re-enables each recorded task that is still disabled, through the Task
    /// Scheduler API. Only the tasks earlier versions suppressed are touched. Earlier versions could
    /// record a task the user had disabled themselves (their check read English schtasks output), so
    /// the result names every task it re-enabled.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            SettingsManager.Logger.Information(
                "[ScheduledTaskSuppressor] Reverting from journal record (watchdog recovery)");

            var stateElement = JsonSerializer.Deserialize<JsonElement>(originalValueJson);
            if (stateElement.ValueKind != JsonValueKind.Object ||
                !stateElement.TryGetProperty("disabledTasks", out var tasksElement) ||
                tasksElement.ValueKind != JsonValueKind.Array)
            {
                return RevertFail("Missing disabledTasks field");
            }

            var reEnabled = new List<string>();
            var notReEnabled = new List<string>();
            int refused = 0;

            foreach (var element in tasksElement.EnumerateArray())
            {
                var taskPath = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                if (taskPath == null || !RecoveryInputRules.SuppressedTasks.Contains(taskPath))
                {
                    SettingsManager.Logger.Warning(
                        "[ScheduledTaskSuppressor] Refused a journal entry that is not a task GameShift suppressed: {Entry}",
                        element.GetRawText());
                    refused++;
                    continue;
                }

                switch (Tasks.GetState(taskPath))
                {
                    case ScheduledTaskState.Disabled:
                        if (Tasks.Enable(taskPath) && Tasks.GetState(taskPath) == ScheduledTaskState.Enabled)
                        {
                            reEnabled.Add(taskPath);
                            SettingsManager.Logger.Information(
                                "[ScheduledTaskSuppressor] Re-enabled task: {TaskPath}", taskPath);
                        }
                        else
                        {
                            notReEnabled.Add(taskPath);
                            SettingsManager.Logger.Warning(
                                "[ScheduledTaskSuppressor] Failed to re-enable task: {TaskPath}", taskPath);
                        }
                        break;
                    case ScheduledTaskState.Unreadable:
                        notReEnabled.Add(taskPath);
                        SettingsManager.Logger.Warning(
                            "[ScheduledTaskSuppressor] Could not read task: {TaskPath}", taskPath);
                        break;
                }
            }

            SettingsManager.Logger.Information(
                "[ScheduledTaskSuppressor] Watchdog revert completed - {Restored} re-enabled, {Errors} not re-enabled, {Refused} refused",
                reEnabled.Count, notReEnabled.Count, refused);

            var parts = new List<string>();
            if (reEnabled.Count > 0)
                parts.Add(ReEnabledPrefix + string.Join(", ", reEnabled));
            if (notReEnabled.Count > 0)
                parts.Add(NotReEnabledPrefix + string.Join(", ", notReEnabled));
            if (refused > 0)
                parts.Add($"Refused {refused} journal entr{(refused == 1 ? "y" : "ies")} that GameShift did not write");
            var message = parts.Count > 0 ? string.Join(". ", parts) : null;

            _isApplied = false;
            // A Reverted result still carries the message, so whoever reports the recovery can name
            // the tasks that were re-enabled.
            return new OptimizationResult(
                OptimizationId, string.Empty, string.Empty,
                notReEnabled.Count == 0 && refused == 0 ? OptimizationState.Reverted : OptimizationState.Failed,
                message);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[ScheduledTaskSuppressor] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);
}
