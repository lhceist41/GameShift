using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Xunit;
using static GameShift.Tests.Presentation.OutcomeFixtures;

namespace GameShift.Tests.Presentation;

/// <summary>
/// The Home headline is active only while a session runs with nothing failed, and its counts come
/// from this session's own results.
/// </summary>
public class HomeStatusTests
{
    private static HomeStatus Home(bool optimizing, bool paused, SessionOutcome? current, string? game = "Test Game") =>
        HomeStatus.For(optimizing, paused, game, current, UtcPlusOne);

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
        var stale = Deactivation([Revert("A", ModuleRevertKind.Reverted)], false, Module("A", ModuleOutcomeKind.Applied));

        foreach (var current in new[] { null, stale })
        {
            var status = Home(true, false, current);
            Assert.Equal(HomeStatusKind.Optimizing, status.Kind);
            Assert.Equal(HomeStatusTone.Neutral, status.Tone);
            Assert.Equal(HomeStatus.WaitingForResults, status.CountsLine);
        }
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
    public void QuickOptimize_WhilePaused_SaysPausedAndKeepsTheCounts()
    {
        var status = Home(false, true, Activation(Module("A", ModuleOutcomeKind.Applied)));

        Assert.Equal(new HomeStatus(HomeStatusKind.Paused, HomeStatusTone.Neutral, "Paused", "Since 21:14 · 1 applied"),
            status);
    }
}
