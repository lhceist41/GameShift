using GameShift.Core.Journal;
using GameShift.Core.Optimization;

namespace GameShift.Tests.Optimization;

/// <summary>
/// The timer request ends with the process, so a record without the registry value has nothing to
/// restore and reverts as is.
/// </summary>
public class TimerResolutionManagerTests
{
    [Fact]
    public void JournalRecordWithoutTheRegistryValue_Reverts()
    {
        // Written by this version, and by earlier versions on Windows 10.
        var result = new TimerResolutionManager().RevertFromRecord("{}");

        Assert.Equal(OptimizationState.Reverted, result.State);
    }
}
