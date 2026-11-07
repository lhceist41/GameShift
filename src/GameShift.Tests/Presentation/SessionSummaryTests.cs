using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Xunit;
using static GameShift.Tests.Presentation.OutcomeFixtures;

namespace GameShift.Tests.Presentation;

/// <summary>
/// The end-of-session text claims an undo only from the deactivation's own revert results.
/// </summary>
public class SessionSummaryTests
{
    [Fact]
    public void AllReverted_SaysAllUndone()
    {
        var summary = SessionSummary.For(Deactivation([
            Revert("C", ModuleRevertKind.Reverted),
            Revert("B", ModuleRevertKind.Reverted),
            Revert("A", ModuleRevertKind.Reverted)]))!;

        Assert.Equal(SessionSummaryTone.Success, summary.Tone);
        Assert.Equal("All 3 changes were undone.", summary.Text);
        Assert.Empty(summary.StillActive);
    }

    [Fact]
    public void OneReverted_SaysTheChangeWasUndone()
    {
        Assert.Equal("The change was undone.",
            SessionSummary.For(Deactivation([Revert("A", ModuleRevertKind.Reverted)]))!.Text);
    }

    [Fact]
    public void AFailedRevert_SaysHowManyAreStillActive()
    {
        var summary = SessionSummary.For(Deactivation([
            Revert("E", ModuleRevertKind.Reverted),
            Revert("D", ModuleRevertKind.Reverted),
            Revert("C", ModuleRevertKind.RevertFailed, "did not revert: denied"),
            Revert("B", ModuleRevertKind.RevertFailed),
            Revert("A", ModuleRevertKind.Reverted)]))!;

        Assert.Equal(SessionSummaryTone.Warning, summary.Tone);
        Assert.Equal("3 of 5 undone, 2 still active.", summary.Text);
        Assert.Equal(["C: Did not revert: denied.", "B"], summary.StillActive);
    }

    [Fact]
    public void QuickOptimizeThenAGame_CountsFromTheRevertsNotTheModules()
    {
        var modules = new[]
        {
            Module("A", ModuleOutcomeKind.AlreadyApplied),
            Module("B", ModuleOutcomeKind.AlreadyApplied),
            Module("C", ModuleOutcomeKind.AlreadyApplied),
        };

        var all = SessionSummary.For(Deactivation([
            Revert("C", ModuleRevertKind.Reverted),
            Revert("B", ModuleRevertKind.Reverted),
            Revert("A", ModuleRevertKind.Reverted)], false, modules))!;
        var partial = SessionSummary.For(Deactivation([
            Revert("C", ModuleRevertKind.Reverted),
            Revert("B", ModuleRevertKind.RevertFailed, "did not revert"),
            Revert("A", ModuleRevertKind.Reverted)], false, modules))!;

        Assert.Equal("All 3 changes were undone.", all.Text);
        Assert.Equal("2 of 3 undone, 1 still active.", partial.Text);
    }

    [Fact]
    public void CrashRecovery_IsNeverAllUndone()
    {
        var summary = SessionSummary.For(Deactivation([], recovered: true, Module("A", ModuleOutcomeKind.Applied)))!;

        Assert.Equal(SessionSummaryTone.Warning, summary.Tone);
        Assert.Equal(SessionSummary.RecoveryText, summary.Text);
        Assert.DoesNotContain("All", summary.Text);
    }

    [Fact]
    public void CrashRecovery_ClaimsNothingAboutWhatItUndid()
    {
        // Recovery also stamps modules it could not revert and skips the ones it does not journal.
        var summary = SessionSummary.For(Deactivation([], recovered: true, Module("A", ModuleOutcomeKind.Applied)))!;

        Assert.Equal(
            "Crash recovery ran during this session. GameShift cannot tell which of its changes recovery undid.",
            summary.Text);
    }

    [Fact]
    public void CrashRecovery_WinsOverAnyRevertList()
    {
        var summary = SessionSummary.For(Deactivation([Revert("A", ModuleRevertKind.Reverted)], recovered: true))!;

        Assert.Equal(SessionSummary.RecoveryText, summary.Text);
    }

    [Fact]
    public void NothingWasApplied_SaysThereWasNothingToUndo()
    {
        var summary = SessionSummary.For(Deactivation([]))!;

        Assert.Equal(SessionSummaryTone.Neutral, summary.Tone);
        Assert.Equal(SessionSummary.NothingToUndoText, summary.Text);
    }

    [Fact]
    public void AnActivationOutcome_IsNoSummary()
    {
        Assert.Null(SessionSummary.For(Activation(Module("A", ModuleOutcomeKind.Applied))));
        Assert.Null(SessionSummary.For(null));
    }

    [Fact]
    public void CountsLine_CountsAppliedAndLeftAsItWas_AndFailed_ButNotSkipped()
    {
        var summary = SessionSummary.For(Deactivation([
            Revert("B", ModuleRevertKind.Reverted),
            Revert("A", ModuleRevertKind.Reverted)], false,
            Module("A", ModuleOutcomeKind.Applied),
            Module("B", ModuleOutcomeKind.AlreadyApplied),
            Module("C", ModuleOutcomeKind.Failed, "did not apply"),
            Module("D", ModuleOutcomeKind.SkippedDisabled),
            Module("E", ModuleOutcomeKind.NotApplicable),
            Module("F", ModuleOutcomeKind.Unavailable)))!;

        Assert.Equal("2 applied, 1 failed", summary.CountsLine);
    }

    [Fact]
    public void CountsLine_LeavesOutFailedWhenNoneFailed()
    {
        var summary = SessionSummary.For(Deactivation([Revert("A", ModuleRevertKind.Reverted)], false,
            Module("A", ModuleOutcomeKind.Applied),
            Module("B", ModuleOutcomeKind.SkippedDisabled)))!;

        Assert.Equal("1 applied", summary.CountsLine);
    }

    [Fact]
    public void CountsLine_IsNullWhenTheActivationListedNoModules()
    {
        Assert.Null(SessionSummary.For(Deactivation([Revert("A", ModuleRevertKind.Reverted)]))!.CountsLine);
    }

    [Fact]
    public void CountsLine_StaysBesideARevertWarningAndCrashRecovery()
    {
        var module = Module("A", ModuleOutcomeKind.Applied);

        var partial = SessionSummary.For(Deactivation([Revert("A", ModuleRevertKind.RevertFailed)], false, module))!;
        var recovered = SessionSummary.For(Deactivation([], recovered: true, module))!;
        var nothingReverted = SessionSummary.For(Deactivation([], false, Module("A", ModuleOutcomeKind.Failed)))!;

        Assert.Equal("1 applied", partial.CountsLine);
        Assert.Equal("1 applied", recovered.CountsLine);
        Assert.Equal("0 applied, 1 failed", nothingReverted.CountsLine);
    }

    [Fact]
    public void KeepOpen_OnlyWhileChangesAreStillActive()
    {
        var partial = SessionSummary.For(Deactivation([
            Revert("B", ModuleRevertKind.Reverted),
            Revert("A", ModuleRevertKind.RevertFailed, "did not revert")]))!;
        var allUndone = SessionSummary.For(Deactivation([Revert("A", ModuleRevertKind.Reverted)]))!;
        var nothingToUndo = SessionSummary.For(Deactivation([]))!;
        var recovered = SessionSummary.For(Deactivation(
            [Revert("A", ModuleRevertKind.RevertFailed)], recovered: true))!;

        Assert.True(partial.KeepOpen);
        Assert.False(allUndone.KeepOpen);
        Assert.False(nothingToUndo.KeepOpen);
        Assert.False(recovered.KeepOpen);
    }
}
