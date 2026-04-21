using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Tests.TestHelpers;
using Serilog;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Tests for <see cref="WatchdogRevertEngine"/>.
///
/// The engine iterates journaled Applied optimizations in LIFO order and calls
/// <c>RevertFromRecord</c> on each via a factory dictionary. After reverting it
/// stamps the recovery timestamp and ends the session. All tests use the
/// internal constructor that allows overriding the factory dictionary with mocks
/// so the real registry/service/process side effects are avoided.
/// </summary>
public class WatchdogRevertEngineTests
{
    private static ILogger NoOpLogger => Logger.None;

    private static GameProfile NewTestProfile() => new()
    {
        Id = "test",
        GameName = "TestGame",
        ExecutableName = "test.exe",
        ProcessId = 1234
    };

    [Fact]
    public void RevertFromJournal_InvokesRevertFromRecord_ForEachAppliedEntry()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        var mock1 = new MockJournaledOptimization { Name = "Opt1" };
        var mock2 = new MockJournaledOptimization { Name = "Opt2" };
        var mock3 = new MockJournaledOptimization { Name = "Opt3" };
        var factories = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            ["Opt1"] = () => mock1,
            ["Opt2"] = () => mock2,
            ["Opt3"] = () => mock3,
        };

        journal.StartSession(NewTestProfile());
        journal.RecordApplied(new OptimizationResult("Opt1", "orig1", "applied1", OptimizationState.Applied));
        journal.RecordApplied(new OptimizationResult("Opt2", "orig2", "applied2", OptimizationState.Applied));
        journal.RecordApplied(new OptimizationResult("Opt3", "orig3", "applied3", OptimizationState.Applied));

        var journalData = journal.LoadJournal();
        Assert.NotNull(journalData);

        var engine = new WatchdogRevertEngine(NoOpLogger, factories);
        engine.RevertFromJournal(journalData!, journal);

        // Each mock should have been invoked exactly once with its OriginalValue
        Assert.Single(mock1.RevertFromRecordCalls);
        Assert.Single(mock2.RevertFromRecordCalls);
        Assert.Single(mock3.RevertFromRecordCalls);
        Assert.Equal("orig1", mock1.RevertFromRecordCalls[0]);
        Assert.Equal("orig2", mock2.RevertFromRecordCalls[0]);
        Assert.Equal("orig3", mock3.RevertFromRecordCalls[0]);
    }

    [Fact]
    public void RevertFromJournal_RevertsInLIFOOrder()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        var sequence = new List<string>();
        var factories = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            ["First"] = () => new RecordingMockJournaledOptimization("First", sequence),
            ["Second"] = () => new RecordingMockJournaledOptimization("Second", sequence),
            ["Third"] = () => new RecordingMockJournaledOptimization("Third", sequence),
        };

        journal.StartSession(NewTestProfile());
        journal.RecordApplied(new OptimizationResult("First", "f", "f", OptimizationState.Applied));
        journal.RecordApplied(new OptimizationResult("Second", "s", "s", OptimizationState.Applied));
        journal.RecordApplied(new OptimizationResult("Third", "t", "t", OptimizationState.Applied));

        var journalData = journal.LoadJournal();
        Assert.NotNull(journalData);

        var engine = new WatchdogRevertEngine(NoOpLogger, factories);
        engine.RevertFromJournal(journalData!, journal);

        // Third was applied last, so reverted first
        Assert.Equal(new[] { "Third", "Second", "First" }, sequence);
    }

    [Fact]
    public void RevertFromJournal_UnknownOptimizationName_IsSkipped_DoesNotThrow()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        // Register a factory for "Known" but not for "UnknownOpt"
        var knownMock = new MockJournaledOptimization { Name = "Known" };
        var factories = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            ["Known"] = () => knownMock,
        };

        journal.StartSession(NewTestProfile());
        journal.RecordApplied(new OptimizationResult("Known", "k", "k", OptimizationState.Applied));
        journal.RecordApplied(new OptimizationResult("UnknownOpt", "orig", "applied", OptimizationState.Applied));

        var journalData = journal.LoadJournal();
        Assert.NotNull(journalData);

        var engine = new WatchdogRevertEngine(NoOpLogger, factories);

        // Must not throw — unknown entries are logged as warnings and skipped
        var ex = Record.Exception(() => engine.RevertFromJournal(journalData!, journal));
        Assert.Null(ex);

        // The known mock was still called despite the unknown entry above it in LIFO order
        Assert.Single(knownMock.RevertFromRecordCalls);
    }

    [Fact]
    public void RevertFromJournal_MockThrows_ContinuesWithOtherOptimizations()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        var throwingMock = new MockJournaledOptimization { Name = "Thrower", ShouldThrow = true };
        var normalMock = new MockJournaledOptimization { Name = "Normal" };

        var factories = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            ["Normal"] = () => normalMock,
            ["Thrower"] = () => throwingMock,
        };

        journal.StartSession(NewTestProfile());
        // Applied order: Normal first, Thrower last. LIFO revert: Thrower first (throws), Normal second.
        journal.RecordApplied(new OptimizationResult("Normal", "n", "n", OptimizationState.Applied));
        journal.RecordApplied(new OptimizationResult("Thrower", "t", "t", OptimizationState.Applied));

        var journalData = journal.LoadJournal();
        Assert.NotNull(journalData);

        var engine = new WatchdogRevertEngine(NoOpLogger, factories);

        // Must not propagate exception from the throwing mock
        var ex = Record.Exception(() => engine.RevertFromJournal(journalData!, journal));
        Assert.Null(ex);

        // The exception-throwing mock was invoked, and the normal mock was invoked afterward
        Assert.Single(throwingMock.RevertFromRecordCalls);
        Assert.Single(normalMock.RevertFromRecordCalls);
    }

    [Fact]
    public void RevertFromJournal_SkipsEntriesNotInAppliedState()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        var alreadyRevertedMock = new MockJournaledOptimization { Name = "AlreadyReverted" };
        var appliedMock = new MockJournaledOptimization { Name = "StillApplied" };

        var factories = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            ["AlreadyReverted"] = () => alreadyRevertedMock,
            ["StillApplied"] = () => appliedMock,
        };

        journal.StartSession(NewTestProfile());

        journal.RecordApplied(new OptimizationResult("AlreadyReverted", "a", "a", OptimizationState.Applied));
        journal.RecordApplied(new OptimizationResult("StillApplied", "s", "s", OptimizationState.Applied));

        // Simulate that "AlreadyReverted" was already reverted via a user-initiated
        // deactivate before the watchdog fired
        journal.RecordReverted("AlreadyReverted", OptimizationState.Reverted);

        var journalData = journal.LoadJournal();
        Assert.NotNull(journalData);

        var engine = new WatchdogRevertEngine(NoOpLogger, factories);
        engine.RevertFromJournal(journalData!, journal);

        // Only the entry still in Applied state should be reverted
        Assert.Empty(alreadyRevertedMock.RevertFromRecordCalls);
        Assert.Single(appliedMock.RevertFromRecordCalls);
    }

    [Fact]
    public void RevertFromJournal_CallsEndSession()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());

        var journalData = journal.LoadJournal();
        Assert.NotNull(journalData);
        Assert.True(journalData!.SessionActive);

        var engine = new WatchdogRevertEngine(NoOpLogger, new Dictionary<string, Func<IJournaledOptimization>>());
        engine.RevertFromJournal(journalData, journal);

        // Reload journal from disk — SessionActive should be false after recovery
        var reloaded = new JournalManager(path).LoadJournal();
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.SessionActive);
    }

    [Fact]
    public void RevertFromJournal_SetsLastRecoveryTimestamp()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());

        var beforeRevert = DateTime.UtcNow;
        var journalData = journal.LoadJournal();
        Assert.NotNull(journalData);

        var engine = new WatchdogRevertEngine(NoOpLogger, new Dictionary<string, Func<IJournaledOptimization>>());
        engine.RevertFromJournal(journalData!, journal);

        // Reload and confirm the recovery timestamp was stamped and is sane
        var reloaded = new JournalManager(path).LoadJournal();
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.LastRecoveryTimestamp);
        Assert.True(reloaded.LastRecoveryTimestamp >= beforeRevert.AddSeconds(-1),
            $"LastRecoveryTimestamp {reloaded.LastRecoveryTimestamp:O} should be >= {beforeRevert.AddSeconds(-1):O}");
    }

    [Fact]
    public void RevertFromJournal_UpdatesEntryStateToReverted()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        var mock = new MockJournaledOptimization { Name = "Opt", ResultState = OptimizationState.Reverted };
        var factories = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            ["Opt"] = () => mock,
        };

        journal.StartSession(NewTestProfile());
        journal.RecordApplied(new OptimizationResult("Opt", "o", "o", OptimizationState.Applied));

        var journalData = journal.LoadJournal();
        Assert.NotNull(journalData);

        var engine = new WatchdogRevertEngine(NoOpLogger, factories);
        engine.RevertFromJournal(journalData!, journal);

        // Reload to observe the on-disk state change recorded by RecordReverted
        var reloaded = new JournalManager(path).LoadJournal();
        Assert.NotNull(reloaded);
        var entry = Assert.Single(reloaded!.Optimizations);
        Assert.Equal(nameof(OptimizationState.Reverted), entry.State);
    }

    [Fact]
    public void RevertFromJournal_EmptyJournal_EndsSessionWithoutError()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());
        // No optimizations recorded

        var journalData = journal.LoadJournal();
        Assert.NotNull(journalData);
        Assert.Empty(journalData!.Optimizations);

        var engine = new WatchdogRevertEngine(NoOpLogger, new Dictionary<string, Func<IJournaledOptimization>>());
        var ex = Record.Exception(() => engine.RevertFromJournal(journalData, journal));
        Assert.Null(ex);

        // Session should still have been ended and timestamp stamped
        var reloaded = new JournalManager(path).LoadJournal();
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.SessionActive);
        Assert.NotNull(reloaded.LastRecoveryTimestamp);
    }
}
