using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameShift.Core.Config;
using Serilog;

namespace GameShift.App.Views;

/// <summary>
/// First-run setup wizard shown on fresh install (when settings.json does not exist).
/// Guides the user through 3 steps: Welcome, Game Library Scan, and Quick Configuration.
/// On Finish, saves settings via SettingsManager.Save() so subsequent launches skip the wizard.
/// Uses x:Name direct code-behind access (no ViewModel) — consistent with ToastNotificationWindow pattern.
/// </summary>
public partial class FirstRunWizardWindow : Window
{
    private int _currentStep = 0; // 0 = Welcome, 1 = Scan, 2 = Config

    /// <summary>
    /// True when the user completed the wizard via Finish. False if they closed it early.
    /// App.xaml.cs checks this to decide whether to reload settings.
    /// </summary>
    public bool WizardCompleted { get; private set; } = false;

    public FirstRunWizardWindow()
    {
        InitializeComponent();
        VersionBadge.Text = $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "3.0"}";
        UpdateStepVisibility();
    }

    // ── Step navigation ───────────────────────────────────────────────────────

    private void OnGetStartedClicked(object sender, RoutedEventArgs e)
    {
        ShowStep(1);
    }

    private void OnScanClicked(object sender, RoutedEventArgs e)
    {
        // Disable scan button to prevent double-click
        ScanNowButton.IsEnabled = false;
        ScanNowButton.Content = "Scanning...";

        try
        {
            // App.Services.Detector is wired before wizard shows (after Step d in App.xaml.cs)
            App.Services.Detector?.ScanLibraries();

            // Get discovered game count from Detector.GetKnownGames()
            var knownGames = App.Services.Detector?.GetKnownGames();
            int gameCount = knownGames?.Count ?? 0;

            GamesCountText.Text = gameCount == 0
                ? "No games found — you can add them manually in Game Library"
                : $"Found {gameCount} game{(gameCount == 1 ? "" : "s")}";
            GamesCountText.Foreground = gameCount > 0
                ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)) // green
                : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)); // gray
            GamesCountText.Visibility = Visibility.Visible;

            ScanNowButton.Content = "Scan Complete";
            Log.Information("FirstRunWizard: Library scan complete — {GameCount} games found", gameCount);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FirstRunWizard: Library scan failed");
            GamesCountText.Text = "Scan failed — you can retry from the Game Library page";
            GamesCountText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // red
            GamesCountText.Visibility = Visibility.Visible;
            ScanNowButton.IsEnabled = true;
            ScanNowButton.Content = "Retry Scan";
        }
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e)
    {
        // Skip the scan step and go to config
        ShowStep(2);
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        ShowStep(_currentStep + 1);
    }

    private void OnFinishClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            // Read values from Step 3 controls
            var settings = new AppSettings
            {
                StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
                ShowNotifications = NotificationsCheckBox.IsChecked == true,
                GpuVendorOverride = (GpuVendorComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto"
            };

            SettingsManager.Save(settings);
            WizardCompleted = true;
            Log.Information("FirstRunWizard completed — settings saved (StartWithWindows={StartWithWindows}, ShowNotifications={ShowNotifications}, GpuVendor={GpuVendor})",
                settings.StartWithWindows, settings.ShowNotifications, settings.GpuVendorOverride);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FirstRunWizard: Failed to save settings");
            // Still close the wizard — app will use defaults
        }

        Close();
    }

    // ── Step visibility ───────────────────────────────────────────────────────

    private void ShowStep(int step)
    {
        _currentStep = step;
        UpdateStepVisibility();
    }

    private void UpdateStepVisibility()
    {
        // Panel visibility
        Step1Panel.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;

        // Step indicator dots (active = accent, inactive = dim)
        Dot1.Fill = new SolidColorBrush(_currentStep == 0
            ? Color.FromRgb(0x58, 0xA6, 0xFF)
            : Color.FromRgb(0x30, 0x36, 0x3D));
        Dot2.Fill = new SolidColorBrush(_currentStep == 1
            ? Color.FromRgb(0x58, 0xA6, 0xFF)
            : Color.FromRgb(0x30, 0x36, 0x3D));
        Dot3.Fill = new SolidColorBrush(_currentStep == 2
            ? Color.FromRgb(0x58, 0xA6, 0xFF)
            : Color.FromRgb(0x30, 0x36, 0x3D));

        // Navigation button visibility
        // Step 1: no bottom buttons (uses Get Started in panel)
        // Step 2: Skip + Next
        // Step 3: Finish only
        SkipButton.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
