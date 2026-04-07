using System.Windows;
using System.Windows.Controls;
using GameShift.App.ViewModels;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Profiles page: game/profile selection and per-game optimization toggles.
/// Content migrated from ProfileEditorWindow. ViewModel created on Loaded via App static properties.
/// </summary>
public partial class ProfilesPage : Page
{
    public ProfilesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null) return; // already set

        var vm = new ProfileEditorViewModel(App.Services.ProfileMgr!, App.Services.Orchestrator!);
        DataContext = vm;
        vm.LoadProfiles();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as ProfileEditorViewModel)?.SaveProfile();
    }
}
