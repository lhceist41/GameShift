using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.BackgroundMode;

/// <summary>A power plan as Windows lists it. <see cref="Name"/> is null when it could not be read.</summary>
public sealed record PowerSchemeInfo(Guid Id, string? Name);

/// <summary>Power plan operations the retirement step needs.</summary>
public interface IPowerPlanStore
{
    /// <summary>Every plan Windows lists. Throws when the list cannot be read.</summary>
    IReadOnlyList<PowerSchemeInfo> List();

    /// <summary>The active plan, or <see cref="Guid.Empty"/> when it cannot be read.</summary>
    Guid GetActive();

    bool Activate(Guid id);

    bool Delete(Guid id);
}

public enum ScheduledTaskState
{
    Enabled,
    Disabled,
    NotFound,
    Unreadable
}

/// <summary>Scheduled task operations the retirement step needs.</summary>
public interface IScheduledTaskStore
{
    ScheduledTaskState GetState(string path);

    bool Enable(string path);
}

/// <param name="Completed">True when the step ran to the end and the retired flag was saved.</param>
/// <param name="Message">What to tell the user, or null when nothing needs saying.</param>
public sealed record BackgroundModeRetirementResult(bool Completed, string? Message);

/// <summary>
/// Removes what Background Mode can have left behind, once, on the first start of a version
/// without it. Background Mode kept the user's previous plan and its deferred tasks only in
/// memory, so after a crash this step can only act on what it can tell is GameShift's own.
/// </summary>
public static class BackgroundModeRetirement
{
    public const string PlanName = "GameShift Performance";

    internal static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>The tasks Background Mode disabled during a game.</summary>
    internal static readonly IReadOnlyList<string> DeferrableTasks = new[]
    {
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan",
        @"\Microsoft\Windows\UpdateOrchestrator\USO_UxBroker",
        @"\Microsoft\Windows\WindowsUpdate\Scheduled Start",
        @"\Microsoft\Windows\Defrag\ScheduledDefrag",
        @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
        @"\Microsoft\Windows\Maintenance\WinSAT",
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting"
    };

    internal const string Intro = "GameShift no longer has Background Mode.";

    /// <param name="alreadyRetired">The settings flag; when set, nothing is read or changed.</param>
    /// <param name="settings">Background Mode settings as the previous version left them.</param>
    /// <param name="journalSessionActive">
    /// The loaded journal still marks a session active, the only persisted sign that the last
    /// session did not end cleanly.
    /// </param>
    /// <param name="markRetired">Persists the flag; called only when the step completed.</param>
    public static BackgroundModeRetirementResult Run(
        bool alreadyRetired,
        BackgroundModeSettings? settings,
        bool journalSessionActive,
        IPowerPlanStore plans,
        IScheduledTaskStore tasks,
        Action markRetired,
        ILogger logger)
    {
        if (alreadyRetired)
            return new BackgroundModeRetirementResult(true, null);

        IReadOnlyList<PowerSchemeInfo> listed;
        Guid active;
        try
        {
            listed = plans.List();
            active = plans.GetActive();
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[BackgroundModeRetirement] Could not list power plans; retrying on next start");
            return new BackgroundModeRetirementResult(false, null);
        }

        var ours = listed.Where(p => p.Name == PlanName).ToList();
        if (ours.Count > 0 && active == Guid.Empty)
        {
            // Without the active plan, deleting could hit the one in use.
            logger.Warning("[BackgroundModeRetirement] Active power plan unreadable; retrying on next start");
            return new BackgroundModeRetirementResult(false, null);
        }

        var parts = new List<string>();
        RetirePlans(listed, ours, active, plans, parts, logger);

        if (settings?.Enabled == true && settings.TaskDeferralEnabled && journalSessionActive)
            RestoreTasks(tasks, parts, logger);

        bool completed = true;
        try
        {
            markRetired();
        }
        catch (Exception ex)
        {
            // The actions above already landed, so the message must still reach the user. The retry
            // finds them done and stays quiet about them.
            completed = false;
            logger.Warning(ex, "[BackgroundModeRetirement] Could not save the retired flag; retrying on next start");
        }

        var message = parts.Count == 0 ? null : Intro + " " + string.Join("\n\n", parts);
        return new BackgroundModeRetirementResult(completed, message);
    }

    private static void RetirePlans(
        IReadOnlyList<PowerSchemeInfo> listed,
        List<PowerSchemeInfo> ours,
        Guid active,
        IPowerPlanStore plans,
        List<string> parts,
        ILogger logger)
    {
        bool oursActive = ours.Any(p => p.Id == active);
        if (oursActive)
        {
            var switchedTo = ActivateReplacement(listed, plans, logger);
            if (switchedTo == null)
            {
                parts.Add($"Its \"{PlanName}\" power plan is still active and GameShift could not switch you to another plan, " +
                          $"so it left that plan in place. Choose another plan in Control Panel > Power Options, then delete \"{PlanName}\" there.");
                ours = ours.Where(p => p.Id != active).ToList();
            }
            else
            {
                var name = switchedTo.Id == BalancedGuid ? "Balanced" : $"the \"{switchedTo.Name}\" plan";
                parts.Add("Its power plan was still active, and GameShift did not record which plan you used before, " +
                          $"so it switched you to {name}. Choose your preferred plan in Control Panel > Power Options.");
                active = switchedTo.Id;
            }
        }

        int failed = 0;
        foreach (var plan in ours)
        {
            if (plan.Id == active)
                continue;

            if (plans.Delete(plan.Id))
            {
                logger.Information("[BackgroundModeRetirement] Deleted power plan {Guid}", plan.Id);
            }
            else
            {
                failed++;
                logger.Warning("[BackgroundModeRetirement] Could not delete power plan {Guid}", plan.Id);
            }
        }

        if (failed > 0)
            parts.Add($"GameShift could not delete its \"{PlanName}\" power plan. You can delete it in Control Panel > Power Options.");
    }

    /// <summary>Balanced first, then the other plans in the order Windows lists them.</summary>
    private static PowerSchemeInfo? ActivateReplacement(IReadOnlyList<PowerSchemeInfo> listed, IPowerPlanStore plans, ILogger logger)
    {
        var candidates = listed
            .Where(p => p.Name != null && p.Name != PlanName)
            .OrderBy(p => p.Id == BalancedGuid ? 0 : 1)
            .ToList();

        foreach (var candidate in candidates)
        {
            // Confirmed by reading back, so the message never claims a switch that did not land.
            if (plans.Activate(candidate.Id) && plans.GetActive() == candidate.Id)
            {
                logger.Information("[BackgroundModeRetirement] Activated power plan {Guid} in place of {Name}", candidate.Id, PlanName);
                return candidate;
            }

            logger.Warning("[BackgroundModeRetirement] Could not activate power plan {Guid}", candidate.Id);
        }

        return null;
    }

    private static void RestoreTasks(IScheduledTaskStore tasks, List<string> parts, ILogger logger)
    {
        var enabled = new List<string>();
        var failed = new List<string>();
        var unreadable = new List<string>();

        foreach (var path in DeferrableTasks)
        {
            switch (tasks.GetState(path))
            {
                case ScheduledTaskState.Disabled:
                    if (tasks.Enable(path) && tasks.GetState(path) == ScheduledTaskState.Enabled)
                    {
                        enabled.Add(path);
                        logger.Information("[BackgroundModeRetirement] Re-enabled scheduled task {Task}", path);
                    }
                    else
                    {
                        failed.Add(path);
                        logger.Warning("[BackgroundModeRetirement] Could not re-enable scheduled task {Task}", path);
                    }
                    break;
                case ScheduledTaskState.Unreadable:
                    unreadable.Add(path);
                    logger.Warning("[BackgroundModeRetirement] Could not read scheduled task {Task}", path);
                    break;
            }
        }

        if (enabled.Count > 0)
            parts.Add("GameShift re-enabled these Windows scheduled tasks because it may have left them disabled. " +
                      "If you had disabled one yourself, disable it again in Task Scheduler:" + Bullets(enabled));
        if (failed.Count > 0)
            parts.Add("GameShift may have left these Windows scheduled tasks disabled and could not re-enable them. " +
                      "You can enable them in Task Scheduler:" + Bullets(failed));
        if (unreadable.Count > 0)
            parts.Add("GameShift could not check these Windows scheduled tasks, which it may have left disabled:" + Bullets(unreadable));
    }

    private static string Bullets(IEnumerable<string> items) =>
        string.Concat(items.Select(i => "\n  • " + i));
}
