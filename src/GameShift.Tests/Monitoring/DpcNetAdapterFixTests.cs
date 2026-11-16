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
    private const string TargetsScript =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
        "$ErrorActionPreference = 'Stop'\n" +
        "try {\n" +
        "    $targets = @(Get-NetAdapter -Physical | Get-NetAdapterAdvancedProperty -RegistryKeyword $env:GS_ARG0 -ErrorAction SilentlyContinue |\n" +
        "        Where-Object { $_.RegistryKeyword -eq $env:GS_ARG0 })\n" +
        "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n";
    private const string QueryScript =
        TargetsScript +
        "foreach ($t in $targets) { '=' + (@($t.RegistryValue) -join ',') }\n" +
        "exit 0";
    private const string SetScript =
        TargetsScript +
        "if ($targets.Count -eq 0) { [Console]::Error.WriteLine('No physical network adapter has this setting.'); exit 1 }\n" +
        "try {\n" +
        "    foreach ($t in $targets) { Set-NetAdapterAdvancedProperty -InputObject $t -RegistryValue $env:GS_ARG1 -ErrorAction Stop }\n" +
        "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n" +
        "exit 0";

    /// <summary>The query's answer for adapters with these current values, one "=value" line each.</summary>
    private static ProcessRunResult Adapters(params string[] values) =>
        FakePowerShellRunner.Exit(0, string.Concat(values.Select(v => "=" + v + "\r\n")));

    private static ProcessRunResult Failed(string stderr) =>
        new(1, string.Empty, stderr, TimedOut: false, Exited: true);

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
        runner.Respond = call => call.Script == QueryScript ? Adapters("3") : FakePowerShellRunner.Exit(0);

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
    public void ApplyNetAdapterFix_SetFailsAndPutBackWorks_ReportsFailureAndStoresNoRollback()
    {
        var (engine, runner) = Create();
        runner.Respond = call =>
            call.Script == QueryScript ? Adapters("1", "1")
            : call.Values[1] == "0" ? Failed("boom")
            : FakePowerShellRunner.Exit(0);

        var (result, applied) = engine.ApplyNetAdapterFix(Fix("*EEE", "0"));

        Assert.False(result.Success);
        Assert.False(result.ChangeLeftInPlace);
        Assert.Contains("earlier value was put back", result.Message);
        Assert.Contains("boom", result.Message);
        Assert.Null(applied);
        Assert.Equal(3, runner.Calls.Count);
        AssertConstantScript(runner.Calls[1], SetScript, "*EEE", "0");
        AssertConstantScript(runner.Calls[2], SetScript, "*EEE", "1");
    }

    [Fact]
    public void ApplyNetAdapterFix_SetAndPutBackFail_KeepsTheEntryForAnUndo()
    {
        var (engine, runner) = Create();
        runner.Respond = call => call.Script == QueryScript ? Adapters("1", "1") : Failed("boom");

        var (result, applied) = engine.ApplyNetAdapterFix(Fix("*EEE", "0"));

        // ApplyFix persists the entry when Success or ChangeLeftInPlace is set.
        Assert.False(result.Success);
        Assert.True(result.ChangeLeftInPlace);
        Assert.Contains("stays recorded", result.Message);
        Assert.Contains("boom", result.Message);
        Assert.NotNull(applied);
        Assert.Equal("SetNetAdapterProperty", applied!.ActionType);
        Assert.Equal("*EEE", applied.Target);
        Assert.Equal("1", applied.PreviousValue);
        Assert.Equal(3, runner.Calls.Count);
        AssertConstantScript(runner.Calls[1], SetScript, "*EEE", "0");
        AssertConstantScript(runner.Calls[2], SetScript, "*EEE", "1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n")]
    public void ApplyNetAdapterFix_NoAdapterHasTheSetting_ChangesNothing(string stdout)
    {
        var (engine, runner) = Create();
        runner.Respond = call => call.Script == QueryScript ? FakePowerShellRunner.Exit(0, stdout) : FakePowerShellRunner.Exit(0);

        var (result, applied) = engine.ApplyNetAdapterFix(Fix("*EEE", "0"));

        Assert.False(result.Success);
        Assert.False(result.ChangeLeftInPlace);
        Assert.Contains("No network adapter", result.Message);
        Assert.Null(applied);
        AssertConstantScript(Assert.Single(runner.Calls), QueryScript, "*EEE");
    }

    [Theory]
    [InlineData("1", "0")]
    [InlineData("1", "01")]
    [InlineData("3", "3", "2")]
    public void ApplyNetAdapterFix_AdaptersDiffer_ChangesNothing(params string[] values)
    {
        var (engine, runner) = Create();
        runner.Respond = call => call.Script == QueryScript ? Adapters(values) : FakePowerShellRunner.Exit(0);

        var (result, applied) = engine.ApplyNetAdapterFix(Fix("*EEE", "0"));

        Assert.False(result.Success);
        Assert.False(result.ChangeLeftInPlace);
        Assert.Contains("different values", result.Message);
        Assert.Null(applied);
        AssertConstantScript(Assert.Single(runner.Calls), QueryScript, "*EEE");
    }

    // Query results the current value cannot be read from: a failed or unfinished run, a line without
    // the value marker (exit 0 with only stderr reaches the engine as the error text), or an adapter
    // value a revert could not write back.
    public static TheoryData<ProcessRunResult> UnreadableQueryResults => new()
    {
        FakePowerShellRunner.Exit(1, "=3\r\n"),
        FakePowerShellRunner.Exit(0, "13\r\n"),
        Adapters("3", "abc"),
        Adapters("3,4"),
        Adapters("3", ""),
        new ProcessRunResult(0, string.Empty, "Get-NetAdapterAdvancedProperty : No matching keyword", TimedOut: false, Exited: true),
        new ProcessRunResult(0, "=3", string.Empty, TimedOut: true, Exited: false),
        new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: false, Exited: false),
    };

    [Theory]
    [MemberData(nameof(UnreadableQueryResults))]
    public void ApplyNetAdapterFix_CurrentValueUnreadable_ChangesNothingAndStoresNoRollback(ProcessRunResult query)
    {
        var (engine, runner) = Create();
        runner.Respond = call => call.Script == QueryScript ? query : FakePowerShellRunner.Exit(0);

        var (result, applied) = engine.ApplyNetAdapterFix(Fix("*EEE", "0"));

        Assert.False(result.Success);
        Assert.False(result.ChangeLeftInPlace);
        Assert.Contains("could not be read", result.Message);
        Assert.Null(applied);
        AssertConstantScript(Assert.Single(runner.Calls), QueryScript, "*EEE");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ApplyThenRevertNetAdapterFix_SharedValue_WritesItBack(int adapters)
    {
        var (engine, runner) = Create();
        runner.Respond = call => call.Script == QueryScript ? Adapters(Enumerable.Repeat("3", adapters).ToArray()) : FakePowerShellRunner.Exit(0);

        var (result, applied) = engine.ApplyNetAdapterFix(Fix("*EEE", "0"));
        Assert.True(result.Success);
        var revert = engine.RevertNetAdapterFix(applied!);

        Assert.True(revert.Success);
        Assert.Equal(3, runner.Calls.Count);
        AssertConstantScript(runner.Calls[1], SetScript, "*EEE", "0");
        AssertConstantScript(runner.Calls[2], SetScript, "*EEE", "3");
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

    [Fact]
    public void RevertNetAdapterFix_NoAdapterHasTheSetting_ReportsFailure()
    {
        var (engine, runner) = Create();
        runner.Respond = _ => Failed("No physical network adapter has this setting.");

        var result = engine.RevertNetAdapterFix(new AppliedDpcFix { FixId = "f", Target = "*EEE", PreviousValue = "1" });

        Assert.False(result.Success);
        Assert.Contains("No physical network adapter", result.Message);
        AssertConstantScript(Assert.Single(runner.Calls), SetScript, "*EEE", "1");
    }
}
