using System.Diagnostics;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Game-specific action that adds/removes Windows Defender exclusion paths via PowerShell.
/// Apply adds exclusions, Revert removes them.
/// Paths are immutable after construction - set once for the game's install directory.
/// </summary>
public class DefenderExclusionAction : GameAction
{
    private readonly string _name;
    private readonly string[] _exclusionPaths;

    // Paths this action actually created (were absent before Apply). Only these are removed on
    // Revert, so we never delete a user's pre-existing, self-managed Defender exclusions.
    private readonly List<string> _createdPaths = new();

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
        _createdPaths.Clear();

        // Read the current exclusion list once. If we can't read it, skip adding entirely - we
        // must never create an exclusion we can't prove we own, or Revert could later delete a
        // user-managed one (and an orphaned AV exclusion is a security risk).
        var existing = GetExistingExclusions();
        if (existing == null)
        {
            Log.Warning("DefenderExclusionAction: Could not read existing Defender exclusions; skipping to avoid clobbering user settings");
            return;
        }

        foreach (var path in _exclusionPaths)
        {
            if (existing.Contains(path))
            {
                Log.Information("DefenderExclusionAction: Exclusion {Path} already present (user-managed), leaving as-is", path);
                continue;
            }

            try
            {
                var psi = new ProcessStartInfo(
                    NativeInterop.SystemExePath("WindowsPowerShell\\v1.0\\powershell.exe"),
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
                _createdPaths.Add(path);
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
        // Only remove exclusions THIS action created - never the user's pre-existing ones.
        foreach (var path in _createdPaths)
        {
            try
            {
                var psi = new ProcessStartInfo(
                    NativeInterop.SystemExePath("WindowsPowerShell\\v1.0\\powershell.exe"),
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

        _createdPaths.Clear();
    }

    /// <summary>
    /// Reads the current Windows Defender exclusion paths via Get-MpPreference.
    /// Returns null if the list cannot be read (so callers can fail safe and not claim ownership).
    /// </summary>
    private static HashSet<string>? GetExistingExclusions()
    {
        try
        {
            var psi = new ProcessStartInfo(
                NativeInterop.SystemExePath("WindowsPowerShell\\v1.0\\powershell.exe"),
                "-Command \"(Get-MpPreference).ExclusionPath\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(); } catch { }
                return null;
            }
            if (process.ExitCode != 0) return null;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }
            return set;
        }
        catch
        {
            return null;
        }
    }
}
