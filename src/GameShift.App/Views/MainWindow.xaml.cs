using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GameShift.App.Helpers;
using GameShift.Core.Config;
using Wpf.Ui.Controls;

namespace GameShift.App.Views;

/// <summary>
/// NavigationView shell window. Hosts all pages in a single-window sidebar-navigated layout.
/// Uses plain Window with dark background (#0D1117) for reliable title bar on Win10/Win11.
/// When tray is available: close and minimize both hide to tray.
/// Applies immersive dark mode title bar via DwmSetWindowAttribute.
/// Saves/restores window position and size via AppSettings.
/// </summary>
public partial class MainWindow : Window
{
    // ── Win32 DWM interop for dark title bar ────────────────────────────
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // ── Snackbar for toast notifications ────────────────────────────────
    private Snackbar? _snackbar;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        PreviewMouseWheel += OnPreviewMouseWheel;

        // Show assembly version in nav pane footer
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "";
    }

    /// <summary>
    /// Called when the window handle is available. Applies dark title bar.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyDarkTitleBar();
    }

    /// <summary>
    /// Called after window is fully loaded. Restores saved position and fixes scrolling.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _snackbar = new Snackbar(SnackbarPresenter);
        RestoreWindowPosition();

        // WPF-UI's NavigationView resets IsDynamicScrollViewerEnabled to true after
        // every page navigation during its internal layout pass. We must re-disable it
        // on EVERY navigation, deferred until after the layout pass completes.
        NavigationView.Navigated += OnNavigated;
    }

    // ── NavigationView Scroll Fix ────────────────────────────────────────

    /// <summary>
    /// Fired after every page navigation. Defers disabling the dynamic ScrollViewer
    /// wrapper until after WPF-UI's layout pass — which is when it resets the property
    /// back to true.
    /// </summary>
    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        // The NavigationView resets IsDynamicScrollViewerEnabled = true during its
        // post-navigation layout pass. A Dispatcher callback at Loaded priority runs
        // after that layout pass completes, so we can override it back to false.
        Dispatcher.InvokeAsync(() =>
        {
            DisableContentPresenterDynamicScroll();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Mouse Wheel Scroll Fix ─────────────────────────────────────────

    /// <summary>
    /// Window-level PreviewMouseWheel handler. WPF-UI's NavigationView swallows mouse wheel
    /// events before they reach page-level ScrollViewers. By handling at the Window root,
    /// we intercept the event first and forward it to the correct ScrollViewer.
    ///
    /// Walks UP the visual tree from the element under the mouse to find the nearest
    /// ScrollViewer tagged with ScrollHelper.FixScrolling="True", then scrolls it.
    /// </summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Walk up from the element under the mouse to find the target ScrollViewer
        var element = e.OriginalSource as DependencyObject;

        // Handle non-Visual sources (e.g. Run inside TextBlock) by walking
        // the logical tree until we find a Visual to start the visual tree walk from.
        if (element != null && element is not Visual)
        {
            var logical = element;
            while (logical != null && logical is not Visual)
            {
                logical = LogicalTreeHelper.GetParent(logical);
            }
            if (logical != null)
                element = logical;
        }

        while (element != null)
        {
            if (element is ScrollViewer sv && ScrollHelper.GetFixScrolling(sv))
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta / 3.0));
                e.Handled = true;
                return;
            }
            element = VisualTreeHelper.GetParent(element);
        }
    }

    // ── NavigationView Content Presenter Fix ─────────────────────────────

    /// <summary>
    /// Disables the NavigationView's dynamic ScrollViewer wrapper.
    /// When enabled (default), it wraps page content in a ScrollViewer that gives
    /// pages unlimited height, making page-level ScrollViewers unable to scroll.
    /// With it disabled, each page manages its own scrolling via its own ScrollViewer.
    /// </summary>
    private void DisableContentPresenterDynamicScroll()
    {
        var presenter = FindVisualChild<NavigationViewContentPresenter>(NavigationView);
        if (presenter != null)
        {
            presenter.SetValue(
                NavigationViewContentPresenter.IsDynamicScrollViewerEnabledProperty,
                false);
        }
    }

    /// <summary>
    /// Walks the visual tree depth-first to find the first child of type T.
    /// </summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Uses DwmSetWindowAttribute with DWMWA_USE_IMMERSIVE_DARK_MODE (attribute 20)
    /// to apply a dark title bar. Works on Win10 20H1+ and all Win11 builds.
    /// Silently fails on older Windows versions.
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int value = 1; // 1 = dark mode
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // Silently ignore — older Windows versions don't support this attribute
        }
    }

    /// <summary>
    /// Navigates the NavigationView to the Dashboard page.
    /// Called on first show to ensure a page is selected.
    /// </summary>
    public void NavigateToDashboard()
    {
        NavigationView.Navigate(typeof(Pages.DashboardPage));
    }

    /// <summary>
    /// Navigates to the specified page type via the NavigationView.
    /// </summary>
    public void NavigateTo(Type pageType)
    {
        NavigationView.Navigate(pageType);
    }

    /// <summary>
    /// Shows a brief auto-dismissing snackbar toast message.
    /// Used for hotkey feedback ("Monitoring Paused" / "Monitoring Resumed").
    /// </summary>
    public void ShowToast(string title, string message, TimeSpan? duration = null)
    {
        if (_snackbar == null) return;

        Dispatcher.Invoke(() =>
        {
            _snackbar.Title = title;
            _snackbar.Content = message;
            _snackbar.Timeout = duration ?? TimeSpan.FromSeconds(2);
            _snackbar.Show();
        });
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // Minimize-to-tray: when tray is available, hide the window instead of
        // showing a minimized taskbar entry.
        if (WindowState == WindowState.Minimized && App.TrayAvailable)
        {
            Hide();
            WindowState = WindowState.Normal; // reset so next Show() isn't minimized
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Save window position before closing/hiding
        SaveWindowPosition();

        if (App.TrayAvailable)
        {
            // Tray icon works — hide to tray, user can re-show via tray menu.
            e.Cancel = true;
            Hide();
        }
        else
        {
            // Tray creation failed — closing the window exits the app.
            e.Cancel = false;
            Application.Current.Shutdown();
        }
    }

    // ── Window Position Persistence ─────────────────────────────────────

    /// <summary>
    /// Saves current window position, size, and maximized state to AppSettings.
    /// </summary>
    private void SaveWindowPosition()
    {
        try
        {
            var settings = SettingsManager.Load();
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            SettingsManager.Save(settings);
        }
        catch
        {
            // Non-critical — ignore save failures
        }
    }

    /// <summary>
    /// Restores window position and size from AppSettings.
    /// Validates the position is within visible screen bounds.
    /// Falls back to CenterScreen if the saved position is off-screen.
    /// </summary>
    private void RestoreWindowPosition()
    {
        try
        {
            var settings = SettingsManager.Load();

            // Only restore if values have been saved (non-zero width)
            if (settings.WindowWidth <= 0 || settings.WindowHeight <= 0)
                return;

            // Clamp to visible screen area
            var left = settings.WindowLeft;
            var top = settings.WindowTop;
            var width = settings.WindowWidth;
            var height = settings.WindowHeight;

            // Get virtual screen bounds (all monitors combined)
            var screenLeft = SystemParameters.VirtualScreenLeft;
            var screenTop = SystemParameters.VirtualScreenTop;
            var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
            var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

            // Ensure at least 100px of the window is visible on some screen
            if (left + 100 > screenRight || left + width < screenLeft + 100 ||
                top + 50 > screenBottom || top < screenTop)
            {
                // Off-screen — don't restore, keep CenterScreen default
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = width;
            Height = height;

            if (settings.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // Non-critical — ignore restore failures
        }
    }
}
