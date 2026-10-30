using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using Xunit;

namespace GameShift.Tests.Monitoring;

/// <summary>
/// The DPC Doctor fixes built from the embedded known-drivers.json, and the guard that keeps a fix
/// from stacking on the retired System Tweak that changed the same setting. Reads only the embedded
/// resource and in-memory settings.
/// </summary>
public class DpcQuickFixListTests
{
    private static List<DriverAutoFix> AllFixes() =>
        KnownDriverDatabase.Load().GetAll().SelectMany(d => d.AutoFixes).ToList();

    [Theory]
    [InlineData("disable_hags")]
    [InlineData("disable_dynamic_tick")]
    [InlineData("high_perf_power_plan")]
    public void DroppedFix_IsNoLongerOffered(string fixId)
    {
        Assert.DoesNotContain(AllFixes(), f => f.Id == fixId);
    }

    [Fact]
    public void NoFix_ChangesABootSetting()
    {
        // Applying a BcdEdit fix is no longer supported; only reverting one an earlier version applied.
        Assert.DoesNotContain(AllFixes(), f => f.ActionType == "BcdEdit");
    }

    [Theory]
    [InlineData("dxgkrnl.sys", "disable_mpo")]
    [InlineData("USBPORT.sys", "disable_usb_selective_suspend")]
    [InlineData("nvlddmkm.sys", "msi_mode_nvidia")]
    [InlineData("atikmdag.sys", "msi_mode_amd")]
    public void SettingsMovedFromSystemTweaks_AreOfferedHere(string driver, string fixId)
    {
        Assert.True(KnownDriverDatabase.Load().TryGetDriver(driver, out var info));
        Assert.Contains(info.AutoFixes, f => f.Id == fixId);
    }

    [Theory]
    [InlineData("disable_mpo", "DisableMpo")]
    [InlineData("disable_usb_selective_suspend", "DisableUsbSelectiveSuspend")]
    [InlineData("msi_mode_nvidia", "OptimizeInterruptHandling")]
    [InlineData("msi_mode_amd", "OptimizeInterruptHandling")]
    public void Fix_IsHeldBack_WhileTheRetiredTweakForTheSameSettingIsRecorded(string fixId, string tweak)
    {
        var settings = new AppSettings { SystemTweaks = new SystemTweaksSettings() };
        settings.SystemTweaks.AppliedTweaks[tweak] = new TweakState { IsAppliedByGameShift = true };

        Assert.Equal(tweak, DpcFixEngine.RetiredTweakHolding(fixId, settings));
    }

    [Fact]
    public void Fix_IsNotHeldBack_ByATweakGameShiftOnlyFoundApplied()
    {
        var settings = new AppSettings { SystemTweaks = new SystemTweaksSettings() };
        settings.SystemTweaks.AppliedTweaks["DisableMpo"] = new TweakState { IsAppliedByGameShift = false };

        Assert.Null(DpcFixEngine.RetiredTweakHolding("disable_mpo", settings));
        Assert.Null(DpcFixEngine.RetiredTweakHolding("disable_mpo", new AppSettings()));
    }

    [Fact]
    public void UnrelatedFix_IsNeverHeldBack()
    {
        var settings = new AppSettings { SystemTweaks = new SystemTweaksSettings() };
        settings.SystemTweaks.AppliedTweaks["DisableMpo"] = new TweakState { IsAppliedByGameShift = true };

        Assert.Null(DpcFixEngine.RetiredTweakHolding("ndis_interrupt_moderation", settings));
    }
}
