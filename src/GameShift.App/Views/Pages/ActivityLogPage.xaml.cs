using System.Windows;
using System.Windows.Controls;
using GameShift.App.ViewModels;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Full activity log page with search and type filtering.
/// Sources activity entries from DashboardViewModel.AllActivities (shared static collection).
/// Provides a searchable and filterable history of all system events.
/// </summary>
public partial class ActivityLogPage : Page
{
    public ActivityLogPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null) return;

        // Get activity entries from DashboardViewModel's static AllActivities collection
        var entries = DashboardViewModel.AllActivities;
        DataContext = new ActivityLogViewModel(entries);
    }
}
