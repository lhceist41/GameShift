using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameShift.App.Services;
using GameShift.App.ViewModels;
using GameShift.Core.Monitoring;
using GameShift.Core.Presentation;

namespace GameShift.App.Views.Pages;

public partial class DpcDoctorPage : Page
{
    public DpcDoctorPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null) return;

        var vm = new DpcDoctorViewModel(
            App.Services.DpcTrace,
            App.Services.DpcFix,
            App.Services.DriverDb,
            App.Services.DpcMon);

        vm.FixApplied += OnFixApplied;
        vm.RebootRequested += OnRebootRequested;

        DataContext = vm;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        (DataContext as DpcDoctorViewModel)?.Cleanup();
    }

    private void OnStartClicked(object sender, RoutedEventArgs e) =>
        (DataContext as DpcDoctorViewModel)?.StartCapture();

    private void OnStopClicked(object sender, RoutedEventArgs e) =>
        (DataContext as DpcDoctorViewModel)?.StopCapture();

    private void OnRun30sClicked(object sender, RoutedEventArgs e) =>
        (DataContext as DpcDoctorViewModel)?.RunFor30Seconds();

    private void OnApplyFixClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is DriverAutoFix fix)
            (DataContext as DpcDoctorViewModel)?.ApplyFix(fix);
    }

    private void OnToggleQuickFixClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is QuickFixViewModel qf)
            (DataContext as DpcDoctorViewModel)?.ToggleQuickFix(qf);
    }

    private void OnDpcInfoToggleClicked(object sender, RoutedEventArgs e) =>
        (DataContext as DpcDoctorViewModel)?.ToggleDpcInfo();

    private void OnRestartAsAdminClicked(object sender, RoutedEventArgs e)
    {
        GameShift.Core.System.AdminHelper.RestartAsAdmin();
        Application.Current.Shutdown();
    }

    private void OnDismissSuccessBanner(object sender, RoutedEventArgs e) =>
        (DataContext as DpcDoctorViewModel)?.DismissSuccessBanner();

    private async void OnRestartNowClicked(object sender, RoutedEventArgs e)
    {
        var choice = await RestartConfirm.AskAsync(this, RestartPrompts.FinishLatencyFixes());
        if (choice != RestartChoice.ApplyAndRestartNow)
            return;

        System.Diagnostics.Process.Start(
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
            "/r /t 10 /c \"GameShift DPC Doctor: Applying fix - restarting in 10 seconds\"");
    }

    private void OnDismissRebootPrompt(object sender, RoutedEventArgs e) =>
        (DataContext as DpcDoctorViewModel)?.DismissRebootPrompt();

    private void OnFixApplied(DpcFixResult result)
    {
        if (!result.Success)
        {
            MessageBox.Show(result.Message, "Fix Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRebootRequested(string fixName)
    {
        // Inline banner replaces the MessageBox - handled by ViewModel now
    }

    // ── CPU core reservation ──────────────────────────────────────────────

    private void OnRemoveCoreIsolationClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as DpcDoctorViewModel)?.RemoveCoreIsolation();
    }
}
