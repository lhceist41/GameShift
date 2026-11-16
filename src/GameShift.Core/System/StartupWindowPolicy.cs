namespace GameShift.Core.System;

/// <summary>
/// Whether a start opens the main window or only the tray icon. Only a start from the logon task
/// with "Start in the tray" on stays in the tray; a start the user made always shows the window.
/// </summary>
public static class StartupWindowPolicy
{
    /// <summary>Known before the tray exists: whether the window may stay hidden for now.</summary>
    public static bool MayStartInTray(bool startedAtLogon, bool startInTray) => startedAtLogon && startInTray;

    /// <summary>
    /// Decided once the tray is set up. A start that may stay in the tray still shows the window when
    /// there is no tray icon to reach it from, or when a recovery notice, the notice that the installed copy
    /// was not used, or the first-run wizard needs it.
    /// </summary>
    public static bool ShouldShowWindow(
        bool startedAtLogon, bool startInTray, bool trayAvailable, bool recoveryNoticeDue, bool installNoticeDue, bool wizardDue) =>
        !MayStartInTray(startedAtLogon, startInTray) || !trayAvailable || recoveryNoticeDue || installNoticeDue || wizardDue;
}
