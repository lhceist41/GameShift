using System.Diagnostics;
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

    /// <param name="name">Display name, e.g. "Valorant Firewall Allow".</param>
    /// <param name="ruleName">Firewall rule display name, e.g. "GameShift_Valorant_In".</param>
    /// <param name="executablePath">Full path to the game executable for program filter.</param>
    /// <param name="direction">"Inbound" or "Outbound". Defaults to "Inbound".</param>
    public FirewallRuleAction(string name, string ruleName,
        string executablePath, string direction = "Inbound")
    {
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
            var psi = new ProcessStartInfo(
                "powershell",
                $"-Command \"New-NetFirewallRule -DisplayName '{_ruleName.Replace("'", "''")}' " +
                $"-Direction {_direction} -Action Allow " +
                $"-Program '{_executablePath.Replace("'", "''")}' -Profile Any -Enabled True\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();

            if (process?.ExitCode == 0)
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
                    process?.ExitCode, _ruleName);
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
            var psi = new ProcessStartInfo(
                "powershell",
                $"-Command \"Remove-NetFirewallRule -DisplayName '{_ruleName.Replace("'", "''")}'\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();

            Log.Information(
                "FirewallRuleAction: Removed firewall rule '{RuleName}'",
                _ruleName);

            _ruleCreated = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "FirewallRuleAction: Failed to remove firewall rule '{RuleName}'",
                _ruleName);
        }
    }

    /// <summary>
    /// Checks if a firewall rule with the given display name already exists.
    /// </summary>
    private bool RuleExists()
    {
        try
        {
            var psi = new ProcessStartInfo(
                "powershell",
                $"-Command \"Get-NetFirewallRule -DisplayName '{_ruleName}' -ErrorAction SilentlyContinue\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit();

            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }
}
