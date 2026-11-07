using System.Globalization;
using GameShift.Core.Config;
using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Xunit;
using static GameShift.Tests.Presentation.OutcomeFixtures;

namespace GameShift.Tests.Presentation;

/// <summary>
/// Home's status strip and "This session" list come from one read of the session state, failed
/// modules first; recent sessions show only what the history recorded.
/// </summary>
public class HomeViewTests
{
    // A game session requested just before the fixtures' activation started.
    private static readonly DateTime Requested = Start.AddSeconds(-1);

    private static SessionModuleInput Input(string name, bool available = true, bool enabled = true) =>
        new(name, available, enabled);

    private static HomeSnapshot Read(
        IReadOnlyList<SessionModuleInput> modules, DateTime? requested, SessionOutcome? outcome, bool paused = false) =>
        HomeSnapshot.Read(modules, () => requested, () => outcome, paused, "Test Game", 0, UtcPlusOne);

    [Fact]
    public void Read_ReadsTheSessionMarkerBeforeTheOutcome()
    {
        var reads = new List<string>();

        HomeSnapshot.Read(
            [Input("A")],
            () => { reads.Add("marker"); return Requested; },
            () => { reads.Add("outcome"); return Activation(Module("A", ModuleOutcomeKind.Applied)); },
            false, "Test Game", 0, UtcPlusOne);

        Assert.Equal(["marker", "outcome"], reads);
    }

    [Fact]
    public void Read_StatusAndModulesComeFromTheSameSession()
    {
        var snapshot = Read([Input("A"), Input("B")], Requested,
            Activation(Module("A", ModuleOutcomeKind.Applied), Module("B", ModuleOutcomeKind.Failed, "access denied")));

        Assert.Equal("Since 21:14 · 1 applied · 1 failed", snapshot.Status.CountsLine);
        Assert.Equal(HomeStatusTone.Warning, snapshot.Status.Tone);
        Assert.Equal(new ModuleStatus(ModuleStatusKind.Failed, "Access denied."), snapshot.Modules[0].Status);
    }

    [Fact]
    public void Read_ListsFailedFirst_ThenApplied_ThenReady_ThenOff_ThenNotAvailable()
    {
        var snapshot = Read(
            [
                Input("Off", enabled: false),
                Input("Applied1"),
                Input("Missing", available: false),
                Input("Ready"),
                Input("Failed"),
                Input("Applied2"),
            ],
            Requested,
            Activation(
                Module("Applied1", ModuleOutcomeKind.Applied),
                Module("Failed", ModuleOutcomeKind.Failed, "did not apply"),
                Module("Applied2", ModuleOutcomeKind.AlreadyApplied)));

        Assert.Equal(["Failed", "Applied1", "Applied2", "Ready", "Off", "Missing"],
            snapshot.Modules.Select(m => m.ModuleName));
    }

    [Fact]
    public void Read_WhilePausedWithoutASession_KeepsPausedModulesAboveOff()
    {
        var snapshot = Read([Input("Off", enabled: false), Input("On")], null, null, paused: true);

        Assert.Equal(["On", "Off"], snapshot.Modules.Select(m => m.ModuleName));
        Assert.Equal(ModuleStatusKind.Paused, snapshot.Modules[0].Status.Kind);
    }

    [Fact]
    public void Read_WithAFailedModule_NeverSaysOptimized()
    {
        var snapshot = Read([Input("A"), Input("B"), Input("C", enabled: false)], Requested,
            Activation(Module("A", ModuleOutcomeKind.Applied), Module("B", ModuleOutcomeKind.Failed, "did not apply")));

        var shown = new List<string?> { snapshot.Status.Headline, snapshot.Status.CountsLine };
        shown.AddRange(snapshot.Modules.SelectMany(m => new[] { m.Status.Word, m.Status.Reason }));

        Assert.All(shown, text => Assert.DoesNotContain("Optimized", text ?? "", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(HomeStatusTone.Active, snapshot.Status.Tone);
    }

    [Fact]
    public void RecentSession_ShowsOnlyWhatTheHistoryRecorded()
    {
        var line = RecentSessionLine.For(new GameSession
        {
            GameName = "Test Game",
            StartTime = new DateTime(2026, 11, 1, 21, 14, 0),
            Duration = TimeSpan.FromMinutes(125),
            OptimizationsApplied = 5,
        });

        Assert.Equal(new RecentSessionLine("Test Game", "1 Nov 21:14", "2 h 5 min", "5 changes active when the game closed"), line);
        Assert.DoesNotContain("failed", line.Counts);
    }

    [Fact]
    public void RecentSession_WithoutNameOrLength_SaysSoInsteadOfInventingThem()
    {
        var line = RecentSessionLine.For(new GameSession { StartTime = new DateTime(2026, 11, 1, 9, 5, 0) });

        Assert.Equal(new RecentSessionLine("Unknown game", "1 Nov 09:05", null, "0 changes active when the game closed"), line);
    }

    [Theory]
    [InlineData(59, "Under 1 min")]
    [InlineData(60, "1 min")]
    [InlineData(59 * 60 + 59, "59 min")]
    [InlineData(60 * 60, "1 h 0 min")]
    public void RecentSession_Length(int seconds, string expected)
    {
        Assert.Equal(expected, RecentSessionLine.LengthText(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void RecentSession_OneChange_IsSingular()
    {
        var line = RecentSessionLine.For(new GameSession { GameName = "G", OptimizationsApplied = 1 });

        Assert.Equal("1 change active when the game closed", line.Counts);
    }

    [Fact]
    public void RecentSession_UsesOneNumberCultureOnAGermanPc()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var line = RecentSessionLine.For(new GameSession
            {
                GameName = "G",
                StartTime = new DateTime(2026, 3, 1, 21, 14, 0),
                Duration = TimeSpan.FromMinutes(90),
            });

            Assert.Equal("1 Mar 21:14", line.When);
            Assert.Equal("1 h 30 min", line.Length);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
