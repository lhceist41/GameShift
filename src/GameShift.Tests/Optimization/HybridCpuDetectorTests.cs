using GameShift.Core.Journal;
using GameShift.Core.Optimization;

namespace GameShift.Tests.Optimization;

/// <summary>
/// A V-Cache CCD is reported only for an X3D CPU whose cores span two L3 groups, which is what lets
/// the power plan switcher tell a dual-CCD X3D from a single-CCD one.
/// </summary>
public class HybridCpuDetectorTests
{
    private const string DualCcdX3DName = "AMD Ryzen 9 7950X3D 16-Core Processor";

    private static List<CpuCore> Cores(params byte[] l3Groups) =>
        l3Groups.Select((group, i) => new CpuCore { CpuSetId = (uint)(256 + i), LastLevelCacheIndex = group }).ToList();

    [Fact]
    public void X3DWithTwoL3Groups_HasAVCacheCcd()
    {
        Assert.Equal(0, HybridCpuDetector.ResolveVCacheCcdIndex(Cores(0, 0, 1, 1), () => DualCcdX3DName));
    }

    [Fact]
    public void X3DWithOneL3Group_HasNone_AndItsNameIsNotRead()
    {
        Assert.Null(HybridCpuDetector.ResolveVCacheCcdIndex(
            Cores(0, 0, 0, 0), () => throw new InvalidOperationException("CPU name read for a single L3 group")));
    }

    [Fact]
    public void TwoL3GroupsWithoutX3D_HasNone()
    {
        Assert.Null(HybridCpuDetector.ResolveVCacheCcdIndex(Cores(0, 0, 1, 1), () => "AMD Ryzen 9 7950X 16-Core Processor"));
    }

    [Fact]
    public void JournalRecordOfTheLivePath_Reverts()
    {
        // The record earlier versions wrote when pinning used CPU sets (no IFEO entry).
        var result = new HybridCpuDetector().RevertFromRecord("""{"ifeoKeys":[]}""");

        Assert.Equal(OptimizationState.Reverted, result.State);
    }

    // Every version that journaled this module wrote an ifeoKeys list, so anything else is not its record.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("""{"ifeoKeys":{}}""")]
    [InlineData("[]")]
    public void RecordWithoutAnIfeoList_IsNotClaimedReverted(string record)
    {
        var result = new HybridCpuDetector().RevertFromRecord(record);

        Assert.Equal(OptimizationState.Failed, result.State);
    }
}
