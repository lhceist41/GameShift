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
            "Core isolation, or with Restore All on GameShift's Lasting changes page.",
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

    /// <summary>
    /// Applying or undoing a system tweak that takes effect after a restart. The tweak does not
    /// restart Windows itself, so the restart is a separate step and "later" is offered.
    /// </summary>
    /// <param name="warning">What the change stops from working, when anything.</param>
    public static RestartPrompt ChangeSystemTweak(string tweakName, bool undo, string? warning)
    {
        var what = (undo
            ? $"GameShift sets {tweakName} back to the value it had before GameShift changed it."
            : $"GameShift applies {tweakName} as recommended for this PC.") +
            " This takes effect after a restart. Restart now restarts Windows 30 seconds after you confirm.";
        if (!string.IsNullOrWhiteSpace(warning))
            what += " " + warning.Trim();

        return new(
            undo ? "Undo a lasting change" : "Apply a lasting change",
            what,
            null,
            undo
                ? "To apply it again, use Apply on the Lasting changes page, then restart."
                : "Use Revert on the Lasting changes page or Restore All, then restart.",
            undo ? "Undo and restart now" : ApplyAndRestartNow,
            undo ? "Undo, restart later" : ApplyRestartLater);
    }

    /// <summary>The warning for turning HAGS off on a GPU that has Frame Generation; null otherwise.</summary>
    public static string? HagsFrameGenerationWarning(bool isFrameGenCapable, bool isHagsEnabled) =>
        isFrameGenCapable && isHagsEnabled
            ? "Your GPU supports Frame Generation (DLSS FG / AFMF), which requires HAGS to be enabled. " +
              "Disabling HAGS will prevent Frame Generation from working."
            : null;

    public static RestartPrompt FinishLatencyFixes() => new(
        "Restart to finish the fixes",
        "Windows restarts 10 seconds after you confirm, so the fixes you applied take effect.",
        null,
        "To undo a fix, turn it off on this page or use Restore All on the Lasting changes page, then restart again.",
        "Restart now",
        null);
}
