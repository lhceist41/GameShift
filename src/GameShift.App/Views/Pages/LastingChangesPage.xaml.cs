using System.Windows;
using System.Windows.Controls;
using GameShift.App.Services;
using GameShift.App.ViewModels;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using GameShift.Core.SystemTweaks;
using GameShift.Core.SystemTweaks.Tweaks;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Lasting changes page: what GameShift left on this PC, Restore All, and the system tweaks it offers.
/// The view model is created on Loaded (NavigationView requires parameterless constructors).
/// </summary>
public partial class LastingChangesPage : Page
{
    public LastingChangesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private LastingChangesViewModel? ViewModel => DataContext as LastingChangesViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DataContext ??= new LastingChangesViewModel();
        var mgr = App.Services.TweaksMgr;
        if (mgr != null) ViewModel!.RefreshTweaks(mgr);
        await LoadChangesAsync();
    }

    private static LastingChangesInventory? CreateInventory(SystemTweaksManager? mgr) =>
        mgr == null
            ? null
            : LastingChangesInventory.CreateForThisMachine(
                mgr, App.Services.DpcFix ?? new DpcFixEngine(), App.Services.VbsToggle ?? new VbsHvciToggle());

    private async Task LoadChangesAsync()
    {
        var vm = ViewModel;
        var inventory = CreateInventory(App.Services.TweaksMgr);
        if (vm == null || inventory == null) return;

        vm.ShowChecking();
        try
        {
            var list = await Task.Run(inventory.List);
            vm.ShowChanges(list.Items, RestoreAllPrompts.Warnings(list, SettingsManager.IsDataFolderRefused()));
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[LastingChanges] Listing failed");
            vm.ShowChanges([], [$"GameShift could not list its lasting changes: {ex.Message}"]);
        }
    }

    private async void OnTweakButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ISystemTweak tweak }) return;
        var mgr = App.Services.TweaksMgr;
        if (mgr == null) return;

        bool undo = mgr.GetTweakStatus(tweak) == SystemTweakRow.AppliedByGameShift;
        string? warning = null;
        if (!undo && tweak is DisableHags hags)
        {
            hags.EvaluateRecommendation();
            warning = RestartPrompts.HagsFrameGenerationWarning(hags.IsFrameGenCapable, hags.IsHagsEnabled);
        }

        var choice = RestartChoice.ApplyRestartLater;
        if (tweak.RequiresReboot)
        {
            choice = await RestartConfirm.AskAsync(this, RestartPrompts.ChangeSystemTweak(tweak.Name, undo, warning));
            if (choice == RestartChoice.Cancel) return;
        }

        bool done = undo ? mgr.RevertTweak(tweak) : mgr.ApplyTweak(tweak);
        ViewModel?.RefreshTweaks(mgr);

        if (!done)
        {
            MessageBox.Show("Could not change this setting. Check the logs for details.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (choice == RestartChoice.ApplyAndRestartNow)
        {
            VbsHvciToggle.ScheduleReboot("GameShift: finishing a change to a Windows setting.");
        }

        await LoadChangesAsync();
    }

    private async void OnRestoreAllClicked(object sender, RoutedEventArgs e)
    {
        var mgr = App.Services.TweaksMgr;
        if (mgr == null) return;

        var inventory = CreateInventory(mgr)!;
        RestoreAllButton.IsEnabled = false;
        try
        {
            var list = await Task.Run(inventory.List);
            var plan = RestoreAllPrompts.Plan(list, SettingsManager.IsDataFolderRefused());

            if (plan.NothingToUndo != null)
            {
                MessageBox.Show(plan.NothingToUndo, RestoreAllPrompts.Title, MessageBoxButton.OK,
                    plan.NothingToUndoIsWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);
                return;
            }

            var toUndo = new List<LastingChange>();
            var separatelyConfirmed = new List<LastingChange>();
            foreach (var question in plan.Questions)
            {
                if (MessageBox.Show(question.Text, RestoreAllPrompts.Title,
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    continue;

                toUndo.AddRange(question.Items);
                if (question.SeparatelyConfirmed)
                    separatelyConfirmed.AddRange(question.Items);
            }

            if (toUndo.Count == 0) return;

            var results = await Task.Run(() => inventory.Undo(toUndo, separatelyConfirmed));
            ViewModel?.RefreshTweaks(mgr);

            bool failed = results.Any(r => r.Outcome == LastingChangeOutcome.Failed);
            MessageBox.Show(LastingChangesInventory.DescribeResults(results), RestoreAllPrompts.Title,
                MessageBoxButton.OK, failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
            if (ViewModel is { } vm)
                vm.StatusMessage = RestoreAllPrompts.Status(results);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[LastingChanges] Restore All failed");
            MessageBox.Show($"Restore All stopped with an error: {ex.Message}", RestoreAllPrompts.Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RestoreAllButton.IsEnabled = true;
        }

        await LoadChangesAsync();
    }

    private void OnOpenLatencyDoctorClicked(object sender, RoutedEventArgs e)
    {
        (Window.GetWindow(this) as MainWindow)?.NavigateTo(typeof(DpcDoctorPage));
    }
}
