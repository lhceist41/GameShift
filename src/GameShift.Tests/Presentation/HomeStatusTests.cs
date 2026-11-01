using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Xunit;
using static GameShift.Tests.Presentation.OutcomeFixtures;

namespace GameShift.Tests.Presentation;

/// <summary>
/// The Home headline is active only while changes are applied and none failed, and its counts come
/// from this session's own results.
/// </summary>
public class HomeStatusTests
{
    // A game session requested just before the fixtures' activation started.
    private static readonly DateTime Requested = Start.AddSeconds(-1);

    private static HomeStatus Home(bool optimizing, bool paused, SessionOutcome? lastOutcome, string? game = "Test Game") =>
        HomeStatus.For(optimizing ? Requested : null, paused, game, lastOutcome, UtcPlusOne);

    [Fact]
    public void Idle_WaitsForAGame()
    {
        Assert.Equal(new HomeStatus(HomeStatusKind.Idle, HomeStatusTone.Neutral, "Waiting for a game", null),
            Home(false, false, null));
    }

    [Fact]
    public void Paused_WithoutASession_SaysPaused()
    {
        Assert.Equal(new HomeStatus(HomeStatusKind.Paused, HomeStatusTone.Neutral, "Paused", null),
            Home(false, true, null));
    }

    [Fact]
    public void Optimizing_AllApplied_IsActiveWithCounts()
    {
        var status = Home(true, false, Activation(
            Module("A", ModuleOutcomeKind.Applied),
            Module("B", ModuleOutcomeKind.Applied),
            Module("C", ModuleOutcomeKind.SkippedDisabled)));

        Assert.Equal(new HomeStatus(HomeStatusKind.Optimizing, HomeStatusTone.Active, "Optimizing Test Game",
            "Since 21:14 · 2 applied"), status);
    }

    [Fact]
    public void Optimizing_WithAFailure_IsNeverActiveAndCountsIt()
    {
        var status = Home(true, false, Activation(
            Module("A", ModuleOutcomeKind.Applied),
            Module("B", ModuleOutcomeKind.AlreadyApplied),
            Module("C", ModuleOutcomeKind.Failed, "did not apply"),
            Module("D", ModuleOutcomeKind.Applied)));

        Assert.Equal(HomeStatusTone.Warning, status.Tone);
        Assert.Equal("Since 21:14 · 3 applied · 1 failed", status.CountsLine);
        Assert.DoesNotContain("Optimized", status.Headline);
    }

    [Fact]
    public void QuickOptimizeThenAGame_CountsTheEarlierChangesAsApplied()
    {
        var status = Home(true, false, Activation(
            Module("A", ModuleOutcomeKind.AlreadyApplied),
            Module("B", ModuleOutcomeKind.AlreadyApplied)));

        Assert.Equal("Since 21:14 · 2 applied", status.CountsLine);
        Assert.Equal(HomeStatusTone.Active, status.Tone);
    }

    [Fact]
    public void Optimizing_WithoutThisSessionsResults_ClaimsNothing()
    {
        var ended = Deactivation([Revert("A", ModuleRevertKind.Reverted)], false, Module("A", ModuleOutcomeKind.Applied));
        var earlier = Activation(Module("A", ModuleOutcomeKind.Applied));

        foreach (var (lastOutcome, requested) in new (SessionOutcome?, DateTime)[] { (null, Requested), (ended, Requested), (earlier, Start.AddMinutes(5)) })
        {
            var status = HomeStatus.For(requested, false, "Test Game", lastOutcome, UtcPlusOne);
            Assert.Equal(HomeStatusKind.Optimizing, status.Kind);
            Assert.Equal(HomeStatusTone.Neutral, status.Tone);
            Assert.Equal(HomeStatus.WaitingForResults, status.CountsLine);
        }
    }

    [Fact]
    public void Optimizing_NothingApplied_IsNeutralWithTheTraysWords()
    {
        var status = Home(true, false, Activation(
            Module("A", ModuleOutcomeKind.SkippedDisabled),
            Module("B", ModuleOutcomeKind.NotApplicable)));

        Assert.Equal(new HomeStatus(HomeStatusKind.Optimizing, HomeStatusTone.Neutral, "No changes active",
            "Since 21:14 · 0 applied"), status);
    }

    [Fact]
    public void Optimizing_EveryChangeFailed_IsAWarning()
    {
        var status = Home(true, false, Activation(Module("A", ModuleOutcomeKind.Failed, "did not apply")));

        Assert.Equal(HomeStatusTone.Warning, status.Tone);
        Assert.Equal("Optimizing Test Game", status.Headline);
        Assert.Equal("Since 21:14 · 0 applied · 1 failed", status.CountsLine);
    }

    [Fact]
    public void Paused_WhileTheGameSessionIsEnding_SaysPaused()
    {
        // Pausing sets paused first and then reverts the session, which still runs meanwhile.
        var status = Home(true, true, Activation(Module("A", ModuleOutcomeKind.Applied)));

        Assert.Equal(new HomeStatus(HomeStatusKind.Paused, HomeStatusTone.Neutral, "Paused", null), status);
    }

    [Theory]
    [InlineData("Detected Game", "Optimizing Detected Game")]
    [InlineData(null, "Optimizing Test Game")]
    public void Optimizing_NamesTheGame(string? game, string headline)
    {
        Assert.Equal(headline, Home(true, false, Activation(Module("A", ModuleOutcomeKind.Applied)), game).Headline);
    }

    [Fact]
    public void Optimizing_WithNoNameAnywhere_SaysOptimizing()
    {
        Assert.Equal("Optimizing", Home(true, false, null, game: null).Headline);
    }

    [Fact]
    public void QuickOptimize_WithoutAGameSession_IsShownAsRunning()
    {
        var status = Home(false, false, Activation(Module("A", ModuleOutcomeKind.Applied)));

        Assert.Equal(new HomeStatus(HomeStatusKind.Optimizing, HomeStatusTone.Active, "Quick Optimize is on",
            "Since 21:14 · 1 applied"), status);
    }

    [Fact]
    public void QuickOptimize_WithNothingApplied_IsIdleLikeTheTray()
    {
        var status = Home(false, false, Activation(
            Module("A", ModuleOutcomeKind.Failed, "did not apply"),
            Module("B", ModuleOutcomeKind.SkippedDisabled)));

        Assert.Equal(new HomeStatus(HomeStatusKind.Idle, HomeStatusTone.Neutral, "Waiting for a game", null), status);
    }

    [Fact]
    public void QuickOptimize_WhilePaused_SaysPausedAndKeepsTheCounts()
    {
        var status = Home(false, true, Activation(Module("A", ModuleOutcomeKind.Applied)));

        Assert.Equal(new HomeStatus(HomeStatusKind.Paused, HomeStatusTone.Neutral, "Paused", "Since 21:14 · 1 applied"),
            status);
    }
}
