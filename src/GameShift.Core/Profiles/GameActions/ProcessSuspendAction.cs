using System.Diagnostics;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Game-specific action that suspends a named process during gameplay and resumes it on revert.
/// Uses NtSuspendProcess/NtResumeProcess P/Invoke. Refuses to suspend anti-cheat processes.
/// Example use: suspend the Electron League client during gameplay to free CPU/memory.
/// </summary>
public class ProcessSuspendAction : GameAction
{
    /// <summary>
    /// Processes that must never be suspended — anti-cheat software and security agents.
    /// Case-insensitive comparison.
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
    private readonly List<int> _suspendedPids = new();

    /// <param name="name">Display name, e.g. "LoL Client Suspension".</param>
    /// <param name="processName">Process name to suspend (with or without .exe extension).</param>
    /// <param name="description">Human-readable description of why this process is suspended.</param>
    public ProcessSuspendAction(string name, string processName, string description)
    {
        _name = name;
        _processName = processName;
    }

    /// <inheritdoc/>
    public override string Name => _name;

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
                "ProcessSuspendAction: Refusing to suspend blocklisted process {ProcessName}",
                processNameWithExt);
            return;
        }

        var bareProcessName = Path.GetFileNameWithoutExtension(_processName);
        var processes = Process.GetProcessesByName(bareProcessName);

        foreach (var process in processes)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = NativeInterop.OpenProcess(
                    NativeInterop.PROCESS_SUSPEND_RESUME, false, process.Id);

                if (handle == IntPtr.Zero)
                {
                    Log.Warning(
                        "ProcessSuspendAction: Could not open process handle for PID {Pid} ({ProcessName})",
                        process.Id, _processName);
                    continue;
                }

                int status = NativeInterop.NtSuspendProcess(handle);
                if (status != 0)
                {
                    Log.Warning(
                        "ProcessSuspendAction: NtSuspendProcess returned {Status} for PID {Pid}",
                        status, process.Id);
                }
                else
                {
                    _suspendedPids.Add(process.Id);
                    Log.Information(
                        "ProcessSuspendAction: Suspended {ProcessName} (PID {Pid})",
                        _processName, process.Id);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "ProcessSuspendAction: Failed to suspend {ProcessName} (PID {Pid})",
                    _processName, process.Id);
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    NativeInterop.CloseHandle(handle);
            }
        }
    }

    /// <inheritdoc/>
    public override void Revert(SystemStateSnapshot snapshot)
    {
        foreach (var pid in _suspendedPids)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                // Verify process still exists before attempting resume
                try
                {
                    _ = Process.GetProcessById(pid);
                }
                catch (ArgumentException)
                {
                    Log.Debug(
                        "ProcessSuspendAction: PID {Pid} no longer exists, skipping resume",
                        pid);
                    continue;
                }

                handle = NativeInterop.OpenProcess(
                    NativeInterop.PROCESS_SUSPEND_RESUME, false, pid);

                if (handle == IntPtr.Zero)
                {
                    Log.Warning(
                        "ProcessSuspendAction: Could not open process handle for resume of PID {Pid}",
                        pid);
                    continue;
                }

                int status = NativeInterop.NtResumeProcess(handle);
                if (status != 0)
                {
                    Log.Warning(
                        "ProcessSuspendAction: NtResumeProcess returned {Status} for PID {Pid}",
                        status, pid);
                }
                else
                {
                    Log.Information(
                        "ProcessSuspendAction: Resumed {ProcessName} (PID {Pid})",
                        _processName, pid);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "ProcessSuspendAction: Failed to resume {ProcessName} (PID {Pid})",
                    _processName, pid);
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    NativeInterop.CloseHandle(handle);
            }
        }

        _suspendedPids.Clear();
    }
}
