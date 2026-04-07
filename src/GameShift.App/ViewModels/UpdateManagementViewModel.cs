using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GameShift.Core.Updates;

namespace GameShift.App.ViewModels;

/// <summary>
/// Manages update checking, downloading, and applying.
/// Self-contained — no event subscriptions to external services.
/// </summary>
public class UpdateManagementViewModel : INotifyPropertyChanged
{
    private bool _showUpdateBanner;
    private string _updateMessage = "";
    private string _updateUrl = "";
    private bool _isDownloading;
    private double _downloadProgress;
    private string _downloadStatusText = "";
    private bool _isUpdateReady;
    private CancellationTokenSource? _downloadCts;
    private UpdateInfo? _pendingUpdate;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowUpdateBanner
    {
        get => _showUpdateBanner;
        private set { _showUpdateBanner = value; OnPropertyChanged(); }
    }

    public string UpdateMessage
    {
        get => _updateMessage;
        private set { _updateMessage = value; OnPropertyChanged(); }
    }

    public string UpdateUrl
    {
        get => _updateUrl;
        private set { _updateUrl = value; OnPropertyChanged(); }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set { _isDownloading = value; OnPropertyChanged(); }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set { _downloadProgress = value; OnPropertyChanged(); }
    }

    public string DownloadStatusText
    {
        get => _downloadStatusText;
        private set { _downloadStatusText = value; OnPropertyChanged(); }
    }

    public bool IsUpdateReady
    {
        get => _isUpdateReady;
        private set { _isUpdateReady = value; OnPropertyChanged(); }
    }

    public UpdateManagementViewModel()
    {
        CheckForUpdatesAsync();
    }

    /// <summary>
    /// Checks GitHub for a newer release. Non-blocking, runs on background thread.
    /// Sets ShowUpdateBanner/UpdateMessage/UpdateUrl on the UI thread if an update exists.
    /// </summary>
    public async void CheckForUpdatesAsync()
    {
        try
        {
            // If update was already downloaded via startup popup, show "ready" state
            if (System.IO.File.Exists(UpdateApplier.GetUpdateStagingPath()))
            {
                var stagedUpdate = await UpdateChecker.CheckForUpdateAsync();
                if (stagedUpdate != null)
                {
                    _pendingUpdate = stagedUpdate;
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        IsUpdateReady = true;
                        ShowUpdateBanner = true;
                        UpdateMessage = $"GameShift v{stagedUpdate.LatestVersion} is downloaded and ready to install";
                        UpdateUrl = stagedUpdate.ReleaseUrl;
                    });
                    return;
                }
            }

            var update = await UpdateChecker.CheckForUpdateAsync();
            if (update != null)
            {
                _pendingUpdate = update;
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    UpdateMessage = $"GameShift v{update.LatestVersion} is available (you have v{update.CurrentVersion})";
                    UpdateUrl = update.ReleaseUrl;
                    ShowUpdateBanner = true;
                });
            }
        }
        catch
        {
            // Non-critical — silently ignore update check failures
        }
    }

    /// <summary>
    /// Opens the release URL in the default browser.
    /// </summary>
    public void OpenUpdateUrl()
    {
        if (string.IsNullOrEmpty(UpdateUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// Downloads the update from GitHub and stages it for replacement.
    /// Falls back to opening the browser if no direct download URL is available.
    /// </summary>
    public async void DownloadAndApplyUpdateAsync()
    {
        if (_pendingUpdate == null) return;

        if (string.IsNullOrEmpty(_pendingUpdate.DownloadUrl))
        {
            OpenUpdateUrl();
            return;
        }

        if (IsDownloading) return;

        IsDownloading = true;
        IsUpdateReady = false;
        DownloadProgress = 0;
        DownloadStatusText = "Starting download...";

        _downloadCts = new CancellationTokenSource();

        try
        {
            var targetPath = UpdateApplier.GetUpdateStagingPath();
            var progress = new Progress<double>(p =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    DownloadProgress = p * 100.0;
                    DownloadStatusText = $"Downloading... {p * 100.0:F0}%";
                });
            });

            var success = await Task.Run(() =>
                UpdateDownloader.DownloadAsync(
                    _pendingUpdate.DownloadUrl!,
                    targetPath,
                    _pendingUpdate.DownloadSize,
                    progress,
                    _downloadCts.Token));

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    IsDownloading = false;
                    IsUpdateReady = true;
                    DownloadProgress = 100;
                    DownloadStatusText = "Ready to install. Click Restart to apply.";
                }
                else
                {
                    IsDownloading = false;
                    DownloadStatusText = "Download failed. Click Download to retry.";
                }
            });
        }
        catch (OperationCanceledException)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsDownloading = false;
                DownloadProgress = 0;
                DownloadStatusText = "";
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Update download failed");
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsDownloading = false;
                DownloadStatusText = "Download failed. Click Download to retry.";
            });
        }
    }

    /// <summary>Cancels an in-progress download.</summary>
    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    /// <summary>
    /// Applies the staged update and shuts down the app for replacement.
    /// </summary>
    public void ApplyUpdateAndRestart()
    {
        if (!IsUpdateReady) return;

        if (UpdateApplier.ApplyUpdate())
        {
            Application.Current.Shutdown();
        }
        else
        {
            DownloadStatusText = "Failed to apply update. Try downloading again.";
            IsUpdateReady = false;
        }
    }

    public void Start() { }
    public void Stop() { }

    public void Cleanup()
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
