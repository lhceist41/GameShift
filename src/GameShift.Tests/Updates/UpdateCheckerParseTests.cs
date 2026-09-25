using System;
using System.Text.Json;
using GameShift.Core.Updates;
using Xunit;

namespace GameShift.Tests.Updates;

/// <summary>
/// Parsing of the GitHub "latest release" response: which asset, URL, size, and digest an update uses
/// in each install mode.
/// </summary>
public class UpdateCheckerParseTests
{
    private const string AppDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string SetupDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string AppUrl = "https://github.com/lhceist41/GameShift/releases/download/v3.9.0/GameShift.App.exe";
    private const string SetupUrl = "https://github.com/lhceist41/GameShift/releases/download/v3.9.0/GameShift-Setup.exe";
    private const string ZipballUrl = "https://api.github.com/repos/lhceist41/GameShift/zipball/v3.9.0";

    private static readonly Version Current = new(3, 8, 8, 0);

    private static object AppAsset => new
    {
        name = "GameShift.App.exe",
        browser_download_url = AppUrl,
        size = 290_000_000L,
        digest = AppDigest
    };

    private static object SetupAsset => new
    {
        name = "GameShift-Setup.exe",
        browser_download_url = SetupUrl,
        size = 70_000_000L,
        digest = SetupDigest
    };

    private static string Release(string tag, params object[] assets) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        html_url = $"https://github.com/lhceist41/GameShift/releases/tag/{tag}",
        body = "notes",
        zipball_url = ZipballUrl,
        assets
    });

    [Fact]
    public void Installed_SelectsInstallerWithItsOwnSizeAndDigest()
    {
        var info = UpdateChecker.ParseRelease(Release("v3.9.0", AppAsset, SetupAsset), Current, InstallMode.Installed);

        Assert.NotNull(info);
        Assert.Equal(SetupUrl, info!.DownloadUrl);
        Assert.Equal(70_000_000L, info.DownloadSize);
        Assert.Equal(SetupDigest, info.ExpectedSha256);
        Assert.Equal("GameShift-Setup.exe", info.AssetName);
        Assert.Equal(InstallMode.Installed, info.Mode);
        Assert.Equal("3.9.0", info.LatestVersion);
    }

    [Fact]
    public void Portable_SelectsPortableExeWithItsOwnSizeAndDigest()
    {
        var info = UpdateChecker.ParseRelease(Release("v3.9.0", SetupAsset, AppAsset), Current, InstallMode.Portable);

        Assert.NotNull(info);
        Assert.Equal(AppUrl, info!.DownloadUrl);
        Assert.Equal(290_000_000L, info.DownloadSize);
        Assert.Equal(AppDigest, info.ExpectedSha256);
        Assert.Equal("GameShift.App.exe", info.AssetName);
        Assert.Equal(InstallMode.Portable, info.Mode);
    }

    [Fact]
    public void Installed_WithoutInstallerAsset_HasNoDownloadAndNoZipballFallback()
    {
        var info = UpdateChecker.ParseRelease(Release("v3.9.0", AppAsset), Current, InstallMode.Installed);

        Assert.NotNull(info);
        Assert.Null(info!.DownloadUrl);
        Assert.Null(info.ExpectedSha256);
        Assert.Null(info.AssetName);
        Assert.Equal("https://github.com/lhceist41/GameShift/releases/tag/v3.9.0", info.ReleaseUrl);
    }

    [Fact]
    public void Portable_WithOnlyInstallerAsset_FallsBackToZipballWithoutTheInstallerDigest()
    {
        var info = UpdateChecker.ParseRelease(Release("v3.9.0", SetupAsset), Current, InstallMode.Portable);

        Assert.NotNull(info);
        Assert.Equal(ZipballUrl, info!.DownloadUrl);
        Assert.Null(info.ExpectedSha256);
        Assert.Null(info.AssetName);
    }

    [Theory]
    [InlineData("v3.8.8")]
    [InlineData("v3.8.7")]
    public void NotNewer_ReturnsNull(string tag)
    {
        Assert.Null(UpdateChecker.ParseRelease(Release(tag, AppAsset, SetupAsset), Current, InstallMode.Installed));
    }

    [Fact]
    public void InstallerUrlOutsideGitHub_IsDropped()
    {
        var foreign = new
        {
            name = "GameShift-Setup.exe",
            browser_download_url = "https://example.com/GameShift-Setup.exe",
            size = 1L,
            digest = SetupDigest
        };

        var info = UpdateChecker.ParseRelease(Release("v3.9.0", foreign), Current, InstallMode.Installed);

        Assert.NotNull(info);
        Assert.Null(info!.DownloadUrl);
        Assert.Null(info.ExpectedSha256);
        Assert.Null(info.AssetName);
    }
}
