using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using GameShift.Core.Detection;

namespace GameShift.App.ViewModels;

/// <summary>
/// Manages the game library view: lists detected games, supports add/remove operations.
/// Uses DetectionOrchestrator for GetKnownGames(), AddManualGame(), RemoveGame().
/// </summary>
public class GameLibraryViewModel : INotifyPropertyChanged
{
    private readonly DetectionOrchestrator _orchestrator;
    private GameListItem? _selectedGame;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Observable collection of games for the list view.
    /// </summary>
    public ObservableCollection<GameListItem> Games { get; } = new();

    /// <summary>
    /// Currently selected game in the list. Drives CanRemove state.
    /// </summary>
    public GameListItem? SelectedGame
    {
        get => _selectedGame;
        set
        {
            _selectedGame = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRemove));
        }
    }

    /// <summary>
    /// True when a game is selected — enables the Remove button.
    /// </summary>
    public bool CanRemove => SelectedGame != null;

    /// <summary>
    /// True when the Games collection is empty — shows the empty state message.
    /// </summary>
    public bool HasNoGames => Games.Count == 0;

    public GameLibraryViewModel(DetectionOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;

        // Update HasNoGames when collection changes
        Games.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasNoGames));
    }

    /// <summary>
    /// Reloads the game list from the orchestrator's known games store.
    /// </summary>
    public void RefreshGames()
    {
        Games.Clear();

        foreach (var game in _orchestrator.GetKnownGames())
        {
            Games.Add(new GameListItem
            {
                Id = game.Id,
                GameName = game.GameName,
                LauncherSource = game.LauncherSource,
                ExecutablePath = game.ExecutablePath,
                DisplayPath = string.IsNullOrEmpty(game.ExecutablePath)
                    ? game.InstallDirectory
                    : game.ExecutablePath
            });
        }
    }

    /// <summary>
    /// Opens a file dialog for the user to select a game executable,
    /// then adds it to the orchestrator and refreshes the list.
    /// </summary>
    public void AddGame()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe",
            Title = "Select Game Executable"
        };

        if (dialog.ShowDialog() == true)
        {
            var result = _orchestrator.AddManualGame(dialog.FileName);
            if (result != null)
            {
                RefreshGames();
            }
            else
            {
                MessageBox.Show("Failed to add game. File may not exist.",
                    "Add Game", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// Removes the selected game after user confirmation.
    /// </summary>
    public void RemoveGame()
    {
        if (SelectedGame == null) return;

        var result = MessageBox.Show(
            $"Remove '{SelectedGame.GameName}' from the game library?",
            "Remove Game", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _orchestrator.RemoveGame(SelectedGame.Id);
            RefreshGames();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Display model for a single game in the library list.
/// </summary>
public class GameListItem
{
    public string Id { get; set; } = "";
    public string GameName { get; set; } = "";
    public string LauncherSource { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string DisplayPath { get; set; } = "";
}
