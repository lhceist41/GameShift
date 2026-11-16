using GameShift.Core.System;
using GameShift.Core.SystemTweaks.Tweaks;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.SystemTweaks;

/// <summary>
/// Tests for <see cref="DisableMemoryCompression"/> through its PowerShell seam, so no PowerShell or
/// memory manager change runs. Each call is a constant script with the System32 MMAgent module and no
/// values; installed RAM comes from the constructor so the result does not depend on the test machine.
/// </summary>
public class DisableMemoryCompressionTests
{
    private const string QueryScript =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n(Get-MMAgent).MemoryCompression";
    private const string DisableScript = "Disable-MMAgent -MemoryCompression";
    private const string EnableScript = "Enable-MMAgent -MemoryCompression";

    private static (DisableMemoryCompression Tweak, FakePowerShellRunner Runner) Create(double ramGb = 64)
    {
        var runner = new FakePowerShellRunner();
        return (new DisableMemoryCompression(ramGb) { RunProcess = runner.Run }, runner);
    }

    private static void AssertConstantScript(FakePowerShellRunner.Call call, string script)
    {
        Assert.Equal(script, call.Script);
        Assert.Equal(FakePowerShellRunner.ExpectedArguments(script), call.Arguments);
        Assert.Equal(FakePowerShellRunner.ManifestPath("MMAgent"), call.ModuleManifest, ignoreCase: true);
        Assert.Empty(call.Values);
    }

    [Fact]
    public void Apply_CompressionEnabled_QueriesThenDisablesAndRecordsIt()
    {
        var (tweak, runner) = Create();
        runner.Respond = call => FakePowerShellRunner.Exit(0, call.Script == QueryScript ? "True\r\n" : "");

        var original = tweak.Apply();

        Assert.Equal(2, runner.Calls.Count);
        AssertConstantScript(runner.Calls[0], QueryScript);
        AssertConstantScript(runner.Calls[1], DisableScript);
        Assert.Equal("{\"WasEnabled\":true}", original);
    }

    // Query output by name: only an explicit False (any case) reads as disabled; empty output, error
    // text that reaches the caller on stderr, or anything else is unknown and counts as enabled.
    private static ProcessRunResult QueryResult(string kind) => kind switch
    {
        "true" => FakePowerShellRunner.Exit(0, "True\r\n"),
        "false" => FakePowerShellRunner.Exit(0, "False\r\n"),
        "false-lower" => FakePowerShellRunner.Exit(0, "false"),
        "empty" => FakePowerShellRunner.Exit(0, ""),
        "stderr-only" => new ProcessRunResult(0, string.Empty, "Get-MMAgent : Access is denied.", TimedOut: false, Exited: true),
        "other" => FakePowerShellRunner.Exit(0, "0\r\n"),
        "failed" => FakePowerShellRunner.Exit(1, "False\r\n"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("false-lower", false)]
    [InlineData("empty", true)]
    [InlineData("stderr-only", true)]
    [InlineData("other", true)]
    [InlineData("failed", true)]
    public void Apply_RecordsEnabledUnlessTheQuerySaysFalse(string query, bool wasEnabled)
    {
        var (tweak, runner) = Create();
        runner.Respond = call => call.Script == QueryScript ? QueryResult(query) : FakePowerShellRunner.Exit(0);

        var original = tweak.Apply();

        Assert.Equal(2, runner.Calls.Count);
        AssertConstantScript(runner.Calls[1], DisableScript);
        Assert.Equal(wasEnabled ? "{\"WasEnabled\":true}" : "{\"WasEnabled\":false}", original);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("stderr-only")]
    public void ApplyThenRevert_UnreadableQuery_ReEnablesCompression(string query)
    {
        var (tweak, runner) = Create();
        runner.Respond = call => call.Script == QueryScript ? QueryResult(query) : FakePowerShellRunner.Exit(0);

        var original = tweak.Apply();
        Assert.True(tweak.Revert(original));

        Assert.Equal([QueryScript, DisableScript, EnableScript], runner.Calls.Select(c => c.Script));
    }

    [Fact]
    public void ApplyThenRevert_QuerySaysFalse_LeavesCompressionAsItWas()
    {
        var (tweak, runner) = Create();
        runner.Respond = call => call.Script == QueryScript ? QueryResult("false") : FakePowerShellRunner.Exit(0);

        var original = tweak.Apply();
        Assert.True(tweak.Revert(original));

        Assert.Equal([QueryScript, DisableScript], runner.Calls.Select(c => c.Script));
    }

    [Fact]
    public void Apply_DisableFails_Throws()
    {
        var (tweak, runner) = Create();
        runner.Respond = call => FakePowerShellRunner.Exit(call.Script == DisableScript ? 1 : 0, "True");

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());
    }

    [Fact]
    public void Apply_LessThan32Gb_ThrowsBeforeAnyPowerShell()
    {
        var (tweak, runner) = Create(ramGb: 16);

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Revert_WasEnabled_ReEnables()
    {
        var (tweak, runner) = Create();

        Assert.True(tweak.Revert("{\"WasEnabled\":true}"));

        AssertConstantScript(Assert.Single(runner.Calls), EnableScript);
    }

    [Fact]
    public void Revert_WasAlreadyDisabled_RunsNothing()
    {
        var (tweak, runner) = Create();

        Assert.True(tweak.Revert("{\"WasEnabled\":false}"));

        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(0, "False\r\n", true)]
    [InlineData(0, "True\r\n", false)]
    [InlineData(1, "False\r\n", false)]
    [InlineData(0, "", false)]
    [InlineData(0, "0\r\n", false)]
    public void DetectIsApplied_ReadsTheQueryResult(int exitCode, string stdout, bool expected)
    {
        var (tweak, runner) = Create();
        runner.Respond = _ => FakePowerShellRunner.Exit(exitCode, stdout);

        Assert.Equal(expected, tweak.DetectIsApplied());
        AssertConstantScript(Assert.Single(runner.Calls), QueryScript);
    }

    [Fact]
    public void DetectIsApplied_StderrOnly_IsNotApplied()
    {
        var (tweak, runner) = Create();
        runner.Respond = _ => QueryResult("stderr-only");

        Assert.False(tweak.DetectIsApplied());
    }
}
