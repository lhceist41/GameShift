using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

/// <summary>Quick fix cards reflow by width: at least 220 DIP each, one to three per row.</summary>
public class QuickFixLayoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void NotLaidOut_UsesOneColumn(double width)
    {
        Assert.Equal(1, QuickFixLayout.Columns(width, 3));
    }

    [Theory]
    [InlineData(200, 1)]
    [InlineData(455, 1)]
    [InlineData(456, 2)]
    [InlineData(683, 2)]
    [InlineData(684, 3)]
    [InlineData(2400, 3)]
    public void Columns_FitCardsOfAtLeastTheMinimumWidth(double width, int expected)
    {
        int columns = QuickFixLayout.Columns(width, 3);

        Assert.Equal(expected, columns);
        Assert.True(width < QuickFixLayout.MinCardWidth + QuickFixLayout.CardGap
                    || width / columns - QuickFixLayout.CardGap >= QuickFixLayout.MinCardWidth);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 3)]
    public void Columns_NeverExceedTheCardCount(int cards, int expected)
    {
        Assert.Equal(expected, QuickFixLayout.Columns(2400, cards));
    }
}
