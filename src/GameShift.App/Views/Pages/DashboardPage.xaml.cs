using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using GameShift.App.ViewModels;
using GameShift.App.Views;

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null)
        {
            // Page navigated back — restart timers/events to resume live updates
            var existing = DataContext as DashboardViewModel;
            if (existing != null)
            {
                existing.Hero.PropertyChanged += OnHeroPropertyChanged;
                existing.StartTimers();
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

        vm.AdvancedModeChanged += advanced =>
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            mainWindow?.ApplyAdvancedMode(advanced);
        };

        vm.Hero.PropertyChanged += OnHeroPropertyChanged;

        DataContext = vm;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Stop event subscriptions when the page is not visible
        var vm = DataContext as DashboardViewModel;
        if (vm == null) return;

        vm.Hero.PropertyChanged -= OnHeroPropertyChanged;

        // When the MainWindow is actually closing (no tray to hide into), do a
        // full Cleanup so every subscription (SessionTracker, AllActivities,
        // sub-VMs, engine/detector events) is released — not just the live timers.
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

    private void OnDismissVbsClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Vbs.DismissVbsBanner();
    }

    private void OnDismissDpcSpikeClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Dpc.DismissDpcSpikeAlert();
    }

    private void OnDisableVbsClicked(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as DashboardViewModel;
        if (vm == null) return;

        // Block disable if anti-cheat requires VBS
        var blockingACs = GameShift.Core.Optimization.AntiCheatDetector.GetVbsRequiringAntiCheats();
        if (blockingACs.Count > 0)
        {
            var acNames = string.Join(", ", blockingACs.Select(ac => ac.DisplayName));
            MessageBox.Show(
                $"Cannot disable Memory Integrity — it is required by {acNames}.\n\n" +
                "Disabling it would cause VAN:RESTRICTION errors and prevent these games from launching.",
                "Blocked by Anti-Cheat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var result = MessageBox.Show(
            "This will disable Memory Integrity (VBS/HVCI) and schedule a reboot in 30 seconds.\n\nContinue?",
            "Disable Memory Integrity",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
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

    private void OnReEnableVbsClicked(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as DashboardViewModel;
        if (vm == null) return;

        var result = MessageBox.Show(
            "This will re-enable Memory Integrity (VBS/HVCI) and schedule a reboot in 30 seconds.\n\n" +
            "This is required for Riot Vanguard and FACEIT Anti-Cheat to function properly.\n\nContinue?",
            "Re-enable Memory Integrity",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
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
    /// Handles the Update Download button click — starts in-app download.
    /// Falls back to browser if no direct download URL is available.
    /// </summary>
    private void OnUpdateDownloadClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.Update.DownloadAndApplyUpdateAsync();
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
    /// Handles "View All" link click — navigates to the full ActivityLogPage.
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
