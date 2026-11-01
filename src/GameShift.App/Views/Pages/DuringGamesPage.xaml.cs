using System.Windows;
using System.Windows.Controls;
using GameShift.App.ViewModels;

namespace GameShift.App.Views.Pages;

/// <summary>
/// During games page: the session modules with their switches and badges.
/// The view model is created on Loaded (NavigationView requires parameterless constructors).
/// </summary>
public partial class DuringGamesPage : Page
{
    public DuringGamesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DuringGamesViewModel vm)
        {
            vm = new DuringGamesViewModel(
                App.Services.Optimizations!, App.Services.Engine!,
                App.Services.Orchestrator, App.Services.ProfileMgr);
            DataContext = vm;
        }
        vm.Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        (DataContext as DuringGamesViewModel)?.Cleanup();
        DataContext = null;
    }
}
