namespace GameShift.Core.Presentation;

/// <summary>The button the user left the restart dialog with.</summary>
public enum RestartDialogButton
{
    None,
    Primary,
    Secondary,
}

public enum RestartChoice
{
    Cancel,
    ApplyAndRestartNow,
    ApplyRestartLater,
}

/// <summary>
/// The text of the one confirm dialog every restart or boot action goes through.
/// </summary>
/// <param name="Command">The single command the action runs, when there is one, shown as written.</param>
/// <param name="LaterText">The secondary button; null when the action schedules its own restart.</param>
public sealed record RestartPrompt(
    string Title,
    string WhatChanges,
    string? Command,
    string HowToUndo,
    string PrimaryText,
    string? LaterText)
{
    public const string CancelText = "Cancel";

    public bool AllowsLater => LaterText != null;

    /// <summary>
    /// What to do for the button pressed. Closing the dialog any other way cancels, and "later" is
    /// never the answer for an action that has no separate restart step.
    /// </summary>
    public RestartChoice Choose(RestartDialogButton button) => button switch
    {
        RestartDialogButton.Primary => RestartChoice.ApplyAndRestartNow,
        RestartDialogButton.Secondary when AllowsLater => RestartChoice.ApplyRestartLater,
        _ => RestartChoice.Cancel,
    };
}

/// <summary>The restart prompts of every action that needs one.</summary>
public static class RestartPrompts
{
    private const string ApplyAndRestartNow = "Apply and restart now";
    private const string ApplyRestartLater = "Apply, restart later";

    /// <param name="dependencyWarning">What stops working without the hypervisor (Hyper-V, WSL2, Docker), when any.</param>
    public static RestartPrompt TurnOffMemoryIntegrity(string? dependencyWarning)
    {
        var what = "GameShift turns off Memory Integrity (VBS/HVCI) and stops the Windows hypervisor from " +
                   "loading at startup. Windows restarts 30 seconds after you confirm.";
        if (!string.IsNullOrWhiteSpace(dependencyWarning))
            what += " " + dependencyWarning.Trim() +
                    " They stop working until you turn Memory Integrity back on and restart.";

        return new(
            "Turn off Memory Integrity",
            what,
            null,
            "Windows will not turn it back on for you. Turn it back on in Windows Security > Device security > " +
            "Core isolation, or with Restore All in GameShift's Settings.",
            ApplyAndRestartNow,
            null);
    }

    public static RestartPrompt TurnOnMemoryIntegrity() => new(
        "Turn Memory Integrity back on",
        "GameShift turns Memory Integrity (VBS/HVCI) back on and lets the Windows hypervisor load at startup " +
        "again. Windows restarts 30 seconds after you confirm. FACEIT Anti-Cheat and Riot Vanguard's " +
        "On-Demand mode require it.",
        null,
        "To turn it off again, use Windows Security > Device security > Core isolation.",
        ApplyAndRestartNow,
        null);

    public static RestartPrompt FixAmdTimer() => new(
        "Fix the AMD timer setting",
        "GameShift removes the platform-timer settings (useplatformtick, disabledynamictick) an older version " +
        "left in the boot configuration, so your AMD CPU uses its efficient per-core timer again. This takes " +
        "effect after a restart. Restart now restarts Windows 30 seconds after you confirm.",
        null,
        "This restores the Windows default, so there is nothing to undo.",
        ApplyAndRestartNow,
        ApplyRestartLater);

    public static RestartPrompt FinishLatencyFixes() => new(
        "Restart to finish the fixes",
        "Windows restarts 10 seconds after you confirm, so the fixes you applied take effect.",
        null,
        "To undo a fix, turn it off on this page or use Restore All in Settings, then restart again.",
        "Restart now",
        null);
}
