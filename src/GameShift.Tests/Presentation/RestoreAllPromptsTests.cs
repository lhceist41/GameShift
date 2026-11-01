using GameShift.Core.Presentation;
using GameShift.Core.SystemTweaks;
using Xunit;

namespace GameShift.Tests.Presentation;

/// <summary>
/// What Restore All asks: recorded changes first, then each kind GameShift did not record, and the
/// records it could not read are named on every path.
/// </summary>
public class RestoreAllPromptsTests
{
    private const string KeptFile = "A settings file GameShift could not read was kept as settings.unreadable-1.json.";

    private static LastingChange Recorded(string key) => new()
    {
        Kind = LastingChangeKind.SystemTweak,
        Key = key,
        DisplayName = key,
        UndoDescription = "Reverts it.",
    };

    private static LastingChange Own(LastingChangeKind kind, string key) => new()
    {
        Kind = kind,
        Key = key,
        DisplayName = key,
        UndoDescription = "Undoes it.",
        NeedsOwnConfirmation = true,
    };

    private static LastingChangesList List(IReadOnlyList<string> warnings, params LastingChange[] items) =>
        new() { Items = items, Warnings = warnings };

    [Fact]
    public void Plan_RecordedChangesComeFirstThenEachOtherKindOnItsOwn()
    {
        var reservation = Own(LastingChangeKind.CpuCoreReservation, "reservation");
        var startupA = Own(LastingChangeKind.RenamedStartupEntry, "AppA");
        var startupB = Own(LastingChangeKind.RenamedStartupEntry, "AppB");
        var tweak = Recorded("Hags");

        var plan = RestoreAllPrompts.Plan(List([], reservation, startupA, tweak, startupB), dataFolderRefused: false);

        Assert.Null(plan.NothingToUndo);
        Assert.Collection(plan.Questions,
            q => { Assert.Equal([tweak], q.Items); Assert.False(q.SeparatelyConfirmed); },
            q =>
            {
                Assert.Equal([reservation], q.Items);
                Assert.True(q.SeparatelyConfirmed);
                Assert.Contains("Earlier versions of GameShift could set one, but so can other tools", q.Text);
                Assert.EndsWith("Remove the reservation?", q.Text);
            },
            q => { Assert.Equal([startupA, startupB], q.Items); Assert.True(q.SeparatelyConfirmed); Assert.EndsWith("Turn these startup apps back on?", q.Text); });
    }

    [Fact]
    public void Plan_WarningsGoIntoTheRecordedQuestionOnly()
    {
        var plan = RestoreAllPrompts.Plan(
            List([KeptFile], Recorded("Hags"), Own(LastingChangeKind.CpuCoreReservation, "reservation")),
            dataFolderRefused: false);

        Assert.Contains(KeptFile, plan.Questions[0].Text);
        Assert.DoesNotContain(KeptFile, plan.Questions[1].Text);
    }

    [Fact]
    public void Plan_WithOnlyUnrecordedChangesTheFirstQuestionStillNamesTheWarnings()
    {
        var plan = RestoreAllPrompts.Plan(
            List([KeptFile], Own(LastingChangeKind.CpuCoreReservation, "reservation"), Own(LastingChangeKind.RenamedStartupEntry, "AppA")),
            dataFolderRefused: true);

        var first = plan.Questions[0].Text;
        Assert.Contains(RestoreAllPrompts.RefusedFolderWarning, first);
        Assert.Contains(KeptFile, first);
        Assert.True(first.IndexOf(RestoreAllPrompts.RefusedFolderWarning, StringComparison.Ordinal) <
                    first.IndexOf(KeptFile, StringComparison.Ordinal));
        Assert.DoesNotContain(KeptFile, plan.Questions[1].Text);
    }

    [Fact]
    public void Plan_NothingFoundWithUnreadRecordsIsAWarningNamingThem()
    {
        var plan = RestoreAllPrompts.Plan(List([KeptFile]), dataFolderRefused: true);

        Assert.True(plan.NothingToUndoIsWarning);
        Assert.Contains(KeptFile, plan.NothingToUndo);
        Assert.Contains(RestoreAllPrompts.RefusedFolderWarning, plan.NothingToUndo);
        Assert.Empty(plan.Questions);
    }

    [Fact]
    public void Plan_NothingFoundAndEverythingReadSaysSo()
    {
        var plan = RestoreAllPrompts.Plan(List([]), dataFolderRefused: false);

        Assert.False(plan.NothingToUndoIsWarning);
        Assert.Equal("GameShift found no lasting changes to undo.", plan.NothingToUndo);
        Assert.Empty(plan.Questions);
    }

    [Fact]
    public void Status_CountsUndoneAndFailed()
    {
        var a = Recorded("A");
        var b = Recorded("B");
        var c = Recorded("C");

        Assert.Equal("2 lasting changes undone, 1 failed.", RestoreAllPrompts.Status([
            new(a, LastingChangeOutcome.Undone, ""), new(b, LastingChangeOutcome.Undone, ""),
            new(c, LastingChangeOutcome.Failed, "x"),
        ]));
        Assert.Equal("1 lasting changes undone.", RestoreAllPrompts.Status([
            new(a, LastingChangeOutcome.Undone, ""), new(b, LastingChangeOutcome.LeftAlone, ""),
        ]));
    }
}
