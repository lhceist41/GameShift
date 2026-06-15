using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Batch 2 safety policy: kernel-anti-cheat classification (used to gate external live process
/// writes) and the new "suspend" crash-recovery kind (resume frozen processes after a crash).
/// </summary>
public class AntiCheatAndSuspendRecoveryTests
{
    [Theory]
    [InlineData(AntiCheatType.None, false)]
    [InlineData(AntiCheatType.ValveAntiCheat, false)]
    [InlineData(AntiCheatType.RiotVanguard, true)]
    [InlineData(AntiCheatType.EasyAntiCheat, true)]
    [InlineData(AntiCheatType.BattlEye, true)]
    [InlineData(AntiCheatType.FaceitAC, true)]
    [InlineData(AntiCheatType.Ricochet, true)]
    [InlineData(AntiCheatType.TencentACE, true)]
    [InlineData(AntiCheatType.Proprietary, true)]
    public void IsKernelLevel_ClassifiesAntiCheats(AntiCheatType type, bool expected)
    {
        Assert.Equal(expected, AntiCheatDetector.IsKernelLevel(type));
    }

    [Theory]
    [InlineData("{\"name\":\"gameshift.nonexistent\",\"pids\":[2147480000]}")] // valid shape, PID won't exist
    [InlineData("{\"pids\":[]}")]
    [InlineData("{\"name\":\"x\"}")]
    [InlineData("{}")]
    [InlineData("{ this is not valid json")]
    [InlineData("null")]
    public void Revert_SuspendKind_NeverThrows(string payload)
    {
        var ex = Record.Exception(() => GameActionRecovery.Revert("suspend", payload, Logger.None));
        Assert.Null(ex);
    }
}
