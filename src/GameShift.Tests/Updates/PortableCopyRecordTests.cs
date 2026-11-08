using System;
using System.IO;
using GameShift.Core.Config;
using GameShift.Core.Updates;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Updates;

/// <summary>
/// Every install path of a portable copy records the running exe before the installer starts, so the
/// installed copy can name the old file once, and removes the record when the installer did not start.
/// The settings file lives in a temp folder.
/// </summary>
[Collection("ConfigState")]
public class PortableCopyRecordTests
{
    private const string InstallDir = @"C:\Program Files\GameShift";
    private const string PortableExe = @"C:\Users\me\Downloads\GameShift.App.exe";
    private const string InstalledExe = @"C:\Program Files\GameShift\GameShift.App.exe";

    private static readonly InstallationInfo Portable = new(InstallMode.Portable, PortableExe, null);
    private static readonly InstallationInfo Installed = new(InstallMode.Installed, InstalledExe, InstallDir);

    private static void WithSettingsFile(Action<string> test)
    {
        using var temp = new TempPath();
        var file = temp.GetFile("settings.json");
        SettingsManager.SettingsFilePathOverride = file;
        try
        {
            test(file);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void PortableCopy_InstallerStarted_KeepsTheRecord_WrittenBeforeTheInstallerStarts()
    {
        WithSettingsFile(_ =>
        {
            string? recordWhenApplying = "not called";

            var started = UpdateCoordinator.ApplyRememberingPortableCopy(Portable, PortableExe, () =>
            {
                recordWhenApplying = SettingsManager.Load().MigratedPortableExePath;
                return true;
            });

            Assert.True(started);
            Assert.Equal(PortableExe, recordWhenApplying);
            Assert.Equal(PortableExe, SettingsManager.Load().MigratedPortableExePath);
        });
    }

    [Fact]
    public void PortableCopy_InstallerNotStarted_RemovesTheRecord_AndKeepsOtherSettings()
    {
        WithSettingsFile(_ =>
        {
            SettingsManager.Save(new AppSettings { SkippedUpdateVersion = "3.9.0" });

            var started = UpdateCoordinator.ApplyRememberingPortableCopy(Portable, PortableExe, () => false);

            Assert.False(started);
            var settings = SettingsManager.Load();
            Assert.Null(settings.MigratedPortableExePath);
            Assert.Equal("3.9.0", settings.SkippedUpdateVersion);
        });
    }

    [Fact]
    public void PortableCopy_InstallerNotStarted_LeavesARecordWrittenMeanwhile()
    {
        const string OtherExe = @"D:\Tools\GameShift.App.exe";
        WithSettingsFile(_ =>
        {
            var started = UpdateCoordinator.ApplyRememberingPortableCopy(Portable, PortableExe, () =>
            {
                SettingsManager.Update(s => s.MigratedPortableExePath = OtherExe);
                return false;
            });

            Assert.False(started);
            Assert.Equal(OtherExe, SettingsManager.Load().MigratedPortableExePath);
        });
    }

    [Fact]
    public void InstalledCopy_RecordsNothing()
    {
        WithSettingsFile(file =>
        {
            var applied = 0;

            var started = UpdateCoordinator.ApplyRememberingPortableCopy(Installed, InstalledExe, () => { applied++; return true; });

            Assert.True(started);
            Assert.Equal(1, applied);
            Assert.False(File.Exists(file));
            Assert.Null(SettingsManager.Load().MigratedPortableExePath);
        });
    }

    [Fact]
    public void PortableCopy_ApplyThrows_RemovesTheRecord_AndTheExceptionReachesTheCaller()
    {
        WithSettingsFile(_ =>
        {
            var failure = new InvalidOperationException("installer start failed");

            var thrown = Assert.Throws<InvalidOperationException>(() =>
                UpdateCoordinator.ApplyRememberingPortableCopy(Portable, PortableExe, () => throw failure));

            Assert.Same(failure, thrown);
            Assert.Null(SettingsManager.Load().MigratedPortableExePath);
        });
    }

    [Fact]
    public void PortableCopy_UnreadableSettings_StillStartsTheInstaller()
    {
        WithSettingsFile(file =>
        {
            File.WriteAllText(file, "{ not json");
            var applied = 0;

            var started = UpdateCoordinator.ApplyRememberingPortableCopy(Portable, PortableExe, () => { applied++; return true; });

            Assert.True(started);
            Assert.Equal(1, applied);
        });
    }
}
