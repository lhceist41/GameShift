using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameShift.App.Services;
using GameShift.App.ViewModels;
using GameShift.App.Views;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using GameShift.Core.SystemTweaks;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Home: notices, the session status strip with "Optimize now", the live cards, this session's
/// changes and recent sessions.
/// ViewModel is created in the Loaded event using App static service properties.
/// </summary>
public partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private Window? _window;
    private bool _countingLastingChanges;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null)
        {
            // Page navigated back - restart timers/events to resume live updates
            var existing = DataContext as DashboardViewModel;
            if (existing != null)
            {
                existing.StartTimers();
                WatchWindowWidth(existing);
                RefreshLastingChangesCount(existing);
            }
            return;
        }

        var vm = new DashboardViewModel(
            App.Services.Orchestrator!,
            App.Services.Engine!,
            App.Services.Detector!,
            App.Services.Optimizations!,
            App.Services.VbsToggle,
            App.Services.DpcMon,
            App.Services.PerfMon,
            App.Services.PingMon,
            App.Services.SessionStore,
            App.Services.SessionTrk);

        DataContext = vm;
        WatchWindowWidth(vm);
        RefreshLastingChangesCount(vm);
    }

    /// <summary>The notices fold by the window's width, not the page's, like the navigation pane.</summary>
    private void WatchWindowWidth(DashboardViewModel vm)
    {
        if (_window != null) _window.SizeChanged -= OnWindowSizeChanged;
        _window = Window.GetWindow(this);
        if (_window == null) return;
        _window.SizeChanged += OnWindowSizeChanged;
        vm.SetWindowWidth(_window.ActualWidth);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.SetWindowWidth(e.NewSize.Width);
    }

    /// <summary>Counts the lasting changes for the Home notice. Listing only reads; it runs off the UI thread.</summary>
    private async void RefreshLastingChangesCount(DashboardViewModel vm)
    {
        var tweaks = App.Services.TweaksMgr;
        if (tweaks == null || _countingLastingChanges) return;

        _countingLastingChanges = true;
        try
        {
            var inventory = LastingChangesInventory.CreateForThisMachine(
                tweaks, App.Services.DpcFix ?? new DpcFixEngine(), App.Services.VbsToggle ?? new VbsHvciToggle());
            var list = await Task.Run(inventory.List);
            vm.SetLastingChanges(list.Items.Count, someRecordsUnreadable: list.Warnings.Count > 0);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[Dashboard] Could not count the lasting changes");
            vm.SetLastingChanges(0, someRecordsUnreadable: true);
        }
        finally
        {
            _countingLastingChanges = false;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Stop event subscriptions when the page is not visible
        var vm = DataContext as DashboardViewModel;
        if (vm == null) return;

        if (_window != null)
        {
            _window.SizeChanged -= OnWindowSizeChanged;
            _window = null;
        }

        // When the MainWindow is actually closing (no tray to hide into), do a
        // full Cleanup so every subscription (SessionTracker, AllActivities,
        // sub-VMs, engine/detector events) is released - not just the live timers.
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow != null && mainWindow.IsClosingForReal)
        {
            vm.Cleanup();
        }
        else
        {
            vm.StopTimers();
        }
    }

    private void OnToggleNoticesClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.ToggleNotices();
    }

    private void OnReviewLastingChangesClicked(object sender, RoutedEventArgs e)
    {
        (Window.GetWindow(this) as MainWindow)?.NavigateTo(typeof(LastingChangesPage));
    }

    private void OnDismissVbsClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Vbs.DismissVbsBanner();
    }

    private void OnDismissDpcSpikeClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Dpc.DismissDpcSpikeAlert();
    }

    private async void OnDisableVbsClicked(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as DashboardViewModel;
        if (vm == null) return;

        // Block disable if anti-cheat requires VBS
        var blockingACs = GameShift.Core.Optimization.AntiCheatDetector.GetVbsRequiringAntiCheats();
        if (blockingACs.Count > 0)
        {
            var acNames = string.Join(", ", blockingACs.Select(ac => ac.DisplayName));
            MessageBox.Show(
                $"GameShift does not turn off Memory Integrity while {acNames} is installed.\n\n" +
                "FACEIT Anti-Cheat and Riot Vanguard's On-Demand mode require Memory Integrity, and GameShift " +
                "cannot tell which Vanguard mode you use. Without it these games may refuse to start.",
                "Blocked by Anti-Cheat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        // Warn if disabling the hypervisor will break Hyper-V / WSL2 / Docker (same check the
        // DPC Doctor BCD path uses). Informed consent, not a hard block - the user may not care.
        string? dependencyWarning = null;
        try
        {
            var deps = new GameShift.Core.SystemTweaks.KernelTuningManager().CheckHypervisorDependencies();
            if (deps.HasDependencies)
                dependencyWarning = deps.Summary;
        }
        catch { /* dependency check is best-effort */ }

        var choice = await RestartConfirm.AskAsync(this, RestartPrompts.TurnOffMemoryIntegrity(dependencyWarning));
        if (choice == RestartChoice.ApplyAndRestartNow)
        {
            if (vm.Vbs.DisableVbsHvci())
            {
                GameShift.Core.Optimization.VbsHvciToggle.ScheduleReboot();
            }
            else
            {
                MessageBox.Show("Failed to disable VBS/HVCI. Check logs for details.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void OnFixAmdTimerClicked(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as DashboardViewModel;
        if (vm == null) return;

        var choice = await RestartConfirm.AskAsync(this, RestartPrompts.FixAmdTimer());
        if (choice == RestartChoice.Cancel) return;

        if (vm.AmdTimer.Fix())
        {
            if (choice == RestartChoice.ApplyAndRestartNow)
                GameShift.Core.Optimization.VbsHvciToggle.ScheduleReboot("GameShift: restoring the AMD per-core timer.");
        }
        else
        {
            MessageBox.Show("Could not apply the fix. Check the logs for details.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDismissAmdTimerClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.AmdTimer.DismissBanner();
    }

    private async void OnReEnableVbsClicked(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as DashboardViewModel;
        if (vm == null) return;

        var choice = await RestartConfirm.AskAsync(this, RestartPrompts.TurnOnMemoryIntegrity());
        if (choice == RestartChoice.ApplyAndRestartNow)
        {
            if (vm.Vbs.ReEnableVbsHvci())
            {
                GameShift.Core.Optimization.VbsHvciToggle.ScheduleReboot();
            }
            else
            {
                MessageBox.Show("Failed to re-enable VBS/HVCI. Check logs for details.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// Handles the "Troubleshoot" button on the DPC spike banner.
    /// Runs DPC driver analysis and shows inline results.
    /// </summary>
    private void OnTroubleshootDpcClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Dpc.RunDpcAnalysisAsync();
    }

    /// <summary>
    /// Handles the "Re-scan" button on the DPC troubleshooter results panel.
    /// </summary>
    private void OnRescanDpcClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Dpc.RunDpcAnalysisAsync();
    }

    /// <summary>
    /// Handles the Update Download button click - starts in-app download.
    /// Falls back to browser if no direct download URL is available.
    /// </summary>
    private void OnUpdateDownloadClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Update.DownloadAndApplyUpdateAsync();
    }

    /// <summary>Opens the release page of the offered update (its URL was checked when the update was found).</summary>
    private void OnUpdateReleasePageClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Update.OpenUpdateUrl();
    }

    /// <summary>
    /// Handles the Cancel button during update download.
    /// </summary>
    private void OnUpdateCancelClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Update.CancelDownload();
    }

    /// <summary>
    /// Handles the "Restart Now" button after update download completes.
    /// </summary>
    private void OnUpdateRestartClicked(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "GameShift will close and restart with the new version.\n\nContinue?",
            "Apply Update",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            (DataContext as DashboardViewModel)?.Update.ApplyUpdateAndRestart();
        }
    }

    /// <summary>
    /// Handles "Open DPC Doctor" button on the DPC spike banner.
    /// Navigates to the full DPC Doctor diagnostic page.
    /// </summary>
    private void OnOpenDpcDoctorClicked(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(typeof(DpcDoctorPage));
    }

    private void OnOptimizationPreviewClicked(object sender, MouseButtonEventArgs e)
    {
        PreviewList.Visibility = PreviewList.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
