using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Core.Verification;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Modules GameShift no longer applies stay registered as revert-only stubs, so a session journal
/// written by an earlier version still reverts after a crash. A stub must never apply again, even for
/// a profile that turns every toggle on.
/// </summary>
public class RevertOnlyStubTests
{
    private static readonly string[] StubIds =
    {
        MpoToggle.OptimizationId,
        VisualEffectReducer.OptimizationId,
        ScheduledTaskSuppressor.OptimizationId,
        NetworkOptimizer.OptimizationId,
        CpuParkingManager.OptimizationId,
        GpuDriverOptimizer.OptimizationId,
        CompetitiveMode.OptimizationId,
        SessionSystemTweaksOptimizer.OptimizationId,
    };

    public static TheoryData<string> Stubs()
    {
        var data = new TheoryData<string>();
        foreach (var id in StubIds)
            data.Add(id);
        return data;
    }

    private static GameProfile FullProfile()
    {
        var profile = new GameProfile
        {
            Id = "full",
            GameName = "Full",
            ExecutableName = "game.exe",
            ProcessId = Environment.ProcessId,
            Intensity = OptimizationIntensity.Competitive,
        };
        foreach (var property in typeof(GameProfile).GetProperties())
        {
            if (property.PropertyType == typeof(bool) && property.CanWrite)
                property.SetValue(profile, true);
        }
        return profile;
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public void Stub_NeverAppliesForAFullProfile(string id)
    {
        var stub = WatchdogRevertEngine.DefaultFactories[id]();
        var context = new SystemContext { Profile = FullProfile(), Snapshot = new SystemStateSnapshot() };

        Assert.False(stub.CanApply(context));
        Assert.False(Assert.IsAssignableFrom<IOptimization>(stub).IsAvailable);
    }

    [Fact]
    public void EveryWatchdogFactory_IsALiveSessionModuleOrARevertOnlyStub()
    {
        var live = RevertVerificationRunner.CreateEngineModules()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unaccounted = WatchdogRevertEngine.DefaultFactories.Keys
            .Where(id => !live.Contains(id) && !StubIds.Contains(id))
            .ToList();

        Assert.True(unaccounted.Count == 0,
            "Watchdog factories that are neither a session module nor a listed stub: "
            + string.Join(", ", unaccounted));
        Assert.DoesNotContain(StubIds, live.Contains);
    }
}
