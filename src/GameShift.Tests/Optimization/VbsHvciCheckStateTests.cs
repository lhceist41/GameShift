using GameShift.Core.Config;
using GameShift.Core.Optimization;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Optimization;

/// <summary>
/// The startup VBS/HVCI check only reads the DeviceGuard registry values; its last-checked
/// timestamp must not create settings.json on a fresh install, or the first-run wizard never shows.
/// </summary>
[Collection("ConfigState")]
public class VbsHvciCheckStateTests
{
    [Fact]
    public void CheckState_FreshInstall_LeavesItAFirstRun()
    {
        using var temp = new TempPath();
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            Assert.True(SettingsManager.IsFirstRun());

            new VbsHvciToggle().CheckState();

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
        using var temp = new TempPath();
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            SettingsManager.Save(new AppSettings { StartWithWindows = false });

            new VbsHvciToggle().CheckState();

            var settings = SettingsManager.Load();
            Assert.NotNull(settings.VbsHvciLastChecked);
            Assert.False(settings.StartWithWindows);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = previous;
        }
    }
}
