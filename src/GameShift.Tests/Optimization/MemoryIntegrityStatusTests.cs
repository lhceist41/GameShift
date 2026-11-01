using GameShift.Core.Config;
using GameShift.Core.Optimization;
using Xunit;

namespace GameShift.Tests.Optimization;

/// <summary>
/// The shown Memory Integrity state comes from what Windows runs (Win32_DeviceGuard), with the
/// registry as the fallback when that cannot be read; a GameShift disable is judged against the boot.
/// </summary>
public class MemoryIntegrityStatusTests
{
    [Theory]
    [InlineData(2, new int[0], new int[0], false, MemoryIntegrityState.Running)]       // VBS running, registry says off
    [InlineData(0, new int[0], new[] { 2 }, false, MemoryIntegrityState.Running)]      // memory integrity listed as running
    [InlineData(1, new int[0], new int[0], false, MemoryIntegrityState.NotRunning)]    // enabled, not running
    [InlineData(0, new[] { 2 }, new int[0], false, MemoryIntegrityState.NotRunning)]   // configured, not running
    [InlineData(0, new[] { 0 }, new[] { 0 }, true, MemoryIntegrityState.NotRunning)]   // registry asks, nothing runs yet
    [InlineData(0, new[] { 0 }, new[] { 0 }, false, MemoryIntegrityState.Off)]
    [InlineData(0, new[] { 1 }, new int[0], false, MemoryIntegrityState.Off)]          // another service configured
    public void Evaluate_ShowsWhatWindowsRuns(
        int vbsStatus, int[] configured, int[] running, bool registryTurnedOn, MemoryIntegrityState expected)
    {
        var reading = new DeviceGuardReading(vbsStatus, configured, running);

        Assert.Equal(expected, MemoryIntegrityStatus.Evaluate(reading, registryTurnedOn));
    }

    [Theory]
    [InlineData(true, MemoryIntegrityState.TurnedOn)]
    [InlineData(false, MemoryIntegrityState.Off)]
    public void Evaluate_WithoutAReading_UsesTheRegistry(bool registryTurnedOn, MemoryIntegrityState expected)
    {
        Assert.Equal(expected, MemoryIntegrityStatus.Evaluate(null, registryTurnedOn));
    }

    private static readonly DateTime DisabledAt = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(MemoryIntegrityState.Running, false, true, true, 5, MemoryIntegrityDisableOutcome.DidNotTakeEffect)]
    [InlineData(MemoryIntegrityState.Running, false, true, true, -5, MemoryIntegrityDisableOutcome.PendingRestart)]
    [InlineData(MemoryIntegrityState.Running, true, true, true, 5, MemoryIntegrityDisableOutcome.None)]   // turned on again since
    [InlineData(MemoryIntegrityState.Running, true, true, true, -5, MemoryIntegrityDisableOutcome.None)]  // turned on again before the restart
    [InlineData(MemoryIntegrityState.NotRunning, false, true, true, 5, MemoryIntegrityDisableOutcome.None)]
    [InlineData(MemoryIntegrityState.TurnedOn, false, true, true, 5, MemoryIntegrityDisableOutcome.None)]
    [InlineData(MemoryIntegrityState.Off, false, true, true, 5, MemoryIntegrityDisableOutcome.None)]
    [InlineData(MemoryIntegrityState.Running, false, true, false, 5, MemoryIntegrityDisableOutcome.None)]  // older record, no time
    [InlineData(MemoryIntegrityState.Running, false, false, true, 5, MemoryIntegrityDisableOutcome.None)]  // record already cleared
    public void JudgeDisable_ComparesTheBootWithTheDisable(
        MemoryIntegrityState state, bool registryTurnedOn, bool disabledByGameShift, bool timeRecorded,
        int bootMinutesAfterDisable, MemoryIntegrityDisableOutcome expected)
    {
        var settings = new AppSettings
        {
            VbsHvciDisabledByGameShift = disabledByGameShift,
            VbsHvciDisabledAtUtc = timeRecorded ? DisabledAt : null,
        };

        Assert.Equal(expected,
            MemoryIntegrityStatus.JudgeDisable(
                state, registryTurnedOn, settings, DisabledAt.AddMinutes(bootMinutesAfterDisable)));
    }

    [Fact]
    public void Parse_ReadsTheWmiTypes()
    {
        var reading = WmiDeviceGuardReader.Parse(2u, new uint[] { 2 }, new uint[] { 1, 2 });

        Assert.NotNull(reading);
        Assert.Equal(2, reading.VbsStatus);
        Assert.Equal([2], reading.ServicesConfigured);
        Assert.Equal([1, 2], reading.ServicesRunning);
    }

    [Fact]
    public void Parse_MissingServiceLists_ReadAsEmpty()
    {
        var reading = WmiDeviceGuardReader.Parse(1u, null, null);

        Assert.NotNull(reading);
        Assert.Empty(reading.ServicesConfigured);
        Assert.Empty(reading.ServicesRunning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2")]
    public void Parse_WithoutAVbsStatus_IsNoReading(object? vbsStatus)
    {
        Assert.Null(WmiDeviceGuardReader.Parse(vbsStatus, new uint[] { 2 }, new uint[] { 2 }));
    }
}
