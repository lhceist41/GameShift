using System.Diagnostics;

namespace GameShift.Core.System;

/// <summary>
/// Compares <see cref="ProcessPriorityClass"/> values by scheduling level; the enum's numeric
/// values are Win32 flags and do not sort that way.
/// </summary>
internal static class ProcessPriorityOrder
{
    internal static int Rank(ProcessPriorityClass priority) => priority switch
    {
        ProcessPriorityClass.Idle => 0,
        ProcessPriorityClass.BelowNormal => 1,
        ProcessPriorityClass.Normal => 2,
        ProcessPriorityClass.AboveNormal => 3,
        ProcessPriorityClass.High => 4,
        ProcessPriorityClass.RealTime => 5,
        _ => 2,
    };

    /// <summary>
    /// True when a writer moving a process to <paramref name="target"/> must leave it alone: it is
    /// already there, or the target is a demotion and the process already sits at or below it.
    /// Another writer (or the user) lowered it; recording that as the "original" would make this
    /// writer's restore put the lowered value back after the other writer restored it.
    /// </summary>
    internal static bool NeedsNoChange(ProcessPriorityClass current, ProcessPriorityClass target)
    {
        if (current == target)
            return true;

        return Rank(target) < Rank(ProcessPriorityClass.Normal) && Rank(current) < Rank(target);
    }
}
