using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameShift.App.ViewModels;
using GameShift.Core.Monitoring;

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

        var settings = GameShift.Core.Config.SettingsManager.Load();
        var vm = new DpcDoctorViewModel(
            App.DpcTrace,
            App.DpcFix,
            App.DriverDb,
            App.DpcMon,
            settings,
            () => GameShift.Core.Config.SettingsManager.Save(settings));

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

    private void OnRestartNowClicked(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start("shutdown", "/r /t 10 /c \"GameShift DPC Doctor: Applying fix - restarting in 10 seconds\"");
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
        // Inline banner replaces the MessageBox — handled by ViewModel now
    }

    private void OnToggleKernelTuningClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn &&
            btn.Tag is ViewModels.KernelTuningItemViewModel item &&
            DataContext is DpcDoctorViewModel vm)
        {
            vm.ToggleKernelTuningSetting(item);
        }
    }
}
