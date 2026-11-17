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

    private static HomeStatus Home(
        bool optimizing, bool paused, SessionOutcome? lastOutcome, string? game = "Test Game", int gameActions = 0) =>
        HomeStatus.For(optimizing ? Requested : null, paused, game, lastOutcome, gameActions, UtcPlusOne);

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
            var status = HomeStatus.For(requested, false, "Test Game", lastOutcome, 0, UtcPlusOne);
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
    public void Paused_WhileTheGameSessionIsEnding_SaysPausedAndNamesWhatIsStillInEffect()
    {
        // Pausing sets paused first and then reverts the session, which still runs meanwhile.
        var status = Home(true, true, Activation(Module("A", ModuleOutcomeKind.Applied)), gameActions: 1);

        Assert.Equal(new HomeStatus(HomeStatusKind.Paused, HomeStatusTone.Neutral, "Paused, 2 changes active", null), status);
    }

    [Fact]
    public void GameActionsInEffect_WithNoModuleApplied_AreActive_NotNoChangesActive()
    {
        var status = Home(true, false, Activation(Module("A", ModuleOutcomeKind.SkippedDisabled)), gameActions: 1);

        Assert.Equal(new HomeStatus(HomeStatusKind.Optimizing, HomeStatusTone.Active, "Optimizing Test Game",
            "Since 21:14 · 1 applied"), status);
    }

    [Fact]
    public void GameActionsReadAsTheSessionEnds_DoNotMakeItQuickOptimize()
    {
        // The orchestrator clears its count and the request time one after the other; a reader can see either first.
        var status = Home(false, false, Activation(Module("A", ModuleOutcomeKind.SkippedDisabled)), gameActions: 1);

        Assert.Equal(new HomeStatus(HomeStatusKind.Idle, HomeStatusTone.Neutral, "Waiting for a game", null), status);
    }

    [Fact]
    public void GameActionsInEffect_AddToTheAppliedCount()
    {
        var status = Home(true, false, Activation(Module("A", ModuleOutcomeKind.Applied)), gameActions: 2);

        Assert.Equal("Since 21:14 · 3 applied", status.CountsLine);
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
    public void QuickOptimize_WhilePaused_SaysPausedNamesItsChangesAndKeepsTheCounts()
    {
        var status = Home(false, true, Activation(Module("A", ModuleOutcomeKind.Applied)));

        Assert.Equal(new HomeStatus(HomeStatusKind.Paused, HomeStatusTone.Neutral, "Paused, 1 change active",
            "Since 21:14 · 1 applied"), status);
    }

    private static HomeModuleLine Line(string name, ModuleStatusKind kind) => new(name, new ModuleStatus(kind, null));

    private static readonly HomeModuleLine[] IdleLines =
    [
        Line("A", ModuleStatusKind.Ready),
        Line("B", ModuleStatusKind.Ready),
        Line("C", ModuleStatusKind.Off),
        Line("D", ModuleStatusKind.NotAvailable),
    ];

    private static HomeStrip Strip(
        bool optimizing, bool paused, SessionOutcome? lastOutcome, IReadOnlyList<HomeModuleLine>? lines = null,
        TimeSpan? elapsed = null, string? game = "Test Game", int gameActions = 0) =>
        HomeStrip.For(optimizing ? Requested : null, paused, game, lastOutcome, gameActions, lines ?? [],
            Start + (elapsed ?? TimeSpan.FromMinutes(42)), UtcPlusOne);

    [Fact]
    public void Strip_Idle_WaitsForAGameAndCountsWhatIsReady()
    {
        Assert.Equal(new HomeStrip(HomeStatusKind.Idle, HomeStatusTone.Neutral, "IDLE", "Waiting for a game",
                "Starts automatically when a library game runs",
                [
                    new HomeCounter(2, "ready", ModuleStatusKind.Ready),
                    new HomeCounter(1, "off", ModuleStatusKind.Off),
                    new HomeCounter(1, "not available", ModuleStatusKind.NotAvailable),
                ]),
            Strip(false, false, null, IdleLines));
    }

    [Theory]
    [InlineData(0, 42, 17, "Since 21:14 · 00:42:17 elapsed")]
    [InlineData(1, 42, 17, "Since 21:14 · 01:42:17 elapsed")]
    [InlineData(26, 3, 4, "Since 21:14 · 1 d 02:03:04 elapsed")]
    public void Strip_Optimizing_SaysSinceWhenAndHowLong(int hours, int minutes, int seconds, string subLine)
    {
        var strip = Strip(true, false, Activation(Module("A", ModuleOutcomeKind.Applied)),
            elapsed: new TimeSpan(hours, minutes, seconds));

        Assert.Equal(subLine, strip.SubLine);
    }

    [Fact]
    public void Strip_ElapsedBeforeTheStart_ShowsZeroNotANegativeTime()
    {
        var strip = Strip(true, false, Activation(Module("A", ModuleOutcomeKind.Applied)), elapsed: TimeSpan.FromSeconds(-5));

        Assert.Equal("Since 21:14 · 00:00:00 elapsed", strip.SubLine);
    }

    [Fact]
    public void Strip_Optimizing_NamesTheGameAndCountsAppliedFailedAndOffOrNotAvailable()
    {
        var outcome = Activation(
            Module("A", ModuleOutcomeKind.Applied),
            Module("B", ModuleOutcomeKind.Failed, "did not apply"),
            Module("C", ModuleOutcomeKind.SkippedDisabled));
        HomeModuleLine[] lines =
        [
            Line("B", ModuleStatusKind.Failed),
            Line("A", ModuleStatusKind.Applied),
            Line("C", ModuleStatusKind.Off),
            Line("D", ModuleStatusKind.NotAvailable),
        ];

        var strip = Strip(true, false, outcome, lines, game: "Valorant", gameActions: 1);

        Assert.Equal(new HomeStrip(HomeStatusKind.Optimizing, HomeStatusTone.Warning, "OPTIMIZING", "Valorant",
                "Since 21:14 · 00:42:00 elapsed",
                [
                    new HomeCounter(2, "applied", ModuleStatusKind.Applied),
                    new HomeCounter(1, "failed", ModuleStatusKind.Failed),
                    new HomeCounter(2, "off or not available", ModuleStatusKind.Off),
                ]),
            strip);
        Assert.Equal([true, true, false], strip.Counters.Select(c => c.Highlight));
    }

    [Fact]
    public void Strip_NoFailure_DoesNotHighlightTheFailedCounter()
    {
        var strip = Strip(true, false, Activation(Module("A", ModuleOutcomeKind.Applied)));

        Assert.Equal(HomeStatusTone.Active, strip.Tone);
        Assert.False(strip.Counters.Single(c => c.StatusKind == ModuleStatusKind.Failed).Highlight);
    }

    [Fact]
    public void Strip_WithoutThisSessionsResults_ShowsNoCounters()
    {
        var strip = Strip(true, false, null, IdleLines);

        Assert.Equal(HomeStatus.WaitingForResults, strip.SubLine);
        Assert.Empty(strip.Counters);
        Assert.Equal(HomeStatusTone.Neutral, strip.Tone);
    }

    [Fact]
    public void Strip_WithNoGameNameAnywhere_SaysUnknownGame()
    {
        var outcome = new SessionOutcome(Start, null, [Module("A", ModuleOutcomeKind.Applied)], [], EndedUtc: null, RevertedByRecovery: false);

        Assert.Equal("Unknown game", Strip(true, false, outcome, game: null).Title);
    }

    [Fact]
    public void Strip_QuickOptimize_IsTitledQuickOptimize()
    {
        var strip = Strip(false, false, Activation(Module("A", ModuleOutcomeKind.Applied)));

        Assert.Equal((HomeStatusKind.Optimizing, "OPTIMIZING", "Quick Optimize"), (strip.Kind, strip.Chip, strip.Title));
    }

    [Fact]
    public void Strip_Paused_SaysSoAndNamesChangesStillActive()
    {
        HomeModuleLine[] lines = [Line("A", ModuleStatusKind.Applied), Line("B", ModuleStatusKind.Paused), Line("C", ModuleStatusKind.Off)];

        var strip = Strip(false, true, Activation(Module("A", ModuleOutcomeKind.Applied)), lines);

        Assert.Equal(new HomeStrip(HomeStatusKind.Paused, HomeStatusTone.Neutral, "PAUSED", "Paused",
                "1 change still active · no game is optimized until you resume",
                [
                    new HomeCounter(1, "paused", ModuleStatusKind.Paused),
                    new HomeCounter(1, "off", ModuleStatusKind.Off),
                    new HomeCounter(0, "not available", ModuleStatusKind.NotAvailable),
                ]),
            strip);
    }

    [Fact]
    public void Strip_PausedWithNothingActive_SaysNoGameIsOptimized()
    {
        Assert.Equal("No game is optimized until you resume", Strip(false, true, null).SubLine);
    }

    [Fact]
    public void Strip_SameContent_IsEqual_SoHomeDoesNotRedrawIt()
    {
        var outcome = Activation(Module("A", ModuleOutcomeKind.Applied));

        Assert.Equal(Strip(true, false, outcome, IdleLines), Strip(true, false, outcome, IdleLines.ToList()));
        Assert.NotEqual(Strip(true, false, outcome), Strip(true, false, outcome, elapsed: TimeSpan.FromMinutes(43)));
    }

    [Theory]
    [InlineData(false, "Ctrl+Shift+G", "Pause", "Pause game detection. A running game session ends. Hotkey: Ctrl+Shift+G")]
    [InlineData(true, "Ctrl+Shift+G", "Resume", "Resume game detection. A game that is still running is optimized again. Hotkey: Ctrl+Shift+G")]
    [InlineData(false, null, "Pause", "Pause game detection. A running game session ends.")]
    public void PauseButton_FollowsThePauseStateAndNamesOnlyARegisteredHotkey(bool paused, string? hotkey, string label, string toolTip)
    {
        Assert.Equal(new HomePauseButton(label, toolTip), HomePauseButton.For(paused, hotkey));
    }
}
