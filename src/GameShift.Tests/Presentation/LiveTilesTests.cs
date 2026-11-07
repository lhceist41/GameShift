using System.Globalization;
using GameShift.Core.Monitoring;
using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

public class LiveTilesTests
{
    private static PingSample Reply(long rtt, double loss = 0, double jitter = 2) =>
        new() { Success = true, RttMilliseconds = rtt, PacketLossPercent = loss, JitterMs = jitter };

    private static PingSample NoReply(double loss) =>
        new() { Success = false, RttMilliseconds = -1, PacketLossPercent = loss };

    // ── DPC ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dpc_CounterMissing_IsNotAvailable_NeverZero()
    {
        var reading = LiveTiles.Dpc(counterReadable: false, monitoring: true, latest: 0, average: 0);
        Assert.Equal(LiveTiles.NotAvailable, reading.Value);
        Assert.DoesNotContain("0", reading.Value);
    }

    [Fact]
    public void Dpc_BetweenSessions_SaysItIsNotMeasuring()
    {
        var reading = LiveTiles.Dpc(counterReadable: true, monitoring: false, latest: 300, average: 300);
        Assert.Equal("Not measuring", reading.Value);
        Assert.Equal(TileTone.Neutral, reading.Tone);
    }

    [Fact]
    public void Dpc_GameWithDpcMonitoringOff_SaysSo()
    {
        var reading = LiveTiles.Dpc(true, monitoring: false, latest: null, average: 0, runningGameHasDpcOff: true);
        Assert.Equal("Not measuring", reading.Value);
        Assert.Equal("DPC monitoring is off in this game's profile", reading.Detail);
        Assert.Equal("Measured while a game runs", LiveTiles.Dpc(true, false, null, 0, runningGameHasDpcOff: false).Detail);
    }

    [Fact]
    public void Dpc_GameWithDpcOff_DoesNotHideALiveReading()
    {
        Assert.Equal("0.05%", LiveTiles.Dpc(true, monitoring: true, latest: 500, average: 500, runningGameHasDpcOff: true).Value);
    }

    [Fact]
    public void Dpc_BeforeTheFirstSample_IsMeasuring()
    {
        Assert.Equal("Measuring", LiveTiles.Dpc(true, true, latest: null, average: 0).Value);
    }

    [Fact]
    public void Dpc_ShowsThePercentOnly()
    {
        var reading = LiveTiles.Dpc(true, true, latest: 500, average: 400);
        Assert.Equal("0.05%", reading.Value);
        Assert.Equal("Raised · average 0.04%", reading.Detail);
        Assert.DoesNotContain("µs", reading.Value + reading.Detail);
    }

    [Theory]
    [InlineData(499, TileTone.Good, "Good")]
    [InlineData(500, TileTone.Raised, "Raised")]
    [InlineData(1000, TileTone.Raised, "Raised")]
    [InlineData(1001, TileTone.High, "High")]
    public void Dpc_BandsFollowTheAlertLevels(double load, TileTone tone, string word)
    {
        var reading = LiveTiles.Dpc(true, true, load, load);
        Assert.Equal(tone, reading.Tone);
        Assert.StartsWith(word + " ", reading.Detail);
    }

    // ── Ping ────────────────────────────────────────────────────────────

    [Fact]
    public void Ping_NoMonitor_IsNotAvailable()
    {
        Assert.Equal(LiveTiles.NotAvailable, LiveTiles.Ping(false, "8.8.8.8", Reply(18)).Value);
    }

    [Fact]
    public void Ping_BeforeTheFirstReply_IsMeasuringItsTarget()
    {
        var reading = LiveTiles.Ping(true, "8.8.8.8", null);
        Assert.Equal("Measuring", reading.Value);
        Assert.Equal("to 8.8.8.8", reading.Detail);
    }

    [Fact]
    public void Ping_Reply_ShowsLatencyTargetLossAndJitter()
    {
        var reading = LiveTiles.Ping(true, "8.8.8.8", Reply(18, loss: 0, jitter: 2.4), replies: 2);
        Assert.Equal("18 ms", reading.Value);
        Assert.Equal("Good · to 8.8.8.8 · 0% loss · jitter 2 ms", reading.Detail);
        Assert.Equal(TileTone.Good, reading.Tone);
    }

    [Fact]
    public void Ping_FirstReply_LeavesOutTheJitterItCannotHaveMeasured()
    {
        var reading = LiveTiles.Ping(true, "8.8.8.8", Reply(18, jitter: 0), replies: 1);
        Assert.Equal("Good · to 8.8.8.8 · 0% loss", reading.Detail);
    }

    [Fact]
    public void Ping_NoTargetAnswered_IsNotAvailable()
    {
        var reading = LiveTiles.Ping(true, "8.8.8.8", NoReply(loss: 100));
        Assert.Equal(LiveTiles.NotAvailable, reading.Value);
        Assert.Equal("No reply from 8.8.8.8", reading.Detail);
        Assert.Equal(TileTone.High, reading.Tone);
    }

    [Fact]
    public void Ping_OneLostReply_SaysSoAndKeepsTheLoss()
    {
        var reading = LiveTiles.Ping(true, "1.1.1.1", NoReply(loss: 5));
        Assert.Equal("No reply", reading.Value);
        Assert.Equal("to 1.1.1.1 · 5% loss", reading.Detail);
    }

    [Theory]
    [InlineData(49, TileTone.Good)]
    [InlineData(50, TileTone.Raised)]
    [InlineData(100, TileTone.Raised)]
    [InlineData(101, TileTone.High)]
    public void Ping_Bands(long rtt, TileTone tone)
    {
        Assert.Equal(tone, LiveTiles.Ping(true, "8.8.8.8", Reply(rtt)).Tone);
    }

    // ── CPU / GPU and memory ────────────────────────────────────────────

    [Fact]
    public void CpuGpu_Both()
    {
        var reading = LiveTiles.CpuGpu(34.4, 70.6);
        Assert.Equal("34 / 71%", reading.Value);
        Assert.Equal("CPU / GPU", reading.Detail);
    }

    [Fact]
    public void CpuGpu_GpuCounterMissing_ShowsCpuOnly_NeverZeroPercentGpu()
    {
        var reading = LiveTiles.CpuGpu(34, null);
        Assert.Equal("34%", reading.Value);
        Assert.Equal("CPU · GPU not available", reading.Detail);
    }

    [Fact]
    public void CpuGpu_CpuCounterMissing_ShowsGpuOnly()
    {
        var reading = LiveTiles.CpuGpu(null, 71);
        Assert.Equal("71%", reading.Value);
        Assert.Equal("GPU · CPU not available", reading.Detail);
    }

    [Fact]
    public void CpuGpu_NeitherCounter_IsNotAvailable()
    {
        Assert.Equal(LiveTiles.NotAvailable, LiveTiles.CpuGpu(null, null).Value);
    }

    [Fact]
    public void Memory_ShowsPercentAndGigabytes()
    {
        var reading = LiveTiles.Memory(35, totalMb: 32768);
        Assert.Equal("35%", reading.Value);
        Assert.Equal("11.2 of 32.0 GB in use · 20.8 GB available", reading.Detail);
    }

    [Fact]
    public void Memory_TotalUnknown_ShowsThePercentOnly()
    {
        var reading = LiveTiles.Memory(35, totalMb: 0);
        Assert.Equal("35%", reading.Value);
        Assert.Equal("in use", reading.Detail);
    }

    [Fact]
    public void Memory_CounterMissing_IsNotAvailable()
    {
        Assert.Equal(LiveTiles.NotAvailable, LiveTiles.Memory(null, 32768).Value);
    }

    [Fact]
    public void Numbers_UseTheInvariantCultureOnAGermanPc()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("11.2 of 32.0 GB in use · 20.8 GB available", LiveTiles.Memory(35, 32768).Detail);
            Assert.Equal("0.05%", LiveTiles.Dpc(true, true, 500, 500).Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ── Stale readings and columns ──────────────────────────────────────

    [Fact]
    public void Stale_OnlyAfterThreeSecondsWithoutASample()
    {
        var last = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(LiveTiles.IsStale(last, last.AddSeconds(3)));
        Assert.True(LiveTiles.IsStale(last, last.AddSeconds(3.5)));
    }

    [Fact]
    public void Stale_DuringAGame_SaysTheReadingsPause()
    {
        Assert.Equal("Paused", LiveTiles.Stale(gameRunning: true).Value);
        Assert.Equal("No new reading", LiveTiles.Stale(gameRunning: false).Value);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(double.NaN, 4)]
    [InlineData(900, 4)]
    [InlineData(680, 4)]
    [InlineData(679, 2)]
    [InlineData(340, 2)]
    [InlineData(339, 1)]
    public void Columns_FourTwoOrOne(double width, int columns)
    {
        Assert.Equal(columns, LiveTileLayout.Columns(width));
    }
}
