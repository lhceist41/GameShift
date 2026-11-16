using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GameShift.Tests.Optimization;

/// <summary>
/// The engine journals a write-ahead module's changes before they happen, confirms the entry when
/// the apply succeeds, and undoes an apply that failed after recording a change through the same
/// record crash recovery would replay. Every journal lives in a temp folder; the modules are mocks.
/// </summary>
[Collection("ConfigState")]
public sealed class WriteAheadEngineTests : IDisposable
{
    private readonly TempPath _temp = new();
    private readonly string? _previousSettingsPath = SettingsManager.SettingsFilePathOverride;
    private readonly List<OptimizationEngine> _engines = new();
    private readonly CapturingSink _log = new();

    public WriteAheadEngineTests()
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

    private OptimizationEngine NewEngine(params IOptimization[] optimizations)
    {
        var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(_log).CreateLogger();
        var engine = new OptimizationEngine(optimizations, new JournalManager(JournalPath), logger);
        _engines.Add(engine);
        return engine;
    }

    private static GameProfile Profile() => new() { Id = "test", GameName = "TestGame", ExecutableName = "game.exe", ProcessId = 1234 };

    private SessionJournalData OnDisk() => new JournalManager(JournalPath).PeekJournal()!;

    private static ModuleOutcome OutcomeOf(OptimizationEngine engine, string name) =>
        engine.LastOutcome!.Modules.Single(m => m.ModuleName == name);

    private static OptimizationResult Result(string name, OptimizationState state, string original = "", string? error = null) =>
        new(name, original, string.Empty, state, error);

    // ── Crash window ─────────────────────────────────────────────────────

    // What is on disk while the module is still inside Apply is exactly what a crash there leaves.
    [Fact]
    public async Task RecordOnDiskDuringApply_IsWhatRecoveryReplays()
    {
        var crashFolder = Directory.CreateDirectory(_temp.GetFile("crash")).FullName;
        var crashJournal = Path.Combine(crashFolder, "state.json");
        SessionJournalData? seenDuringApply = null;
        var module = new WriteAheadMock("WA")
        {
            OnApply = recorder =>
            {
                recorder.BeforeChange("first");
                recorder.BeforeChange("first+second");
                seenDuringApply = new JournalManager(JournalPath).PeekJournal();
                File.Copy(JournalPath, crashJournal);
                return Result("WA", OptimizationState.Applied, "first+second");
            },
        };

        await NewEngine(module).ActivateProfileAsync(Profile());

        var entry = Assert.Single(seenDuringApply!.Optimizations);
        Assert.Equal(("Applied", true, "first+second"), (entry.State, entry.InProgress, entry.OriginalValue));

        var replayed = new WriteAheadMock("WA");
        var recovery = StartupRecovery.Run(
            new JournalManager(crashJournal),
            _ => new JournalTrustResult(true, Array.Empty<string>()),
            new WatchdogRevertEngine(Logger.None, new Dictionary<string, Func<IJournaledOptimization>> { ["WA"] = () => replayed }),
            Logger.None,
            (_, _) => true);

        Assert.Equal(new[] { "first+second" }, replayed.RevertFromRecordCalls);
        Assert.Equal(StartupRecoveryOutcome.Recovered, recovery.Outcome);
        Assert.EndsWith(StartupRecovery.InProgressRecoveredSentence, recovery.Notice);
    }

    // ── Applied ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AppliedAfterIntents_LeavesOneConfirmedEntry_ThatTheSessionEndReverts()
    {
        var module = new WriteAheadMock("WA")
        {
            OnApply = recorder =>
            {
                recorder.BeforeChange("a");
                recorder.BeforeChange("a+b");
                return Result("WA", OptimizationState.Applied, "a+b");
            },
        };
        var engine = NewEngine(module);

        await engine.ActivateProfileAsync(Profile());

        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Applied", false, "a+b"), (entry.State, entry.InProgress, entry.OriginalValue));
        Assert.Equal(ModuleOutcomeKind.Applied, OutcomeOf(engine, "WA").Kind);

        await engine.DeactivateProfileAsync();

        Assert.Equal(1, module.RevertCalls);
        Assert.Empty(module.RevertFromRecordCalls);
        Assert.Equal("Reverted", Assert.Single(OnDisk().Optimizations).State);
    }

    [Fact]
    public async Task JournaledModuleWithoutRecorder_IsAppendedAfterItsApplyAsBefore()
    {
        var module = new PlainJournaledMock("Plain");
        var engine = NewEngine(module);

        await engine.ActivateProfileAsync(Profile());

        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Plain", "Applied", false, "plain-original"), (entry.Name, entry.State, entry.InProgress, entry.OriginalValue));
    }

    // ── Failed apply after an intent ─────────────────────────────────────

    [Fact]
    public async Task FailedAfterAnIntent_IsUndoneWithTheLastRecord_AndNotKept()
    {
        var module = new WriteAheadMock("WA")
        {
            OnApply = recorder =>
            {
                recorder.BeforeChange("one");
                recorder.BeforeChange("one+two");
                return Result("WA", OptimizationState.Failed, error: "second change refused");
            },
        };
        var engine = NewEngine(module);

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(new[] { "one+two" }, module.RevertFromRecordCalls);
        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Reverted", false), (entry.State, entry.InProgress));
        Assert.Equal(0, engine.AppliedCount);
        var outcome = OutcomeOf(engine, "WA");
        Assert.Equal((ModuleOutcomeKind.Failed, "did not apply: second change refused"), (outcome.Kind, outcome.Reason));

        await engine.DeactivateProfileAsync();

        Assert.Equal(0, module.RevertCalls);
    }

    [Fact]
    public async Task ThrowAfterAnIntent_IsUndoneWithTheLastRecord_AndNotKept()
    {
        var module = new WriteAheadMock("WA")
        {
            OnApply = recorder =>
            {
                recorder.BeforeChange("one");
                throw new InvalidOperationException("the change threw");
            },
        };
        var engine = NewEngine(module);

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(new[] { "one" }, module.RevertFromRecordCalls);
        Assert.Equal(("Reverted", false), (OnDisk().Optimizations.Single().State, OnDisk().Optimizations.Single().InProgress));
        Assert.Equal(0, engine.AppliedCount);
        var outcome = OutcomeOf(engine, "WA");
        Assert.Equal((ModuleOutcomeKind.Failed, "did not apply: the change threw"), (outcome.Kind, outcome.Reason));
    }

    [Fact]
    public async Task FailedOrThrowWithoutAnIntent_JournalsNothing_AsBefore()
    {
        var failing = new WriteAheadMock("Fails") { OnApply = _ => Result("Fails", OptimizationState.Failed, error: "no") };
        var throwing = new WriteAheadMock("Throws") { OnApply = _ => throw new InvalidOperationException("boom") };
        var engine = NewEngine(failing, throwing);

        await engine.ActivateProfileAsync(Profile());

        Assert.Empty(OnDisk().Optimizations);
        Assert.Empty(failing.RevertFromRecordCalls);
        Assert.Empty(throwing.RevertFromRecordCalls);
        Assert.Equal("did not apply: no", OutcomeOf(engine, "Fails").Reason);
        Assert.Equal("did not apply: boom", OutcomeOf(engine, "Throws").Reason);
    }

    // ── Undo that does not succeed ───────────────────────────────────────

    [Fact]
    public async Task UndoThatFails_IsReported_AndBlocksAnotherApplyUntilTheSessionEnds()
    {
        var module = new WriteAheadMock("WA")
        {
            OnApply = recorder =>
            {
                recorder.BeforeChange("one");
                return Result("WA", OptimizationState.Failed, error: "refused");
            },
            OnRevertFromRecord = _ => Result("WA", OptimizationState.Failed, error: "value still set"),
        };
        var engine = NewEngine(module);

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(
            "did not apply: refused; what it had started to change may still be in effect: value still set",
            OutcomeOf(engine, "WA").Reason);
        Assert.Equal(("Failed", false), (OnDisk().Optimizations.Single().State, OnDisk().Optimizations.Single().InProgress));

        // A second game joins the open session.
        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(1, module.ApplyCalls);
        var blocked = OutcomeOf(engine, "WA");
        Assert.Equal(ModuleOutcomeKind.Failed, blocked.Kind);
        Assert.Equal(
            "not applied again this session, because after an earlier attempt what it had started to change may still be in effect: value still set",
            blocked.Reason);

        await engine.DeactivateProfileAsync();
        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(2, module.ApplyCalls);
    }

    [Fact]
    public async Task UndoThatThrows_IsReported_AndTheNextModuleStillApplies()
    {
        var module = new WriteAheadMock("WA")
        {
            OnApply = recorder =>
            {
                recorder.BeforeChange("one");
                return Result("WA", OptimizationState.Failed, error: "refused");
            },
            OnRevertFromRecord = _ => throw new InvalidOperationException("undo threw"),
        };
        var next = new MockOptimizationSuccess { Name = "Next" };
        var engine = NewEngine(module, next);

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(1, next.ApplyCallCount);
        Assert.Equal(ModuleOutcomeKind.Applied, OutcomeOf(engine, "Next").Kind);
        Assert.Equal(
            "did not apply: refused; what it had started to change may still be in effect: undo threw",
            OutcomeOf(engine, "WA").Reason);
        Assert.Equal("Failed", OnDisk().Optimizations.Single(e => e.Name == "WA").State);
    }

    // The engine must not count on RevertFromRecord clearing the module's own flag.
    [Fact]
    public async Task ModuleStillAppliedAfterItsUndo_IsLoggedAsAnError()
    {
        var module = new WriteAheadMock("WA")
        {
            OnApply = recorder =>
            {
                recorder.BeforeChange("one");
                return Result("WA", OptimizationState.Failed, error: "refused");
            },
        };
        module.SetAppliedDuringApply = true;
        var engine = NewEngine(module);

        await engine.ActivateProfileAsync(Profile());

        Assert.Contains(_log.Events, e =>
            e.Level == LogEventLevel.Error &&
            e.RenderMessage().Contains("still reports itself applied", StringComparison.Ordinal) &&
            e.RenderMessage().Contains("WA", StringComparison.Ordinal));
    }

    // ── Intent whose save fails ──────────────────────────────────────────

    // The handle shares neither read nor write, so the intent's save cannot read the journal.
    [Fact]
    public async Task IntentWhoseSaveFails_LetsTheApplyFinish_AndTheConfirmationWritesTheEntry()
    {
        int entriesOnDiskAfterIntent = -1;
        var module = new WriteAheadMock("WA")
        {
            OnApply = recorder =>
            {
                using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
                    recorder.BeforeChange("one");
                entriesOnDiskAfterIntent = new JournalManager(JournalPath).PeekJournal()!.Optimizations.Count;
                return Result("WA", OptimizationState.Applied, "one");
            },
        };

        await NewEngine(module).ActivateProfileAsync(Profile());

        Assert.Equal(0, entriesOnDiskAfterIntent);
        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Applied", false, "one"), (entry.State, entry.InProgress, entry.OriginalValue));
    }

    [Fact]
    public async Task IntentWhoseSaveFails_StillDrivesTheUndoOfAFailedApply()
    {
        var module = new WriteAheadMock("WA")
        {
            OnApply = recorder =>
            {
                using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
                    recorder.BeforeChange("one");
                return Result("WA", OptimizationState.Failed, error: "refused");
            },
        };
        var engine = NewEngine(module);

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(new[] { "one" }, module.RevertFromRecordCalls);
        Assert.Equal("did not apply: refused", OutcomeOf(engine, "WA").Reason);
    }

    // ── Mocks ────────────────────────────────────────────────────────────

    private sealed class WriteAheadMock : IOptimization, IWriteAheadJournaledOptimization
    {
        public WriteAheadMock(string name) => Name = name;

        public string Name { get; }
        public string Description => "Write-ahead mock";
        public bool IsApplied { get; private set; }
        public bool IsAvailable => true;

        public Func<IApplyRecorder, OptimizationResult> OnApply { get; init; } = _ => throw new InvalidOperationException("not set up");
        public Func<string, OptimizationResult>? OnRevertFromRecord { get; init; }
        public bool SetAppliedDuringApply { get; set; }

        public int ApplyCalls { get; private set; }
        public int RevertCalls { get; private set; }
        public List<string> RevertFromRecordCalls { get; } = new();

        public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile) => throw new InvalidOperationException("journaled path only");
        public Task<bool> RevertAsync(SystemStateSnapshot snapshot) => throw new InvalidOperationException("journaled path only");

        public bool CanApply(SystemContext context) => true;

        public OptimizationResult Apply() => Apply(NoOpApplyRecorder.Instance);

        public OptimizationResult Apply(IApplyRecorder recorder)
        {
            ApplyCalls++;
            if (SetAppliedDuringApply)
                IsApplied = true;
            var result = OnApply(recorder);
            if (result.State == OptimizationState.Applied)
                IsApplied = true;
            return result;
        }

        public OptimizationResult Revert()
        {
            RevertCalls++;
            IsApplied = false;
            return new OptimizationResult(Name, string.Empty, string.Empty, OptimizationState.Reverted);
        }

        public bool Verify() => IsApplied;

        public OptimizationResult RevertFromRecord(string originalValueJson)
        {
            RevertFromRecordCalls.Add(originalValueJson);
            return OnRevertFromRecord?.Invoke(originalValueJson)
                ?? new OptimizationResult(Name, string.Empty, string.Empty, OptimizationState.Reverted);
        }
    }

    private sealed class PlainJournaledMock : IOptimization, IJournaledOptimization
    {
        public PlainJournaledMock(string name) => Name = name;

        public string Name { get; }
        public string Description => "Journaled mock";
        public bool IsApplied { get; private set; }
        public bool IsAvailable => true;

        public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile) => throw new InvalidOperationException("journaled path only");
        public Task<bool> RevertAsync(SystemStateSnapshot snapshot) => throw new InvalidOperationException("journaled path only");

        public bool CanApply(SystemContext context) => true;

        public OptimizationResult Apply()
        {
            IsApplied = true;
            return new OptimizationResult(Name, "plain-original", "plain-applied", OptimizationState.Applied);
        }

        public OptimizationResult Revert()
        {
            IsApplied = false;
            return new OptimizationResult(Name, string.Empty, string.Empty, OptimizationState.Reverted);
        }

        public bool Verify() => IsApplied;

        public OptimizationResult RevertFromRecord(string originalValueJson) =>
            new(Name, string.Empty, string.Empty, OptimizationState.Reverted);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
