using System.IO;
using GameShift.Core.Config;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// The install offer flag and the recorded portable exe survive a save and load under their JSON
/// names, beside the 3.9.0 recommendation flag, which keeps its own meaning.
/// </summary>
[Collection("ConfigState")]
public class InstallHandoverSettingsTests
{
    private const string OldExe = @"C:\Users\me\Downloads\GameShift.App.exe";

    [Fact]
    public void NewFields_RoundTrip_UnderTheirJsonNames()
    {
        using var temp = new TempPath();
        var file = temp.GetFile("settings.json");
        SettingsManager.SettingsFilePathOverride = file;
        try
        {
            SettingsManager.Save(new AppSettings { InstallOfferShown = true, MigratedPortableExePath = OldExe });

            var json = File.ReadAllText(file);
            Assert.Contains("\"InstallOfferShown\": true", json);
            Assert.Contains("\"MigratedPortableExePath\": \"C:\\\\Users\\\\me\\\\Downloads\\\\GameShift.App.exe\"", json);

            var reloaded = SettingsManager.Load();
            Assert.True(reloaded.InstallOfferShown);
            Assert.Equal(OldExe, reloaded.MigratedPortableExePath);
            Assert.False(reloaded.InstallRecommendationShown);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void SettingsFrom390_WithTheOldFlagOnly_StillGetTheInstallOffer()
    {
        using var temp = new TempPath();
        var file = temp.GetFile("settings.json");
        SettingsManager.SettingsFilePathOverride = file;
        try
        {
            File.WriteAllText(file, "{ \"InstallRecommendationShown\": true }");

            var loaded = SettingsManager.Load();

            Assert.True(loaded.InstallRecommendationShown);
            Assert.False(loaded.InstallOfferShown);
            Assert.Null(loaded.MigratedPortableExePath);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void ClearingTheRecordedExe_RoundTripsAsNull()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            SettingsManager.Save(new AppSettings { MigratedPortableExePath = OldExe, SkippedUpdateVersion = "3.9.0" });

            SettingsManager.Update(s => s.MigratedPortableExePath = null);

            var reloaded = SettingsManager.Load();
            Assert.Null(reloaded.MigratedPortableExePath);
            Assert.Equal("3.9.0", reloaded.SkippedUpdateVersion);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }
}
