using System;
using System.Linq;
using GameShift.Core.Profiles;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Profiles;

/// <summary>
/// Tests for <see cref="FirewallRuleAction"/> through its process-start seam, so no PowerShell or
/// firewall call runs. Every call must be the constant script with the System32 NetSecurity module,
/// and the rule name, direction and program must travel only in the child's environment.
/// </summary>
public class FirewallRuleActionTests
{
    private const string ExistsScript = "Get-NetFirewallRule -DisplayName $env:GS_ARG0 -ErrorAction SilentlyContinue";
    private const string CreateScript =
        "New-NetFirewallRule -DisplayName $env:GS_ARG0 -Direction $env:GS_ARG1 -Action Allow " +
        "-Program $env:GS_ARG2 -Profile Any -Enabled True";
    private const string RemoveScript = "Remove-NetFirewallRule -DisplayName $env:GS_ARG0";

    private const string HostileName = "GameShift_x'; Get-Date; \u2019$(Get-Date)`\n\"";
    private const string HostileProgram = "C:\\Games\\it's\\a\u2018b;$(Get-Date)`\n\\game.exe";

    private static (FirewallRuleAction Action, FakePowerShellRunner Runner) Create(
        string ruleName = HostileName, string program = HostileProgram, string direction = "Inbound")
    {
        var runner = new FakePowerShellRunner();
        var action = new FirewallRuleAction("Test Firewall Allow", ruleName, program, direction) { RunProcess = runner.Run };
        return (action, runner);
    }

    private static void AssertConstantScript(FakePowerShellRunner.Call call, string script, params string[] values)
    {
        Assert.Equal(script, call.Script);
        Assert.Equal(FakePowerShellRunner.ExpectedArguments(script), call.Arguments);
        Assert.Equal(FakePowerShellRunner.ManifestPath("NetSecurity"), call.ModuleManifest, ignoreCase: true);
        Assert.Equal(values, call.Values);
    }

    [Theory]
    [InlineData("Inbound")]
    [InlineData("Outbound")]
    public void ApplyAndRevert_HostileValues_CommandLineIsConstantAndValuesTravelInEnvironment(string direction)
    {
        var (action, runner) = Create(direction: direction);

        action.Apply(new SystemStateSnapshot());
        action.Revert(new SystemStateSnapshot());

        Assert.Equal(3, runner.Calls.Count);
        AssertConstantScript(runner.Calls[0], ExistsScript, HostileName);
        AssertConstantScript(runner.Calls[1], CreateScript, HostileName, direction, HostileProgram);
        AssertConstantScript(runner.Calls[2], RemoveScript, HostileName);
        Assert.Null(action.GetCrashRevertRecord());
    }

    [Fact]
    public void Apply_RuleAlreadyExists_CreatesNothingAndRevertLeavesItAlone()
    {
        var (action, runner) = Create();
        runner.Respond = call => FakePowerShellRunner.Exit(0, call.Script == ExistsScript ? "Name : existing\r\n" : "");

        action.Apply(new SystemStateSnapshot());
        action.Revert(new SystemStateSnapshot());

        AssertConstantScript(Assert.Single(runner.Calls), ExistsScript, HostileName);
        Assert.Null(action.GetCrashRevertRecord());
    }

    [Fact]
    public void Apply_CreateFails_RecordsNothingToRevert()
    {
        var (action, runner) = Create();
        runner.Respond = call => FakePowerShellRunner.Exit(call.Script == CreateScript ? 1 : 0);

        action.Apply(new SystemStateSnapshot());
        action.Revert(new SystemStateSnapshot());

        Assert.Equal(2, runner.Calls.Count);
        Assert.Null(action.GetCrashRevertRecord());
    }

    [Fact]
    public void Apply_CreateTimesOut_TreatsTheRuleAsCreated()
    {
        var (action, runner) = Create();
        runner.Respond = call => call.Script == CreateScript
            ? new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: true, Exited: false)
            : FakePowerShellRunner.Exit(0);

        action.Apply(new SystemStateSnapshot());

        var record = action.GetCrashRevertRecord();
        Assert.NotNull(record);
        Assert.Equal("firewall", record!.Kind);
    }

    [Fact]
    public void Revert_RemovalFails_KeepsTheCrashRecord()
    {
        var (action, runner) = Create();
        action.Apply(new SystemStateSnapshot());
        runner.Respond = call => FakePowerShellRunner.Exit(call.Script == RemoveScript ? 1 : 0);

        action.Revert(new SystemStateSnapshot());

        Assert.NotNull(action.GetCrashRevertRecord());
    }

    [Fact]
    public void RemoveLeftovers_HostileValues_PassesEachRuleAsThreeEnvironmentValuesInOrder()
    {
        var hostile = new FirewallRuleAction("a", HostileName, HostileProgram, "Outbound");
        var plain = new FirewallRuleAction("b", "GameShift_Plain_In", @"C:\Games\plain.exe");
        var runner = new FakePowerShellRunner();

        FirewallRuleAction.RemoveLeftovers(new[] { hostile, plain }, runner.Run);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(FakePowerShellRunner.ExpectedArguments(call.Script), call.Arguments);
        Assert.Equal(FakePowerShellRunner.ManifestPath("NetSecurity"), call.ModuleManifest, ignoreCase: true);
        Assert.Equal(
            new[] { HostileName, "Outbound", HostileProgram, "GameShift_Plain_In", "Inbound", @"C:\Games\plain.exe" },
            call.Values);
        Assert.DoesNotContain(call.Values, v => call.Script.Contains(v, StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_PresetRules_ScriptDoesNotDependOnTheRules()
    {
        var runner = new FakePowerShellRunner();

        FirewallRuleAction.RemoveLeftovers(CompetitivePresets.GetFirewallRules(), runner.Run);
        FirewallRuleAction.RemoveLeftovers(new[] { new FirewallRuleAction("x", "Other", @"C:\x.exe") }, runner.Run);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(runner.Calls[0].Arguments, runner.Calls[1].Arguments);
        Assert.Equal(CompetitivePresets.GetFirewallRules().Count * 3, runner.Calls[0].Values.Count);
    }

    [Fact]
    public void RemoveLeftovers_ReturnsTheRemovedRuleNamesFromOutput()
    {
        var runner = new FakePowerShellRunner
        {
            Respond = _ => new ProcessRunResult(1, "GameShift_OW2_In\r\nGameShift_LoL_In\r\n", "Could not remove x", TimedOut: false, Exited: true),
        };

        var removed = FirewallRuleAction.RemoveLeftovers(CompetitivePresets.GetFirewallRules(), runner.Run);

        Assert.Equal(new[] { "GameShift_OW2_In", "GameShift_LoL_In" }, removed);
    }
}
