using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using GameShift.Core.Profiles;
using Xunit;
using static GameShift.Tests.Presentation.OutcomeFixtures;

namespace GameShift.Tests.Presentation;

/// <summary>
/// The During games rows: plain text and the profile switch per module id, and badges read in an
/// order that leaves no stale state when a session ends mid-read.
/// </summary>
public class SessionModulesTests
{
    private static readonly string[] Switched =
    [
        ServiceSuppressor.OptimizationId,
        PowerPlanSwitcher.OptimizationId,
        TimerResolutionManager.OptimizationId,
        ProcessPriorityBooster.OptimizationId,
        HybridCpuDetector.OptimizationId,
        EfficiencyModeController.OptimizationId,
    ];

    public static TheoryData<string> SwitchedModules => new(Switched);

    [Theory]
    [MemberData(nameof(SwitchedModules))]
    public void TrySet_WritesTheSwitchTheEngineReads(string moduleName)
    {
        var profile = new GameProfile();

        Assert.True(ModuleSwitch.HasSwitch(moduleName));
        Assert.True(ModuleSwitch.TrySet(profile, moduleName, false));
        Assert.False(profile.IsOptimizationEnabled(moduleName));
        Assert.True(ModuleSwitch.TrySet(profile, moduleName, true));
        Assert.True(profile.IsOptimizationEnabled(moduleName));
    }

    [Fact]
    public void TrySet_OnlyChangesTheNamedModule()
    {
        var profile = new GameProfile();
        var before = Switched.ToDictionary(m => m, profile.IsOptimizationEnabled);

        ModuleSwitch.TrySet(profile, TimerResolutionManager.OptimizationId, !before[TimerResolutionManager.OptimizationId]);

        foreach (var (module, enabled) in before.Where(p => p.Key != TimerResolutionManager.OptimizationId))
            Assert.Equal(enabled, profile.IsOptimizationEnabled(module));
    }

    [Theory]
    [InlineData(CpuSchedulingOptimizer.OptimizationId)]
    [InlineData("Some Future Module")]
    public void TrySet_AModuleWithoutASwitchChangesNothing(string moduleName)
    {
        var profile = new GameProfile();
        var json = System.Text.Json.JsonSerializer.Serialize(profile);

        Assert.False(ModuleSwitch.HasSwitch(moduleName));
        Assert.False(ModuleSwitch.TrySet(profile, moduleName, false));
        Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize(profile));
    }

    [Fact]
    public void Text_KnownModulesHaveAPlainTitleAndUnknownOnesKeepTheirOwn()
    {
        var known = SessionModuleText.For(TimerResolutionManager.OptimizationId, "ignored");
        var unknown = SessionModuleText.For("Some Future Module", "What it does.");

        Assert.NotEqual(TimerResolutionManager.OptimizationId, known.Title);
        Assert.Equal(new SessionModuleText("Some Future Module", "What it does."), unknown);
    }

    [Fact]
    public void Read_MatchesBadgesByModuleNotByPosition()
    {
        var modules = new[]
        {
            new SessionModuleInput("Timer", true, true),
            new SessionModuleInput("Power", true, false),
            new SessionModuleInput("Services", true, true),
        };
        var outcome = Activation(
            Module("Services", ModuleOutcomeKind.Failed, "access denied"),
            Module("Timer", ModuleOutcomeKind.Applied));

        var snapshot = SessionModuleStates.Read(modules, () => null, () => outcome, isPaused: false);

        Assert.Equal(ModuleStatusKind.Applied, snapshot.ByModule["Timer"].Kind);
        Assert.Equal(ModuleStatusKind.Off, snapshot.ByModule["Power"].Kind);
        Assert.Equal(ModuleStatusKind.Failed, snapshot.ByModule["Services"].Kind);
        Assert.Equal("Access denied.", snapshot.ByModule["Services"].Reason);
    }

    [Fact]
    public void Read_KeepsPollingWhileAGameSessionIsRequested()
    {
        var modules = new[] { new SessionModuleInput("Timer", true, true) };

        Assert.True(SessionModuleStates.Read(modules, () => Start, () => null, false).SessionRunning);
        Assert.False(SessionModuleStates.Read(modules, () => null, () => null, false).SessionRunning);
    }

    [Fact]
    public void Read_ASessionEndingBetweenTheReadsStillAsksForAnotherRead()
    {
        // The session's activation is read, then its deactivation finishes and the marker clears.
        // Reading the marker first sees the session still running, so the page reads again.
        DateTime? marker = Start;
        var activation = Activation(Module("Timer", ModuleOutcomeKind.Applied));
        SessionOutcome? ReadOutcome()
        {
            var seen = activation;
            marker = null;
            return seen;
        }

        var snapshot = SessionModuleStates.Read(
            [new SessionModuleInput("Timer", true, true)], () => marker, ReadOutcome, isPaused: false);

        Assert.True(snapshot.SessionRunning);
    }
}
