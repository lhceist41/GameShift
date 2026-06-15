using GameShift.Core.SystemTweaks;
using Xunit;

namespace GameShift.Tests.SystemTweaks;

/// <summary>
/// Verifies the tier-selection policy that gates the platform-timer BCD tweaks
/// (disabledynamictick, useplatformtick) off on AMD / invariant-TSC CPUs, where forcing
/// the platform timer degrades performance. The pure overload is tested directly so the
/// policy is independent of the host CPU running the tests.
/// </summary>
public class KernelTuningTierTests
{
    [Fact]
    public void Competitive_ExcludesPlatformTimerTweaks_WhenHarmful()
    {
        var ids = KernelTuningManager
            .GetSettingsForTier("Competitive", excludePlatformTimerTweaks: true)
            .Select(s => s.Id).ToHashSet();

        Assert.DoesNotContain("disabledynamictick", ids);
        Assert.DoesNotContain("useplatformtick", ids);

        // The remaining Competitive settings are untouched.
        Assert.Contains("tscsyncpolicy", ids);
        Assert.Contains("x2apicpolicy", ids);
        Assert.Contains("hypervisorlaunchtype", ids);
        Assert.Contains("useplatformclock", ids);
    }

    [Fact]
    public void Competitive_IncludesPlatformTimerTweaks_WhenNotHarmful()
    {
        var set = KernelTuningManager.GetSettingsForTier("Competitive", excludePlatformTimerTweaks: false);
        var ids = set.Select(s => s.Id).ToHashSet();

        Assert.Contains("disabledynamictick", ids);
        Assert.Contains("useplatformtick", ids);

        // Nothing is excluded when not harmful: every defined setting is present.
        foreach (var defined in KernelTuningManager.AllSettings)
            Assert.Contains(defined.Id, ids);
    }

    [Fact]
    public void Casual_NeverIncludesPlatformTimerTweaks_OnEitherCpu()
    {
        foreach (var harmful in new[] { true, false })
        {
            var set = KernelTuningManager.GetSettingsForTier("Casual", excludePlatformTimerTweaks: harmful);
            Assert.DoesNotContain(set, s => s.PlatformTimerTweak);
            Assert.All(set, s => Assert.Equal("Both", s.Tier));
        }
    }

    [Fact]
    public void PlatformTimerTweak_FlagIsSetOnExactlyTheTwoTimerSettings()
    {
        var flagged = KernelTuningManager.AllSettings
            .Where(s => s.PlatformTimerTweak)
            .Select(s => s.Id)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "disabledynamictick", "useplatformtick" }, flagged);
    }

    // ── SelectActivePlatformTimerTweaks (pure detection) ─────────────────────

    [Fact]
    public void SelectActivePlatformTimerTweaks_NotHarmful_ReturnsEmpty()
    {
        var values = new Dictionary<string, string?>
        {
            ["useplatformtick"] = "Yes",
            ["disabledynamictick"] = "Yes",
        };
        Assert.Empty(KernelTuningManager.SelectActivePlatformTimerTweaks(harmful: false, values));
    }

    [Fact]
    public void SelectActivePlatformTimerTweaks_BothSet_ReturnsBoth()
    {
        var values = new Dictionary<string, string?>
        {
            ["useplatformtick"] = "Yes",
            ["disabledynamictick"] = "Yes",
            ["x2apicpolicy"] = "Enable", // not a platform-timer tweak - must be ignored
        };
        var ids = KernelTuningManager.SelectActivePlatformTimerTweaks(harmful: true, values)
            .Select(s => s.Id).ToHashSet();

        Assert.Equal(2, ids.Count);
        Assert.Contains("useplatformtick", ids);
        Assert.Contains("disabledynamictick", ids);
        Assert.DoesNotContain("x2apicpolicy", ids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No")]
    public void SelectActivePlatformTimerTweaks_OnlyEnabledValuesCount(string? disabledValue)
    {
        var values = new Dictionary<string, string?>
        {
            ["useplatformtick"] = "Yes",
            ["disabledynamictick"] = disabledValue,
        };
        var ids = KernelTuningManager.SelectActivePlatformTimerTweaks(harmful: true, values)
            .Select(s => s.Id).ToList();

        Assert.Equal(new[] { "useplatformtick" }, ids);
    }
}
