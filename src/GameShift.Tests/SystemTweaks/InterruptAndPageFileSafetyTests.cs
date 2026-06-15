using GameShift.Core.SystemTweaks.Tweaks;
using Xunit;

namespace GameShift.Tests.SystemTweaks;

/// <summary>
/// Pure-policy tests for the Batch 1 safety hardening: interrupt-core selection must never pick an
/// invalid/non-existent core (and must skip exotic CPUs), and the fixed page file must never be
/// shrunk below 8 GB (which would lower the commit limit and break kernel crash dumps).
/// </summary>
public class InterruptAndPageFileSafetyTests
{
    // ── ChooseInterruptCore ───────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ChooseInterruptCore_SkipsLowCoreCpus(int lp)
    {
        Assert.Equal(-1, OptimizeInterruptHandling.ChooseInterruptCore(lp, System.Array.Empty<int>(), lp));
    }

    [Theory]
    [InlineData(65)]
    [InlineData(128)]
    public void ChooseInterruptCore_SkipsMultiGroupCpus(int lp)
    {
        Assert.Equal(-1, OptimizeInterruptHandling.ChooseInterruptCore(lp, System.Array.Empty<int>(), lp));
    }

    [Fact]
    public void ChooseInterruptCore_NonHybrid_PicksLastCore()
    {
        // 8 logical processors, no P/E split -> last core (7), never Core 0.
        Assert.Equal(7, OptimizeInterruptHandling.ChooseInterruptCore(8, System.Array.Empty<int>(), 8));
    }

    [Fact]
    public void ChooseInterruptCore_Hybrid_PicksLastPCore()
    {
        // 16 logical processors, 8 P-cores (0..7) + 8 E-cores -> last P-core (7).
        var pcores = new[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        Assert.Equal(7, OptimizeInterruptHandling.ChooseInterruptCore(16, pcores, 16));
    }

    [Fact]
    public void ChooseInterruptCore_NeverReturnsCoreZeroOrOutOfRange()
    {
        for (int lp = 4; lp <= 64; lp++)
        {
            int core = OptimizeInterruptHandling.ChooseInterruptCore(lp, System.Array.Empty<int>(), lp);
            Assert.InRange(core, 1, lp - 1);
            Assert.True(core < 64);
        }
    }

    // ── SizeForRamMB ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(8192, 8192)]
    [InlineData(16384, 8192)]
    [InlineData(32768, 16384)]
    [InlineData(65536, 16384)]
    [InlineData(131072, 24576)]
    public void SizeForRamMB_MatchesPolicy(long ramMB, int expected)
    {
        Assert.Equal(expected, OptimizePageFile.SizeForRamMB(ramMB));
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(32768)]
    [InlineData(262144)]
    public void SizeForRamMB_NeverShrinksBelow8Gb(long ramMB)
    {
        Assert.True(OptimizePageFile.SizeForRamMB(ramMB) >= 8192);
    }
}
