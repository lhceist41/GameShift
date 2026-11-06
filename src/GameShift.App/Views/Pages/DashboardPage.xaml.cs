using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
/// Dashboard page: live optimization status, active game count, optimization list,
/// DPC latency indicator, VBS/HVCI warning banner, expandable optimization rows,
/// and recent activity feed with View All navigation.
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
                existing.Hero.PropertyChanged += OnHeroPropertyChanged;
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

        vm.Hero.PropertyChanged += OnHeroPropertyChanged;

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

        vm.Hero.PropertyChanged -= OnHeroPropertyChanged;

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
    /// Handles clicking anywhere on an optimization row to toggle expand/collapse.
    /// Skips the toggle when the click originated from the CheckBox (the toggle
    /// has its own Checked/Unchecked handler and should not also expand/collapse).
    /// </summary>
    private void OnOptimizationRowClicked(object sender, MouseButtonEventArgs e)
    {
        // If the click came from the CheckBox or a child of a CheckBox, ignore it
        if (e.OriginalSource is DependencyObject source)
        {
            var parent = source;
            while (parent != null)
            {
                if (parent is CheckBox) return;
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
        }

        if (sender is FrameworkElement element && element.DataContext is ExpandableOptimizationItem item)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    /// <summary>
    /// Handles CheckBox Checked/Unchecked for the optimization enable/disable toggle.
    /// The TwoWay binding has already updated IsEnabled on the item.
    /// Invokes the OnToggled callback to persist the change to the default GameProfile.
    /// </summary>
    private void OnOptimizationToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ExpandableOptimizationItem item)
        {
            item.OnToggled?.Invoke(item);
        }
        // Stop bubbling so the row's click handler (expand/collapse) does not fire
        e.Handled = true;
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

    /// <summary>
    /// Handles "View All" link click - navigates to the full ActivityLogPage.
    /// </summary>
    private void OnViewAllActivityClicked(object sender, MouseButtonEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.NavigateTo(typeof(ActivityLogPage));
    }

    private void OnOptimizationPreviewClicked(object sender, MouseButtonEventArgs e)
    {
        PreviewList.Visibility = PreviewList.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private Storyboard? _heroAnimationStoryboard;

    private void OnHeroPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HeroOptimizeViewModel.IsApplyingHero)) return;
        var hero = sender as HeroOptimizeViewModel;
        if (hero == null) return;

        if (hero.IsApplyingHero)
            StartHeroAnimation();
        else
            StopHeroAnimation();
    }

    private void StartHeroAnimation()
    {
        HeroSpinner.Visibility = Visibility.Visible;

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        // Spin the arrow icon
        var spin = new DoubleAnimation(0, 360, new Duration(System.TimeSpan.FromSeconds(1)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(spin, HeroSpinner);
        Storyboard.SetTargetProperty(spin, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        sb.Children.Add(spin);

        // Pulse the glow behind the button
        var glowIn = new DoubleAnimation(0, 0.4, new Duration(System.TimeSpan.FromSeconds(0.8)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(glowIn, HeroGlow);
        Storyboard.SetTargetProperty(glowIn, new PropertyPath("Opacity"));
        sb.Children.Add(glowIn);

        _heroAnimationStoryboard = sb;
        sb.Begin();
    }

    private void StopHeroAnimation()
    {
        _heroAnimationStoryboard?.Stop();
        _heroAnimationStoryboard = null;
        HeroSpinner.Visibility = Visibility.Collapsed;
        HeroGlow.Opacity = 0;
    }
}
