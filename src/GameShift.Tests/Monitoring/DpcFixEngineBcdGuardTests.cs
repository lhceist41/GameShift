using GameShift.Core.Monitoring;
using Xunit;

namespace GameShift.Tests.Monitoring;

/// <summary>
/// Verifies the pure policy that blocks applying platform-timer BCD fixes
/// (useplatformtick / disabledynamictick) on AMD, while never blocking reverts.
/// Covers the DPC Doctor "Disable HPET" quick fix and known-driver auto-fixes,
/// which both flow through DpcFixEngine.ApplyBcdEditFix.
/// </summary>
public class DpcFixEngineBcdGuardTests
{
    [Theory]
    [InlineData("bcdedit /set useplatformtick yes")]
    [InlineData("bcdedit /set disabledynamictick yes")]
    public void Skips_PlatformTimerApply_OnHarmfulCpu(string command)
    {
        Assert.True(DpcFixEngine.ShouldSkipBcdApply(command, platformTimerTweaksHarmful: true));
    }

    [Theory]
    [InlineData("bcdedit /set useplatformtick yes")]
    [InlineData("bcdedit /set disabledynamictick yes")]
    public void Allows_PlatformTimerApply_OnNonHarmfulCpu(string command)
    {
        Assert.False(DpcFixEngine.ShouldSkipBcdApply(command, platformTimerTweaksHarmful: false));
    }

    [Theory]
    [InlineData("bcdedit /deletevalue useplatformtick")]
    [InlineData("bcdedit /deletevalue disabledynamictick")]
    public void NeverBlocks_Revert_EvenOnHarmfulCpu(string command)
    {
        // Revert commands are /deletevalue, not /set, so a value set before the guard can be removed.
        Assert.False(DpcFixEngine.ShouldSkipBcdApply(command, platformTimerTweaksHarmful: true));
    }

    [Theory]
    [InlineData("bcdedit /set x2apicpolicy enable")]
    [InlineData("bcdedit /set tscsyncpolicy enhanced")]
    [InlineData(null)]
    [InlineData("")]
    public void Allows_UnrelatedOrEmptyCommands(string? command)
    {
        Assert.False(DpcFixEngine.ShouldSkipBcdApply(command, platformTimerTweaksHarmful: true));
    }
}
