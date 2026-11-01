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
        Assert.Same(Installer, UpdateAssetSelector.Select([Portable, Installer]));
        Assert.Same(Installer, UpdateAssetSelector.Select([Installer, Portable]));
    }

    [Fact]
    public void Installed_NeverFallsBackToAnotherAsset()
    {
        // Without the installer there is nothing an installed copy may run.
        var assets = new[] { Portable, Asset("GameShift.exe"), Asset("tools.exe"), Asset("GameShift.zip") };

        Assert.Null(UpdateAssetSelector.Select(assets));
    }

    [Fact]
    public void Installed_MatchesInstallerNameCaseInsensitively()
    {
        var installer = Asset("gameshift-setup.EXE");

        Assert.Same(installer, UpdateAssetSelector.Select([Portable, installer]));
    }

    [Fact]
    public void BothAssets_PicksTheInstaller_ForAPortableCopyToo()
    {
        Assert.Same(Installer, UpdateAssetSelector.Select([Portable, Installer]));
        Assert.Same(Installer, UpdateAssetSelector.Select([Installer, Portable]));
    }

    [Fact]
    public void OnlyThePortableExe_IsNothingToUpdateWith()
    {
        Assert.Null(UpdateAssetSelector.Select([Portable]));
    }

    [Fact]
    public void NeverAnotherExeOrAZip()
    {
        var assets = new[] { Asset("GameShift.exe"), Asset("Other.exe"), Asset("GameShift.zip"), Asset("GameShift-Setup.zip") };

        Assert.Null(UpdateAssetSelector.Select(assets));
    }
}
