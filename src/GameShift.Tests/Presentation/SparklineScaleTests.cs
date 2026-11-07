using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

public class SparklineScaleTests
{
    // A 59 x 10 box with no inset puts the 60 slots exactly 1 DIP apart.
    private const int Capacity = 60;
    private const double Width = 59;
    private const double Height = 10;

    private static IReadOnlyList<IReadOnlyList<SparkPoint>> Segments(params double[] values) =>
        SparklineScale.Segments(values, Capacity, Width, Height, bottom: 0, floorTop: 100, inset: 0);

    [Fact]
    public void Empty_DrawsNothing()
    {
        Assert.Empty(Segments());
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    public void BoxWithoutRoom_DrawsNothing(double width, double height)
    {
        Assert.Empty(SparklineScale.Segments([50.0], Capacity, width, height, 0, 100, 0));
    }

    [Fact]
    public void InsetLargerThanTheBox_DrawsNothing()
    {
        Assert.Empty(SparklineScale.Segments([50.0], Capacity, 4, 4, 0, 100, inset: 2));
    }

    [Fact]
    public void OneSample_IsOnePointAtTheRightEdge()
    {
        var segment = Assert.Single(Segments(50));
        var point = Assert.Single(segment);
        Assert.Equal(new SparkPoint(59, 5), point);
    }

    [Fact]
    public void NewestSampleSitsAtTheRightEdge_OlderOnesStepLeft()
    {
        var segment = Assert.Single(Segments(0, 100, 50));
        Assert.Equal(new[] { new SparkPoint(57, 10), new SparkPoint(58, 0), new SparkPoint(59, 5) }, segment);
    }

    [Fact]
    public void FlatSeries_IsAFlatLineAtItsValue()
    {
        var segment = Assert.Single(Segments(25, 25, 25, 25));
        Assert.All(segment, p => Assert.Equal(7.5, p.Y, 6));
    }

    [Fact]
    public void FlatSeriesAtTheBottom_LiesOnTheBottomEdge()
    {
        var segment = Assert.Single(Segments(0, 0, 0));
        Assert.All(segment, p => Assert.Equal(Height, p.Y));
    }

    [Fact]
    public void FloorAtTheBottom_StillDividesSafely()
    {
        var segment = Assert.Single(SparklineScale.Segments([0.0, 0.0], Capacity, Width, Height, 0, floorTop: 0, inset: 0));
        Assert.All(segment, p => Assert.True(double.IsFinite(p.Y)));
        Assert.All(segment, p => Assert.Equal(Height, p.Y));
    }

    [Fact]
    public void SpikeAboveTheFloor_ReachesTheTopAndScalesTheRest()
    {
        var segment = Assert.Single(Segments(100, 400));
        Assert.Equal(0, segment[1].Y);
        Assert.Equal(7.5, segment[0].Y, 6);
    }

    [Fact]
    public void ValuesBelowTheFloorTop_KeepTheFloorScale()
    {
        Assert.Equal(100, SparklineScale.Top([10.0, 40.0], 0, 100));
        Assert.Equal(400, SparklineScale.Top([10.0, 400.0], 0, 100));
    }

    [Fact]
    public void ValueBelowTheBottom_IsDrawnOnTheBottomEdge()
    {
        var point = Assert.Single(Assert.Single(Segments(-20)));
        Assert.Equal(Height, point.Y);
    }

    [Fact]
    public void Gap_SplitsTheLineAndLeavesItsSlotEmpty()
    {
        var segments = Segments(10, double.NaN, 20, 30);
        Assert.Equal(2, segments.Count);
        Assert.Equal(new[] { 56.0 }, segments[0].Select(p => p.X));
        Assert.Equal(new[] { 58.0, 59.0 }, segments[1].Select(p => p.X));
    }

    [Fact]
    public void InfinityIsAGap_AndDoesNotStretchTheScale()
    {
        var segments = Segments(50, double.PositiveInfinity, 50);
        Assert.Equal(2, segments.Count);
        Assert.All(segments.SelectMany(s => s), p => Assert.Equal(5, p.Y, 6));
    }

    [Fact]
    public void OnlyGaps_DrawsNothing()
    {
        Assert.Empty(Segments(double.NaN, double.NaN));
    }

    [Fact]
    public void MoreThanCapacity_ShowsTheNewestAndScalesOnThemOnly()
    {
        // The old spike has left the window, so it no longer sets the top.
        var values = new double[] { 1000 }.Concat(Enumerable.Repeat(50.0, Capacity)).ToArray();
        var segment = Assert.Single(Segments(values));
        Assert.Equal(Capacity, segment.Count);
        Assert.Equal(0, segment[0].X);
        Assert.All(segment, p => Assert.Equal(5, p.Y, 6));
    }

    [Fact]
    public void Inset_KeepsEveryPointInsideTheBox()
    {
        var segment = Assert.Single(SparklineScale.Segments([0.0, 100.0], Capacity, 61, 12, 0, 100, inset: 1));
        Assert.Equal(new SparkPoint(59, 11), segment[0]);
        Assert.Equal(new SparkPoint(60, 1), segment[1]);
    }

    [Fact]
    public void Buffer_KeepsTheLastSixtyOldestFirst()
    {
        var buffer = new SparklineBuffer();
        for (int i = 1; i <= 65; i++) buffer.Push(i);

        var snapshot = buffer.Snapshot();
        Assert.Equal(SparklineBuffer.DefaultCapacity, buffer.Count);
        Assert.Equal(6, snapshot[0]);
        Assert.Equal(65, snapshot[^1]);
    }

    [Fact]
    public void Buffer_WithItsOwnCapacity_KeepsThatMany()
    {
        // DPC samples twice a second, so 120 samples cover the same minute as the other tiles.
        var buffer = new SparklineBuffer(120);
        for (int i = 1; i <= 130; i++) buffer.Push(i);

        Assert.Equal(120, buffer.Capacity);
        Assert.Equal(120, buffer.Count);
        Assert.Equal(11, buffer.Snapshot()[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Buffer_TooSmallToDrawALine_IsRefused(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparklineBuffer(capacity));
    }

    [Fact]
    public void Buffer_StoresInfinityAsAGap_AndClearEmptiesIt()
    {
        var buffer = new SparklineBuffer();
        int changes = 0;
        buffer.Changed += (_, _) => changes++;

        buffer.Push(double.NegativeInfinity);
        buffer.Push(3);
        Assert.True(double.IsNaN(buffer.Snapshot()[0]));

        buffer.Clear();
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Snapshot());
        Assert.Equal(3, changes);
    }
}
