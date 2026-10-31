using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using Xunit;

namespace GameShift.Tests.Monitoring;

/// <summary>
/// Tests for DPC Doctor's power plan fix through its powrprof reader and powercfg seams, so no power
/// setting is read from or written to this machine. The value a revert needs is read without
/// parsing powercfg's text, which is in the Windows display language.
/// </summary>
public class DpcPowerPlanFixTests
{
    private const string Subgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string Setting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SetPrefix = $"/setacvalueindex SCHEME_CURRENT {Subgroup} {Setting} ";
    private const string SetActive = "/setactive SCHEME_CURRENT";

    private sealed class FakePowercfg
    {
        public List<string> Calls { get; } = new();
        public Func<string, int, bool> Succeeds { get; set; } = (_, _) => true;

        public (bool, string) Run(string args)
        {
            Calls.Add(args);
            int occurrence = Calls.Count(c => c == args);
            return Succeeds(args, occurrence) ? (true, "") : (false, "powercfg error");
        }
    }

    private static (DpcFixEngine Engine, FakePowercfg Powercfg) Create(uint? current = 1, uint? windowsDefault = 1)
    {
        var powercfg = new FakePowercfg();
        var engine = new DpcFixEngine
        {
            RunPowercfg = powercfg.Run,
            ReadAcIndex = (_, _) => current,
            ReadAcDefaultIndex = (_, _) => windowsDefault,
        };
        return (engine, powercfg);
    }

    private static DriverAutoFix UsbFix() => new()
    {
        Id = "disable_usb_selective_suspend",
        Name = "Disable USB Selective Suspend",
        ActionType = "PowerPlanSetting",
        Subgroup = Subgroup,
        Setting = Setting,
        Value = "0",
    };

    private static AppliedDpcFix Entry(string? previousValue) => new()
    {
        FixId = "disable_usb_selective_suspend",
        Description = "Disable USB Selective Suspend",
        ActionType = "PowerPlanSetting",
        PreviousValue = previousValue,
        Target = $"{Subgroup}|{Setting}",
    };

    [Fact]
    public void Apply_CurrentValueUnreadable_ChangesAndRecordsNothing()
    {
        var (engine, powercfg) = Create(current: null);

        var (result, applied) = engine.ApplyPowerPlanFix(UsbFix());

        Assert.False(result.Success);
        Assert.False(result.ChangeLeftInPlace);
        Assert.Contains("nothing was changed", result.Message);
        Assert.Null(applied);
        Assert.Empty(powercfg.Calls);
    }

    [Theory]
    [InlineData(1u, "1")]
    [InlineData(0u, "0")]
    [InlineData(3u, "3")]
    public void Apply_RecordsTheValueReadThroughPowrprof_WithoutQueryingPowercfg(uint current, string recorded)
    {
        var (engine, powercfg) = Create(current: current);

        var (result, applied) = engine.ApplyPowerPlanFix(UsbFix());

        Assert.True(result.Success);
        Assert.NotNull(applied);
        Assert.Equal(recorded, applied.PreviousValue);
        Assert.Equal($"{Subgroup}|{Setting}", applied.Target);
        Assert.Equal("PowerPlanSetting", applied.ActionType);
        // No "/query" and no text parsing: only the write and the activation.
        Assert.Equal([SetPrefix + "0", SetActive], powercfg.Calls);
    }

    [Fact]
    public void Apply_WriteFails_RecordsNothingAndDoesNotActivate()
    {
        var (engine, powercfg) = Create();
        powercfg.Succeeds = (args, _) => !args.StartsWith("/setacvalueindex", StringComparison.Ordinal);

        var (result, applied) = engine.ApplyPowerPlanFix(UsbFix());

        Assert.False(result.Success);
        Assert.Null(applied);
        Assert.Equal([SetPrefix + "0"], powercfg.Calls);
    }

    [Fact]
    public void Apply_ActivationFails_PutsTheEarlierValueBackAndRecordsNothing()
    {
        var (engine, powercfg) = Create(current: 1);
        powercfg.Succeeds = (args, occurrence) => !(args == SetActive && occurrence == 1);

        var (result, applied) = engine.ApplyPowerPlanFix(UsbFix());

        Assert.False(result.Success);
        Assert.False(result.ChangeLeftInPlace);
        Assert.Contains("earlier value was put back", result.Message);
        Assert.Null(applied);
        Assert.Equal([SetPrefix + "0", SetActive, SetPrefix + "1", SetActive], powercfg.Calls);
    }

    [Fact]
    public void Apply_ActivationAndRollbackFail_KeepsTheEntryForAnUndo()
    {
        var (engine, powercfg) = Create(current: 1);
        powercfg.Succeeds = (args, occurrence) =>
            args != SetActive && !(args == SetPrefix + "1" && occurrence == 1);

        var (result, applied) = engine.ApplyPowerPlanFix(UsbFix());

        Assert.False(result.Success);
        Assert.True(result.ChangeLeftInPlace);
        Assert.NotNull(applied);
        Assert.Equal("1", applied.PreviousValue);
        Assert.Equal([SetPrefix + "0", SetActive, SetPrefix + "1", SetActive], powercfg.Calls);
    }

    [Fact]
    public void Revert_RecordedValue_WritesItAndActivates()
    {
        bool defaultRead = false;
        var (engine, powercfg) = Create();
        engine.ReadAcDefaultIndex = (_, _) => { defaultRead = true; return 1; };

        var result = engine.RevertPowerPlanFix(Entry("2"));

        Assert.True(result.Success);
        Assert.Equal("Setting reverted.", result.Message);
        Assert.Equal([SetPrefix + "2", SetActive], powercfg.Calls);
        Assert.False(defaultRead);
    }

    [Fact]
    public void Revert_LegacyEntryWithoutValue_RestoresTheWindowsDefaultAndSaysSo()
    {
        // Shaped as earlier versions saved it on Windows in another display language.
        var entry = JsonSerializer.Deserialize<AppliedDpcFix>(
            $$"""{"FixId":"disable_usb_selective_suspend","Description":"Disable USB Selective Suspend","ActionType":"PowerPlanSetting","PreviousValue":null,"Target":"{{Subgroup}}|{{Setting}}","AppliedAt":"2026-10-01T12:00:00+02:00","RequiresReboot":false}""")!;
        Guid? readSubgroup = null, readSetting = null;
        var (engine, powercfg) = Create(windowsDefault: 1);
        engine.ReadAcDefaultIndex = (subgroup, setting) => { readSubgroup = subgroup; readSetting = setting; return 1; };

        var result = engine.RevertPowerPlanFix(entry);

        Assert.True(result.Success);
        Assert.Contains("not recorded", result.Message);
        Assert.Contains("Windows default", result.Message);
        Assert.Equal([SetPrefix + "1", SetActive], powercfg.Calls);
        Assert.Equal(new Guid(Subgroup), readSubgroup);
        Assert.Equal(new Guid(Setting), readSetting);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Revert_LegacyEntry_DefaultUnreadable_ChangesNothing(string? previousValue)
    {
        var (engine, powercfg) = Create(windowsDefault: null);

        var result = engine.RevertPowerPlanFix(Entry(previousValue));

        Assert.False(result.Success);
        Assert.Contains("nothing was changed", result.Message);
        Assert.Empty(powercfg.Calls);
    }

    [Fact]
    public void Revert_ActivationFails_ReportsFailureSoTheEntryStays()
    {
        var (engine, powercfg) = Create();
        powercfg.Succeeds = (args, _) => args != SetActive;

        var result = engine.RevertPowerPlanFix(Entry("1"));

        Assert.False(result.Success);
        Assert.Equal([SetPrefix + "1", SetActive], powercfg.Calls);
    }

    [Fact]
    public void Revert_WriteFails_ReportsFailureAndDoesNotActivate()
    {
        var (engine, powercfg) = Create();
        powercfg.Succeeds = (_, _) => false;

        var result = engine.RevertPowerPlanFix(Entry("1"));

        Assert.False(result.Success);
        Assert.Equal([SetPrefix + "1"], powercfg.Calls);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1 /x")]
    [InlineData("0x1")]
    [InlineData("4294967296")]
    [InlineData("+1")]
    public void Revert_RefusesAValueThatIsNotAnIndex_WithoutRunningPowercfg(string previousValue)
    {
        var (engine, powercfg) = Create();

        var result = engine.RevertPowerPlanFix(Entry(previousValue));

        Assert.False(result.Success);
        Assert.Empty(powercfg.Calls);
    }

    [Theory]
    [InlineData("PowerPlanSetting", null, Subgroup + "|" + Setting, true)]
    [InlineData("PowerPlanSetting", "", Subgroup + "|" + Setting, true)]
    [InlineData("PowerPlanSetting", "1", Subgroup + "|" + Setting, false)]
    [InlineData("PowerPlanSetting", null, Subgroup, false)]
    [InlineData("RegistrySet", null, Subgroup + "|" + Setting, false)]
    public void IsUnrecordedPowerSetting_OnlyForASubSettingEntryWithoutValue(
        string actionType, string? previousValue, string target, bool expected)
    {
        var entry = new AppliedDpcFix { FixId = "f", ActionType = actionType, PreviousValue = previousValue, Target = target };

        Assert.Equal(expected, DpcFixEngine.IsUnrecordedPowerSetting(entry));
    }
}
