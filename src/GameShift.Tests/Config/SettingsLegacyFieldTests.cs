using System.IO;
using GameShift.Core.Config;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// Settings files written by earlier versions keep loading after a setting is removed.
/// <see cref="SettingsManager.Load"/> falls back to defaults on any error, so a removed
/// property that broke deserialization would silently reset every setting.
/// </summary>
[Collection("ConfigState")]
public class SettingsLegacyFieldTests
{
    [Fact]
    public void Load_KeepsValues_WhenFileHasRemovedPreferEtwDpcMonitoring()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("settings.json");
        SettingsManager.SettingsFilePathOverride = path;
        try
        {
            File.WriteAllText(path, """
                {
                  "PreferEtwDpcMonitoring": false,
                  "DefaultDpcThresholdMicroseconds": 2500,
                  "SkippedUpdateVersion": "3.8.8"
                }
                """);

            var loaded = SettingsManager.Load();

            Assert.Equal(2500, loaded.DefaultDpcThresholdMicroseconds);
            Assert.Equal("3.8.8", loaded.SkippedUpdateVersion);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }
}
