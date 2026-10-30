using GameShift.Core.Optimization;
using GameShift.Core.Profiles;

namespace GameShift.Tests.Optimization;

/// <summary>
/// CPU set steering follows the core pinning switch (or an active core reservation); the QoS half
/// follows the efficiency mode switch.
/// </summary>
public class CpuSchedulingOptimizerTests
{
    [Fact]
    public void Defaults_SetQosOnly()
    {
        Assert.Equal((false, true), CpuSchedulingOptimizer.Decide(new GameProfile(), reservationActive: false));
    }

    [Fact]
    public void CorePinningOn_SteersCpuSets()
    {
        var profile = new GameProfile { PinGameCores = true };

        Assert.True(CpuSchedulingOptimizer.Decide(profile, reservationActive: false).SteerCpuSets);
    }

    [Fact]
    public void ActiveReservation_SteersCpuSets_EvenWithPinningOff()
    {
        Assert.True(CpuSchedulingOptimizer.Decide(new GameProfile(), reservationActive: true).SteerCpuSets);
    }

    [Fact]
    public void EfficiencyModeOff_SetsNoQos()
    {
        var profile = new GameProfile { EnableEfficiencyMode = false };

        Assert.False(CpuSchedulingOptimizer.Decide(profile, reservationActive: false).SetQos);
    }
}
