using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Optimization;

/// <summary>
/// The engine reports what each module really did on activation and what each revert really did on
/// deactivation, without changing what is applied or reverted, in which order, or what the journal
/// records.
/// </summary>
[Collection("ConfigState")]
public sealed class SessionOutcomeTests : IDisposable
{
    private readonly TempPath _temp = new();
    private readonly string? _previousSettingsPath = SettingsManager.SettingsFilePathOverride;
    private readonly List<OptimizationEngine> _engines = new();
    private readonly List<string> _calls = new();

    public SessionOutcomeTests()
    {
        SettingsManager.SettingsFilePathOverride = _temp.GetFile("settings.json");
    }

    public void Dispose()
    {
        foreach (var engine in _engines)
            engine.Dispose();
        SettingsManager.SettingsFilePathOverride = _previousSettingsPath;
        _temp.Dispose();
    }

    private string JournalPath => _temp.GetFile("state.json");

    private OptimizationEngine NewEngine(params IOptimization[] modules) => NewEngine(new JournalManager(JournalPath), modules);

    private OptimizationEngine NewEngine(JournalManager journal, params IOptimization[] modules)
    {
        var engine = new OptimizationEngine(modules, journal);
        _engines.Add(engine);
        return engine;
    }

    private static GameProfile Profile() =>
        new() { Id = "test", GameName = "TestGame", ProcessId = 1234, SetTimerResolution = false };

    private class Module : IOptimization
    {
        private readonly List<string> _calls;

        public Module(string name, List<string> calls)
        {
            Name = name;
            _calls = calls;
        }

        public string Name { get; }
        public string Description => "test module";
        public bool IsApplied { get; set; }
        public bool IsAvailable { get; set; } = true;

        public bool ApplyReturns { get; set; } = true;
        public Exception? ApplyThrows { get; set; }
        public bool RevertReturns { get; set; } = true;
        public Exception? RevertThrows { get; set; }

        public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
        {
            _calls.Add($"apply {Name}");
            if (ApplyThrows != null) throw ApplyThrows;
            IsApplied = ApplyReturns;
            return Task.FromResult(ApplyReturns);
        }

        public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
        {
            _calls.Add($"revert {Name}");
            if (RevertThrows != null) throw RevertThrows;
            if (RevertReturns) IsApplied = false;
            return Task.FromResult(RevertReturns);
        }

        protected void Record(string call) => _calls.Add(call);
    }

    private sealed class JournaledModule : Module, IJournaledOptimization
    {
        public JournaledModule(string name, List<string> calls) : base(name, calls) { }

        public bool CanApplyReturns { get; set; } = true;
        public OptimizationState ApplyState { get; set; } = OptimizationState.Applied;
        public string? ApplyError { get; set; }
        public OptimizationState RevertState { get; set; } = OptimizationState.Reverted;
        public string? RevertError { get; set; }

        public bool CanApply(SystemContext context) => CanApplyReturns;

        public OptimizationResult Apply()
        {
            Record($"apply {Name}");
            IsApplied = ApplyState == OptimizationState.Applied;
            return new OptimizationResult(Name, "original", "applied", ApplyState, ApplyError);
        }

        public OptimizationResult Revert()
        {
            Record($"revert {Name}");
            if (RevertThrows != null) throw RevertThrows;
            return new OptimizationResult(Name, "original", "applied", RevertState, RevertError);
        }

        public bool Verify() => true;

        public OptimizationResult RevertFromRecord(string originalValueJson) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Activation_ReportsEveryModuleInApplyOrder()
    {
        var modules = new IOptimization[]
        {
            new Module("Unavailable", _calls) { IsAvailable = false },
            new Module("Already", _calls) { IsApplied = true },
            new Module(TimerResolutionManager.OptimizationId, _calls),
            new JournaledModule("Declines", _calls) { CanApplyReturns = false },
            new Module("Applies", _calls),
            new Module("ReturnsFalse", _calls) { ApplyReturns = false },
            new Module("Throws", _calls) { ApplyThrows = new InvalidOperationException("boom") },
            new JournaledModule("JournaledFails", _calls) { ApplyState = OptimizationState.Failed, ApplyError = "access denied" },
            new JournaledModule("JournaledApplies", _calls),
        };
        var engine = NewEngine(modules);
        var raised = new List<SessionOutcome>();
        engine.ActivationCompleted += (_, e) => raised.Add(e.Outcome);

        await engine.ActivateProfileAsync(Profile(), "Detected Game");

        var outcome = Assert.Single(raised);
        Assert.Same(outcome, engine.LastOutcome);
        Assert.Equal(modules.Select(m => m.Name), outcome.Modules.Select(m => m.ModuleName));
        Assert.Equal(
            new (ModuleOutcomeKind, string?)[]
            {
                (ModuleOutcomeKind.Unavailable, null),
                (ModuleOutcomeKind.AlreadyApplied, null),
                (ModuleOutcomeKind.SkippedDisabled, null),
                (ModuleOutcomeKind.NotApplicable, null),
                (ModuleOutcomeKind.Applied, null),
                (ModuleOutcomeKind.Failed, "did not apply"),
                (ModuleOutcomeKind.Failed, "did not apply: boom"),
                (ModuleOutcomeKind.Failed, "did not apply: access denied"),
                (ModuleOutcomeKind.Applied, null),
            },
            outcome.Modules.Select(m => (m.Kind, m.Reason)));
        Assert.Equal("Detected Game", outcome.GameName);
        Assert.NotNull(outcome.StartedUtc);
        Assert.Empty(outcome.Reverts);
        Assert.Null(outcome.EndedUtc);
        Assert.False(outcome.RevertedByRecovery);

        // Only the modules that got that far were asked to apply, in list order.
        Assert.Equal(
            ["apply Applies", "apply ReturnsFalse", "apply Throws", "apply JournaledFails", "apply JournaledApplies"],
            _calls);
    }

    [Fact]
    public async Task Activation_WithoutAGameName_UsesTheProfileName()
    {
        var engine = NewEngine(new Module("Applies", _calls));

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal("TestGame", engine.LastOutcome!.GameName);
    }

    [Fact]
    public async Task Deactivation_ReportsEveryRevertAndKeepsRevertingAfterAFailure()
    {
        var engine = NewEngine(
            new Module("A", _calls),
            new Module("B", _calls) { RevertThrows = new InvalidOperationException("stuck") },
            new JournaledModule("C", _calls) { RevertState = OptimizationState.Failed, RevertError = "denied" },
            new Module("D", _calls) { RevertReturns = false },
            new JournaledModule("E", _calls));
        var raised = new List<SessionOutcome>();
        var reverted = new List<string>();
        engine.DeactivationCompleted += (_, e) => raised.Add(e.Outcome);
        engine.OptimizationReverted += (_, e) => reverted.Add(e.Optimization.Name);

        await engine.ActivateProfileAsync(Profile());
        var activation = engine.LastOutcome!;
        _calls.Clear();
        await engine.DeactivateProfileAsync();

        var outcome = Assert.Single(raised);
        Assert.Same(outcome, engine.LastOutcome);
        Assert.Equal(
            new (string, ModuleRevertKind, string?)[]
            {
                ("E", ModuleRevertKind.Reverted, null),
                ("D", ModuleRevertKind.RevertFailed, "did not revert"),
                ("C", ModuleRevertKind.RevertFailed, "did not revert: denied"),
                ("B", ModuleRevertKind.RevertFailed, "did not revert: stuck"),
                ("A", ModuleRevertKind.Reverted, null),
            },
            outcome.Reverts.Select(r => (r.ModuleName, r.Kind, r.Reason)));
        Assert.Equal(["revert E", "revert D", "revert C", "revert B", "revert A"], _calls);
        Assert.Equal(["E", "A"], reverted);
        Assert.Equal(activation.Modules, outcome.Modules);
        Assert.Equal(activation.StartedUtc, outcome.StartedUtc);
        Assert.NotNull(outcome.EndedUtc);
        Assert.False(outcome.RevertedByRecovery);
    }

    [Fact]
    public async Task OutcomeEvents_FireOnceAfterThePerModuleEvents()
    {
        var engine = NewEngine(new Module("A", _calls), new Module("B", _calls) { ApplyReturns = false }, new Module("C", _calls));
        var events = new List<string>();
        engine.OptimizationApplied += (_, e) => events.Add($"applied {e.Optimization.Name}");
        engine.OptimizationFailed += (_, e) => events.Add($"failed {e.Optimization.Name}");
        engine.OptimizationReverted += (_, e) => events.Add($"reverted {e.Optimization.Name}");
        engine.ActivationCompleted += (_, _) => events.Add("activation completed");
        engine.DeactivationCompleted += (_, _) => events.Add("deactivation completed");

        await engine.ActivateProfileAsync(Profile());
        await engine.DeactivateProfileAsync();

        Assert.Equal(
            ["applied A", "failed B", "applied C", "activation completed", "reverted C", "reverted A", "deactivation completed"],
            events);
    }

    [Fact]
    public async Task ThrowingOutcomeSubscribers_DoNotBreakActivationOrDeactivation()
    {
        var a = new Module("A", _calls);
        var engine = NewEngine(a);
        var applied = new List<string>();
        engine.OptimizationApplied += (_, e) => applied.Add(e.Optimization.Name);
        engine.ActivationCompleted += (_, _) => throw new InvalidOperationException("subscriber");
        engine.DeactivationCompleted += (_, _) => throw new InvalidOperationException("subscriber");

        await engine.ActivateProfileAsync(Profile());
        Assert.Equal(["A"], applied);
        Assert.Equal(1, engine.AppliedCount);

        await engine.DeactivateProfileAsync();
        Assert.False(a.IsApplied);
        Assert.Equal(0, engine.AppliedCount);
        Assert.False(new JournalManager(JournalPath).LoadJournal()!.SessionActive);
    }

    [Fact]
    public async Task JournalRecords_ApplyAndRevertSequenceUnchanged()
    {
        var engine = NewEngine(
            new JournaledModule("J1", _calls) { RevertState = OptimizationState.Failed },
            new JournaledModule("J2", _calls) { ApplyState = OptimizationState.Failed },
            new JournaledModule("J3", _calls),
            new Module("N", _calls));

        await engine.ActivateProfileAsync(Profile(), "Detected Game");

        var afterActivation = new JournalManager(JournalPath).LoadJournal()!;
        Assert.True(afterActivation.SessionActive);
        Assert.Equal("Detected Game", afterActivation.ActiveGame!.Name);
        Assert.Equal(
            [("J1", "Applied"), ("J3", "Applied")],
            afterActivation.Optimizations.Select(o => (o.Name, o.State)));

        await engine.DeactivateProfileAsync();

        var afterDeactivation = new JournalManager(JournalPath).LoadJournal()!;
        Assert.False(afterDeactivation.SessionActive);
        Assert.Equal(
            [("J1", "Failed"), ("J3", "Reverted")],
            afterDeactivation.Optimizations.Select(o => (o.Name, o.State)));
        Assert.Equal(["apply J1", "apply J2", "apply J3", "apply N", "revert N", "revert J3", "revert J1"], _calls);
    }

    [Fact]
    public async Task Deactivation_AfterCrashRecovery_ReportsItAndRevertsNothing()
    {
        var journal = new JournalManager(JournalPath);
        var a = new Module("A", _calls);
        var engine = NewEngine(journal, a);
        var raised = new List<SessionOutcome>();
        engine.DeactivationCompleted += (_, e) => raised.Add(e.Outcome);

        await engine.ActivateProfileAsync(Profile());
        journal.RecordRecoveryTimestamp(DateTime.UtcNow.AddSeconds(1));
        await engine.DeactivateProfileAsync();

        var outcome = Assert.Single(raised);
        Assert.True(outcome.RevertedByRecovery);
        Assert.Empty(outcome.Reverts);
        Assert.Equal(["A"], outcome.Modules.Select(m => m.ModuleName));
        Assert.DoesNotContain("revert A", _calls);
        Assert.Equal(0, engine.AppliedCount);
    }

    [Fact]
    public async Task Deactivation_WithoutAnActivation_ReportsAnEmptySession()
    {
        var engine = NewEngine(new Module("A", _calls));

        await engine.ActivateProfileAsync(Profile());
        await engine.DeactivateProfileAsync();
        await engine.DeactivateProfileAsync();

        var outcome = engine.LastOutcome!;
        Assert.Null(outcome.StartedUtc);
        Assert.Null(outcome.GameName);
        Assert.Empty(outcome.Modules);
        Assert.Empty(outcome.Reverts);
        Assert.NotNull(outcome.EndedUtc);
    }
}
