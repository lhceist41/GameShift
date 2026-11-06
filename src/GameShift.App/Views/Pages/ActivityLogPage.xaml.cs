using System.Windows;
using System.Windows.Controls;
using GameShift.App.ViewModels;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Activity page with two tabs: the activity feed with search and type filtering, sourced from
/// DashboardViewModel.AllActivities (shared static collection), and the log viewer for today's log file.
/// </summary>
public partial class ActivityLogPage : Page
{
    private LogViewerViewModel? _logViewModel;

    public ActivityLogPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLogRefresh();

        // Unloaded unsubscribes, so a page instance shown again has to subscribe again.
        if (DataContext is ActivityLogViewModel existing)
        {
            existing.Resume();
            return;
        }

        // Get activity entries from DashboardViewModel's static AllActivities collection
        var entries = DashboardViewModel.AllActivities;
        DataContext = new ActivityLogViewModel(entries);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        (DataContext as ActivityLogViewModel)?.Cleanup();
        _logViewModel?.StopAutoRefresh();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // The filter ComboBox inside the Activity tab raises the same routed event.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        UpdateLogRefresh();
    }

    /// <summary>Reads the log only while its tab is shown; the viewer polls the file every 3 seconds.</summary>
    private void UpdateLogRefresh()
    {
        if (!LogTab.IsSelected || !IsLoaded)
        {
            _logViewModel?.StopAutoRefresh();
            return;
        }

        if (_logViewModel == null)
        {
            _logViewModel = new LogViewerViewModel();
            LogPanel.DataContext = _logViewModel;
        }

        _logViewModel.StartAutoRefresh();
    }

    private void OnRefreshLogClicked(object sender, RoutedEventArgs e)
    {
        _logViewModel?.RefreshContent();
    }

    private void OnOpenLogFolderClicked(object sender, RoutedEventArgs e)
    {
        _logViewModel?.OpenLogFolder();
    }
}
