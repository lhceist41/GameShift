using GameShift.Core.Updates;
using Xunit;

namespace GameShift.Tests.Updates;

public class InstallationDetectorTests
{
    private const string InstallDir = @"C:\Program Files\GameShift";

    [Theory]
    [InlineData(@"C:\Program Files\GameShift\")]   // as Inno Setup writes InstallLocation
    [InlineData(@"C:\Program Files\GameShift")]
    [InlineData(@"c:\program files\gameshift\")]
    [InlineData(@"  C:\Program Files\GameShift\  ")]
    public void ExeInRegisteredFolder_IsInstalled(string registered)
    {
        var info = InstallationDetector.Detect(@"C:\Program Files\GameShift\GameShift.App.exe", registered);

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
        var info = InstallationDetector.Detect(exePath, registered);

        Assert.Equal(InstallMode.Installed, info.Mode);
    }

    [Fact]
    public void ExePathWithDotSegments_IsNormalizedBeforeComparing()
    {
        var info = InstallationDetector.Detect(
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
        var info = InstallationDetector.Detect(exePath, @"C:\Program Files\GameShift\");

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
        var info = InstallationDetector.Detect(@"C:\Program Files\GameShift\GameShift.App.exe", registered);

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
        var info = InstallationDetector.Detect(exePath, @"C:\Program Files\GameShift\");

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
}
