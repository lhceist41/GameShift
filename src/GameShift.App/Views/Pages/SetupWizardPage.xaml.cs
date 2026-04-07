using System;
using System.Windows;
using System.Windows.Controls;
using GameShift.Core.Detection;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Setup Wizard page accessible from the sidebar navigation.
/// Allows re-running hardware scan and game library re-initialization at any time.
/// </summary>
public partial class SetupWizardPage : Page
{
    private HardwareScanner? _scanner;

    public SetupWizardPage()
    {
        InitializeComponent();
    }

    private async void OnScanClicked(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanButton.Content = "Scanning...";
        ResultsPanel.Visibility = Visibility.Collapsed;

        try
        {
            _scanner = new HardwareScanner();
            var progress = new Progress<string>(msg =>
            {
                ScanProgress.Text = msg;
            });

            await _scanner.ScanAsync(progress);

            // Show results
            GpuResult.Text = _scanner.GpuName;
            RamResult.Text = $"{_scanner.TotalRamGb:F0} GB";
            VbsResult.Text = _scanner.VbsEnabled ? "Enabled (impacts performance)" : "Disabled";
            DpcResult.Text = $"{_scanner.DpcBaselineUs:F0} \u00B5s" +
                (_scanner.DpcBaselineUs > 1000 ? " (High \u2014 check drivers)" : " (Normal)");
            ResultsPanel.Visibility = Visibility.Visible;
            ScanProgress.Text = "Scan complete.";
        }
        catch (Exception ex)
        {
            ScanProgress.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
            ScanButton.Content = "Scan Hardware";
        }
    }

    private async void OnLibraryScanClicked(object sender, RoutedEventArgs e)
    {
        LibraryScanButton.IsEnabled = false;
        LibraryScanButton.Content = "Scanning...";

        try
        {
            LibraryScanProgress.Text = "Re-initializing game detection...";

            var orchestrator = App.Services.Orchestrator;
            if (orchestrator == null)
            {
                LibraryScanProgress.Text = "Detection system not available.";
                return;
            }

            // Re-initialize the detection system (re-scans all libraries)
            await orchestrator.InitializeAsync();
            LibraryScanProgress.Text = "Game libraries re-scanned successfully.";
        }
        catch (Exception ex)
        {
            LibraryScanProgress.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            LibraryScanButton.IsEnabled = true;
            LibraryScanButton.Content = "Scan Libraries";
        }
    }
}
