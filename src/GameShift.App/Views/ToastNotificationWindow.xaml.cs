using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameShift.Core.Monitoring;
using GameShift.Core.Presentation;

namespace GameShift.App.Views;

/// <summary>
/// Post-session summary toast window.
/// Positioned above the taskbar near the tray area.
/// Auto-closes after 5 seconds unless it lists changes still active. Close or Dismiss closes it;
/// Open activity asks for the Activity page, then closes it.
/// ShowActivated=False prevents stealing focus.
/// </summary>
public partial class ToastNotificationWindow : Window
{
    private readonly DispatcherTimer _autoCloseTimer;
    private bool _keepOpen;

    /// <summary>Raised when the user asks for the Activity page; the toast closes afterwards.</summary>
    public event Action? OpenActivityRequested;

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
        Left = workArea.Right - ActualWidth - 8;
        Top = workArea.Bottom - ActualHeight - 8;

        if (!_keepOpen)
            _autoCloseTimer.Start();
    }

    /// <summary>
    /// Populates the toast with session summary data.
    /// Call this before Show().
    /// </summary>
    /// <param name="summary">What the session's end undid, from its deactivation outcome; null hides it.</param>
    public void SetSessionData(
        string gameName,
        TimeSpan duration,
        SessionSummary? summary,
        double avgDpcMicroseconds,
        double peakDpcMicroseconds)
    {
        TitleText.Text = $"{gameName} session ended";
        _keepOpen = summary?.KeepOpen == true;

        // Format duration as "Xh Ym" or "Xm Ys"
        if (duration.TotalHours >= 1)
            DurationText.Text = $"{(int)duration.TotalHours}h {duration.Minutes}m";
        else if (duration.TotalMinutes >= 1)
            DurationText.Text = $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        else
            DurationText.Text = $"{duration.Seconds}s";

        if (summary == null)
        {
            OutcomePanel.Visibility = Visibility.Collapsed;
            ChangesText.Text = "N/A";
        }
        else
        {
            // The counts line is already "{n} applied" or "{n} applied, {m} failed"; it is null
            // when the session ran no modules.
            ChangesText.Text = summary.CountsLine ?? "None";
            SummaryText.Text = summary.Text;
            SummaryText.SetResourceReference(TextBlock.ForegroundProperty,
                summary.Tone == SessionSummaryTone.Warning ? "GS.State.Warning" : "GS.Fg.Primary");
            StillActiveList.ItemsSource = summary.StillActive;
            StillActiveList.Visibility = summary.StillActive.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        AvgDpcText.Text = avgDpcMicroseconds > 0 ? DpcLoad.Format(avgDpcMicroseconds) : "N/A";
        PeakDpcText.Text = peakDpcMicroseconds > 0 ? DpcLoad.Format(peakDpcMicroseconds) : "N/A";
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _autoCloseTimer.Stop();
        Close();
    }

    private void OnOpenActivityClicked(object sender, RoutedEventArgs e)
    {
        _autoCloseTimer.Stop();
        OpenActivityRequested?.Invoke();
        Close();
    }
}
