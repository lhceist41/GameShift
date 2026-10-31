using System.Text.Json;
using GameShift.Core.SystemTweaks.Tweaks;
using Xunit;

namespace GameShift.Tests.SystemTweaks;

/// <summary>
/// Tests for the retired USB selective suspend tweak through its powrprof reader and powercfg seams,
/// so no power setting is read from or written to this machine. Earlier versions recorded -1 for a
/// value they could not read; a revert must never hand that to powercfg.
/// </summary>
public class DisableUsbSelectiveSuspendTests
{
    private const string Ids = "2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SetActive = "/setactive SCHEME_CURRENT";

    private static string SetAc(object value) => $"/setacvalueindex SCHEME_CURRENT {Ids} {value}";
    private static string SetDc(object value) => $"/setdcvalueindex SCHEME_CURRENT {Ids} {value}";

    private static (DisableUsbSelectiveSuspend Tweak, List<string> Calls) Create(
        uint? currentAc = 1, uint? currentDc = 1, uint? defaultAc = 1, uint? defaultDc = 1,
        Func<string, int>? exitCode = null)
    {
        var calls = new List<string>();
        var tweak = new DisableUsbSelectiveSuspend
        {
            ReadIndex = ac => ac ? currentAc : currentDc,
            ReadDefaultIndex = ac => ac ? defaultAc : defaultDc,
            RunPowercfg = args => { calls.Add(args); return exitCode?.Invoke(args) ?? 0; },
        };
        return (tweak, calls);
    }

    [Fact]
    public void Revert_LegacyMinusOneOriginals_RestoresTheWindowsDefaults_NeverPassesMinusOne()
    {
        var (tweak, calls) = Create(defaultAc: 1, defaultDc: 2);

        Assert.True(tweak.Revert("""{"OriginalAc":-1,"OriginalDc":-1}"""));

        Assert.Equal([SetAc(1), SetDc(2), SetActive], calls);
        Assert.DoesNotContain(calls, c => c.Split(' ').Contains("-1"));
    }

    [Fact]
    public void Revert_OneSlotUnrecorded_RestoresTheRecordedOneAndTheDefaultForTheOther()
    {
        var (tweak, calls) = Create(defaultAc: 5, defaultDc: 1);

        Assert.True(tweak.Revert("""{"OriginalAc":0,"OriginalDc":-1}"""));

        Assert.Equal([SetAc(0), SetDc(1), SetActive], calls);
    }

    [Theory]
    [InlineData(null, 1u)]
    [InlineData(1u, null)]
    public void Revert_LegacyOriginal_DefaultUnreadable_ChangesNothing(uint? defaultAc, uint? defaultDc)
    {
        var (tweak, calls) = Create(defaultAc: defaultAc, defaultDc: defaultDc);

        Assert.False(tweak.Revert("""{"OriginalAc":-1,"OriginalDc":-1}"""));

        Assert.Empty(calls);
    }

    [Fact]
    public void Revert_RecordedOriginals_WritesThemWithoutReadingDefaults()
    {
        var calls = new List<string>();
        var tweak = new DisableUsbSelectiveSuspend
        {
            ReadDefaultIndex = _ => throw new InvalidOperationException("default read"),
            RunPowercfg = args => { calls.Add(args); return 0; },
        };

        Assert.True(tweak.Revert("""{"OriginalAc":1,"OriginalDc":0}"""));

        Assert.Equal([SetAc(1), SetDc(0), SetActive], calls);
    }

    [Theory]
    [InlineData("/setacvalueindex")]
    [InlineData("/setdcvalueindex")]
    [InlineData("/setactive")]
    public void Revert_AnyPowercfgFailure_ReportsFailure(string failing)
    {
        var (tweak, _) = Create(exitCode: args => args.StartsWith(failing, StringComparison.Ordinal) ? 1 : 0);

        Assert.False(tweak.Revert("""{"OriginalAc":1,"OriginalDc":1}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"OriginalAc":1}""")]
    [InlineData("""{"OriginalAc":4294967296,"OriginalDc":1}""")]
    public void Revert_UnusableRecord_ChangesNothing(string? json)
    {
        var (tweak, calls) = Create();

        Assert.False(tweak.Revert(json));

        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("""{"OriginalAc":-1,"OriginalDc":-1}""", true)]
    [InlineData("""{"OriginalAc":1,"OriginalDc":-1}""", true)]
    [InlineData("""{"OriginalAc":-1,"OriginalDc":0}""", true)]
    [InlineData("""{"OriginalAc":1,"OriginalDc":0}""", false)]
    [InlineData(null, false)]
    [InlineData("not json", false)]
    public void HasUnrecordedOriginal_OnlyForANegativeOriginal(string? json, bool expected) =>
        Assert.Equal(expected, DisableUsbSelectiveSuspend.HasUnrecordedOriginal(json));

    [Theory]
    [InlineData(null, 1u)]
    [InlineData(1u, null)]
    public void Apply_ValueUnreadable_ChangesNothing(uint? currentAc, uint? currentDc)
    {
        var (tweak, calls) = Create(currentAc: currentAc, currentDc: currentDc);

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());

        Assert.Empty(calls);
    }

    [Fact]
    public void Apply_RecordsTheOriginalsInTheShapeRevertReads()
    {
        var (tweak, calls) = Create(currentAc: 1, currentDc: 0);

        var json = tweak.Apply();

        Assert.Equal([SetAc(0), SetDc(0), SetActive], calls);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal(1, doc.RootElement.GetProperty("OriginalAc").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("OriginalDc").GetInt32());
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, false)]
    [InlineData(null, false)]
    public void DetectIsApplied_UsesThePowrprofRead(uint? currentAc, bool expected)
    {
        var (tweak, calls) = Create(currentAc: currentAc);

        Assert.Equal(expected, tweak.DetectIsApplied());
        Assert.Empty(calls);
    }
}
