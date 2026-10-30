using System.Diagnostics;
using GameShift.Core.System;

namespace GameShift.Tests.TestHelpers;

/// <summary>
/// In-memory process table for the priority writers. Records every write GameShift makes;
/// <see cref="SetByOtherWriter"/> changes a priority without recording it.
/// </summary>
internal sealed class FakeProcessPriorities : IProcessPriorityAccess
{
    private readonly Dictionary<int, (string Name, ProcessPriorityClass Priority)> _processes = new();

    public List<(int Pid, ProcessPriorityClass Priority)> Writes { get; } = new();

    public Exception? GetNameFailure { get; set; }

    public Exception? GetPriorityFailure { get; set; }

    public void Add(int pid, string name, ProcessPriorityClass priority) =>
        _processes[pid] = (name, priority);

    public ProcessPriorityClass PriorityOf(int pid) => _processes[pid].Priority;

    public void SetByOtherWriter(int pid, ProcessPriorityClass priority) =>
        _processes[pid] = (_processes[pid].Name, priority);

    public IReadOnlyList<int> FindByName(string processName) =>
        _processes.Where(p => string.Equals(p.Value.Name, processName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .ToList();

    public string GetName(int pid)
    {
        if (GetNameFailure != null)
            throw GetNameFailure;
        return Lookup(pid).Name;
    }

    public ProcessPriorityClass GetPriority(int pid)
    {
        if (GetPriorityFailure != null)
            throw GetPriorityFailure;
        return Lookup(pid).Priority;
    }

    public void SetPriority(int pid, ProcessPriorityClass priority)
    {
        var process = Lookup(pid);
        _processes[pid] = (process.Name, priority);
        Writes.Add((pid, priority));
    }

    private (string Name, ProcessPriorityClass Priority) Lookup(int pid) =>
        _processes.TryGetValue(pid, out var process)
            ? process
            : throw new ArgumentException($"Process with an Id of {pid} is not running.");
}
