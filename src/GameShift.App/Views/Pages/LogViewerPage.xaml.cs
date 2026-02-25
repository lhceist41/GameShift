using System.Windows;
using System.Windows.Controls;
using GameShift.App.ViewModels;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Log Viewer page: displays today's Serilog rolling log file with search,
/// auto-refresh (3-second DispatcherTimer), and Open Folder button.
/// </summary>
public partial class LogViewerPage : Page
{
    private LogViewerViewModel? _viewModel;

    public LogViewerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
        {
            _viewModel = new LogViewerViewModel();
            DataContext = _viewModel;
        }

        _viewModel.StartAutoRefresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel?.StopAutoRefresh();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        _viewModel?.RefreshContent();
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        _viewModel?.OpenLogFolder();
    }
}
