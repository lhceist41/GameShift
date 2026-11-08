using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using GameShift.Core.Config;
using GameShift.Core.Updates;
using Serilog;

namespace GameShift.App.Views;

/// <summary>
/// Modal popup window shown at startup when a new version is available.
/// Supports downloading and installing updates directly from the popup.
/// 4 states: Initial (show release notes), Downloading, Ready, Error.
/// </summary>
public partial class UpdateWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const string UpdateDownloadFailedText = "The download failed or could not be verified.";

    private readonly UpdateInfo _updateInfo;
    private CancellationTokenSource? _downloadCts;
    private bool _isUpdateReady;
    private StagedUpdate? _staged;

    /// <summary>
    /// Creates a new UpdateWindow for the given update info.
    /// </summary>
    /// <param name="updateInfo">Update details from UpdateChecker.</param>
    public UpdateWindow(UpdateInfo updateInfo)
    {
        _updateInfo = updateInfo;
        InitializeComponent();
        ApplyDarkTitleBar();

        // Populate header
        VersionText.Text = $"v{updateInfo.CurrentVersion}  \u2192  v{updateInfo.LatestVersion}";

        // Populate release notes
        if (!string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes))
        {
            ReleaseNotesText.Text = updateInfo.ReleaseNotes;
        }
        else
        {
            ReleaseNotesText.Text = "No release notes available.";
        }

        // If no download URL at all, fall back to opening the release page in the browser.
        // The button always says "Download & Install" - the fallback is transparent to the user.

        // Show urgent banner for critical updates (crash fixes, data loss, security)
        if (IsCriticalUpdate(updateInfo.ReleaseNotes))
        {
            UrgentBanner.Visibility = System.Windows.Visibility.Visible;
            UrgentBannerText.Text = "\u26a0 Critical update - fixes crashes and performance issues. Update strongly recommended.";
        }
    }

    // ── State transitions ─────────────────────────────────────────────

    private void ShowInitialState()
    {
        NotesPanel.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;

        InitialButtons.Visibility = Visibility.Visible;
        DownloadingButtons.Visibility = Visibility.Collapsed;
        ReadyButtons.Visibility = Visibility.Collapsed;
        ErrorButtons.Visibility = Visibility.Collapsed;
    }

    private void ShowDownloadingState()
    {
        NotesPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;

        InitialButtons.Visibility = Visibility.Collapsed;
        DownloadingButtons.Visibility = Visibility.Visible;
        ReadyButtons.Visibility = Visibility.Collapsed;
        ErrorButtons.Visibility = Visibility.Collapsed;

        DownloadStatusText.Text = "Downloading update...";
        DownloadProgress.Value = 0;
        ProgressPercentText.Text = "0%";
    }

    private void ShowReadyState()
    {
        _isUpdateReady = true;

        NotesPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;

        InitialButtons.Visibility = Visibility.Collapsed;
        DownloadingButtons.Visibility = Visibility.Collapsed;
        ReadyButtons.Visibility = Visibility.Visible;
        ErrorButtons.Visibility = Visibility.Collapsed;

        DownloadStatusText.Text = "Update downloaded and ready to install.";
        DownloadProgress.Value = 100;
        ProgressPercentText.Text = "100%";
    }

    private void ShowErrorState(string message)
    {
        NotesPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;

        InitialButtons.Visibility = Visibility.Collapsed;
        DownloadingButtons.Visibility = Visibility.Collapsed;
        ReadyButtons.Visibility = Visibility.Collapsed;
        ErrorButtons.Visibility = Visibility.Visible;

        // The reason, then a way out that always works: a refusal does not go away by trying again.
        ErrorText.Inlines.Clear();
        ErrorText.Inlines.Add(new Run(message));
        if (Uri.TryCreate(_updateInfo.ReleaseUrl, UriKind.Absolute, out var releaseUri))
        {
            var link = new Hyperlink(new Run("Download the new version from the release page")) { NavigateUri = releaseUri };
            link.RequestNavigate += OnReleasePageRequested;
            ErrorText.Inlines.Add(new LineBreak());
            ErrorText.Inlines.Add(new LineBreak());
            ErrorText.Inlines.Add(link);
        }
    }

    private void OnReleasePageRequested(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _updateInfo.ReleaseUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open release URL");
        }
        e.Handled = true;
    }

    // ── Event handlers ────────────────────────────────────────────────

    private async void OnDownloadClicked(object sender, RoutedEventArgs e)
    {
        // If no direct download URL, open the release page in browser
        if (string.IsNullOrEmpty(_updateInfo.DownloadUrl))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _updateInfo.ReleaseUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to open release URL");
            }
            Close();
            return;
        }

        ShowDownloadingState();
        _downloadCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                DownloadProgress.Value = percent;
                ProgressPercentText.Text = $"{percent}%";
            });

            var result = await UpdateCoordinator.DownloadAsync(_updateInfo, progress, _downloadCts.Token);
            _staged = result.Staged;

            if (_staged != null)
            {
                ShowReadyState();
            }
            else
            {
                ShowErrorState(result.FailureReason ?? UpdateDownloadFailedText);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Update download cancelled by user");
            ShowInitialState();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update download failed");
            ShowErrorState($"Download failed: {ex.Message}");
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        if (!_isUpdateReady || _staged == null) return;

        if (UpdateCoordinator.ApplyRememberingPortableCopy(_staged))
        {
            Application.Current.Shutdown();
        }
        else
        {
            ShowErrorState("Failed to apply update. Try downloading again.");
            _isUpdateReady = false;
            _staged = null;
        }
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = SettingsManager.Load();
            settings.SkippedUpdateVersion = _updateInfo.LatestVersion;
            SettingsManager.Save(settings);
            Log.Information("User skipped update v{Version}", _updateInfo.LatestVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save skipped update version");
        }

        Close();
    }

    private void OnRemindLaterClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    /// <summary>
    /// Detects critical updates by scanning release notes for keywords indicating
    /// crash fixes, data loss, security patches, or severe performance issues.
    /// </summary>
    private static bool IsCriticalUpdate(string? releaseNotes)
    {
        if (string.IsNullOrEmpty(releaseNotes)) return false;

        var criticalKeywords = new[]
        {
            "crash", "critical", "urgent", "security",
            "100% CPU", "data loss", "corruption",
            "hotfix", "Hotfix", "native crash",
            "access violation", "BSOD"
        };

        foreach (var keyword in criticalKeywords)
        {
            if (releaseNotes.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;
            int value = 1;
            DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
        }
        catch
        {
            // Silently ignore on older Windows versions
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Cancel any in-flight download
        if (_downloadCts != null && !_downloadCts.IsCancellationRequested)
        {
            _downloadCts.Cancel();
        }
    }
}
