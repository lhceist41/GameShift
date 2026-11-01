using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Xunit;
using static GameShift.Tests.Presentation.OutcomeFixtures;

namespace GameShift.Tests.Presentation;

/// <summary>
/// An activation that throws leaves the engine's previous outcome in place; it must never be shown
/// as the session the caller just asked for.
/// </summary>
public class CurrentSessionTests
{
    [Fact]
    public void Select_ActivationStartedAfterTheRequest_IsCurrent()
    {
        var outcome = Activation(Module("A", ModuleOutcomeKind.Applied));

        Assert.Same(outcome, CurrentSession.Select(outcome, Start.AddSeconds(-1)));
        Assert.Same(outcome, CurrentSession.Select(outcome, Start));
    }

    [Fact]
    public void Select_ActivationFromBeforeTheRequest_IsStale()
    {
        var previous = Activation(Module("A", ModuleOutcomeKind.Applied));

        Assert.Null(CurrentSession.Select(previous, Start.AddMinutes(5)));
    }

    [Fact]
    public void Select_EndedSession_IsNotCurrent()
    {
        var ended = Deactivation([Revert("A", ModuleRevertKind.Reverted)]);

        Assert.Null(CurrentSession.Select(ended, Start.AddSeconds(-1)));
    }

    [Fact]
    public void Select_Nothing_IsNotCurrent()
    {
        Assert.Null(CurrentSession.Select(null, Start));
        Assert.Null(CurrentSession.Select(
            new SessionOutcome(null, null, [], [], null, false), Start.AddDays(-1)));
    }
}
