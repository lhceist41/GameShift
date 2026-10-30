using System.Diagnostics;

namespace GameShift.Core.System;

/// <summary>
/// Reads and sets the priority class of other processes by PID. A seam so the priority writers
/// can be tested without touching real processes.
/// </summary>
internal interface IProcessPriorityAccess
{
    /// <summary>PIDs of the running processes with this name (without extension).</summary>
    IReadOnlyList<int> FindByName(string processName);

    /// <summary>Throws <see cref="ArgumentException"/> when no process has this PID.</summary>
    string GetName(int pid);

    ProcessPriorityClass GetPriority(int pid);

    void SetPriority(int pid, ProcessPriorityClass priority);
}

internal sealed class SystemProcessPriorityAccess : IProcessPriorityAccess
{
    public static readonly SystemProcessPriorityAccess Instance = new();

    private SystemProcessPriorityAccess() { }

    public IReadOnlyList<int> FindByName(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        var pids = new int[processes.Length];
        for (int i = 0; i < processes.Length; i++)
        {
            pids[i] = processes[i].Id;
            processes[i].Dispose();
        }
        return pids;
    }

    public string GetName(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.ProcessName;
    }

    public ProcessPriorityClass GetPriority(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.PriorityClass;
    }

    public void SetPriority(int pid, ProcessPriorityClass priority)
    {
        using var process = Process.GetProcessById(pid);
        process.PriorityClass = priority;
    }
}

internal static class ProcessPriorityAccessExtensions
{
    /// <summary>
    /// Restores a recorded priority only while <paramref name="pid"/> still belongs to
    /// <paramref name="processName"/>: a PID reused by another process during the session is left
    /// alone. Returns false when skipped; throws <see cref="ArgumentException"/> when the PID is gone.
    /// </summary>
    public static bool RestoreIfSameProcess(
        this IProcessPriorityAccess access, int pid, string processName, ProcessPriorityClass priority)
    {
        if (!string.Equals(access.GetName(pid), processName, StringComparison.OrdinalIgnoreCase))
            return false;

        access.SetPriority(pid, priority);
        return true;
    }
}
