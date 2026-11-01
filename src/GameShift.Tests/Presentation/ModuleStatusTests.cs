using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Xunit;
using static GameShift.Tests.Presentation.OutcomeFixtures;

namespace GameShift.Tests.Presentation;

/// <summary>
/// Each module badge comes from that module's own result in the current session.
/// </summary>
public class ModuleStatusTests
{
    private static ModuleStatus During(ModuleOutcome outcome, bool isAvailable = true, bool enabled = true, bool paused = false) =>
        ModuleStatus.For(outcome.ModuleName, isAvailable, enabled, paused, Activation(outcome));

    [Theory]
    [InlineData(ModuleOutcomeKind.Applied, ModuleStatusKind.Applied)]
    [InlineData(ModuleOutcomeKind.AlreadyApplied, ModuleStatusKind.Applied)]   // Quick Optimize, then a game
    [InlineData(ModuleOutcomeKind.SkippedDisabled, ModuleStatusKind.Off)]
    [InlineData(ModuleOutcomeKind.Unavailable, ModuleStatusKind.NotAvailable)]
    public void During_ASession_TheBadgeFollowsTheModulesOwnResult(ModuleOutcomeKind kind, ModuleStatusKind expected)
    {
        var status = During(Module("Timer", kind));

        Assert.Equal(expected, status.Kind);
        Assert.Null(status.Reason);
    }

    [Fact]
    public void During_ASession_AFailureShowsItsReason()
    {
        var status = During(Module("Timer", ModuleOutcomeKind.Failed, "did not apply: access denied"));

        Assert.Equal(ModuleStatusKind.Failed, status.Kind);
        Assert.Equal("Did not apply: access denied.", status.Reason);
        Assert.Equal("Failed", status.Word);
    }

    [Fact]
    public void During_ASession_ADeclinedModuleIsNotAvailableWithAReason()
    {
        var status = During(Module("Timer", ModuleOutcomeKind.NotApplicable));

        Assert.Equal(ModuleStatusKind.NotAvailable, status.Kind);
        Assert.Equal("Does not apply to this game or system.", status.Reason);
    }

    [Fact]
    public void UnavailableModule_IsNotAvailableWhateverTheSessionSays()
    {
        Assert.Equal(ModuleStatusKind.NotAvailable,
            During(Module("Timer", ModuleOutcomeKind.Applied), isAvailable: false).Kind);
    }

    [Fact]
    public void PausedWithQuickOptimizeStillApplied_ShowsApplied()
    {
        Assert.Equal(ModuleStatusKind.Applied, During(Module("Timer", ModuleOutcomeKind.Applied), paused: true).Kind);
    }

    [Theory]
    [InlineData(true, false, ModuleStatusKind.Ready)]
    [InlineData(true, true, ModuleStatusKind.Paused)]
    [InlineData(false, false, ModuleStatusKind.Off)]
    [InlineData(false, true, ModuleStatusKind.Off)]
    public void WithoutASession_TheProfileSwitchAndPauseDecide(bool enabled, bool paused, ModuleStatusKind expected)
    {
        Assert.Equal(expected, ModuleStatus.For("Timer", true, enabled, paused, current: null).Kind);
    }

    [Fact]
    public void AnEndedSession_IsNotThisModulesStateNow()
    {
        var ended = Deactivation([Revert("Timer", ModuleRevertKind.Reverted)], false,
            Module("Timer", ModuleOutcomeKind.Failed, "did not apply"));

        Assert.Equal(ModuleStatusKind.Ready, ModuleStatus.For("Timer", true, true, false, ended).Kind);
    }

    [Fact]
    public void AModuleTheSessionDidNotList_IsReady()
    {
        Assert.Equal(ModuleStatusKind.Ready,
            ModuleStatus.For("Other", true, true, false, Activation(Module("Timer", ModuleOutcomeKind.Applied))).Kind);
    }

    [Theory]
    [InlineData(ModuleStatusKind.Applied, "Applied")]
    [InlineData(ModuleStatusKind.Failed, "Failed")]
    [InlineData(ModuleStatusKind.Off, "Off")]
    [InlineData(ModuleStatusKind.NotAvailable, "Not available")]
    [InlineData(ModuleStatusKind.Ready, "Ready")]
    [InlineData(ModuleStatusKind.Paused, "Paused")]
    public void Word_MatchesTheStateVocabulary(ModuleStatusKind kind, string word)
    {
        Assert.Equal(word, new ModuleStatus(kind, null).Word);
    }

    [Theory]
    [InlineData("did not apply", "Did not apply.")]
    [InlineData("did not revert: denied.", "Did not revert: denied.")]
    [InlineData("  ", "")]
    public void Sentence_CapitalisesAndEndsTheReason(string reason, string expected)
    {
        Assert.Equal(expected, ModuleStatus.Sentence(reason));
    }
}
