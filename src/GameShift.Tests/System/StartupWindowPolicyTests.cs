using GameShift.Core.System;
using Xunit;

namespace GameShift.Tests.SystemRunner;

public class StartupWindowPolicyTests
{
    [Fact]
    public void LogonStart_WithStartInTrayOn_StaysInTheTray()
    {
        Assert.True(StartupWindowPolicy.MayStartInTray(startedAtLogon: true, startInTray: true));
        Assert.False(Decide(startedAtLogon: true, startInTray: true));
    }

    [Fact]
    public void ManualStart_ShowsTheWindow_EvenWithStartInTrayOn()
    {
        Assert.False(StartupWindowPolicy.MayStartInTray(startedAtLogon: false, startInTray: true));
        Assert.True(Decide(startedAtLogon: false, startInTray: true));
    }

    [Fact]
    public void LogonStart_WithStartInTrayOff_ShowsTheWindow()
    {
        Assert.False(StartupWindowPolicy.MayStartInTray(startedAtLogon: true, startInTray: false));
        Assert.True(Decide(startedAtLogon: true, startInTray: false));
    }

    [Fact]
    public void TrayStart_WithoutATrayIcon_ShowsTheWindow()
    {
        Assert.True(Decide(startedAtLogon: true, startInTray: true, trayAvailable: false));
    }

    [Fact]
    public void TrayStart_WithARecoveryNoticeDue_ShowsTheWindow()
    {
        Assert.True(Decide(startedAtLogon: true, startInTray: true, recoveryNoticeDue: true));
    }

    [Fact]
    public void TrayStart_WithTheInstalledCopyNoticeDue_ShowsTheWindow()
    {
        Assert.True(Decide(startedAtLogon: true, startInTray: true, installNoticeDue: true));
    }

    [Fact]
    public void TrayStart_WithTheFirstRunWizardDue_ShowsTheWindow()
    {
        Assert.True(Decide(startedAtLogon: true, startInTray: true, wizardDue: true));
    }

    private static bool Decide(
        bool startedAtLogon, bool startInTray, bool trayAvailable = true, bool recoveryNoticeDue = false,
        bool installNoticeDue = false, bool wizardDue = false) =>
        StartupWindowPolicy.ShouldShowWindow(startedAtLogon, startInTray, trayAvailable, recoveryNoticeDue, installNoticeDue, wizardDue);
}
