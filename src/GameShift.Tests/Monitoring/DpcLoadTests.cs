using GameShift.Core.Monitoring;
using Xunit;

namespace GameShift.Tests.Monitoring;

/// <summary>
/// The DPC number is "% DPC Time" x 10000 in storage. These pin the display conversion, the
/// threshold entry and the unchanged alert rule, so saved thresholds and session history keep meaning.
/// </summary>
public class DpcLoadTests
{
    [Theory]
    [InlineData(0.1, 1000)]
    [InlineData(0.05, 500)]
    [InlineData(2.5, 25000)]
    public void CounterPercent_ConvertsToStoredScale(double counterPercent, double expectedStored)
    {
        Assert.Equal(expectedStored, DpcLoad.FromCounterPercent(counterPercent), 6);
    }

    [Theory]
    [InlineData(1000, "0.10%")]
    [InlineData(500, "0.05%")]
    [InlineData(1234, "0.1234%")] // a value saved in session history by an earlier version
    [InlineData(1001, "0.1001%")] // just over the default threshold must not read as 0.10%
    [InlineData(1, "0.0001%")]
    [InlineData(25000, "2.50%")]
    [InlineData(0, "0.00%")]
    public void Format_ShowsStoredValueAsPercentOfCpuTime(double stored, string expected)
    {
        Assert.Equal(expected, DpcLoad.Format(stored));
    }

    [Fact]
    public void CounterSample_RoundTripsToTheSamePercent()
    {
        var stored = DpcLoad.FromCounterPercent(0.37);
        Assert.Equal(0.37, DpcLoad.ToPercent(stored), 9);
        Assert.Equal("0.37%", DpcLoad.Format(stored));
    }

    [Theory]
    [InlineData(1000.0, 1000, false)] // exactly at the threshold: no alert, as before
    [InlineData(1000.0001, 1000, true)]
    [InlineData(999.9999, 1000, false)]
    [InlineData(0.0, 0, false)]
    [InlineData(0.0001, 0, true)]
    public void ExceedsThreshold_IsStrictlyGreaterThan(double stored, int threshold, bool expected)
    {
        Assert.Equal(expected, DpcLoad.ExceedsThreshold(stored, threshold));
    }

    [Fact]
    public void DefaultThreshold_AlertsAboveOneTenthPercent()
    {
        Assert.True(DpcLoad.TryParseThreshold("0.1", out var threshold));
        Assert.Equal(1000, threshold); // AppSettings.DefaultDpcThresholdMicroseconds default

        Assert.False(DpcLoad.ExceedsThreshold(DpcLoad.FromCounterPercent(0.1), threshold));
        Assert.True(DpcLoad.ExceedsThreshold(DpcLoad.FromCounterPercent(0.1001), threshold));
    }

    [Theory]
    [InlineData(1000, "0.1")]
    [InlineData(500, "0.05")]
    [InlineData(0, "0")]
    [InlineData(1, "0.0001")]
    [InlineData(15000, "1.5")]
    public void FormatThreshold_RoundTripsThroughParse(int stored, string expectedText)
    {
        var text = DpcLoad.FormatThreshold(stored);
        Assert.Equal(expectedText, text);
        Assert.True(DpcLoad.TryParseThreshold(text, out var parsed));
        Assert.Equal(stored, parsed);
    }

    [Theory]
    [InlineData("0,1", 1000)]
    [InlineData(" 0.25 ", 2500)]
    [InlineData("0.05%", 500)]
    [InlineData("100", 1000000)]
    [InlineData("0.00005", 1)] // rounds half away from zero
    public void TryParseThreshold_AcceptsPlayerInput(string text, int expected)
    {
        Assert.True(DpcLoad.TryParseThreshold(text, out var threshold));
        Assert.Equal(expected, threshold);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("100.5")]
    [InlineData("NaN")]
    [InlineData("1e9")]
    public void TryParseThreshold_RejectsInvalidInput(string? text)
    {
        Assert.False(DpcLoad.TryParseThreshold(text, out _));
    }
}
