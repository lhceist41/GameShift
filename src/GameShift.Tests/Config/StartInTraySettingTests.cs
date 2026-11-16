using GameShift.Core.Config;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// The Settings page saves "Start in the tray" as <see cref="AppSettings.StartMinimized"/> by loading the
/// stored settings, overlaying its fields and saving them back. These tests run that store path against an
/// isolated settings.json.
/// </summary>
[Collection("ConfigState")]
public class StartInTraySettingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartInTray_SavedThroughTheSettingsPagePath_IsReadBack(bool startInTray)
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            SettingsManager.Save(new AppSettings { StartMinimized = !startInTray, SkippedUpdateVersion = "3.8.8" });

            var settings = SettingsManager.Load();
            settings.StartMinimized = startInTray;
            SettingsManager.Save(settings);

            var reloaded = SettingsManager.Load();
            Assert.Equal(startInTray, reloaded.StartMinimized);
            Assert.Equal("3.8.8", reloaded.SkippedUpdateVersion);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void StartInTrayOff_SurvivesAnUpdateOfAnotherField()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            SettingsManager.Save(new AppSettings { StartMinimized = false });

            SettingsManager.Update(s => s.InstallOfferShown = true);

            Assert.False(SettingsManager.Load().StartMinimized);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void StartInTray_IsOnByDefault()
    {
        Assert.True(new AppSettings().StartMinimized);
    }
}
