using System.Windows;
using System.Windows.Controls;
using GameShift.App.ViewModels;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Settings page: global application preferences.
/// Content migrated from SettingsWindow. ViewModel created on Loaded via App static properties.
/// </summary>
public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null) return; // already set

        var vm = new SettingsViewModel();
        vm.SetVbsHvciToggle(App.VbsToggle);
        DataContext = vm;
        vm.LoadSettings();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.SaveSettings();
    }

    private void OnReEnableVbsClicked(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will re-enable Memory Integrity (VBS/HVCI) and schedule a reboot in 30 seconds.\n\nContinue?",
            "Re-enable Memory Integrity",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            if ((DataContext as SettingsViewModel)?.ReEnableVbsHvci() == true)
            {
                GameShift.Core.Optimization.VbsHvciToggle.ScheduleReboot();
            }
            else
            {
                MessageBox.Show("Failed to re-enable VBS/HVCI. Check logs for details.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.ExportSettings();
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.ImportSettings();
    }
}
