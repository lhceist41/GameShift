using System.Diagnostics;
using GameShift.Core.GameProfiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Removes the Windows Firewall allow rules that earlier versions added for preset games.
/// GameShift no longer creates these rules; a rule left by a crash or an older version would
/// otherwise stay for good, and a later session would take it for the user's own.
/// </summary>
internal static class FirewallRuleAction
{
    private const string FirewallModule = "NetSecurity";

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

    /// <summary>
    /// Removes rules that match one of <paramref name="rules"/> on display name, program, direction
    /// and the Allow action, which is exactly how earlier versions created them. Returns the display
    /// names of the rules removed.
    /// </summary>
    internal static IReadOnlyList<string> RemoveLeftovers(IEnumerable<FirewallLeftoverRule> rules) =>
        RemoveLeftovers(rules, ParameterizedPowerShell.Run);

    /// <param name="rules">The rules whose leftovers to remove.</param>
    /// <param name="runProcess">Process-start seam; tests pass a fake so no PowerShell or firewall call runs.</param>
    internal static IReadOnlyList<string> RemoveLeftovers(
        IEnumerable<FirewallLeftoverRule> rules, Func<ProcessStartInfo, int, ProcessRunResult> runProcess)
    {
        var values = rules.SelectMany(rule => new[] { rule.RuleName, rule.Direction, rule.Program }).ToArray();
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
}
