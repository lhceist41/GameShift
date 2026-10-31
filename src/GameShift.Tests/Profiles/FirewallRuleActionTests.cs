using System;
using System.Linq;
using GameShift.Core.GameProfiles;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Profiles;

/// <summary>
/// Tests for <see cref="FirewallRuleAction.RemoveLeftovers"/> through its process-start seam, so no
/// PowerShell or firewall call runs. The call must be the constant script with the System32
/// NetSecurity module, and the rule name, direction and program must travel only in the child's
/// environment.
/// </summary>
public class FirewallRuleActionTests
{
    private const string HostileName = "GameShift_x'; Get-Date; \u2019$(Get-Date)`\n\"";
    private const string HostileProgram = "C:\\Games\\it's\\a\u2018b;$(Get-Date)`\n\\game.exe";

    [Fact]
    public void RemoveLeftovers_HostileValues_PassesEachRuleAsThreeEnvironmentValuesInOrder()
    {
        var hostile = new FirewallLeftoverRule(HostileName, "Outbound", HostileProgram);
        var plain = new FirewallLeftoverRule("GameShift_Plain_In", "Inbound", @"C:\Games\plain.exe");
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

        FirewallRuleAction.RemoveLeftovers(BuiltInProfiles.GetFirewallLeftovers(), runner.Run);
        FirewallRuleAction.RemoveLeftovers(new[] { new FirewallLeftoverRule("Other", "Inbound", @"C:\x.exe") }, runner.Run);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(runner.Calls[0].Arguments, runner.Calls[1].Arguments);
        Assert.Equal(BuiltInProfiles.GetFirewallLeftovers().Count * 3, runner.Calls[0].Values.Count);
    }

    [Fact]
    public void RemoveLeftovers_ReturnsTheRemovedRuleNamesFromOutput()
    {
        var runner = new FakePowerShellRunner
        {
            Respond = _ => new ProcessRunResult(1, "GameShift_OW2_In\r\nGameShift_LoL_In\r\n", "Could not remove x", TimedOut: false, Exited: true),
        };

        var removed = FirewallRuleAction.RemoveLeftovers(BuiltInProfiles.GetFirewallLeftovers(), runner.Run);

        Assert.Equal(new[] { "GameShift_OW2_In", "GameShift_LoL_In" }, removed);
    }
}
