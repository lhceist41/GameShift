using System.Diagnostics;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Game-specific action that sets a named process to a specific priority class during gameplay.
/// Less aggressive than ProcessSuspendAction — lowers priority instead of freezing the process.
/// Example use: set LeagueClientUx to BelowNormal during gameplay to free CPU time.
/// Restores original priority on revert.
/// </summary>
public class ProcessPrioritySetAction : GameAction
{
    /// <summary>
    /// Processes that must never have priority changed — anti-cheat software and security agents.
    /// </summary>
    private static readonly HashSet<string> AntiCheatBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "vgc.exe",
        "vgtray.exe",
        "EasyAntiCheat.exe",
        "EasyAntiCheat_EOS.exe",
        "BEService.exe",
        "BattlEye.exe",
        "FACEITClient.exe"
    };

    private readonly string _name;
    private readonly string _processName;
    private readonly ProcessPriorityClass _targetPriority;
    private readonly Dictionary<int, ProcessPriorityClass> _originalPriorities = new();

    /// <param name="name">Display name, e.g. "LoL Client Priority Reduction".</param>
    /// <param name="processName">Process name (with or without .exe).</param>
    /// <param name="targetPriority">Target priority class to set during gameplay.</param>
    public ProcessPrioritySetAction(string name, string processName,
        ProcessPriorityClass targetPriority)
    {
        _name = name;
        _processName = processName;
        _targetPriority = targetPriority;
    }

    /// <inheritdoc/>
    public override string Name => _name;

    /// <inheritdoc/>
    public override string Impact => $"Set {_processName} priority to {_targetPriority}";

    /// <inheritdoc/>
    public override void Apply(SystemStateSnapshot snapshot)
    {
        // Ensure .exe suffix for blocklist check
        var processNameWithExt = _processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? _processName
            : _processName + ".exe";

        if (AntiCheatBlocklist.Contains(processNameWithExt))
        {
            Log.Warning(
                "ProcessPrioritySetAction: Refusing to modify priority of blocklisted process {ProcessName}",
                processNameWithExt);
            return;
        }

        var bareProcessName = Path.GetFileNameWithoutExtension(_processName);

        try
        {
            var processes = Process.GetProcessesByName(bareProcessName);

            foreach (var process in processes)
            {
                try
                {
                    // Record original priority before changing
                    _originalPriorities[process.Id] = process.PriorityClass;

                    process.PriorityClass = _targetPriority;
                    Log.Information(
                        "ProcessPrioritySetAction: Set {ProcessName} (PID {Pid}) priority to {Priority}",
                        _processName, process.Id, _targetPriority);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "ProcessPrioritySetAction: Failed to set priority for {ProcessName} (PID {Pid})",
                        _processName, process.Id);
                }
            }

            if (processes.Length == 0)
            {
                Log.Debug(
                    "ProcessPrioritySetAction: No running processes found for {ProcessName}",
                    _processName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "ProcessPrioritySetAction: Failed to enumerate processes for {ProcessName}",
                _processName);
        }
    }

    /// <inheritdoc/>
    public override void Revert(SystemStateSnapshot snapshot)
    {
        foreach (var (pid, originalPriority) in _originalPriorities)
        {
            try
            {
                // Verify process still exists
                var process = Process.GetProcessById(pid);
                process.PriorityClass = originalPriority;
                Log.Information(
                    "ProcessPrioritySetAction: Restored {ProcessName} (PID {Pid}) priority to {Priority}",
                    _processName, pid, originalPriority);
            }
            catch (ArgumentException)
            {
                Log.Debug(
                    "ProcessPrioritySetAction: PID {Pid} no longer exists, skipping restore",
                    pid);
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "ProcessPrioritySetAction: Failed to restore priority for {ProcessName} (PID {Pid})",
                    _processName, pid);
            }
        }

        _originalPriorities.Clear();
    }
}
