using System.Windows;
using System.Windows.Controls;
using GameShift.App.Services;
using GameShift.App.ViewModels;
using GameShift.Core.Presentation;
using GameShift.Core.GameProfiles;
using Serilog;

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
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null) return; // already set

        var vm = new SettingsViewModel();
        vm.SetVbsHvciToggle(App.Services.VbsToggle);

        DataContext = vm;
        vm.LoadSettings();
        PopulateSupportedGames();
    }

    // A theme picked but not saved must not outlive the visit, or the app would show a theme it never saved.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.DiscardThemePreview();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.SaveSettings();
    }

    private async void OnReEnableVbsClicked(object sender, RoutedEventArgs e)
    {
        var choice = await RestartConfirm.AskAsync(this, RestartPrompts.TurnOnMemoryIntegrity());
        if (choice == RestartChoice.ApplyAndRestartNow)
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

    private void PopulateSupportedGames()
    {
        var tweakMgr = App.Services.TweaksMgr;
        SupportedGamesList.ItemsSource = SettingsChoices.SupportedGames(BuiltInProfiles.GetAll(), className =>
        {
            var tweak = tweakMgr?.GetTweakByClassName(className);
            if (tweak == null) return null;
            try
            {
                return (tweak.Name, tweak.DetectIsApplied());
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read whether {Tweak} is in place", className);
                return (tweak.Name, false);
            }
        });
    }
}
