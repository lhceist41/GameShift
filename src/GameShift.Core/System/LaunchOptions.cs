namespace GameShift.Core.System;

/// <summary>
/// What the command line asked for. Unknown arguments are ignored, so a task or shortcut made by
/// another version never stops the app from starting.
/// </summary>
public sealed record LaunchOptions(bool StartedAtLogon)
{
    public static LaunchOptions None { get; } = new(StartedAtLogon: false);

    public static LaunchOptions Parse(IEnumerable<string>? args)
    {
        var startedAtLogon = false;
        foreach (var arg in args ?? [])
        {
            if (string.Equals(arg, StartupManager.LogonArgument, StringComparison.OrdinalIgnoreCase))
                startedAtLogon = true;
        }

        return new LaunchOptions(startedAtLogon);
    }
}
