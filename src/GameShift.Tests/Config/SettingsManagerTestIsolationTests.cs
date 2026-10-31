using System;
using System.IO;
using System.Linq;
using GameShift.Core.Config;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// Guards the test assembly's redirect of SettingsManager's default folder: without a per-test
/// path override, settings and logs must land in the per-run temp root, never in the real
/// %AppData%\GameShift.
/// </summary>
[Collection("ConfigState")]
public class SettingsManagerTestIsolationTests
{
    private static readonly string RealAppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameShift");

    [Fact]
    public void DefaultPaths_ResolveToTheTestRoot()
    {
        Assert.Null(SettingsManager.SettingsFilePathOverride);

        Assert.Equal(TestAppData.Root, SettingsManager.GetAppDataPath());
        Assert.Equal(Path.Combine(TestAppData.Root, "logs"), SettingsManager.GetLogsPath());
        Assert.False(
            SettingsManager.GetAppDataPath().StartsWith(RealAppDataPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SaveAndLogWithoutPathOverride_WriteUnderTheTestRoot()
    {
        Assert.Null(SettingsManager.SettingsFilePathOverride);
        var settingsFile = Path.Combine(TestAppData.Root, "settings.json");
        var probe = Guid.NewGuid().ToString("N");
        try
        {
            SettingsManager.Save(new AppSettings { SkippedUpdateVersion = "9.9.9" });
            SettingsManager.Logger.Information("Isolation probe {Probe}", probe);

            Assert.True(File.Exists(settingsFile));
            Assert.Equal("9.9.9", SettingsManager.Load().SkippedUpdateVersion);
            var logFiles = Directory.GetFiles(Path.Combine(TestAppData.Root, "logs"), "gameshift-*.log");
            Assert.Contains(logFiles, f => ReadShared(f).Contains(probe, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
