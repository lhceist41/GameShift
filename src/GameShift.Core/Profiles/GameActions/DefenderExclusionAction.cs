using System.Diagnostics;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Game-specific action that adds/removes Windows Defender exclusion paths via PowerShell.
/// Apply adds exclusions, Revert removes them.
/// Paths are immutable after construction — set once for the game's install directory.
/// </summary>
public class DefenderExclusionAction : GameAction
{
    private readonly string _name;
    private readonly string[] _exclusionPaths;

    /// <param name="name">Display name, e.g. "Valorant Defender Exclusions".</param>
    /// <param name="exclusionPaths">Paths to add/remove from Windows Defender exclusions.</param>
    public DefenderExclusionAction(string name, string[] exclusionPaths)
    {
        _name = name;
        _exclusionPaths = exclusionPaths;
    }

    /// <inheritdoc/>
    public override string Name => _name;

    /// <inheritdoc/>
    public override void Apply(SystemStateSnapshot snapshot)
    {
        foreach (var path in _exclusionPaths)
        {
            try
            {
                var psi = new ProcessStartInfo(
                    "powershell",
                    $"-Command \"Add-MpPreference -ExclusionPath '{path.Replace("'", "''")}'\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(psi);
                if (process != null && !process.WaitForExit(15_000))
                {
                    Log.Warning("DefenderExclusionAction: PowerShell timed out adding exclusion for {Path}, killing process", path);
                    try { process.Kill(); } catch { }
                }
                Log.Information("DefenderExclusionAction: Added exclusion path {Path}", path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DefenderExclusionAction: Failed to add exclusion path {Path}", path);
            }
        }
    }

    /// <inheritdoc/>
    public override void Revert(SystemStateSnapshot snapshot)
    {
        foreach (var path in _exclusionPaths)
        {
            try
            {
                var psi = new ProcessStartInfo(
                    "powershell",
                    $"-Command \"Remove-MpPreference -ExclusionPath '{path.Replace("'", "''")}'\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(psi);
                if (process != null && !process.WaitForExit(15_000))
                {
                    Log.Warning("DefenderExclusionAction: PowerShell timed out removing exclusion for {Path}, killing process", path);
                    try { process.Kill(); } catch { }
                }
                Log.Information("DefenderExclusionAction: Removed exclusion path {Path}", path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DefenderExclusionAction: Failed to remove exclusion path {Path}", path);
            }
        }
    }
}
