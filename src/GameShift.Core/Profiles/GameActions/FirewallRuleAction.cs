using System.Diagnostics;
using System.Text.Json;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Game-specific action that adds a Windows Firewall allow rule for a game executable.
/// Uses PowerShell New-NetFirewallRule / Remove-NetFirewallRule.
/// Apply creates the rule if it doesn't exist, Revert removes it.
/// </summary>
public class FirewallRuleAction : GameAction
{
    private readonly string _name;
    private readonly string _ruleName;
    private readonly string _executablePath;
    private readonly string _direction;
    private bool _ruleCreated;

    private const string FirewallModule = "NetSecurity";
    private const string RuleExistsScript = "Get-NetFirewallRule -DisplayName $env:GS_ARG0 -ErrorAction SilentlyContinue";
    private const string CreateRuleScript =
        "New-NetFirewallRule -DisplayName $env:GS_ARG0 -Direction $env:GS_ARG1 -Action Allow " +
        "-Program $env:GS_ARG2 -Profile Any -Enabled True";
    private const string RemoveRuleScript = "Remove-NetFirewallRule -DisplayName $env:GS_ARG0";

    // One rule per three values (display name, direction, program), read until the next name is absent.
    private const string RemoveLeftoversScript =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
        "for ($i = 0; $null -ne [Environment]::GetEnvironmentVariable(\"GS_ARG$i\"); $i += 3) {\n" +
        "    $name = [Environment]::GetEnvironmentVariable(\"GS_ARG$i\")\n" +
        "    $direction = [Environment]::GetEnvironmentVariable(\"GS_ARG$($i + 1)\")\n" +
        "    $program = [Environment]::GetEnvironmentVariable(\"GS_ARG$($i + 2)\")\n" +
        "    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Where-Object { $_.Direction -eq $direction" +
        " -and $_.Action -eq 'Allow' -and (($_ | Get-NetFirewallApplicationFilter).Program -eq $program) }" +
        " | ForEach-Object { $r = $_; try { $r | Remove-NetFirewallRule -ErrorAction Stop; $r.DisplayName }" +
        " catch { [Console]::Error.WriteLine('Could not remove ' + $r.DisplayName + ': ' + $_) } }\n" +
        "}";

    /// <summary>Process-start seam; tests replace it so no PowerShell or firewall call runs.</summary>
    internal Func<ProcessStartInfo, int, ProcessRunResult> RunProcess { get; set; } = ParameterizedPowerShell.Run;

    /// <param name="name">Display name, e.g. "Valorant Firewall Allow".</param>
    /// <param name="ruleName">Firewall rule display name, e.g. "GameShift_Valorant_In".</param>
    /// <param name="executablePath">Full path to the game executable for program filter.</param>
    /// <param name="direction">"Inbound" or "Outbound". Defaults to "Inbound".</param>
    public FirewallRuleAction(string name, string ruleName,
        string executablePath, string direction = "Inbound")
    {
        if (direction is not ("Inbound" or "Outbound"))
            throw new ArgumentException($"Invalid firewall direction: {direction}", nameof(direction));

        _name = name;
        _ruleName = ruleName;
        _executablePath = executablePath;
        _direction = direction;
    }

    /// <inheritdoc/>
    public override string Name => _name;

    /// <inheritdoc/>
    public override string Impact => $"Firewall {_direction} allow rule";

    /// <inheritdoc/>
    public override void Apply(SystemStateSnapshot snapshot)
    {
        try
        {
            // Check if rule already exists
            if (RuleExists())
            {
                Log.Information(
                    "FirewallRuleAction: Rule '{RuleName}' already exists, skipping creation",
                    _ruleName);
                return;
            }

            // Create the firewall rule
            var result = RunProcess(
                ParameterizedPowerShell.CreateStartInfo(FirewallModule, CreateRuleScript, _ruleName, _direction, _executablePath),
                15_000);

            if (result.TimedOut)
            {
                Log.Warning("FirewallRuleAction: PowerShell timed out creating rule '{RuleName}', killing process", _ruleName);

                // The cmdlet may have created the rule before powershell.exe was slow to exit. Mark it
                // created so Revert's idempotent removal cleans it up rather than orphaning the rule.
                _ruleCreated = true;
                return;
            }

            if (!result.Exited)
            {
                Log.Warning("FirewallRuleAction: failed to start PowerShell for rule '{RuleName}'", _ruleName);
                return;
            }

            if (result.ExitCode == 0)
            {
                _ruleCreated = true;
                Log.Information(
                    "FirewallRuleAction: Created {Direction} allow rule '{RuleName}' for {ExePath}",
                    _direction, _ruleName, _executablePath);
            }
            else
            {
                Log.Warning(
                    "FirewallRuleAction: PowerShell exited with code {ExitCode} for rule '{RuleName}'",
                    result.ExitCode, _ruleName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "FirewallRuleAction: Failed to create firewall rule '{RuleName}'",
                _ruleName);
        }
    }

    /// <inheritdoc/>
    public override void Revert(SystemStateSnapshot snapshot)
    {
        if (!_ruleCreated) return;

        try
        {
            var result = RunProcess(
                ParameterizedPowerShell.CreateStartInfo(FirewallModule, RemoveRuleScript, _ruleName), 15_000);

            if (result.TimedOut)
                Log.Warning("FirewallRuleAction: PowerShell timed out removing rule '{RuleName}', killing process", _ruleName);

            if (result.Exited && result.ExitCode == 0)
            {
                Log.Information("FirewallRuleAction: Removed firewall rule '{RuleName}'", _ruleName);
                _ruleCreated = false;
            }
            else
            {
                // Keep _ruleCreated = true so the failure stays visible instead of logging a false success.
                Log.Warning(
                    "FirewallRuleAction: Failed to remove rule '{RuleName}' (exit {ExitCode}); rule may persist",
                    _ruleName, result.Exited ? result.ExitCode : null);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "FirewallRuleAction: Failed to remove firewall rule '{RuleName}'",
                _ruleName);
        }
    }

    /// <inheritdoc/>
    public override GameActionRevertRecord? GetCrashRevertRecord()
    {
        if (!_ruleCreated) return null;
        return new GameActionRevertRecord("firewall", JsonSerializer.Serialize(new { ruleName = _ruleName }));
    }

    /// <summary>The rule's display name, e.g. "GameShift_Valorant_In".</summary>
    internal string RuleName => _ruleName;

    /// <summary>The program the rule allows.</summary>
    internal string Program => _executablePath;

    /// <summary>
    /// Removes rules left behind by an earlier GameShift version or a crash: rules that match one of
    /// <paramref name="rules"/> on display name, program, direction and the Allow action, which is
    /// exactly how <see cref="Apply"/> creates them. A later session would otherwise take such a rule
    /// for the user's own and never remove it. Returns the display names of the rules removed.
    /// </summary>
    internal static IReadOnlyList<string> RemoveLeftovers(IEnumerable<FirewallRuleAction> rules) =>
        RemoveLeftovers(rules, ParameterizedPowerShell.Run);

    /// <param name="rules">The preset rules whose leftovers to remove.</param>
    /// <param name="runProcess">Process-start seam; tests pass a fake so no PowerShell or firewall call runs.</param>
    internal static IReadOnlyList<string> RemoveLeftovers(
        IEnumerable<FirewallRuleAction> rules, Func<ProcessStartInfo, int, ProcessRunResult> runProcess)
    {
        var values = rules.SelectMany(rule => new[] { rule._ruleName, rule._direction, rule._executablePath }).ToArray();
        var result = runProcess(ParameterizedPowerShell.CreateStartInfo(FirewallModule, RemoveLeftoversScript, values), 30_000);

        // The exit code is not checked: a lookup that finds no rule sets it too, which is the normal case.
        if (!result.Exited || !string.IsNullOrWhiteSpace(result.StdErr))
        {
            Log.Warning(
                "FirewallRuleAction: Leftover rule cleanup did not finish cleanly (exited: {Exited}): {Error}",
                result.Exited, result.StdErr.Trim());
        }

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// Checks if a firewall rule with the given display name already exists.
    /// </summary>
    private bool RuleExists()
    {
        try
        {
            var result = RunProcess(
                ParameterizedPowerShell.CreateStartInfo(FirewallModule, RuleExistsScript, _ruleName), 15_000);

            // Rule exists iff the query produced output. A failed-to-start / timed-out / empty run
            // yields no output -> false, matching the prior behaviour.
            return !string.IsNullOrWhiteSpace(result.StdOut);
        }
        catch
        {
            return false;
        }
    }
}
