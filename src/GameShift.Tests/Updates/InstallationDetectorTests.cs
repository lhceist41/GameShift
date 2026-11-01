using System;
using System.Collections.Generic;
using GameShift.Core.Updates;
using Xunit;

namespace GameShift.Tests.Updates;

public class InstallationDetectorTests
{
    private const string InstallDir = @"C:\Program Files\GameShift";

    private static readonly InstallFolderTrustResult TrustedInstall = new(true, Array.Empty<string>());

    private static InstallationInfo Detect(string? exePath, string? registered) =>
        InstallationDetector.Detect(exePath, registered, _ => TrustedInstall);

    [Theory]
    [InlineData(@"C:\Program Files\GameShift\")]   // as Inno Setup writes InstallLocation
    [InlineData(@"C:\Program Files\GameShift")]
    [InlineData(@"c:\program files\gameshift\")]
    [InlineData(@"  C:\Program Files\GameShift\  ")]
    public void ExeInRegisteredFolder_IsInstalled(string registered)
    {
        var info = Detect(@"C:\Program Files\GameShift\GameShift.App.exe", registered);

        Assert.Equal(InstallMode.Installed, info.Mode);
        Assert.Equal(InstallDir, info.InstallDirectory, ignoreCase: true);
        Assert.Equal(InstallDir, info.RegisteredInstallDirectory, ignoreCase: true);
    }

    [Theory]
    [InlineData(@"\\?\C:\Program Files\GameShift\GameShift.App.exe", @"C:\Program Files\GameShift\")]
    [InlineData(@"\\?\UNC\server\share\GameShift\GameShift.App.exe", @"\\server\share\GameShift\")]
    public void ExePathWithLongPathPrefix_IsInstalled(string exePath, string registered)
    {
        // Environment.ProcessPath keeps the path as the process was launched, including a \\?\ prefix.
        var info = Detect(exePath, registered);

        Assert.Equal(InstallMode.Installed, info.Mode);
    }

    [Fact]
    public void ExePathWithDotSegments_IsNormalizedBeforeComparing()
    {
        var info = Detect(
            @"C:\Program Files\Other\..\GameShift\GameShift.App.exe", @"C:\Program Files\GameShift\");

        Assert.Equal(InstallMode.Installed, info.Mode);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\GameShift.App.exe")]
    [InlineData(@"C:\Program Files\GameShift2\GameShift.App.exe")]        // shares the folder name as a prefix
    [InlineData(@"C:\Program Files\GameShift\updates\x\GameShift.App.exe")] // below the install folder
    [InlineData(@"C:\Program Files\GameShift.App.exe")]                  // above the install folder
    public void ExeOutsideRegisteredFolder_IsPortable_AndReportsTheInstalledCopy(string exePath)
    {
        var info = Detect(exePath, @"C:\Program Files\GameShift\");

        Assert.Equal(InstallMode.Portable, info.Mode);
        Assert.Null(info.InstallDirectory);
        Assert.Equal(InstallDir, info.RegisteredInstallDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Program Files\GameShift")]  // relative
    [InlineData(@"\Program Files\GameShift")] // rooted but not fully qualified
    [InlineData("C:\\Program Files\\Game\0Shift")]
    public void MissingOrUnusableRegistration_IsPortable(string? registered)
    {
        var info = Detect(@"C:\Program Files\GameShift\GameShift.App.exe", registered);

        Assert.Equal(InstallMode.Portable, info.Mode);
        Assert.Null(info.InstallDirectory);
        Assert.Null(info.RegisteredInstallDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GameShift.App.exe")]
    public void UnknownExePath_IsPortable(string? exePath)
    {
        var info = Detect(exePath, @"C:\Program Files\GameShift\");

        Assert.Equal(InstallMode.Portable, info.Mode);
        Assert.Null(info.InstallDirectory);
    }

    [Fact]
    public void UninstallKey_IsTheInnoSetupKeyForTheInstallerAppId()
    {
        Assert.Equal(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{E56444A0-53F3-4443-86A2-D7B59168BC56}_is1",
            InstallationDetector.UninstallKeyPath);
    }

    [Fact]
    public void ExeInRegisteredFolderThatIsNotAdminOnly_IsPortable_AndReportsTheFolder()
    {
        var info = InstallationDetector.Detect(@"C:\GS\GameShift.App.exe", @"C:\GS\",
            _ => InstallFolderTrustResult.Untrusted("Authenticated Users may modify it"));

        Assert.Equal(InstallMode.Portable, info.Mode);
        Assert.Null(info.InstallDirectory);
        Assert.Equal(@"C:\GS", info.RegisteredInstallDirectory);
    }

    [Fact]
    public void TrustCheckThatThrows_IsPortable()
    {
        var info = InstallationDetector.Detect(@"C:\Program Files\GameShift\GameShift.App.exe", @"C:\Program Files\GameShift\",
            _ => throw new UnauthorizedAccessException("denied"));

        Assert.Equal(InstallMode.Portable, info.Mode);
        Assert.Null(info.InstallDirectory);
    }

    [Fact]
    public void TrustCheck_IsAskedAboutTheRunningExe_OnlyWhenItSitsInTheRegisteredFolder()
    {
        var asked = new List<string>();
        InstallFolderTrustResult Check(string exe)
        {
            asked.Add(exe);
            return TrustedInstall;
        }

        InstallationDetector.Detect(@"C:\Users\me\Downloads\GameShift.App.exe", @"C:\Program Files\GameShift\", Check);
        InstallationDetector.Detect(@"C:\Program Files\GameShift\GameShift.App.exe", null, Check);
        Assert.Empty(asked);

        InstallationDetector.Detect(@"C:\Program Files\GameShift\GameShift.App.exe", @"C:\Program Files\GameShift\", Check);
        Assert.Equal(new[] { @"C:\Program Files\GameShift\GameShift.App.exe" }, asked);
    }
}
