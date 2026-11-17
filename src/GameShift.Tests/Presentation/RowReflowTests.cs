using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

/// <summary>List rows put the badge and switch under the title only when the row is narrow.</summary>
public class RowReflowTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(RowReflow.StackBelowWidth - 0.5)]
    public void NarrowRow_Stacks(double width)
    {
        Assert.True(RowReflow.Stacks(width));
    }

    [Theory]
    [InlineData(RowReflow.StackBelowWidth)]
    [InlineData(RowReflow.StackBelowWidth + 0.5)]
    [InlineData(1200)]
    public void WideRow_StaysSideBySide(double width)
    {
        Assert.False(RowReflow.Stacks(width));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NotLaidOutOrUnconstrained_StaysSideBySide(double width)
    {
        Assert.False(RowReflow.Stacks(width));
    }
}
