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

    /// <summary>Forces an immediate refresh of the log content.</summary>
    public void RefreshContent()
    {
        try
        {
            _currentLogPath = GetTodaysLogPath();

            if (!File.Exists(_currentLogPath))
            {
                LogContent = "No log file found for today.";
                StatusText = "No log file";
                return;
            }

            // Read file with sharing (Serilog holds a write lock)
            string allText;
            using (var fs = new FileStream(_currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                allText = reader.ReadToEnd();
            }

            var lines = allText.Split('\n');

            if (!string.IsNullOrWhiteSpace(SearchFilter))
            {
                lines = lines
                    .Where(l => l.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            LogContent = string.Join("\n", lines);
            StatusText = $"{Path.GetFileName(_currentLogPath)} — {lines.Length} lines" +
                (!string.IsNullOrWhiteSpace(SearchFilter) ? $" (filtered: \"{SearchFilter}\")" : "");
        }
        catch (Exception ex)
        {
            LogContent = $"Error reading log: {ex.Message}";
            StatusText = "Error";
        }
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
