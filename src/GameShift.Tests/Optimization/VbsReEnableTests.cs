using GameShift.Core.Config;
using GameShift.Core.Optimization;
using Xunit;

namespace GameShift.Tests.Optimization;

/// <summary>
/// Re-enabling Memory Integrity reads both records an earlier version could have written: the
/// toggle's (flag plus RequirePlatformSecurityFeatures original) and the retired tweak's captured
/// originals. The registry and bcdedit steps are admin-only, so the decision and the record
/// clearing are tested on in-memory settings.
/// </summary>
public class VbsReEnableTests
{
    private static AppSettings Settings(bool toggle, bool? tweakByGameShift)
    {
        var settings = new AppSettings { VbsHvciDisabledByGameShift = toggle };
        if (tweakByGameShift is bool byGameShift)
        {
            settings.SystemTweaks = new SystemTweaksSettings();
            settings.SystemTweaks.AppliedTweaks["DisableMemoryIntegrity"] = new TweakState
            {
                IsAppliedByGameShift = byGameShift,
                OriginalValues = byGameShift ? """{"Enabled":1,"Vbs":1,"ReqFeatures":null}""" : null,
            };
        }
        return settings;
    }

    [Theory]
    [InlineData(true, null, true, false)]   // toggle record only
    [InlineData(false, true, false, true)]  // tweak record only: restore its originals, nothing else
    [InlineData(true, true, true, true)]    // both: toggle first, then the older tweak originals
    [InlineData(false, null, true, false)]  // nothing recorded: the user asked to turn it on
    [InlineData(false, false, true, false)] // tweak found it already off: not GameShift's change
    public void ReEnable_ReadsEitherRecord(bool toggle, bool? tweakByGameShift, bool turnOn, bool restoreTweak)
    {
        Assert.Equal((turnOn, restoreTweak), VbsHvciToggle.PlanReEnable(Settings(toggle, tweakByGameShift)));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, null, false)]
    [InlineData(false, false, false)]
    public void DisabledByGameShift_WhenEitherRecordSaysSo(bool toggle, bool? tweakByGameShift, bool expected)
    {
        Assert.Equal(expected, VbsHvciToggle.IsDisabledByGameShift(Settings(toggle, tweakByGameShift)));
    }

    [Fact]
    public void ClearingTheRecords_KeepsEveryOtherTweakEntry()
    {
        var settings = Settings(toggle: true, tweakByGameShift: true);
        settings.VbsHvciOriginalRpsfExisted = true;
        settings.VbsHvciOriginalRpsfValue = 3;
        settings.SystemTweaks!.AppliedTweaks["DisableHags"] = new TweakState { IsAppliedByGameShift = true };

        VbsHvciToggle.ForgetToggleRecord(settings);
        VbsHvciToggle.ForgetTweakRecord(settings);

        Assert.False(settings.VbsHvciDisabledByGameShift);
        Assert.False(settings.VbsHvciOriginalRpsfExisted);
        Assert.Equal(0, settings.VbsHvciOriginalRpsfValue);
        Assert.Equal(["DisableHags"], settings.SystemTweaks.AppliedTweaks.Keys);
        Assert.False(VbsHvciToggle.IsDisabledByGameShift(settings));
    }
}
