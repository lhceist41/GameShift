using GameShift.Core.BackgroundMode;
using GameShift.Core.Config;
using Serilog;
using Xunit;

namespace GameShift.Tests.BackgroundMode;

/// <summary>
/// The one-time Background Mode cleanup removes only what it can tell is GameShift's, never the
/// plan in use, re-enables tasks only after an unclean session with deferral on, and says only
/// what actually happened. Runs against in-memory plans and tasks; nothing reaches the real ones.
/// </summary>
public class BackgroundModeRetirementTests
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UserPlan = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Ours1 = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Ours2 = new("aaaaaaaa-0000-0000-0000-000000000002");

    private const string DefragTask = @"\Microsoft\Windows\Defrag\ScheduledDefrag";
    private const string WinSatTask = @"\Microsoft\Windows\Maintenance\WinSAT";

    private const string SwitchedToBalancedMessage =
        "GameShift no longer has Background Mode. Its power plan was still active, and GameShift did not record " +
        "which plan you used before, so it switched you to Balanced. Choose your preferred plan in Control Panel > Power Options.";

    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    private static PowerSchemeInfo Plan(Guid id, string? name) => new(id, name);
    private static PowerSchemeInfo OursPlan(Guid id) => new(id, BackgroundModeRetirement.PlanName);

    private static BackgroundModeSettings DeferralOn() => new() { Enabled = true, TaskDeferralEnabled = true };

    private static (BackgroundModeRetirementResult Result, int Marked) Run(
        FakePlans plans,
        FakeTasks? tasks = null,
        BackgroundModeSettings? settings = null,
        bool sessionActive = false,
        bool alreadyRetired = false,
        bool markThrows = false)
    {
        int marked = 0;
        tasks ??= new FakeTasks();
        plans.MarkedNow = () => marked;
        tasks.MarkedNow = () => marked;
        var result = BackgroundModeRetirement.Run(
            alreadyRetired, settings, sessionActive, plans, tasks,
            () =>
            {
                marked++;
                if (markThrows)
                    throw new IOException("settings.json is locked");
            },
            Silent);
        return (result, marked);
    }

    // ── Plans ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CleanExit_DeletesInactiveGameShiftPlans_WithoutMessage()
    {
        var plans = new FakePlans(Balanced, Plan(Balanced, "Balanced"), OursPlan(Ours1), OursPlan(Ours2));

        var (result, marked) = Run(plans);

        Assert.Equal(new[] { Ours1, Ours2 }, plans.Deleted);
        Assert.Empty(plans.ActivateCalls);
        Assert.Equal(Balanced, plans.Active);
        Assert.Null(result.Message);
        Assert.True(result.Completed);
        Assert.Equal(1, marked);
    }

    [Fact]
    public void ActiveGameShiftPlan_SwitchesToBalanced_DeletesEveryGameShiftPlan_AndSaysSo()
    {
        var plans = new FakePlans(Ours1,
            Plan(UserPlan, "My plan"), Plan(Balanced, "Balanced"), OursPlan(Ours1), OursPlan(Ours2));

        var (result, _) = Run(plans);

        Assert.Equal(Balanced, plans.Active);
        Assert.Equal(new[] { Balanced }, plans.ActivateCalls);
        Assert.Equal(new[] { Ours1, Ours2 }, plans.Deleted);
        Assert.Equal(SwitchedToBalancedMessage, result.Message);
    }

    [Fact]
    public void BalancedMissing_SwitchesToFirstPlanThatIsNotGameShifts()
    {
        var plans = new FakePlans(Ours1,
            OursPlan(Ours1), Plan(HighPerformance, "High performance"), Plan(UserPlan, "My plan"));

        var (result, _) = Run(plans);

        Assert.Equal(HighPerformance, plans.Active);
        Assert.Equal(new[] { Ours1 }, plans.Deleted);
        Assert.Contains("so it switched you to the \"High performance\" plan.", result.Message);
        Assert.DoesNotContain("Balanced", result.Message);
    }

    [Fact]
    public void NothingElseActivatable_LeavesActivePlan_DeletesOnlyInactiveOnes_AndSaysSo()
    {
        var plans = new FakePlans(Ours1, OursPlan(Ours1), OursPlan(Ours2));

        var (result, marked) = Run(plans);

        Assert.Equal(Ours1, plans.Active);
        Assert.DoesNotContain(Ours1, plans.DeleteCalls);
        Assert.Equal(new[] { Ours2 }, plans.Deleted);
        Assert.Contains("could not switch you to another plan, so it left that plan in place", result.Message);
        Assert.DoesNotContain("switched you to", result.Message);
        Assert.Equal(1, marked);
    }

    [Fact]
    public void FailedActivation_IsNotClaimed_AndTheNextPlanIsTried()
    {
        var plans = new FakePlans(Ours1, Plan(Balanced, "Balanced"), Plan(UserPlan, "My plan"), OursPlan(Ours1));
        plans.FailActivate.Add(Balanced);

        var (result, _) = Run(plans);

        Assert.Equal(UserPlan, plans.Active);
        Assert.Contains("so it switched you to the \"My plan\" plan.", result.Message);
        Assert.DoesNotContain("Balanced", result.Message);
        Assert.Equal(new[] { Ours1 }, plans.Deleted);
    }

    [Fact]
    public void ActivationThatDoesNotLand_CountsAsFailed_AndThePlanIsKept()
    {
        var plans = new FakePlans(Ours1, Plan(Balanced, "Balanced"), OursPlan(Ours1)) { ActivateReportsSuccessWithoutSwitching = true };

        var (result, _) = Run(plans);

        Assert.Equal(Ours1, plans.Active);
        Assert.DoesNotContain(Ours1, plans.DeleteCalls);
        Assert.Contains("left that plan in place", result.Message);
    }

    [Fact]
    public void FailedDeletion_IsReported_NotClaimed()
    {
        var plans = new FakePlans(Balanced, Plan(Balanced, "Balanced"), OursPlan(Ours1));
        plans.FailDelete.Add(Ours1);

        var (result, marked) = Run(plans);

        Assert.Empty(plans.Deleted);
        Assert.Contains("could not delete its \"GameShift Performance\" power plan", result.Message);
        Assert.StartsWith(BackgroundModeRetirement.Intro, result.Message);
        Assert.Equal(1, marked);
    }

    [Fact]
    public void OnlyTheExactPlanName_CountsAsGameShifts()
    {
        var plans = new FakePlans(Balanced,
            Plan(Balanced, "Balanced"),
            Plan(UserPlan, "GameShift Performance (copy)"),
            Plan(HighPerformance, "gameshift performance"));

        var (result, _) = Run(plans);

        Assert.Empty(plans.DeleteCalls);
        Assert.Null(result.Message);
    }

    [Fact]
    public void PlanWithUnreadableName_IsNeitherDeletedNorChosen()
    {
        var plans = new FakePlans(Ours1, Plan(UserPlan, null), OursPlan(Ours1));

        var (result, _) = Run(plans);

        Assert.Empty(plans.ActivateCalls);
        Assert.Empty(plans.DeleteCalls);
        Assert.Contains("left that plan in place", result.Message);
    }

    [Fact]
    public void UnreadableActivePlan_WithGameShiftPlanPresent_ChangesNothing_AndRetriesLater()
    {
        var plans = new FakePlans(Guid.Empty, Plan(Balanced, "Balanced"), OursPlan(Ours1));

        var (result, marked) = Run(plans, new FakeTasks(), DeferralOn(), sessionActive: true);

        Assert.Empty(plans.ActivateCalls);
        Assert.Empty(plans.DeleteCalls);
        Assert.False(result.Completed);
        Assert.Null(result.Message);
        Assert.Equal(0, marked);
    }

    [Fact]
    public void UnreadablePlanList_ChangesNothing_AndRetriesLater()
    {
        var plans = new FakePlans(Balanced) { ListThrows = true };
        var tasks = new FakeTasks { [DefragTask] = ScheduledTaskState.Disabled };

        var (result, marked) = Run(plans, tasks, DeferralOn(), sessionActive: true);

        Assert.Empty(tasks.EnableCalls);
        Assert.False(result.Completed);
        Assert.Equal(0, marked);
    }

    // ── Flag ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AlreadyRetired_ReadsAndChangesNothing()
    {
        var plans = new FakePlans(Ours1, Plan(Balanced, "Balanced"), OursPlan(Ours1));
        var tasks = new FakeTasks { [DefragTask] = ScheduledTaskState.Disabled };

        var (result, marked) = Run(plans, tasks, DeferralOn(), sessionActive: true, alreadyRetired: true);

        Assert.Equal(0, plans.ListCalls);
        Assert.Empty(plans.ActivateCalls);
        Assert.Empty(plans.DeleteCalls);
        Assert.Empty(tasks.StateReads);
        Assert.Empty(tasks.EnableCalls);
        Assert.Null(result.Message);
        Assert.Equal(0, marked);
    }

    [Fact]
    public void FlagIsWritten_OnlyAfterEveryAction()
    {
        var plans = new FakePlans(Ours1, Plan(Balanced, "Balanced"), OursPlan(Ours1), OursPlan(Ours2));
        var tasks = new FakeTasks { [DefragTask] = ScheduledTaskState.Disabled };

        var (_, marked) = Run(plans, tasks, DeferralOn(), sessionActive: true);

        Assert.NotEmpty(plans.MarkedAtAction);
        Assert.NotEmpty(tasks.MarkedAtAction);
        Assert.All(plans.MarkedAtAction, m => Assert.Equal(0, m));
        Assert.All(tasks.MarkedAtAction, m => Assert.Equal(0, m));
        Assert.Equal(1, marked);
    }

    [Fact]
    public void FailedFlagWrite_StillReturnsTheMessage_AndReportsNotRetired()
    {
        var plans = new FakePlans(Ours1, Plan(Balanced, "Balanced"), OursPlan(Ours1));

        var (result, _) = Run(plans, markThrows: true);

        Assert.Equal(Balanced, plans.Active);
        Assert.Equal(SwitchedToBalancedMessage, result.Message);
        Assert.False(result.Completed);
    }

    [Fact]
    public void RetryAfterFailedFlagWrite_RedoesNothing_AndSaysNothing()
    {
        var plans = new FakePlans(Ours1, Plan(Balanced, "Balanced"), OursPlan(Ours1));
        var tasks = new FakeTasks { [DefragTask] = ScheduledTaskState.Disabled };
        Run(plans, tasks, DeferralOn(), sessionActive: true, markThrows: true);
        int activations = plans.ActivateCalls.Count, deletions = plans.DeleteCalls.Count, enables = tasks.EnableCalls.Count;

        var (retry, marked) = Run(plans, tasks, DeferralOn(), sessionActive: true);

        Assert.Equal(activations, plans.ActivateCalls.Count);
        Assert.Equal(deletions, plans.DeleteCalls.Count);
        Assert.Equal(enables, tasks.EnableCalls.Count);
        Assert.Null(retry.Message);
        Assert.True(retry.Completed);
        Assert.Equal(1, marked);
    }

    [Fact]
    public void PlanAndTaskOutcomes_ShareOneMessage_PlanFirst()
    {
        var plans = new FakePlans(Ours1, Plan(Balanced, "Balanced"), OursPlan(Ours1));
        var tasks = new FakeTasks { [DefragTask] = ScheduledTaskState.Disabled };

        var (result, _) = Run(plans, tasks, DeferralOn(), sessionActive: true);

        var parts = result.Message!.Split("\n\n");
        Assert.Equal(2, parts.Length);
        Assert.Equal(SwitchedToBalancedMessage, parts[0]);
        Assert.StartsWith("GameShift re-enabled these Windows scheduled tasks", parts[1]);
        Assert.Contains(DefragTask, parts[1]);
        Assert.Equal(result.Message.IndexOf(BackgroundModeRetirement.Intro), result.Message.LastIndexOf(BackgroundModeRetirement.Intro));
    }

    [Fact]
    public void NothingToFind_StillMarksRetired()
    {
        var (result, marked) = Run(new FakePlans(Balanced, Plan(Balanced, "Balanced")));

        Assert.True(result.Completed);
        Assert.Null(result.Message);
        Assert.Equal(1, marked);
    }

    // ── Tasks ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void Tasks_AreReEnabled_OnlyWhenModeOnDeferralOnAndSessionUnfinished(
        bool modeEnabled, bool deferralEnabled, bool sessionActive, bool expectEnabled)
    {
        var tasks = new FakeTasks { [DefragTask] = ScheduledTaskState.Disabled };
        var settings = new BackgroundModeSettings { Enabled = modeEnabled, TaskDeferralEnabled = deferralEnabled };

        var (result, _) = Run(new FakePlans(Balanced, Plan(Balanced, "Balanced")), tasks, settings, sessionActive);

        if (expectEnabled)
        {
            Assert.Equal(new[] { DefragTask }, tasks.EnableCalls);
            Assert.Contains("If you had disabled one yourself, disable it again", result.Message);
        }
        else
        {
            Assert.Empty(tasks.StateReads);
            Assert.Empty(tasks.EnableCalls);
            Assert.Null(result.Message);
        }
    }

    [Fact]
    public void Tasks_NeverConfigured_AreLeftAlone()
    {
        var tasks = new FakeTasks { [DefragTask] = ScheduledTaskState.Disabled };

        Run(new FakePlans(Balanced, Plan(Balanced, "Balanced")), tasks, settings: null, sessionActive: true);

        Assert.Empty(tasks.EnableCalls);
    }

    [Fact]
    public void Tasks_OnlyDisabledOnesAreTouched_AndEachOutcomeIsNamed()
    {
        var tasks = new FakeTasks
        {
            [DefragTask] = ScheduledTaskState.Disabled,
            [WinSatTask] = ScheduledTaskState.Disabled,
            [@"\Microsoft\Windows\WindowsUpdate\Scheduled Start"] = ScheduledTaskState.Enabled,
            [@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan"] = ScheduledTaskState.Unreadable
        };
        tasks.FailEnable.Add(WinSatTask);

        var (result, _) = Run(new FakePlans(Balanced, Plan(Balanced, "Balanced")), tasks, DeferralOn(), sessionActive: true);

        Assert.Equal(new[] { DefragTask, WinSatTask }, tasks.EnableCalls);
        Assert.Equal(ScheduledTaskState.Enabled, tasks[DefragTask]);
        Assert.Equal(ScheduledTaskState.Disabled, tasks[WinSatTask]);

        var parts = result.Message!.Split("\n\n");
        Assert.Equal(3, parts.Length);
        Assert.Contains("re-enabled these Windows scheduled tasks", parts[0]);
        Assert.Contains(DefragTask, parts[0]);
        Assert.DoesNotContain(WinSatTask, parts[0]);
        Assert.Contains("could not re-enable them", parts[1]);
        Assert.Contains(WinSatTask, parts[1]);
        Assert.Contains("could not check", parts[2]);
        Assert.Contains("Schedule Scan", parts[2]);
        Assert.DoesNotContain("Scheduled Start", result.Message);
    }

    [Fact]
    public void Tasks_OutsideTheDeferredSet_AreNeverRead()
    {
        var other = @"\Microsoft\Windows\Other\SomeTask";
        var tasks = new FakeTasks { [other] = ScheduledTaskState.Disabled };

        Run(new FakePlans(Balanced, Plan(Balanced, "Balanced")), tasks, DeferralOn(), sessionActive: true);

        Assert.DoesNotContain(other, tasks.StateReads);
        Assert.Empty(tasks.EnableCalls);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakePlans : IPowerPlanStore
    {
        private readonly List<PowerSchemeInfo> _plans;

        public FakePlans(Guid active, params PowerSchemeInfo[] plans)
        {
            Active = active;
            _plans = plans.ToList();
        }

        public Guid Active { get; private set; }
        public Func<int> MarkedNow { get; set; } = () => 0;
        public List<int> MarkedAtAction { get; } = new();
        public bool ListThrows { get; init; }
        public bool ActivateReportsSuccessWithoutSwitching { get; init; }
        public HashSet<Guid> FailActivate { get; } = new();
        public HashSet<Guid> FailDelete { get; } = new();
        public int ListCalls { get; private set; }
        public List<Guid> ActivateCalls { get; } = new();
        public List<Guid> DeleteCalls { get; } = new();
        public List<Guid> Deleted { get; } = new();

        public IReadOnlyList<PowerSchemeInfo> List()
        {
            ListCalls++;
            if (ListThrows)
                throw new InvalidOperationException("list failed");
            return _plans.ToList();
        }

        public Guid GetActive() => Active;

        public bool Activate(Guid id)
        {
            ActivateCalls.Add(id);
            MarkedAtAction.Add(MarkedNow());
            if (FailActivate.Contains(id))
                return false;
            if (!ActivateReportsSuccessWithoutSwitching)
                Active = id;
            return true;
        }

        public bool Delete(Guid id)
        {
            DeleteCalls.Add(id);
            MarkedAtAction.Add(MarkedNow());
            // Windows refuses to delete the active scheme.
            if (FailDelete.Contains(id) || id == Active)
                return false;
            _plans.RemoveAll(p => p.Id == id);
            Deleted.Add(id);
            return true;
        }
    }

    private sealed class FakeTasks : Dictionary<string, ScheduledTaskState>, IScheduledTaskStore
    {
        public HashSet<string> FailEnable { get; } = new();
        public Func<int> MarkedNow { get; set; } = () => 0;
        public List<int> MarkedAtAction { get; } = new();
        public List<string> StateReads { get; } = new();
        public List<string> EnableCalls { get; } = new();

        public ScheduledTaskState GetState(string path)
        {
            StateReads.Add(path);
            return TryGetValue(path, out var state) ? state : ScheduledTaskState.NotFound;
        }

        public bool Enable(string path)
        {
            EnableCalls.Add(path);
            MarkedAtAction.Add(MarkedNow());
            if (FailEnable.Contains(path) || !ContainsKey(path))
                return false;
            this[path] = ScheduledTaskState.Enabled;
            return true;
        }
    }
}
