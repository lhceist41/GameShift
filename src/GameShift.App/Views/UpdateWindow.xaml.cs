using System;
using System.IO;
using System.Threading;
using System.Windows;
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
    private readonly UpdateInfo _updateInfo;
    private CancellationTokenSource? _downloadCts;
    private bool _isUpdateReady;

    /// <summary>
    /// Creates a new UpdateWindow for the given update info.
    /// </summary>
    /// <param name="updateInfo">Update details from UpdateChecker.</param>
    /// <param name="alreadyStaged">True if the update file is already downloaded and staged.</param>
    public UpdateWindow(UpdateInfo updateInfo, bool alreadyStaged = false)
    {
        _updateInfo = updateInfo;
        InitializeComponent();

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

        // If no direct download URL, hide the Download button and show "View Release" instead
        if (string.IsNullOrEmpty(updateInfo.DownloadUrl) && !alreadyStaged)
        {
            DownloadButton.Content = "View Release";
        }

        // If update is already staged, go straight to Ready state
        if (alreadyStaged)
        {
            ShowReadyState();
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

        ErrorText.Text = message;
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
            var targetPath = UpdateApplier.GetUpdateStagingPath();
            var progress = new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                DownloadProgress.Value = percent;
                ProgressPercentText.Text = $"{percent}%";
            });

            bool success = await UpdateDownloader.DownloadAsync(
                _updateInfo.DownloadUrl!,
                targetPath,
                _updateInfo.DownloadSize,
                progress,
                _downloadCts.Token);

            if (success)
            {
                ShowReadyState();
            }
            else
            {
                ShowErrorState("Download failed. Please try again.");
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
        if (!_isUpdateReady) return;

        if (UpdateApplier.ApplyUpdate())
        {
            Application.Current.Shutdown();
        }
        else
        {
            ShowErrorState("Failed to apply update. Try downloading again.");
            _isUpdateReady = false;
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

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Cancel any in-flight download
        if (_downloadCts != null && !_downloadCts.IsCancellationRequested)
        {
            _downloadCts.Cancel();
        }
    }
}
