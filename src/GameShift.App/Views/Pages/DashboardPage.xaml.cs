using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            (DataContext as DashboardViewModel)?.StartTimers();
            return;
        }

        DataContext = new DashboardViewModel(
            App.Orchestrator!,
            App.Engine!,
            App.Detector!,
            App.Optimizations!,
            App.VbsToggle,
            App.DpcMon,
            App.PerfMon,
            App.PingMon,
            App.SessionStore,
            App.SessionTrk);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Stop event subscriptions when the page is not visible
        (DataContext as DashboardViewModel)?.StopTimers();
    }

    private void OnDismissVbsClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.DismissVbsBanner();
    }

    private void OnDismissDpcSpikeClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.DismissDpcSpikeAlert();
    }

    private void OnDisableVbsClicked(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as DashboardViewModel;
        if (vm == null) return;

        var result = MessageBox.Show(
            "This will disable Memory Integrity (VBS/HVCI) and schedule a reboot in 30 seconds.\n\nContinue?",
            "Disable Memory Integrity",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            if (vm.DisableVbsHvci())
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

    /// <summary>
    /// Handles clicking anywhere on an optimization row to toggle expand/collapse.
    /// Sender is the Border containing the DataContext for the row item.
    /// </summary>
    private void OnOptimizationRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ExpandableOptimizationItem item)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    /// <summary>
    /// Prevents the toggle click from propagating to the row click handler (expand/collapse).
    /// The PreviewMouseLeftButtonDown is marked Handled so the row's MouseLeftButtonUp
    /// does not fire when the user clicks the CheckBox toggle.
    /// </summary>
    private void OnTogglePreviewClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
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
        (DataContext as DashboardViewModel)?.RunDpcAnalysisAsync();
    }

    /// <summary>
    /// Handles the "Re-scan" button on the DPC troubleshooter results panel.
    /// </summary>
    private void OnRescanDpcClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.RunDpcAnalysisAsync();
    }

    /// <summary>
    /// Handles the Update Download button click — starts in-app download.
    /// Falls back to browser if no direct download URL is available.
    /// </summary>
    private void OnUpdateDownloadClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.DownloadAndApplyUpdateAsync();
    }

    /// <summary>
    /// Handles the Cancel button during update download.
    /// </summary>
    private void OnUpdateCancelClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DashboardViewModel)?.CancelDownload();
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
            (DataContext as DashboardViewModel)?.ApplyUpdateAndRestart();
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
}
