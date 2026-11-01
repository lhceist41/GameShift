namespace GameShift.Core.System;

/// <summary>
/// What the command line asked for. Unknown arguments are ignored, so a task or shortcut made by
/// another version never stops the app from starting.
/// </summary>
/// <param name="RestoreTweaks">Undo a crashed session and every recorded lasting change, without any window.</param>
/// <param name="RemoveTasks">Delete GameShift's scheduled tasks, without any window.</param>
public sealed record LaunchOptions(bool StartedAtLogon, bool RestoreTweaks = false, bool RemoveTasks = false)
{
    public const string RestoreTweaksArgument = "--restore-tweaks";
    public const string RemoveTasksArgument = "--remove-tasks";

    public static LaunchOptions None { get; } = new(StartedAtLogon: false);

    /// <summary>The run does its command and exits; no window, tray icon or app instance is created.</summary>
    public bool IsHeadless => RestoreTweaks || RemoveTasks;

    public static LaunchOptions Parse(IEnumerable<string>? args)
    {
        bool startedAtLogon = false, restoreTweaks = false, removeTasks = false;
        foreach (var arg in args ?? [])
        {
            if (string.Equals(arg, StartupManager.LogonArgument, StringComparison.OrdinalIgnoreCase))
                startedAtLogon = true;
            else if (string.Equals(arg, RestoreTweaksArgument, StringComparison.OrdinalIgnoreCase))
                restoreTweaks = true;
            else if (string.Equals(arg, RemoveTasksArgument, StringComparison.OrdinalIgnoreCase))
                removeTasks = true;
        }

        return new LaunchOptions(startedAtLogon, restoreTweaks, removeTasks);
    }
}
