using GameShift.Core.SystemTweaks;
using Xunit;

namespace GameShift.Tests.SystemTweaks;

/// <summary>
/// Tweaks that can no longer be applied stay registered for revert, and ApplyTweak refuses them.
/// The refusal is driven by class name, the same key the ledger uses, so it is exercised with
/// stand-ins of the same names whose every method throws: a broken guard fails the test before it
/// can reach the settings file or the registry.
/// </summary>
public class RetiredTweakTests
{
    [Fact]
    public void OnlyHags_IsStillOffered_AndEveryOtherTweakStaysRegistered()
    {
        var manager = new SystemTweaksManager();

        Assert.Equal([nameof(Core.SystemTweaks.Tweaks.DisableHags)], manager.OfferedTweaks.Select(t => t.GetType().Name));
        Assert.Equal(16, manager.Tweaks.Count);
        Assert.All(manager.Tweaks.Where(t => t is not Core.SystemTweaks.Tweaks.DisableHags),
            t => Assert.True(SystemTweaksManager.IsRetired(t), t.GetType().Name));
    }

    [Fact]
    public void StandIns_CoverEveryRetiredTweak()
    {
        var retired = new SystemTweaksManager().Tweaks
            .Where(SystemTweaksManager.IsRetired)
            .Select(t => t.GetType().Name)
            .OrderBy(n => n);

        Assert.Equal(retired, AllStandIns.Select(t => t.GetType().Name).OrderBy(n => n));
    }

    [Theory]
    [MemberData(nameof(StandIns))]
    public void ApplyTweak_IsRefused_ForEveryRetiredTweak(ISystemTweak standIn)
    {
        Assert.False(new SystemTweaksManager().ApplyTweak(standIn));
    }

    public static TheoryData<ISystemTweak> StandIns() => new(AllStandIns);

    private static readonly ISystemTweak[] AllStandIns =
    [
        new DisableGameDvr(),
        new DisableMpo(),
        new OptimizeMmcss(),
        new OptimizeWin32PrioritySeparation(),
        new DisableMemoryIntegrity(),
        new DisablePowerThrottling(),
        new OptimizePageFile(),
        new DisableLastAccessTimestamp(),
        new DisableNtfs8dot3(),
        new DisableUsbSelectiveSuspend(),
        new OptimizeInterruptHandling(),
        new DisableMemoryCompression(),
        new EnableLargePages(),
        new OptimizeNtfsMemoryUsage(),
        new OptimizeKernelMemory(),
    ];

    private abstract class StandIn : ISystemTweak
    {
        public string Name => GetType().Name;
        public string Description => "";
        public string Category => "";
        public bool RequiresReboot => false;
        public bool DetectIsApplied() => throw new InvalidOperationException("A retired tweak was inspected for apply.");
        public string? Apply() => throw new InvalidOperationException("A retired tweak was applied.");
        public bool Revert(string? originalValuesJson) => throw new InvalidOperationException("Not part of this test.");
    }

    private sealed class DisableGameDvr : StandIn;
    private sealed class DisableMpo : StandIn;
    private sealed class OptimizeMmcss : StandIn;
    private sealed class OptimizeWin32PrioritySeparation : StandIn;
    private sealed class DisableMemoryIntegrity : StandIn;
    private sealed class DisablePowerThrottling : StandIn;
    private sealed class OptimizePageFile : StandIn;
    private sealed class DisableLastAccessTimestamp : StandIn;
    private sealed class DisableNtfs8dot3 : StandIn;
    private sealed class DisableUsbSelectiveSuspend : StandIn;
    private sealed class OptimizeInterruptHandling : StandIn;
    private sealed class DisableMemoryCompression : StandIn;
    private sealed class EnableLargePages : StandIn;
    private sealed class OptimizeNtfsMemoryUsage : StandIn;
    private sealed class OptimizeKernelMemory : StandIn;
}
