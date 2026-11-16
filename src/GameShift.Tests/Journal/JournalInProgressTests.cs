using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Tests.TestHelpers;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Entries recorded while a module is still applying: one entry per apply, completed or resolved in
/// place, replayed like a finished one and reported without claiming more than was recorded. All
/// journals live in a temp folder.
/// </summary>
public class JournalInProgressTests : IDisposable
{
    private readonly TempPath _temp = new();
    private readonly List<string> _sequence = new();

    private string JournalPath => _temp.GetFile("state.json");

    public void Dispose() => _temp.Dispose();

    private static GameProfile Profile(string game = "Overwatch") => new()
    {
        Id = "test",
        GameName = game,
        ExecutableName = "Overwatch.exe",
        ProcessId = 4242,
    };

    private JournalManager Session(string game = "Overwatch")
    {
        var journal = new JournalManager(JournalPath);
        journal.StartSession(Profile(game));
        return journal;
    }

    private SessionJournalData OnDisk() => new JournalManager(JournalPath).PeekJournal()!;

    private StartupRecoveryResult Recover(params string[] optimizations)
    {
        var factories = optimizations.ToDictionary(
            n => n, n => (Func<IJournaledOptimization>)(() => new RecordingMockJournaledOptimization(n, _sequence)), StringComparer.Ordinal);
        var engine = new WatchdogRevertEngine(Logger.None, factories);
        return StartupRecovery.Run(
            new JournalManager(JournalPath), _ => new JournalTrustResult(true, Array.Empty<string>()), engine, Logger.None, (_, _) => true);
    }

    // ── File format ──────────────────────────────────────────────────────

    // The options the journal writes with: indented, nulls left out.
    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void FinishedEntry_SerializesExactlyAsEntriesWithoutTheMember()
    {
        var at = new DateTimeOffset(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);
        var entry = new JournalEntry { Name = "Opt", State = "Applied", OriginalValue = "orig", AppliedValue = "applied", AppliedAt = at };

        var withoutMember = JsonSerializer.Serialize(
            new { Name = "Opt", State = "Applied", OriginalValue = "orig", AppliedValue = "applied", AppliedAt = at }, WriterOptions);

        Assert.Equal(withoutMember, JsonSerializer.Serialize(entry, WriterOptions));
    }

    [Fact]
    public void InProgressEntry_IsWrittenWithTheMember_AndAFinishedOneWithout()
    {
        var journal = Session();
        var handle = journal.RecordInProgress("Opt", "intent");

        Assert.Contains("\"InProgress\": true", File.ReadAllText(JournalPath));

        journal.RecordApplied(new OptimizationResult("Opt", "intent", "applied", OptimizationState.Applied), handle);

        Assert.DoesNotContain("InProgress", File.ReadAllText(JournalPath));
    }

    [Fact]
    public void JournalWithoutTheMember_ReadsAsFinished()
    {
        File.WriteAllText(JournalPath, """
            {
              "Version": 2,
              "SessionActive": true,
              "Optimizations": [
                { "Name": "Opt", "State": "Applied", "OriginalValue": "orig", "AppliedValue": "applied", "AppliedAt": "2026-11-16T20:00:00+00:00" }
              ]
            }
            """);

        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.False(entry.InProgress);
        Assert.Equal("Applied", entry.State);
    }

    // ── One entry per apply ──────────────────────────────────────────────

    [Fact]
    public void Intents_ThenApplied_LeaveOneFinishedEntryWithTheLastRecord()
    {
        var journal = Session();
        var handle = journal.RecordInProgress("Opt", "first");

        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Applied", true, "first"), (entry.State, entry.InProgress, entry.OriginalValue));

        handle = journal.RecordInProgress("Opt", "first+second", handle);
        Assert.Equal("first+second", Assert.Single(OnDisk().Optimizations).OriginalValue);

        journal.RecordApplied(new OptimizationResult("Opt", "first+second", "applied", OptimizationState.Applied), handle);

        entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Applied", false, "first+second", "applied"), (entry.State, entry.InProgress, entry.OriginalValue, entry.AppliedValue));
    }

    [Fact]
    public void RecordedRevertOfAnIntent_LeavesOneResolvedEntry()
    {
        var journal = Session();
        journal.RecordInProgress("Opt", "intent");

        journal.RecordReverted("Opt", OptimizationState.Reverted);

        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Reverted", false), (entry.State, entry.InProgress));
        Assert.DoesNotContain("InProgress", File.ReadAllText(JournalPath));
    }

    [Fact]
    public void AppliedWithoutAHandle_AppendsAsBefore()
    {
        var journal = Session();
        journal.RecordInProgress("First", "intent");

        journal.RecordApplied(new OptimizationResult("Second", "orig", "applied", OptimizationState.Applied));

        Assert.Equal(new[] { ("First", true), ("Second", false) }, OnDisk().Optimizations.Select(e => (e.Name, e.InProgress)));
    }

    // A handle from an earlier session must not complete an entry the current session no longer holds.
    [Fact]
    public void HandleFromAnEarlierSession_AppendsToTheCurrentSession()
    {
        var journal = Session("First game");
        var stale = journal.RecordInProgress("Opt", "earlier");
        journal.EndSession();
        journal.StartSession(Profile("Second game"));

        journal.RecordInProgress("Opt", "current", stale);

        var data = OnDisk();
        Assert.Equal("Second game", data.ActiveGame!.Name);
        var entry = Assert.Single(data.Optimizations);
        Assert.Equal(("current", true), (entry.OriginalValue, entry.InProgress));

        journal.RecordApplied(new OptimizationResult("Opt", "done", "applied", OptimizationState.Applied), stale);

        Assert.Equal(new[] { ("current", true), ("done", false) }, OnDisk().Optimizations.Select(e => (e.OriginalValue, e.InProgress)));
    }

    // A handle names one module's entry; another module's record must not land on it.
    [Fact]
    public void HandleOfAnotherModule_AppendsInsteadOfUpdatingIt()
    {
        var journal = Session();
        var first = journal.RecordInProgress("First", "first-intent");

        journal.RecordInProgress("Second", "second-intent", first);

        Assert.Equal(new[] { ("First", "first-intent", true), ("Second", "second-intent", true) },
            OnDisk().Optimizations.Select(e => (e.Name, e.OriginalValue, e.InProgress)));
    }

    [Fact]
    public void AppliedWithTheHandleOfAnotherModule_AppendsAndLeavesThatEntryInProgress()
    {
        var journal = Session();
        var first = journal.RecordInProgress("First", "first-intent");

        journal.RecordApplied(new OptimizationResult("Second", "second-original", "applied", OptimizationState.Applied), first);

        Assert.Equal(new[] { ("First", "Applied", true), ("Second", "Applied", false) },
            OnDisk().Optimizations.Select(e => (e.Name, e.State, e.InProgress)));
    }

    // The handle shares neither read nor write, so the save cannot read the file and writes nothing.
    [Fact]
    public void IntentWhoseSaveFails_DoesNotThrow_AndTheNextSaveWritesIt()
    {
        var journal = Session();

        using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
            journal.RecordInProgress("Opt", "intent");

        Assert.Empty(OnDisk().Optimizations);

        journal.RecordApplied(new OptimizationResult("Other", "orig", "applied", OptimizationState.Applied));

        Assert.Equal(new[] { ("Opt", true), ("Other", false) }, OnDisk().Optimizations.Select(e => (e.Name, e.InProgress)));
    }

    // ── Recovery ─────────────────────────────────────────────────────────

    [Fact]
    public void InProgressEntry_IsReplayed_AndTheNoticeClaimsOnlyWhatWasRecorded()
    {
        var journal = Session();
        journal.RecordApplied(new OptimizationResult("First", "orig", "applied", OptimizationState.Applied));
        journal.RecordInProgress("Second", "intent");

        var result = Recover("First", "Second");

        Assert.Equal(StartupRecoveryOutcome.Recovered, result.Outcome);
        Assert.Equal(new[] { "Second", "First" }, _sequence);
        Assert.Equal(new[] { ("Second", true), ("First", false) }, result.Items.Select(i => (i.Name, i.InProgress)));
        Assert.EndsWith(" It was still making its changes then. It has now undone every change it had recorded.", result.Notice);
        Assert.All(OnDisk().Optimizations, e => Assert.Equal(("Reverted", false), (e.State, e.InProgress)));
        Assert.False(OnDisk().SessionActive);
    }

    [Fact]
    public void SessionWithNothingRecorded_SaysSo()
    {
        Session();

        var result = Recover();

        Assert.Equal(StartupRecoveryOutcome.Recovered, result.Outcome);
        Assert.Empty(result.Items);
        Assert.EndsWith(" It found no recorded changes to undo.", result.Notice);
    }

    [Fact]
    public void PartialRecoveryWithAnInProgressItem_KeepsThePartialWording()
    {
        var notice = StartupRecovery.DescribeForUser(new StartupRecoveryResult(
            true, StartupRecoveryOutcome.Partial,
            new[]
            {
                new JournalRevertItem(false, "Unknown module", RevertOutcome.Failed, "failed", InProgress: true),
                new JournalRevertItem(false, "Other module", RevertOutcome.Reverted, null),
            },
            "Game", SessionStarted: null));

        Assert.Equal(
            "GameShift did not close normally during Game. It has undone what it could.\n\n" +
            $"Some changes could not be undone: a setting from an earlier version. {StartupRecovery.NotUndoneAdvice}",
            notice);
    }

    [Fact]
    public void SkippedInProgressEntry_KeepsItsFlagInTheItem()
    {
        var journal = Session();
        journal.RecordInProgress("No factory", "intent");

        var result = Recover();

        var item = Assert.Single(result.Items);
        Assert.Equal((RevertOutcome.Skipped, true), (item.Outcome, item.InProgress));
        Assert.Equal(StartupRecoveryOutcome.Partial, result.Outcome);
    }

    // ── Flushed atomic write ─────────────────────────────────────────────

    [Fact]
    public void WriteFileAtomically_FlushesTheNewFileBeforeItReplacesTheOld()
    {
        using var folder = JournalFolder.Open(_temp.Path, out _)!;
        folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("old"));
        var seenAtFlush = new List<(string Journal, bool TempExists)>();
        folder.FlushToDisk = handle =>
        {
            seenAtFlush.Add((File.ReadAllText(JournalPath), File.Exists(JournalPath + ".tmp")));
            RandomAccess.FlushToDisk(handle);
        };

        folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("new"));

        Assert.Equal(new[] { ("old", true) }, seenAtFlush);
        Assert.Equal("new", File.ReadAllText(JournalPath));
        Assert.False(File.Exists(JournalPath + ".tmp"));
    }

    [Fact]
    public void WriteFileAtomically_WhenTheFlushFails_KeepsTheOldFileAndLeavesNoTempFile()
    {
        using var folder = JournalFolder.Open(_temp.Path, out _)!;
        folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("old"));
        folder.FlushToDisk = _ => throw new IOException("The disk did not take the data");

        Assert.Throws<IOException>(() => folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("new")));

        Assert.Equal("old", File.ReadAllText(JournalPath));
        Assert.False(File.Exists(JournalPath + ".tmp"));
    }
}
