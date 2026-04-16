using System.Windows;
using System.Windows.Controls;
using GameShift.App.ViewModels;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Optimizations page: grouped list of all 11 optimizations with live status indicators.
/// ViewModel is created in the Loaded handler (NavigationView requires parameterless constructors).
/// </summary>
public partial class OptimizationsPage : Page
{
    public OptimizationsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null) return;

        DataContext = new OptimizationsViewModel(
            App.Services.Optimizations!,
            App.Services.Engine!);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        (DataContext as OptimizationsViewModel)?.Cleanup();
    }
}
