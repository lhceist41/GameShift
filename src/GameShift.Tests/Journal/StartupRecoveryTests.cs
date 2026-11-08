using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Tests.TestHelpers;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Startup recovery replays a session that did not end normally, through a temp journal, mock
/// optimizations, a fake game-action reverter and a fake folder repair, so nothing touches the
/// machine. The only real system call is the in-place rename of an untrusted file in a temp folder.
/// </summary>
public class StartupRecoveryTests : IDisposable
{
    private readonly TempPath _temp = new();
    private readonly List<string> _sequence = new();
    private readonly List<string> _repaired = new();
    private int _trustChecks;

    private string JournalPath => _temp.GetFile("state.json");

    public void Dispose() => _temp.Dispose();

    private static readonly JournalTrustResult Trusted = new(true, Array.Empty<string>());

    private static GameProfile Profile(string game = "Overwatch") => new()
    {
        Id = "test",
        GameName = game,
        ExecutableName = "Overwatch.exe",
        ProcessId = 4242,
    };

    /// <summary>Writes an active session as the crashed app left it, through its own instance.</summary>
    private JournalManager CrashedSession(string game = "Overwatch", params string[] optimizations)
    {
        var writer = new JournalManager(JournalPath);
        writer.StartSession(Profile(game));
        foreach (var name in optimizations)
            writer.RecordApplied(new OptimizationResult(name, $"orig-{name}", $"applied-{name}", OptimizationState.Applied));
        return writer;
    }

    private Dictionary<string, Func<IJournaledOptimization>> Recording(params string[] names) =>
        names.ToDictionary(n => n, n => (Func<IJournaledOptimization>)(() => new RecordingMockJournaledOptimization(n, _sequence)), StringComparer.Ordinal);

    private WatchdogRevertEngine Engine(
        Dictionary<string, Func<IJournaledOptimization>> factories,
        Func<string, string, Serilog.ILogger, GameActionRevertResult>? gameActions = null,
        Action? reset = null) =>
        new(Logger.None, factories, gameActions ?? ((kind, payload, _) =>
        {
            _sequence.Add($"{kind}:{payload}");
            return new GameActionRevertResult(RevertOutcome.Reverted);
        }), resetBackgroundProcesses: reset);

    private StartupRecoveryResult Run(WatchdogRevertEngine engine, Func<string, JournalTrustResult>? trust = null) =>
        StartupRecovery.Run(
            new JournalManager(JournalPath),
            path =>
            {
                _trustChecks++;
                return (trust ?? (_ => Trusted))(path);
            },
            engine, Logger.None,
            (folder, _) =>
            {
                _repaired.Add(folder);
                return true;
            });

    private SessionJournalData OnDisk() => new JournalManager(JournalPath).PeekJournal()!;

    private string[] Evidence(string kind) => Directory.GetFiles(_temp.Path, $"state.{kind}-*.json");

    // ── Nothing to do ─────────────────────────────────────────────────────

    [Fact]
    public void NoJournal_IsNothingToDo()
    {
        var result = Run(Engine(Recording()));

        Assert.Equal(StartupRecoveryOutcome.NothingToDo, result.Outcome);
        Assert.False(result.WasSessionActive);
        Assert.Equal(0, _trustChecks);
        Assert.Null(result.Notice);
    }

    // ── Unreadable ────────────────────────────────────────────────────────

    // Whether the last session ended is unknown, so it must not be reported as clean.
    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("")]
    public void UnparsableJournal_IsUnreadable_AndKeptUnderANewName(string content)
    {
        File.WriteAllText(JournalPath, content);

        var result = Run(Engine(Recording()));

        Assert.Equal(StartupRecoveryOutcome.Unreadable, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Equal(1, _trustChecks);
        Assert.Empty(_sequence);
        Assert.Empty(_repaired);
        Assert.False(File.Exists(JournalPath));
        var kept = Assert.Single(Evidence("unreadable"));
        Assert.Equal(content, File.ReadAllText(kept));
        Assert.Equal(kept, result.EvidencePath);
        Assert.Equal(string.Format(StartupRecovery.UnreadableKeptNotice, Path.GetFileName(kept)), result.Notice);
        Assert.Matches(@"^state\.unreadable-\d{8}T\d{9}Z\.json$", Path.GetFileName(kept));
    }

    [Fact]
    public void UnparsableJournal_IsReportedOnce()
    {
        File.WriteAllText(JournalPath, "{ not json");
        Run(Engine(Recording()));

        var second = Run(Engine(Recording()));

        Assert.Equal(StartupRecoveryOutcome.NothingToDo, second.Outcome);
        Assert.False(second.WasSessionActive);
        Assert.Null(second.Notice);
        Assert.Single(Evidence("unreadable"));
    }

    [Fact]
    public void UnparsableUntrustedJournal_IsSetAsideAsUntrusted()
    {
        File.WriteAllText(JournalPath, "{ not json");

        var result = Run(Engine(Recording()), _ => JournalTrustResult.Untrusted("owned by a user"));

        Assert.Equal(StartupRecoveryOutcome.Untrusted, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Equal("{ not json", File.ReadAllText(Assert.Single(Evidence("untrusted"))));
        Assert.Empty(Evidence("unreadable"));
        Assert.Equal(new[] { _temp.Path }, _repaired);
        Assert.Equal(StartupRecovery.UntrustedNotice, result.Notice);
    }

    // A read can fail while another process holds the file, so nothing is moved. The handle shares
    // delete, so a rename would still succeed if the code tried one.
    [Fact]
    public void JournalThatCannotBeRead_IsUnreadable_AndLeftInPlace()
    {
        CrashedSession("Overwatch", "Opt1");
        var text = File.ReadAllText(JournalPath);

        StartupRecoveryResult result;
        using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
            result = Run(Engine(Recording("Opt1")));

        Assert.Equal(StartupRecoveryOutcome.Unreadable, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Equal(0, _trustChecks);
        Assert.Empty(_sequence);
        Assert.Equal(text, File.ReadAllText(JournalPath));
        Assert.Empty(Directory.GetFiles(_temp.Path, "state.*-*.json"));
        Assert.Null(result.EvidencePath);
        Assert.Equal(StartupRecovery.UnreadableNotice, result.Notice);
    }

    [Fact]
    public void UnparsableJournalThatCannotBeRenamed_IsUnreadable_AndSaysSo()
    {
        File.WriteAllText(JournalPath, "{ not json");

        StartupRecoveryResult result;
        using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = Run(Engine(Recording()));

        Assert.Equal(StartupRecoveryOutcome.Unreadable, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Equal("{ not json", File.ReadAllText(JournalPath));
        Assert.Empty(Evidence("unreadable"));
        Assert.Equal(StartupRecovery.UnreadableNotice, result.Notice);
    }

    // The file can change between the trusted peek and the second read; unknown is not clean.
    [Fact]
    public void TrustedJournalThatCannotBeReadAgain_IsUnreadable_AndLeftInPlace()
    {
        CrashedSession("Overwatch", "Opt1");
        var text = File.ReadAllText(JournalPath);
        FileStream? held = null;

        StartupRecoveryResult result;
        try
        {
            result = Run(Engine(Recording("Opt1")), path =>
            {
                held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
                return Trusted;
            });
        }
        finally
        {
            held?.Dispose();
        }

        Assert.Equal(StartupRecoveryOutcome.Unreadable, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Equal(1, _trustChecks);
        Assert.Empty(_sequence);
        Assert.Equal(text, File.ReadAllText(JournalPath));
        Assert.Empty(Directory.GetFiles(_temp.Path, "state.*-*.json"));
        Assert.Null(result.EvidencePath);
        Assert.Equal(StartupRecovery.UnreadableNotice, result.Notice);
    }

    [Fact]
    public void TrustedJournalThatEndedBeforeTheSecondRead_KeepsTheNothingToDoMapping()
    {
        CrashedSession("Overwatch", "Opt1");

        var result = Run(Engine(Recording("Opt1")), path =>
        {
            var other = new JournalManager(path);
            other.LoadJournal();
            other.EndSession();
            return Trusted;
        });

        Assert.Equal(StartupRecoveryOutcome.NothingToDo, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Empty(_sequence);
    }

    // An unexpected failure while reporting an unreadable record must not turn into "clean".
    [Fact]
    public void FailureWhileReportingAnUnreadableJournal_CountsTheSessionAsActive()
    {
        CrashedSession("Overwatch", "Opt1");
        var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().AuditTo.Sink(new ThrowOnWarning()).CreateLogger();

        StartupRecoveryResult result;
        using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
            result = StartupRecovery.Run(new JournalManager(JournalPath), _ => Trusted, Engine(Recording("Opt1")), logger, (_, _) => true);

        Assert.Equal(StartupRecoveryOutcome.Partial, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.NotNull(result.Notice);
        Assert.Empty(_sequence);
    }

    /// <summary>An audit sink, so the exception reaches the caller instead of being swallowed.</summary>
    private sealed class ThrowOnWarning : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            if (logEvent.Level == Serilog.Events.LogEventLevel.Warning)
                throw new InvalidOperationException("logging failed");
        }
    }

    [Fact]
    public void SessionThatEndedNormally_IsNothingToDo_AndStillLoadsTheWarnings()
    {
        var writer = CrashedSession("Overwatch", "Opt1");
        writer.EndSession();
        writer.RecordPendingRebootFix("MSI mode");
        var journal = new JournalManager(JournalPath);

        var result = StartupRecovery.Run(journal, _ => { _trustChecks++; return Trusted; }, Engine(Recording("Opt1")), Logger.None,
            (folder, _) => { _repaired.Add(folder); return true; });

        Assert.Equal(StartupRecoveryOutcome.NothingToDo, result.Outcome);
        Assert.False(result.WasSessionActive);
        Assert.Equal(0, _trustChecks);
        Assert.Empty(_sequence);
        Assert.Equal(new[] { "MSI mode" }, journal.GetPendingRebootFixDescriptions());
    }

    [Fact]
    public void UnknownJournalVersion_IsNothingToDoAndLeftAlone()
    {
        CrashedSession("Overwatch", "Opt1");
        var text = File.ReadAllText(JournalPath).Replace("\"Version\": 2", "\"Version\": 3");
        File.WriteAllText(JournalPath, text);

        var result = Run(Engine(Recording("Opt1")));

        Assert.Equal(StartupRecoveryOutcome.NothingToDo, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Equal(0, _trustChecks);
        Assert.Empty(_sequence);
        Assert.Equal(text, File.ReadAllText(JournalPath));
    }

    // ── Recovered ─────────────────────────────────────────────────────────

    [Fact]
    public void ActiveTrustedJournal_UndoesGameActionsNewestFirstThenOptimizationsLifo()
    {
        var writer = CrashedSession("Overwatch", "First", "Second");
        writer.RecordGameAction("registry", "older");
        writer.RecordGameAction("firewall", "newer");

        var result = Run(Engine(Recording("First", "Second")));

        Assert.Equal(StartupRecoveryOutcome.Recovered, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Equal(new[] { "firewall:newer", "registry:older", "Second", "First" }, _sequence);
        Assert.Equal(1, _trustChecks);
        Assert.False(OnDisk().SessionActive);
        Assert.Equal(1, OnDisk().RecoveryAttempts);
        Assert.Empty(Evidence("unrecovered"));
        Assert.Equal("Overwatch", result.GameName);
        Assert.StartsWith("GameShift did not close normally during Overwatch (", result.Notice);
        Assert.EndsWith("It has now undone the changes it made then.", result.Notice);
    }

    // The counter must be on disk before any record is replayed, or a replay that takes the
    // process down would run again on every start.
    [Fact]
    public void AttemptIsPersistedBeforeTheFirstRecordIsReplayed()
    {
        CrashedSession("Overwatch", "Opt1");
        int? attemptsDuringReplay = null;
        var factories = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            ["Opt1"] = () =>
            {
                attemptsDuringReplay = OnDisk().RecoveryAttempts;
                return new MockJournaledOptimization { Name = "Opt1" };
            },
        };

        Run(Engine(factories));

        Assert.Equal(1, attemptsDuringReplay);
    }

    [Fact]
    public void ScheduledTasksReEnabled_AreNamedWithTheAdviceToDisableThemAgain()
    {
        CrashedSession("Overwatch", ScheduledTaskSuppressor.OptimizationId);
        var tasks = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            [ScheduledTaskSuppressor.OptimizationId] = () => new MessageMock(ScheduledTaskSuppressor.OptimizationId, OptimizationState.Reverted,
                ScheduledTaskSuppressor.ReEnabledPrefix + @"\Microsoft\Windows\Defrag\ScheduledDefrag"),
        };

        var result = Run(Engine(tasks));

        Assert.Equal(StartupRecoveryOutcome.Recovered, result.Outcome);
        Assert.Contains(@"Re-enabled scheduled tasks: \Microsoft\Windows\Defrag\ScheduledDefrag. " + StartupRecovery.TaskReEnabledAdvice, result.Notice);
        Assert.DoesNotContain("could not be undone", result.Notice);
    }

    // ── Untrusted ─────────────────────────────────────────────────────────

    [Fact]
    public void UntrustedJournal_RunsNothing_IsSetAsideAndThenTheFolderIsRepaired()
    {
        var writer = CrashedSession("Overwatch", "Opt1");
        writer.RecordGameAction("registry", "{}");
        bool renamedBeforeRepair = false;

        var result = StartupRecovery.Run(
            new JournalManager(JournalPath),
            _ => JournalTrustResult.Untrusted("owned by a user"),
            Engine(Recording("Opt1")), Logger.None,
            (folder, _) =>
            {
                renamedBeforeRepair = !File.Exists(JournalPath) && Evidence("untrusted").Length == 1;
                _repaired.Add(folder);
                return true;
            });

        Assert.Equal(StartupRecoveryOutcome.Untrusted, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Empty(_sequence);
        Assert.Empty(result.Items);
        Assert.False(File.Exists(JournalPath));
        Assert.Single(Evidence("untrusted"));
        Assert.True(renamedBeforeRepair);
        Assert.Equal(new[] { _temp.Path }, _repaired);
        Assert.Equal(StartupRecovery.UntrustedNotice, result.Notice);
        Assert.Null(result.GameName);
    }

    // A later metadata save (acknowledging a warning) must not write the untrusted session back
    // under the journal's name, where the repaired folder would make it look trusted.
    [Fact]
    public void UntrustedJournal_IsNeverAdoptedByTheInstance()
    {
        CrashedSession("Overwatch", "Opt1");
        var journal = new JournalManager(JournalPath);

        StartupRecovery.Run(journal, _ => JournalTrustResult.Untrusted("test"), Engine(Recording("Opt1")), Logger.None, (_, _) => true);
        journal.ClearBuildChangedWarning();

        var rewritten = new JournalManager(JournalPath).PeekJournal();
        Assert.False(rewritten?.SessionActive ?? false);
        Assert.Empty(rewritten?.Optimizations ?? new List<JournalEntry>());
    }

    [Fact]
    public void TrustCheckThatThrows_IsUntrusted()
    {
        CrashedSession("Overwatch", "Opt1");

        var result = Run(Engine(Recording("Opt1")), _ => throw new UnauthorizedAccessException());

        Assert.Equal(StartupRecoveryOutcome.Untrusted, result.Outcome);
        Assert.Empty(_sequence);
    }

    // Repairing while the file still has the journal's name would make the untrusted record look trusted.
    [Fact]
    public void UntrustedJournalThatCannotBeRenamed_IsNotRepaired()
    {
        CrashedSession("Overwatch", "Opt1");

        StartupRecoveryResult result;
        using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = Run(Engine(Recording("Opt1")), _ => JournalTrustResult.Untrusted("test"));

        Assert.Equal(StartupRecoveryOutcome.Untrusted, result.Outcome);
        Assert.Empty(_sequence);
        Assert.True(File.Exists(JournalPath));
        Assert.Empty(_repaired);
    }

    // ── Partial ───────────────────────────────────────────────────────────

    [Fact]
    public void FailedRecord_IsPartial_AndTheRecordsAreKeptWhileTheSessionWasStillActive()
    {
        CrashedSession("Overwatch", PowerPlanSwitcher.OptimizationId, "Opt2");
        var factories = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            [PowerPlanSwitcher.OptimizationId] = () => new MockJournaledOptimization
            {
                Name = PowerPlanSwitcher.OptimizationId,
                ResultState = OptimizationState.Failed,
            },
            ["Opt2"] = () => new MockJournaledOptimization { Name = "Opt2" },
        };

        var result = Run(Engine(factories));

        Assert.Equal(StartupRecoveryOutcome.Partial, result.Outcome);
        var evidence = Assert.Single(Evidence("unrecovered"));
        Assert.Equal(evidence, result.EvidencePath);
        var kept = new JournalManager(evidence).PeekJournal()!;
        Assert.True(kept.SessionActive);
        Assert.Equal(nameof(OptimizationState.Failed), kept.Optimizations[0].State);
        Assert.False(OnDisk().SessionActive);
        Assert.Contains("It has undone what it could.", result.Notice);
        Assert.Contains(
            $"Some changes could not be undone: {PowerPlanSwitcher.OptimizationId}. GameShift kept a copy of that session's record, and its log lists what is left.",
            result.Notice);
        Assert.DoesNotContain("Restore All", result.Notice);
    }

    [Fact]
    public void ThrowingRecord_DoesNotStopTheRest()
    {
        CrashedSession("Overwatch", "Opt1", "Opt2", "Opt3");
        var opt1 = new MockJournaledOptimization { Name = "Opt1" };
        var opt3 = new MockJournaledOptimization { Name = "Opt3" };
        var factories = new Dictionary<string, Func<IJournaledOptimization>>(StringComparer.Ordinal)
        {
            ["Opt1"] = () => opt1,
            ["Opt2"] = () => new MockJournaledOptimization { Name = "Opt2", ShouldThrow = true },
            ["Opt3"] = () => opt3,
        };

        var result = Run(Engine(factories));

        Assert.Equal(StartupRecoveryOutcome.Partial, result.Outcome);
        Assert.Single(opt1.RevertFromRecordCalls);
        Assert.Single(opt3.RevertFromRecordCalls);
        Assert.Single(Evidence("unrecovered"));
    }

    // Record text never reaches the notice: a name GameShift does not know and the paths a
    // Defender record lists are shown only as fixed labels.
    [Fact]
    public void NotUndoneItems_AreNamedOnlyByFixedLabels()
    {
        var writer = CrashedSession("Overwatch", "Injected <b>name</b>");
        writer.RecordGameAction("defender", """{"paths":["C:\\Users\\victim\\secret"]}""");

        var result = Run(Engine(Recording(), (kind, _, _) =>
            new GameActionRevertResult(RevertOutcome.Failed, @"Not removed: C:\Users\victim\secret")));

        Assert.Equal(StartupRecoveryOutcome.Partial, result.Outcome);
        Assert.Contains("a Windows Security exclusion added for the game", result.Notice);
        Assert.Contains("a setting from an earlier version", result.Notice);
        Assert.DoesNotContain("victim", result.Notice);
        Assert.DoesNotContain("Injected", result.Notice);
    }

    // A replay that dies after some records were undone (here the step after them throws) leaves
    // the session active; the next start skips what was already undone.
    [Fact]
    public void ReplayCutShort_IsRetriedSkippingWhatWasUndone()
    {
        CrashedSession("Overwatch", "Opt1", "Opt2");

        var first = Run(Engine(Recording("Opt1", "Opt2"), reset: () => throw new InvalidOperationException("process died")));

        Assert.Equal(StartupRecoveryOutcome.Partial, first.Outcome);
        Assert.Equal(new[] { "Opt2", "Opt1" }, _sequence);
        Assert.True(OnDisk().SessionActive);
        Assert.Equal(1, OnDisk().RecoveryAttempts);

        _sequence.Clear();
        var second = Run(Engine(Recording("Opt1", "Opt2")));

        Assert.Equal(StartupRecoveryOutcome.Recovered, second.Outcome);
        Assert.Empty(_sequence);
        Assert.False(OnDisk().SessionActive);
    }

    // ── Gave up ───────────────────────────────────────────────────────────

    [Fact]
    public void TwoUnfinishedAttempts_ReplayNothing_KeepTheRecordsAndEndTheSession()
    {
        var writer = CrashedSession("Overwatch", PowerPlanSwitcher.OptimizationId);
        writer.RecordGameAction("firewall", "{}");
        writer.RecordRecoveryAttempt();
        writer.RecordRecoveryAttempt();

        var result = Run(Engine(Recording(PowerPlanSwitcher.OptimizationId)));

        Assert.Equal(StartupRecoveryOutcome.GaveUp, result.Outcome);
        Assert.True(result.WasSessionActive);
        Assert.Empty(_sequence);
        Assert.False(OnDisk().SessionActive);
        Assert.Single(Evidence("unrecovered"));
        Assert.Equal(new[] { (true, "firewall"), (false, PowerPlanSwitcher.OptimizationId) },
            result.Items.Select(i => (i.IsGameAction, i.Name)));
        Assert.Contains("could not undo those changes automatically", result.Notice);
        Assert.Contains($"a firewall rule added for the game, {PowerPlanSwitcher.OptimizationId}", result.Notice);
    }

    [Fact]
    public void ThirdStartAfterTwoReplaysDied_GivesUp()
    {
        CrashedSession("Overwatch", "Opt1");
        Func<IJournaledOptimization> dies = () => throw new InvalidOperationException("never reached");
        var dying = Engine(new Dictionary<string, Func<IJournaledOptimization>> { ["Opt1"] = () => new MockJournaledOptimization { Name = "Opt1", ShouldThrow = true } },
            reset: () => throw new InvalidOperationException("process died"));

        Assert.Equal(StartupRecoveryOutcome.Partial, Run(dying).Outcome);
        Assert.Equal(StartupRecoveryOutcome.Partial, Run(dying).Outcome);
        var third = Run(Engine(new Dictionary<string, Func<IJournaledOptimization>> { ["Opt1"] = dies }));

        Assert.Equal(StartupRecoveryOutcome.GaveUp, third.Outcome);
        Assert.False(OnDisk().SessionActive);
    }

    [Fact]
    public void GameNameMissing_IsDescribedAsAGameSession()
    {
        var notice = StartupRecovery.DescribeForUser(new StartupRecoveryResult(
            true, StartupRecoveryOutcome.Recovered, Array.Empty<JournalRevertItem>(), GameName: "", SessionStarted: null));

        Assert.Equal("GameShift did not close normally during a game session. It has now undone the changes it made then.", notice);
    }

    [Fact]
    public void Notices_HaveNoLongDashes()
    {
        var outcomes = new[] { StartupRecoveryOutcome.Recovered, StartupRecoveryOutcome.Partial, StartupRecoveryOutcome.GaveUp, StartupRecoveryOutcome.Untrusted };
        var items = new[] { new JournalRevertItem(false, ScheduledTaskSuppressor.OptimizationId, RevertOutcome.Failed,
            ScheduledTaskSuppressor.ReEnabledPrefix + "a. " + ScheduledTaskSuppressor.NotReEnabledPrefix + "b") };
        foreach (var outcome in outcomes)
        {
            var notice = StartupRecovery.DescribeForUser(new StartupRecoveryResult(true, outcome, items, "Game", DateTimeOffset.UtcNow));
            Assert.DoesNotContain('\u2014', notice!);
            Assert.DoesNotContain('\u2013', notice!);
        }
    }

    private sealed class MessageMock : IJournaledOptimization
    {
        private readonly OptimizationState _state;
        private readonly string _message;

        public MessageMock(string name, OptimizationState state, string message)
        {
            Name = name;
            _state = state;
            _message = message;
        }

        public string Name { get; }
        public bool CanApply(SystemContext context) => true;
        public OptimizationResult Apply() => new(Name, string.Empty, string.Empty, OptimizationState.Applied);
        public OptimizationResult Revert() => new(Name, string.Empty, string.Empty, OptimizationState.Reverted);
        public bool Verify() => true;
        public OptimizationResult RevertFromRecord(string originalValueJson) => new(Name, string.Empty, string.Empty, _state, _message);
    }
}
