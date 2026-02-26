using System;
using System.Windows;
using System.Windows.Threading;

namespace GameShift.App.Views;

/// <summary>
/// Post-session summary toast window.
/// Positioned above the taskbar near the tray area.
/// Auto-closes after 5 seconds. User can click X to close early.
/// ShowActivated=False prevents stealing focus.
/// </summary>
public partial class ToastNotificationWindow : Window
{
    private readonly DispatcherTimer _autoCloseTimer;

    public ToastNotificationWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // Auto-close after 5 seconds
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            Close();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position above the taskbar, near the right side (tray area)
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 8;
        Top = workArea.Bottom - Height - 8;

        _autoCloseTimer.Start();
    }

    /// <summary>
    /// Populates the toast with session summary data.
    /// Call this before Show().
    /// </summary>
    public void SetSessionData(
        string gameName,
        TimeSpan duration,
        int optimizationCount,
        double avgDpcMicroseconds,
        double peakDpcMicroseconds)
    {
        GameNameText.Text = gameName;

        // Format duration as "Xh Ym" or "Xm Ys"
        if (duration.TotalHours >= 1)
            DurationText.Text = $"{(int)duration.TotalHours}h {duration.Minutes}m";
        else if (duration.TotalMinutes >= 1)
            DurationText.Text = $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        else
            DurationText.Text = $"{duration.Seconds}s";

        OptCountText.Text = optimizationCount.ToString();
        AvgDpcText.Text = avgDpcMicroseconds > 0 ? $"{avgDpcMicroseconds:F0} \u00B5s" : "N/A";
        PeakDpcText.Text = peakDpcMicroseconds > 0 ? $"{peakDpcMicroseconds:F0} \u00B5s" : "N/A";
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _autoCloseTimer.Stop();
        Close();
    }
}
