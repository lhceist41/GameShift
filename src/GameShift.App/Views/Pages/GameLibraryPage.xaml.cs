using System.Windows;
using System.Windows.Controls;
using GameShift.App.ViewModels;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Game Library page: lists detected games, supports add/remove operations.
/// Content migrated from GameLibraryWindow. ViewModel created on Loaded via App static properties.
/// </summary>
public partial class GameLibraryPage : Page
{
    public GameLibraryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null)
        {
            // Re-activate: refresh in case games changed while page was hidden
            (DataContext as GameLibraryViewModel)?.RefreshGames();
            return;
        }

        var vm = new GameLibraryViewModel(App.Services.Orchestrator!);
        DataContext = vm;
        vm.RefreshGames();
    }

    private void OnAddGameClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as GameLibraryViewModel)?.AddGame();
    }

    private void OnRemoveGameClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as GameLibraryViewModel)?.RemoveGame();
    }
}
