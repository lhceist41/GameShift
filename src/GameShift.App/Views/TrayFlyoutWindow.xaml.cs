using System;
using System.Windows;
using GameShift.App.ViewModels;

namespace GameShift.App.Views;

/// <summary>
/// Compact flyout panel shown on tray left-click.
/// Positioned above the taskbar near the tray area.
/// Light-dismiss: closes when window loses focus.
/// ShowActivated=False ensures it doesn't steal focus from games.
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    private TrayFlyoutViewModel? _viewModel;

    public TrayFlyoutWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position the flyout above the taskbar, near the right side (tray area)
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 8;
        Top = workArea.Bottom - Height - 8;
    }

    /// <summary>
    /// Sets the ViewModel. Called by TrayIconManager before Show().
    /// </summary>
    public void SetViewModel(TrayFlyoutViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// Light-dismiss — close the flyout when the user clicks outside.
    /// Window.Deactivated fires when the window loses focus for any reason.
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        _viewModel?.Dispose();
        Close();
    }
}
