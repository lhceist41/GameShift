using System.Windows;
using System.Windows.Controls;
using GameShift.App.ViewModels;

namespace GameShift.App.Views.Pages;

/// <summary>
/// System Overview page: hardware info, Windows features, temperatures, top processes, startup apps.
/// ViewModel is created in the Loaded event.
/// </summary>
public partial class SystemPage : Page
{
    public SystemPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null) return;
        DataContext = new SystemViewModel();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as SystemViewModel)?.RefreshAsync();
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is StartupAppItem item)
        {
            (DataContext as SystemViewModel)?.ToggleStartupApp(item);
        }
    }
}
