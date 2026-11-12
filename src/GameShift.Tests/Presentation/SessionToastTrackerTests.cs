using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Xunit;
using static GameShift.Tests.Presentation.OutcomeFixtures;

namespace GameShift.Tests.Presentation;

/// <summary>
/// The end-of-session toast shows when the game session's own deactivation arrives, whatever ended
/// the session, with that session's duration and DPC load. A game exit, Home's End session and tray
/// Pause all reach this type as that deactivation; what tells them apart is what does not follow.
/// </summary>
public class SessionToastTrackerTests
{
    private static readonly DateTime K1 = Start;
    private static readonly DateTime K2 = Start.AddMinutes(30);

    private static SessionOutcome Activated(DateTime key, string? game = "Test Game") =>
        new(key, game, [Module("A", ModuleOutcomeKind.Applied)], [], EndedUtc: null, RevertedByRecovery: false);

    private static SessionOutcome Deactivated(
        DateTime key, TimeSpan after, ModuleRevertKind kind = ModuleRevertKind.Reverted, string? game = "Test Game") =>
        new(key, game, [Module("A", ModuleOutcomeKind.Applied)], [Revert("A", kind)],
            key + after, RevertedByRecovery: false);

    [Fact]
    public void GameExit_ShowsTheToastOnceWithTheEngineSessionsDurationAndDpc()
    {
        var tracker = new SessionToastTracker();
        var revert = Deactivated(K1, TimeSpan.FromMinutes(5));

        tracker.OnActivation(Activated(K1), isGameSession: true);
        tracker.OnLatencySampled(100);
        tracker.OnLatencySampled(300);
        var toast = tracker.OnDeactivation(revert);

        Assert.NotNull(toast);
        Assert.Equal("Test Game", toast.GameName);
        Assert.Equal(TimeSpan.FromMinutes(5), toast.Duration);
        Assert.Equal(200, toast.AvgDpc);
        Assert.Equal(300, toast.PeakDpc);
        Assert.Same(revert, toast.Deactivation);
        Assert.Null(tracker.OnDeactivation(revert));
    }

    [Fact]
    public void EndSession_ShowsTheToastAtOnceWithTheDurationUpToTheEndNotTheGameExit()
    {
        var tracker = new SessionToastTracker();

        tracker.OnActivation(Activated(K1), isGameSession: true);
        var toast = tracker.OnDeactivation(Deactivated(K1, TimeSpan.FromMinutes(7)));

        // The game runs on and its later exit reaches no deactivation, so this toast is the only one.
        Assert.NotNull(toast);
        Assert.Equal(TimeSpan.FromMinutes(7), toast.Duration);
    }

    [Fact]
    public void Pause_ShowsTheToastAtOnceThoughNoGameStopIsReportedWhilePaused()
    {
        var tracker = new SessionToastTracker();

        tracker.OnActivation(Activated(K1), isGameSession: true);
        tracker.OnLatencySampled(50);
        var toast = tracker.OnDeactivation(Deactivated(K1, TimeSpan.FromMinutes(12)));

        Assert.NotNull(toast);
        Assert.Equal(TimeSpan.FromMinutes(12), toast.Duration);
        Assert.Equal(50, toast.PeakDpc);
    }

    [Fact]
    public void PauseThenResumeWithTheGameRunning_EachSessionHasItsOwnDurationAndDpc()
    {
        var tracker = new SessionToastTracker();
        var paused = Deactivated(K1, TimeSpan.FromMinutes(5));

        tracker.OnActivation(Activated(K1), isGameSession: true);
        tracker.OnLatencySampled(900);
        var first = tracker.OnDeactivation(paused);

        tracker.OnActivation(Activated(K2), isGameSession: true);
        tracker.OnLatencySampled(100);
        Assert.Null(tracker.OnDeactivation(paused));
        var second = tracker.OnDeactivation(Deactivated(K2, TimeSpan.FromMinutes(10)));

        Assert.NotNull(first);
        Assert.Equal(TimeSpan.FromMinutes(5), first.Duration);
        Assert.NotNull(second);
        Assert.Equal(TimeSpan.FromMinutes(10), second.Duration);
        Assert.Equal(100, second.PeakDpc);
        Assert.Equal(100, second.AvgDpc);
    }

    [Fact]
    public void GameShiftExit_ShowsNoToast()
    {
        var tracker = new SessionToastTracker();

        tracker.OnActivation(Activated(K1), isGameSession: true);
        tracker.OnExiting();

        Assert.Null(tracker.OnDeactivation(Deactivated(K1, TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void QuickOptimizeSession_ShowsNoToast()
    {
        var tracker = new SessionToastTracker();

        tracker.OnActivation(Activated(K2), isGameSession: false);

        Assert.Null(tracker.OnDeactivation(Deactivated(K2, TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void QuickOptimizeDuringTheGame_LeavesTheGameSessionsToastAsItWas()
    {
        var tracker = new SessionToastTracker();
        var gameRevert = Deactivated(K1, TimeSpan.FromMinutes(40), ModuleRevertKind.RevertFailed);

        tracker.OnActivation(Activated(K1), isGameSession: true);
        tracker.OnLatencySampled(500);
        tracker.OnActivation(Activated(K2), isGameSession: false);
        Assert.Null(tracker.OnDeactivation(Deactivated(K2, TimeSpan.FromMinutes(1))));
        var toast = tracker.OnDeactivation(gameRevert);

        Assert.NotNull(toast);
        Assert.Same(gameRevert, toast.Deactivation);
        Assert.Equal(500, toast.PeakDpc);
        Assert.True(SessionSummary.For(toast.Deactivation)!.KeepOpen);
    }

    [Fact]
    public void DpcSamplesOutsideAGameSession_AreNotCounted()
    {
        var tracker = new SessionToastTracker();

        tracker.OnLatencySampled(999);
        tracker.OnActivation(Activated(K1), isGameSession: true);
        Assert.NotNull(tracker.OnDeactivation(Deactivated(K1, TimeSpan.FromMinutes(5))));
        tracker.OnLatencySampled(888);
        tracker.OnActivation(Activated(K2), isGameSession: true);
        var toast = tracker.OnDeactivation(Deactivated(K2, TimeSpan.FromMinutes(5)));

        Assert.NotNull(toast);
        Assert.Equal(0, toast.PeakDpc);
        Assert.Equal(0, toast.AvgDpc);
    }

    [Fact]
    public void ADeactivationWithoutAGameName_NamesAnUnknownGame()
    {
        var tracker = new SessionToastTracker();

        tracker.OnActivation(Activated(K1, game: null), isGameSession: true);

        Assert.Equal("Unknown Game", tracker.OnDeactivation(Deactivated(K1, TimeSpan.FromMinutes(5), game: null))!.GameName);
    }
}
