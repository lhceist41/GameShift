using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Monitoring;

/// <summary>
/// Tests for the network adapter fix in <see cref="DpcFixEngine"/> through its PowerShell seam, so no
/// PowerShell or adapter change runs. The keyword and value travel only in the child's environment,
/// the cmdlets come from the System32 NetAdapter module, and the ledger validators still refuse a
/// tampered entry before any process starts.
/// </summary>
public class DpcNetAdapterFixTests
{
    private const string QueryScript =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
        "Get-NetAdapter -Physical | Get-NetAdapterAdvancedProperty -RegistryKeyword $env:GS_ARG0 " +
        "-ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty RegistryValue";
    private const string SetScript =
        "Get-NetAdapter -Physical | Set-NetAdapterAdvancedProperty -RegistryKeyword $env:GS_ARG0 " +
        "-RegistryValue $env:GS_ARG1 -ErrorAction SilentlyContinue";

    private static (DpcFixEngine Engine, FakePowerShellRunner Runner) Create()
    {
        var runner = new FakePowerShellRunner();
        return (new DpcFixEngine { RunPowerShell = runner.Run }, runner);
    }

    private static void AssertConstantScript(FakePowerShellRunner.Call call, string script, params string[] values)
    {
        Assert.Equal(script, call.Script);
        Assert.Equal(FakePowerShellRunner.ExpectedArguments(script), call.Arguments);
        Assert.Equal(FakePowerShellRunner.ManifestPath("NetAdapter"), call.ModuleManifest, ignoreCase: true);
        Assert.Equal(values, call.Values);
    }

    private static DriverAutoFix Fix(string property, string value) => new()
    {
        Id = "test_fix",
        Name = "Test fix",
        ActionType = "SetNetAdapterProperty",
        Property = property,
        Value = value,
    };

    [Theory]
    [InlineData("*InterruptModeration", "0", "0")]
    [InlineData("*EEE", "007", "007")]
    [InlineData("*EEE", " 5 ", "5")]
    [InlineData("*EEE", "+5", "+5")]
    [InlineData("ITR", "-1", "-1")]
    public void ApplyNetAdapterFix_QueriesThenSets_ValuesOnlyInEnvironment(string keyword, string value, string sent)
    {
        var (engine, runner) = Create();
        runner.Respond = call => FakePowerShellRunner.Exit(0, call.Script == QueryScript ? "3\r\n" : "");

        var (result, applied) = engine.ApplyNetAdapterFix(Fix(keyword, value));

        Assert.True(result.Success);
        Assert.Equal(2, runner.Calls.Count);
        AssertConstantScript(runner.Calls[0], QueryScript, keyword);
        AssertConstantScript(runner.Calls[1], SetScript, keyword, sent);
        Assert.NotNull(applied);
        Assert.Equal("3", applied!.PreviousValue);
        Assert.Equal(keyword, applied.Target);
    }

    [Fact]
    public void ApplyNetAdapterFix_SetFails_ReportsFailure()
    {
        var (engine, runner) = Create();
        runner.Respond = call => call.Script == SetScript
            ? new ProcessRunResult(1, string.Empty, "boom", TimedOut: false, Exited: true)
            : FakePowerShellRunner.Exit(0);

        var (result, _) = engine.ApplyNetAdapterFix(Fix("*EEE", "0"));

        Assert.False(result.Success);
        Assert.Contains("boom", result.Message);
    }

    [Theory]
    [InlineData("x'; Get-Date; ('", "1")]
    [InlineData("*EEE", "1; Get-Date")]
    [InlineData("*EEE", "$(Get-Date)")]
    public void ApplyNetAdapterFix_UnsafeKeywordOrValue_RunsNothing(string keyword, string value)
    {
        var (engine, runner) = Create();

        var (result, applied) = engine.ApplyNetAdapterFix(Fix(keyword, value));

        Assert.False(result.Success);
        Assert.Null(applied);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("01")]
    public void RevertNetAdapterFix_ValidLedgerEntry_SetsExactlyThePreviousValue(string previousValue)
    {
        var (engine, runner) = Create();

        var result = engine.RevertNetAdapterFix(new AppliedDpcFix { FixId = "f", Target = "*EEE", PreviousValue = previousValue });

        Assert.True(result.Success);
        AssertConstantScript(Assert.Single(runner.Calls), SetScript, "*EEE", previousValue);
    }

    [Theory]
    [InlineData("x'; Get-Date; ('", "1")]
    [InlineData("*EEE", "1'; Get-Date; '")]
    [InlineData("*EEE", "abc")]
    public void RevertNetAdapterFix_TamperedLedgerEntry_RunsNothing(string target, string previousValue)
    {
        var (engine, runner) = Create();

        var result = engine.RevertNetAdapterFix(new AppliedDpcFix { FixId = "f", Target = target, PreviousValue = previousValue });

        Assert.False(result.Success);
        Assert.Empty(runner.Calls);
    }
}
