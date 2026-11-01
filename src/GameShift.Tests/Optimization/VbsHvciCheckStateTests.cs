using GameShift.Core.Config;
using GameShift.Core.Optimization;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Optimization;

/// <summary>
/// The startup VBS/HVCI check reads the DeviceGuard registry values and Win32_DeviceGuard (both
/// faked here); its last-checked timestamp must not create settings.json on a fresh install, or the
/// first-run wizard never shows.
/// </summary>
[Collection("ConfigState")]
public class VbsHvciCheckStateTests
{
    private const string OnMessage =
        "Memory Integrity (VBS/HVCI) is on. It can lower gaming FPS by a few percent on average.";

    private static readonly DeviceGuardReading RunningReading = new(2, [2], [2]);

    private sealed class FakeDeviceGuard(DeviceGuardReading? reading) : IDeviceGuardReader
    {
        public DeviceGuardReading? Read() => reading;
    }

    private static VbsHvciToggle Toggle(int registryValue, DeviceGuardReading? reading, DateTime? bootUtc = null) =>
        new((_, _) => registryValue, new FakeDeviceGuard(reading), () => bootUtc ?? DateTime.UtcNow.AddHours(-1));

    private static void WithSettings(AppSettings? saved, Action test)
    {
        using var temp = new TempPath();
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            if (saved != null)
                SettingsManager.Save(saved);
            test();
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = previous;
        }
    }

    [Fact]
    public void CheckState_FreshInstall_LeavesItAFirstRun()
    {
        using var temp = new TempPath();
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            Assert.True(SettingsManager.IsFirstRun());

            Toggle(1, RunningReading).CheckState();

            Assert.True(SettingsManager.IsFirstRun());
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = previous;
        }

        Assert.False(File.Exists(temp.GetFile("settings.json")));
    }

    [Fact]
    public void CheckState_SavedSettings_RecordsTheCheckAndKeepsTheRest()
    {
        WithSettings(new AppSettings { StartWithWindows = false }, () =>
        {
            Toggle(1, RunningReading).CheckState();

            var settings = SettingsManager.Load();
            Assert.NotNull(settings.VbsHvciLastChecked);
            Assert.False(settings.StartWithWindows);
        });
    }

    [Fact]
    public void CheckState_RunningWhileTheRegistrySaysOff_ShowsItAsOn()
    {
        WithSettings(new AppSettings(), () =>
        {
            var toggle = Toggle(0, RunningReading);
            toggle.CheckState();

            Assert.Equal(MemoryIntegrityState.Running, toggle.State);
            Assert.True(toggle.ShouldShowBanner);
            Assert.Equal(OnMessage, toggle.BannerMessage);
            Assert.Equal("Running", toggle.StatusText);
        });
    }

    [Fact]
    public void CheckState_TurnedOnButNotRunning_SaysSo()
    {
        WithSettings(new AppSettings(), () =>
        {
            var toggle = Toggle(1, new DeviceGuardReading(1, [2], [0]));
            toggle.CheckState();

            Assert.Equal(MemoryIntegrityState.NotRunning, toggle.State);
            Assert.True(toggle.ShouldShowBanner);
            Assert.Contains("not running right now", toggle.BannerMessage);
            Assert.Equal("Turned on, not running", toggle.StatusText);
        });
    }

    [Fact]
    public void CheckState_NothingConfiguredOrRunning_IsOff()
    {
        WithSettings(new AppSettings(), () =>
        {
            var toggle = Toggle(0, new DeviceGuardReading(0, [0], [0]));
            toggle.CheckState();

            Assert.Equal(MemoryIntegrityState.Off, toggle.State);
            Assert.False(toggle.ShouldShowBanner);
            Assert.Equal("Off", toggle.StatusText);
        });
    }

    [Theory]
    [InlineData(1, MemoryIntegrityState.TurnedOn, true, "Turned on")]
    [InlineData(-1, MemoryIntegrityState.Off, false, "Off")]
    public void CheckState_WithoutAReading_FallsBackToTheRegistry(
        int registryValue, MemoryIntegrityState expected, bool banner, string status)
    {
        WithSettings(new AppSettings(), () =>
        {
            var toggle = Toggle(registryValue, reading: null);
            toggle.CheckState();

            Assert.Equal(expected, toggle.State);
            Assert.Equal(banner, toggle.ShouldShowBanner);
            Assert.Equal(status, toggle.StatusText);
            if (banner)
                Assert.Equal(OnMessage, toggle.BannerMessage);
        });
    }

    [Fact]
    public void CheckState_StillRunningAfterARestart_SaysTheDisableDidNotTakeEffect()
    {
        var disabledAt = DateTime.UtcNow.AddHours(-2);
        WithSettings(new AppSettings { VbsHvciDisabledByGameShift = true, VbsHvciDisabledAtUtc = disabledAt }, () =>
        {
            var toggle = Toggle(0, RunningReading, bootUtc: disabledAt.AddMinutes(1));
            toggle.CheckState();

            Assert.Equal(MemoryIntegrityDisableOutcome.DidNotTakeEffect, toggle.DisableOutcome);
            Assert.True(toggle.ShouldShowBanner);
            Assert.StartsWith("Turning off Memory Integrity (VBS/HVCI) did not take effect", toggle.BannerMessage);
            Assert.Equal("Running. Turning it off did not take effect.", toggle.StatusText);
        });
    }

    [Fact]
    public void CheckState_TurnedOnAgainAfterTheDisable_ShowsItAsOn()
    {
        var disabledAt = DateTime.UtcNow.AddHours(-2);
        WithSettings(new AppSettings { VbsHvciDisabledByGameShift = true, VbsHvciDisabledAtUtc = disabledAt }, () =>
        {
            var toggle = Toggle(1, RunningReading, bootUtc: disabledAt.AddMinutes(1));
            toggle.CheckState();

            Assert.Equal(MemoryIntegrityDisableOutcome.None, toggle.DisableOutcome);
            Assert.True(toggle.ShouldShowBanner);
            Assert.Equal(OnMessage, toggle.BannerMessage);
            Assert.Equal("Running", toggle.StatusText);
        });
    }

    [Fact]
    public void CheckState_StillRunningBeforeTheRestart_AsksForARestart()
    {
        var disabledAt = DateTime.UtcNow.AddMinutes(-5);
        WithSettings(new AppSettings { VbsHvciDisabledByGameShift = true, VbsHvciDisabledAtUtc = disabledAt }, () =>
        {
            var toggle = Toggle(0, RunningReading, bootUtc: disabledAt.AddDays(-1));
            toggle.CheckState();

            Assert.Equal(MemoryIntegrityDisableOutcome.PendingRestart, toggle.DisableOutcome);
            Assert.Equal(
                "Memory Integrity (VBS/HVCI) is still running. Restart Windows to finish turning it off.",
                toggle.BannerMessage);
            Assert.Equal("Running. Restart Windows to finish turning it off.", toggle.StatusText);
        });
    }

    [Fact]
    public void CheckState_Dismissed_HidesTheBannerWhileRunning()
    {
        WithSettings(new AppSettings { VbsHvciNotificationDismissed = true }, () =>
        {
            var toggle = Toggle(1, RunningReading);
            toggle.CheckState();

            Assert.Equal(MemoryIntegrityState.Running, toggle.State);
            Assert.False(toggle.ShouldShowBanner);
        });
    }
}
