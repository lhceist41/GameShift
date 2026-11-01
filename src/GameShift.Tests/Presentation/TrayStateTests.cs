using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

public class TrayStateTests
{
    private static readonly DateTimeOffset SpikeAt = new(2026, 11, 1, 21, 14, 30, TimeSpan.Zero);

    private static TrayStateInput Input(
        bool optimizing = false, bool paused = false, int applied = 0, int failed = 0,
        DateTimeOffset? spikeAt = null, TimeSpan spikeAge = default)
        => new(optimizing, paused, applied, failed, spikeAt, spikeAge);

    [Fact]
    public void Idle_WhenNoSession()
    {
        var state = TrayState.Resolve(Input());

        Assert.Equal(TrayIconShape.Idle, state.Shape);
        Assert.Equal("GameShift - Idle", state.ToolTip);
        Assert.Equal("GameShift: Idle", state.StatusText);
    }

    [Fact]
    public void Active_WhenChangesAppliedAndNoneFailed()
    {
        var state = TrayState.Resolve(Input(optimizing: true, applied: 5));

        Assert.Equal(TrayIconShape.Active, state.Shape);
        Assert.Equal("GameShift - 5 changes active", state.ToolTip);
        Assert.Equal("GameShift: 5 changes active", state.StatusText);
    }

    [Fact]
    public void Active_WhenQuickOptimizeChangesAreInEffectWithoutASession()
    {
        var state = TrayState.Resolve(Input(applied: 1));

        Assert.Equal(TrayIconShape.Active, state.Shape);
        Assert.Equal("GameShift - 1 change active", state.ToolTip);
        Assert.Equal("GameShift: 1 change active", state.StatusText);
    }

    [Fact]
    public void Warning_WhenAChangeFailed_EvenWithOthersApplied()
    {
        var state = TrayState.Resolve(Input(optimizing: true, applied: 4, failed: 1));

        Assert.Equal(TrayIconShape.Warning, state.Shape);
        Assert.Equal("GameShift - 4 changes active, 1 failed", state.ToolTip);
        Assert.Equal("GameShift: 1 change failed", state.StatusText);
    }

    [Fact]
    public void Warning_WhenQuickOptimizeHadAFailure()
    {
        var state = TrayState.Resolve(Input(applied: 3, failed: 2));

        Assert.Equal(TrayIconShape.Warning, state.Shape);
        Assert.Equal("GameShift - 3 changes active, 2 failed", state.ToolTip);
        Assert.Equal("GameShift: 2 changes failed", state.StatusText);
    }

    [Fact]
    public void Warning_WhenEveryChangeFailed()
    {
        var state = TrayState.Resolve(Input(optimizing: true, failed: 2));

        Assert.Equal(TrayIconShape.Warning, state.Shape);
        Assert.Equal("GameShift - 2 changes failed", state.ToolTip);
        Assert.Equal("GameShift: 2 changes failed", state.StatusText);
    }

    [Fact]
    public void Idle_WithoutASession_WhenNothingIsInEffect_EvenAfterAFailure()
    {
        // Nothing would ever clear a warning that has no session or active change behind it.
        var state = TrayState.Resolve(Input(failed: 1));

        Assert.Equal(TrayIconShape.Idle, state.Shape);
        Assert.Equal("GameShift - Idle", state.ToolTip);
    }

    [Fact]
    public void NotActive_WhenASessionHasNothingApplied()
    {
        var state = TrayState.Resolve(Input(optimizing: true));

        Assert.Equal(TrayIconShape.Idle, state.Shape);
        Assert.Equal("GameShift - No changes active", state.ToolTip);
        Assert.Equal("GameShift: No changes active", state.StatusText);
    }

    [Fact]
    public void Paused_WhenPausedWithoutASession()
    {
        var state = TrayState.Resolve(Input(paused: true));

        Assert.Equal(TrayIconShape.Paused, state.Shape);
        Assert.Equal("GameShift - Paused", state.ToolTip);
        Assert.Equal("GameShift: Paused", state.StatusText);
    }

    [Fact]
    public void Paused_WhilePausedWithAGameRunning_WinsOverEverySessionState()
    {
        var state = TrayState.Resolve(Input(
            optimizing: true, paused: true, applied: 3, failed: 1, spikeAt: SpikeAt, spikeAge: TimeSpan.FromSeconds(10)));

        Assert.Equal(TrayIconShape.Paused, state.Shape);
        Assert.Equal("GameShift: Paused", state.StatusText);
    }

    [Fact]
    public void DpcSpike_LeavesTheIconUnchanged_AndAddsALine()
    {
        var state = TrayState.Resolve(Input(
            optimizing: true, applied: 5, spikeAt: SpikeAt, spikeAge: TimeSpan.FromSeconds(30)));

        Assert.Equal(TrayIconShape.Active, state.Shape);
        Assert.Equal("GameShift - 5 changes active\nHigh DPC load at 21:14", state.ToolTip);
        Assert.Equal("GameShift: 5 changes active\nHigh DPC load at 21:14", state.StatusText);
    }

    [Fact]
    public void DpcSpike_KeepsAWarningAWarning()
    {
        var state = TrayState.Resolve(Input(
            optimizing: true, applied: 1, failed: 1, spikeAt: SpikeAt, spikeAge: TimeSpan.Zero));

        Assert.Equal(TrayIconShape.Warning, state.Shape);
        Assert.EndsWith("\nHigh DPC load at 21:14", state.StatusText);
    }

    [Fact]
    public void DpcSpike_ClearsAfterFiveMinutes()
    {
        var justBefore = TrayState.Resolve(Input(
            optimizing: true, applied: 5, spikeAt: SpikeAt, spikeAge: TrayState.DpcNoticeDuration - TimeSpan.FromSeconds(1)));
        var atExpiry = TrayState.Resolve(Input(
            optimizing: true, applied: 5, spikeAt: SpikeAt, spikeAge: TrayState.DpcNoticeDuration));

        Assert.Contains("High DPC load", justBefore.ToolTip);
        Assert.Equal("GameShift - 5 changes active", atExpiry.ToolTip);
        Assert.Equal("GameShift: 5 changes active", atExpiry.StatusText);
    }

    [Fact]
    public void DpcSpike_IsNotShownOutsideASession()
    {
        var state = TrayState.Resolve(Input(spikeAt: SpikeAt, spikeAge: TimeSpan.FromSeconds(5)));

        Assert.Equal("GameShift - Idle", state.ToolTip);
    }

    [Fact]
    public void Error_IsNeverResolvedYet()
    {
        for (var bits = 0; bits < 32; bits++)
        {
            var state = TrayState.Resolve(Input(
                optimizing: (bits & 1) != 0,
                paused: (bits & 2) != 0,
                applied: (bits >> 2) & 1,
                failed: (bits >> 3) & 1,
                spikeAt: (bits & 16) != 0 ? SpikeAt : null));
            Assert.NotEqual(TrayIconShape.Error, state.Shape);
        }
    }
}

public class TrayStatusTrackerTests
{
    private readonly ManualTimeProvider _time = new();
    private bool _optimizing;
    private bool _paused;
    private int _applied;
    private SessionOutcome? _outcome;

    private TrayStatusTracker CreateTracker() => new(() => _optimizing, () => _paused, () => _applied, () => _outcome, _time);

    private static SessionOutcome Activation(params ModuleOutcomeKind[] kinds) => new(
        new DateTime(2026, 11, 1, 21, 0, 0, DateTimeKind.Utc),
        "Quick Optimize",
        kinds.Select((kind, i) => new ModuleOutcome($"Module {i}", kind, null, DateTime.UtcNow)).ToArray(),
        Array.Empty<ModuleRevert>(),
        EndedUtc: null,
        RevertedByRecovery: false);

    private static SessionOutcome Deactivation(SessionOutcome activation) =>
        activation with { EndedUtc = new DateTime(2026, 11, 1, 21, 30, 0, DateTimeKind.Utc) };

    [Fact]
    public void FailureDuringASession_ShowsWarning()
    {
        var tracker = CreateTracker();
        _optimizing = true;
        _applied = 3;
        _outcome = Activation(ModuleOutcomeKind.Applied, ModuleOutcomeKind.Failed, ModuleOutcomeKind.Applied, ModuleOutcomeKind.Applied);

        var state = tracker.Resolve();

        Assert.Equal(TrayIconShape.Warning, state.Shape);
        Assert.Equal("GameShift: 1 change failed", state.StatusText);
    }

    [Fact]
    public void QuickOptimizeFailure_ShowsWarningWithoutASession()
    {
        var tracker = CreateTracker();
        _applied = 2;
        _outcome = Activation(ModuleOutcomeKind.Applied, ModuleOutcomeKind.Failed, ModuleOutcomeKind.Applied);

        Assert.Equal(TrayIconShape.Warning, tracker.Resolve().Shape);
    }

    [Fact]
    public void QuickOptimizeFailure_IsNotShownForAGameSessionThatStartsLater()
    {
        var tracker = CreateTracker();
        _applied = 2;
        _outcome = Activation(ModuleOutcomeKind.Applied, ModuleOutcomeKind.Failed, ModuleOutcomeKind.Applied);
        Assert.Equal(TrayIconShape.Warning, tracker.Resolve().Shape);

        // The game was detected while Quick Optimize was still applying; its own run applies the
        // module that failed before and finds the rest already applied.
        _optimizing = true;
        _applied = 3;
        _outcome = Activation(ModuleOutcomeKind.AlreadyApplied, ModuleOutcomeKind.Applied, ModuleOutcomeKind.AlreadyApplied);
        var state = tracker.Resolve();

        Assert.Equal(TrayIconShape.Active, state.Shape);
        Assert.Equal("GameShift - 3 changes active", state.ToolTip);
    }

    [Fact]
    public void FailuresAreForgotten_OnceTheirRunIsReverted()
    {
        var tracker = CreateTracker();
        _applied = 2;
        var quickOptimize = Activation(ModuleOutcomeKind.Applied, ModuleOutcomeKind.Failed, ModuleOutcomeKind.Applied);
        _outcome = quickOptimize;
        Assert.Equal(TrayIconShape.Warning, tracker.Resolve().Shape);

        // The deactivation outcome still lists the activation's modules, failure included.
        _applied = 0;
        _outcome = Deactivation(quickOptimize);
        _optimizing = true;
        Assert.Equal("GameShift - No changes active", tracker.Resolve().ToolTip);

        _optimizing = false;
        Assert.Equal("GameShift - Idle", tracker.Resolve().ToolTip);
    }

    [Fact]
    public void GameSessionWithNothingApplied_ShowsNoChangesActive()
    {
        var tracker = CreateTracker();
        _optimizing = true;
        _outcome = Activation(ModuleOutcomeKind.SkippedDisabled, ModuleOutcomeKind.NotApplicable, ModuleOutcomeKind.Unavailable);

        var state = tracker.Resolve();

        Assert.Equal(TrayIconShape.Idle, state.Shape);
        Assert.Equal("GameShift - No changes active", state.ToolTip);
        Assert.Equal("GameShift: No changes active", state.StatusText);
    }

    [Fact]
    public void DpcSpikeIsForgotten_OnceTheSessionIsSeenEnding()
    {
        var tracker = CreateTracker();
        _optimizing = true;
        _applied = 2;
        tracker.RecordDpcSpike();

        _optimizing = false;
        _applied = 0;
        Assert.Equal(TrayIconShape.Idle, tracker.Resolve().Shape);

        _optimizing = true;
        _applied = 2;
        var next = tracker.Resolve();
        Assert.Equal(TrayIconShape.Active, next.Shape);
        Assert.DoesNotContain("DPC", next.ToolTip);
    }

    [Fact]
    public void DpcSpike_LeavesTheIconUnchanged()
    {
        var tracker = CreateTracker();
        _optimizing = true;
        _applied = 5;

        tracker.RecordDpcSpike();
        var state = tracker.Resolve();

        Assert.Equal(TrayIconShape.Active, state.Shape);
        Assert.EndsWith("\nHigh DPC load at 21:14", state.ToolTip);
    }

    [Fact]
    public void DpcSpike_ClearsFiveMinutesAfterTheLastOne()
    {
        var tracker = CreateTracker();
        _optimizing = true;
        _applied = 5;
        tracker.RecordDpcSpike();

        _time.Advance(TimeSpan.FromMinutes(4));
        tracker.RecordDpcSpike();
        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.EndsWith("High DPC load at 21:18", tracker.Resolve().ToolTip);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("GameShift - 5 changes active", tracker.Resolve().ToolTip);
    }

    [Fact]
    public void DpcSpikeOutsideASession_DoesNotSurfaceInTheNextOne()
    {
        var tracker = CreateTracker();
        tracker.RecordDpcSpike();

        _optimizing = true;
        _applied = 1;

        Assert.DoesNotContain("DPC", tracker.Resolve().ToolTip);
    }

    [Fact]
    public void PausedWhileAGameRuns_ShowsPaused()
    {
        var tracker = CreateTracker();
        _optimizing = true;
        _applied = 4;
        _paused = true;

        Assert.Equal(TrayIconShape.Paused, tracker.Resolve().Shape);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 21, 14, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _now.Ticks;

        public void Advance(TimeSpan by) => _now += by;
    }
}

public class TrayIconSetsTests
{
    [Theory]
    [InlineData(false, TrayIconTheme.DarkTaskbar)]
    [InlineData(true, TrayIconTheme.LightTaskbar)]
    public void FollowsTheLightThemeSetting_OutsideHighContrast(bool systemUsesLightTheme, TrayIconTheme expected)
    {
        // The background is ignored outside high contrast: a white window with a dark taskbar is the Windows default.
        Assert.Equal(expected, TrayIconSets.Choose(systemUsesLightTheme, highContrast: false, 255, 255, 255));
        Assert.Equal(expected, TrayIconSets.Choose(systemUsesLightTheme, highContrast: false, 0, 0, 0));
    }

    [Theory]
    [InlineData(0, 0, 0, TrayIconTheme.DarkTaskbar)]
    [InlineData(255, 255, 255, TrayIconTheme.LightTaskbar)]
    [InlineData(255, 250, 239, TrayIconTheme.LightTaskbar)]
    [InlineData(45, 50, 54, TrayIconTheme.DarkTaskbar)]
    public void InHighContrast_PicksTheSetThatContrastsWithTheBackground(byte r, byte g, byte b, TrayIconTheme expected)
    {
        Assert.Equal(expected, TrayIconSets.Choose(systemUsesLightTheme: true, highContrast: true, r, g, b));
        Assert.Equal(expected, TrayIconSets.Choose(systemUsesLightTheme: false, highContrast: true, r, g, b));
    }

    [Fact]
    public void EveryShapeAndTheme_HasItsOwnResource()
    {
        var names = new HashSet<string>();
        foreach (var shape in Enum.GetValues<TrayIconShape>())
        {
            foreach (var theme in Enum.GetValues<TrayIconTheme>())
                Assert.True(names.Add(TrayIconSets.ResourceName(shape, theme)));
        }

        Assert.Equal("tray-paused-light", TrayIconSets.ResourceName(TrayIconShape.Paused, TrayIconTheme.LightTaskbar));
        Assert.Equal("tray-active-dark", TrayIconSets.ResourceName(TrayIconShape.Active, TrayIconTheme.DarkTaskbar));
    }
}
