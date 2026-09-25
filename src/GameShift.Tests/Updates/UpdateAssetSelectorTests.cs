using GameShift.Core.Updates;
using Xunit;

namespace GameShift.Tests.Updates;

public class UpdateAssetSelectorTests
{
    private static ReleaseAsset Asset(string name) =>
        new(name, $"https://github.com/lhceist41/GameShift/releases/download/v9.9.9/{name}", 100, "sha256:" + new string('a', 64));

    private static readonly ReleaseAsset Portable = Asset("GameShift.App.exe");
    private static readonly ReleaseAsset Installer = Asset("GameShift-Setup.exe");

    [Fact]
    public void Installed_PicksInstaller_WhenBothAssetsPresent()
    {
        Assert.Same(Installer, UpdateAssetSelector.Select([Portable, Installer], InstallMode.Installed));
        Assert.Same(Installer, UpdateAssetSelector.Select([Installer, Portable], InstallMode.Installed));
    }

    [Fact]
    public void Installed_NeverFallsBackToAnotherAsset()
    {
        // Without the installer there is nothing an installed copy may run.
        var assets = new[] { Portable, Asset("GameShift.exe"), Asset("tools.exe"), Asset("GameShift.zip") };

        Assert.Null(UpdateAssetSelector.Select(assets, InstallMode.Installed));
    }

    [Fact]
    public void Installed_MatchesInstallerNameCaseInsensitively()
    {
        var installer = Asset("gameshift-setup.EXE");

        Assert.Same(installer, UpdateAssetSelector.Select([Portable, installer], InstallMode.Installed));
    }

    [Fact]
    public void Portable_PicksPortableExe_WhenBothAssetsPresent()
    {
        Assert.Same(Portable, UpdateAssetSelector.Select([Portable, Installer], InstallMode.Portable));
        Assert.Same(Portable, UpdateAssetSelector.Select([Installer, Portable], InstallMode.Portable));
    }

    [Fact]
    public void Portable_AcceptsLegacyExeName()
    {
        var legacy = Asset("GameShift.exe");

        Assert.Same(legacy, UpdateAssetSelector.Select([Installer, legacy], InstallMode.Portable));
    }

    [Fact]
    public void Portable_NeverTakesTheInstallerAsExeFallback()
    {
        // The portable path moves the download over the running exe; the installer there would break the app.
        Assert.Null(UpdateAssetSelector.Select([Installer], InstallMode.Portable));

        var other = Asset("Other.exe");
        Assert.Same(other, UpdateAssetSelector.Select([Installer, other], InstallMode.Portable));

        var zip = Asset("GameShift.zip");
        Assert.Same(zip, UpdateAssetSelector.Select([Installer, zip], InstallMode.Portable));
    }

    [Fact]
    public void Portable_PrefersExeFallbackOverZip()
    {
        var zip = Asset("GameShift.zip");
        var other = Asset("Other.exe");

        Assert.Same(other, UpdateAssetSelector.Select([zip, other], InstallMode.Portable));
    }
}
