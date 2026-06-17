using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using GameShift.Core.Config;

namespace GameShift.App.ViewModels;

/// <summary>
/// ViewModel for the Log Viewer page. Reads Serilog rolling log files from
/// %AppData%\GameShift\logs\ and displays them with search filtering
/// and auto-refresh via DispatcherTimer (3-second polling interval).
/// </summary>
public class LogViewerViewModel : INotifyPropertyChanged
{
    private readonly DispatcherTimer _refreshTimer;
    private string _logContent = "";
    private string _searchFilter = "";
    private string _statusText = "Ready";
    private string _currentLogPath = "";

    // Single-flight guard: the file read/filter runs off the UI thread, so a timer tick or a
    // search keystroke arriving while one is in flight is coalesced into a single rerun instead
    // of overlapping (which could let a stale read overwrite a newer one). Touched only on the UI thread.
    private bool _isRefreshing;
    private bool _refreshQueued;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The displayed log content (filtered if search is active).</summary>
    public string LogContent
    {
        get => _logContent;
        private set { _logContent = value; OnPropertyChanged(); }
    }

    /// <summary>Search filter text. When non-empty, only matching lines are shown.</summary>
    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            _searchFilter = value;
            OnPropertyChanged();
            RefreshContent();
        }
    }

    /// <summary>Status bar text showing file info and line count.</summary>
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public LogViewerViewModel()
    {
        _currentLogPath = GetTodaysLogPath();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _refreshTimer.Tick += (_, _) => RefreshContent();

        RefreshContent();
    }

    /// <summary>Starts the auto-refresh timer.</summary>
    public void StartAutoRefresh()
    {
        _refreshTimer.Start();
    }

    /// <summary>Stops the auto-refresh timer (called when page unloads).</summary>
    public void StopAutoRefresh()
    {
        _refreshTimer.Stop();
    }

    /// <summary>
    /// Refreshes the displayed log content. The file read, split and filter run on a background
    /// thread (the log can be several MB and this is polled every 3s), then the bound properties
    /// are updated back on the UI thread. Single-flighted so a tick or search keystroke arriving
    /// mid-read is coalesced into one rerun rather than overlapping.
    /// </summary>
    public void RefreshContent()
    {
        if (_isRefreshing)
        {
            _refreshQueued = true;
            return;
        }
        _isRefreshing = true;

        var filter = SearchFilter; // capture on the UI thread
        var path = GetTodaysLogPath();

        Task.Run(() =>
        {
            string content, status;
            try
            {
                if (!File.Exists(path))
                {
                    content = "No log file found for today.";
                    status = "No log file";
                }
                else
                {
                    // Read with sharing (Serilog holds a write lock).
                    string allText;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs))
                    {
                        allText = reader.ReadToEnd();
                    }

                    var lines = allText.Split('\n');
                    if (!string.IsNullOrWhiteSpace(filter))
                    {
                        lines = lines
                            .Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    }

                    content = string.Join("\n", lines);
                    status = $"{Path.GetFileName(path)} - {lines.Length} lines" +
                        (!string.IsNullOrWhiteSpace(filter) ? $" (filtered: \"{filter}\")" : "");
                }
            }
            catch (Exception ex)
            {
                content = $"Error reading log: {ex.Message}";
                status = "Error";
            }

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _currentLogPath = path;
                LogContent = content;
                StatusText = status;
                _isRefreshing = false;

                // A refresh was requested while this one was in flight (e.g. a search keystroke):
                // run once more so the latest filter is applied.
                if (_refreshQueued)
                {
                    _refreshQueued = false;
                    RefreshContent();
                }
            });
        });
    }

    /// <summary>Opens the log folder in Windows Explorer.</summary>
    public void OpenLogFolder()
    {
        try
        {
            var logsDir = SettingsManager.GetLogsPath();
            if (Directory.Exists(logsDir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logsDir,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    /// <summary>
    /// Gets the path to today's Serilog rolling log file.
    /// Pattern: gameshift-YYYYMMDD.log
    /// </summary>
    private static string GetTodaysLogPath()
    {
        var logsDir = SettingsManager.GetLogsPath();
        var todayFile = $"gameshift-{DateTime.Now:yyyyMMdd}.log";
        return Path.Combine(logsDir, todayFile);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
