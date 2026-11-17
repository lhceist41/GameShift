using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

/// <summary>The During games summary chips: counts per badge kind, in page order, zero counts left out.</summary>
public class ModuleSummaryTests
{
    [Fact]
    public void NoModules_GiveNoChips()
    {
        Assert.Empty(ModuleSummary.Chips([]));
    }

    [Fact]
    public void Chips_CountEachKind_InPageOrder_AndLeaveOutZeroCounts()
    {
        var chips = ModuleSummary.Chips(
        [
            ModuleStatusKind.Ready, ModuleStatusKind.Failed, ModuleStatusKind.Applied,
            ModuleStatusKind.Applied, ModuleStatusKind.NotAvailable, ModuleStatusKind.Applied,
        ]);

        Assert.Equal(["3 applied", "1 failed", "1 not available", "1 ready"], chips.Select(c => c.Text));
        Assert.Equal(
            [ModuleStatusKind.Applied, ModuleStatusKind.Failed, ModuleStatusKind.NotAvailable, ModuleStatusKind.Ready],
            chips.Select(c => c.StatusKind));
        Assert.Equal([3, 1, 1, 1], chips.Select(c => c.Count));
    }

    [Fact]
    public void OneAndMany_UseTheSameWord()
    {
        Assert.Equal("1 off", Assert.Single(ModuleSummary.Chips([ModuleStatusKind.Off])).Text);
        Assert.Equal("2 off", Assert.Single(ModuleSummary.Chips([ModuleStatusKind.Off, ModuleStatusKind.Off])).Text);
    }

    // A new badge kind without a chip word would silently vanish from the summary.
    [Fact]
    public void EveryKind_IsCountedExactlyOnce()
    {
        var all = Enum.GetValues<ModuleStatusKind>();

        var chips = ModuleSummary.Chips(all);

        Assert.Equal(all.Length, chips.Count);
        Assert.Equal(all.OrderBy(k => k), chips.Select(c => c.StatusKind).OrderBy(k => k));
        Assert.All(chips, c => Assert.Equal(1, c.Count));
    }

    [Theory]
    [InlineData(ModuleStatusKind.Applied, "1 applied")]
    [InlineData(ModuleStatusKind.Failed, "1 failed")]
    [InlineData(ModuleStatusKind.Off, "1 off")]
    [InlineData(ModuleStatusKind.NotAvailable, "1 not available")]
    [InlineData(ModuleStatusKind.Ready, "1 ready")]
    [InlineData(ModuleStatusKind.Paused, "1 paused")]
    public void EachKind_HasItsWord(ModuleStatusKind kind, string expected)
    {
        Assert.Equal(expected, Assert.Single(ModuleSummary.Chips([kind])).Text);
    }

    [Fact]
    public void UnknownKind_IsNotCounted_AndDoesNotThrow()
    {
        var chips = ModuleSummary.Chips([(ModuleStatusKind)99, ModuleStatusKind.Applied]);

        Assert.Equal("1 applied", Assert.Single(chips).Text);
    }
}
