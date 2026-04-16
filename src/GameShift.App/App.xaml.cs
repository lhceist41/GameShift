using System;
using System.IO;
using System.Threading;
using System.Windows;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.Journal;
using GameShift.Core.System;
using GameShift.Core.Watchdog;
using GameShift.App.Services;
using GameShift.App.Views;
using GameShift.App.Views.Pages;
using Serilog;

namespace GameShift.App;

/// <summary>
/// Interaction logic for App.xaml
/// Tray-first application: no visible window on startup, all interaction via system tray icon.
/// Single-window navigation: all pages are hosted inside MainWindow via NavigationView.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    private TrayIconManager? _trayManager;
    private MainWindow? _mainWindow;
    private GlobalHotkeyService? _hotkeyService;
    private SingleInstancePipe? _singleInstancePipe;
    private WatchdogHeartbeatClient? _watchdogHeartbeat;

    // Holds event subscriptions for clean unsubscription in OnExit
    private EventSubscriptions? _eventSubs;

    /// <summary>
    /// True when the tray icon was created successfully. When false (tray creation failed),
    /// closing the window exits the app instead of hiding to tray.
    /// </summary>
    internal static bool TrayAvailable { get; private set; }

    // ── Static service registry for page ViewModel construction ────────────────
    // Pages are instantiated by WPF UI NavigationView via parameterless constructors.
    // They access App.Services.* in their Loaded event to create ViewModels.

    /// <summary>Centralised service registry — replaces individual static properties.</summary>
    public static ServiceRegistry Services { get; } = new();

    /// <summary>
    /// Static constructor: registers global crash handlers as early as possible —
    /// before XAML resources load, before OnStartup fires. This catches crashes during
    /// resource dictionary initialization (e.g., WPF UI theme loading on Win10).
    /// </summary>
    static App()
    {
        // Register unhandled exception handler at the AppDomain level immediately.
        // This fires even if the crash happens during static initialization or XAML loading.
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            WriteCrashLog("STATIC_UNHANDLED", args.ExceptionObject as Exception);
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WriteDiag("OnStartup entered");

        // Single-instance guard: only one GameShift process allowed
        _singleInstanceMutex = new Mutex(true, "Global\\GameShift_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            WriteDiag("Single-instance check FAILED — another instance is running.");
            WriteDiag("Sending 'show' to first instance via named pipe...");
            SingleInstancePipe.SendShowMessage();
            Shutdown();
            return;
        }
        WriteDiag("Single-instance check passed");

        // Additional global exception handlers on the WPF Dispatcher thread.
        // The AppDomain handler registered in static ctor covers pre-startup crashes.
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            WriteCrashLog("UNHANDLED", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            WriteCrashLog("DISPATCHER", args.Exception);
            args.Handled = true;
        };

        try
        {
            // Step a: Admin elevation check
            WriteDiag("Step a: Admin elevation check...");
            bool isAdmin;
            try
            {
                isAdmin = AdminHelper.IsRunningAsAdmin();
            }
            catch (Exception ex)
            {
                WriteDiag($"Admin check EXCEPTION: {ex.Message}");
                Log.Error(ex, "Admin elevation check failed");
                MessageBox.Show($"Admin check failed:\n{ex.Message}", "Error");
                Shutdown();
                return;
            }
            WriteDiag($"Admin check result: {isAdmin}");

            if (!isAdmin)
            {
                WriteDiag("Not admin — showing elevation dialog");
                var result = MessageBox.Show(
                    "GameShift requires administrator privileges for system optimizations. Restart as admin?",
                    "Administrator Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    AdminHelper.RestartAsAdmin();
                }
                else
                {
                    MessageBox.Show(
                        "GameShift cannot function without administrator privileges. The application will now exit.",
                        "Cannot Continue",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                Shutdown();
                return;
            }

            // Step b: Crash recovery check
            WriteDiag("Step b: Crash recovery check...");
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var gameshiftPath = Path.Combine(appDataPath, "GameShift");
            CrashRecoveryHandler.RecoverIfNeeded(gameshiftPath);

            // Steps c-d: Create all services, load settings
            var settings = ServiceFactory.CreateAll(Services, WriteDiag);

            // Wire inter-service events (background mode, monitor pause, game profiles, etc.)
            _eventSubs = EventWiringHelper.WireAll(Services);

            // Wire game tip notifications to snackbar toast
            OneTimeTipAction.TipTriggered += message =>
            {
                Dispatcher.InvokeAsync(() => _mainWindow?.ShowToast("Game Tip", message, TimeSpan.FromSeconds(6)));
            };

            WriteDiag("Core services wired OK");

            // Step e: Create tray icon BEFORE async init (icon appears < 1 sec)
            // Wrapped in try/catch: tray icon registration can fail on some Win10 configurations
            // (e.g., pack URI resolution fails before Application is fully initialized).
            WriteDiag("Step e: Creating TrayIconManager...");
            try
            {
                if (Services.Detector != null)
                {
                    _trayManager = new TrayIconManager(Services.Orchestrator!, Services.Engine!, Services.Detector, settings, Services.DpcMon, Services.ProfileMgr);

                    // Wire tray menu actions — show MainWindow and navigate to the requested page
                    _trayManager.DashboardRequested += () => ShowAndNavigate(typeof(DashboardPage));
                    _trayManager.GameLibraryRequested += () => ShowAndNavigate(typeof(GameLibraryPage));
                    _trayManager.ProfileEditorRequested += () => ShowAndNavigate(typeof(ProfilesPage));
                    _trayManager.SettingsRequested += () => ShowAndNavigate(typeof(SettingsPage));
                }
                WriteDiag("TrayIconManager created OK");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TrayIconManager failed to initialize — continuing without tray icon");
                WriteDiag($"TrayIconManager FAILED: {ex.Message}");
                WriteCrashLog("TRAY_INIT", ex);
            }

            // Hardcodet.NotifyIcon.Wpf works reliably on both Win10 and Win11.
            TrayAvailable = _trayManager != null;
            WriteDiag($"TrayAvailable={TrayAvailable}");

            // Step e2: First-run wizard check
            // Shown AFTER services and tray are wired (wizard's Scan button needs App.Services.Detector),
            // BEFORE ShowAndNavigate so the wizard is the first thing the user sees.
            var settingsFilePath = Path.Combine(SettingsManager.GetAppDataPath(), "settings.json");
            if (!File.Exists(settingsFilePath))
            {
                WriteDiag("Step e2: First launch detected — showing setup wizard...");
                var wizard = new FirstRunWizardWindow();
                wizard.ShowDialog(); // Modal — blocks until wizard closes

                if (wizard.WizardCompleted)
                {
                    // Reload settings from what the wizard just saved
                    settings = SettingsManager.Load();
                    WriteDiag("Setup wizard completed — settings loaded");
                }
                else
                {
                    WriteDiag("Setup wizard cancelled — using defaults");
                }
            }

            // Always show the main window on startup.
            // The tray icon may or may not render on Win10 (Register() succeeds but the icon
            // doesn't appear in the notification area). Showing the window guarantees the user
            // always sees something. The tray icon still works alongside the window when available.
            WriteDiag("Step f: Showing MainWindow...");
            ShowAndNavigate(typeof(DashboardPage));
            WriteDiag("MainWindow shown OK");

            // Step f2: Register global hotkey
            // Must happen after MainWindow is shown so the window handle is available.
            WriteDiag("Step f2: Registering global hotkey...");
            try
            {
                _hotkeyService = new GlobalHotkeyService();
                _hotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
                _hotkeyService.Register(_mainWindow!, settings.GlobalHotkeyBinding);
                WriteDiag("Global hotkey registered OK");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to register global hotkey — continuing without it");
                WriteDiag($"Global hotkey FAILED: {ex.Message}");
            }

            // Step f3: Check for updates and show popup if available
            WriteDiag("Step f3: Checking for updates...");
            await CheckAndShowUpdatePopupAsync(settings);

            // Wire DPC spike toast notifications
            if (Services.DpcMon != null)
            {
                Services.DpcMon.DpcSpikeDetected += OnDpcSpikeDetected;
            }

            // Start named pipe server for single-instance bring-to-front
            _singleInstancePipe = new SingleInstancePipe();
            _singleInstancePipe.ShowRequested += () =>
            {
                Dispatcher.Invoke(() => ShowAndNavigate(typeof(DashboardPage)));
            };
            _singleInstancePipe.StartServer();
            WriteDiag("Single-instance pipe server started");

            // Start watchdog heartbeat — best-effort, silent if watchdog service not installed
            _watchdogHeartbeat = new WatchdogHeartbeatClient();
            _watchdogHeartbeat.Start();
            WriteDiag("Watchdog heartbeat client started");

            // Register boot-recovery scheduled task — idempotent, skipped silently if
            // GameShift.Watchdog.exe is not present alongside the app (e.g. dev builds).
            var watchdogExe = Path.Combine(AppContext.BaseDirectory, "GameShift.Watchdog.exe");
            BootRecoveryTaskManager.EnsureRegistered(watchdogExe);
            WriteDiag($"Boot recovery task registration attempted (watchdog={watchdogExe})");

            // Shutdown mode depends on tray availability.
            // With tray: OnExplicitShutdown keeps app alive when window hides.
            // Without tray (Win10): OnMainWindowClose lets the app exit normally.
            ShutdownMode = TrayAvailable
                ? ShutdownMode.OnExplicitShutdown
                : ShutdownMode.OnMainWindowClose;

            // Step g: Initialize detection system asynchronously
            // Tray icon is already visible - this runs in background
            WriteDiag("Step g: Starting async initialization...");
            await Services.Orchestrator!.InitializeAsync();

            WriteDiag("Fully initialized. Monitoring for games.");
            Log.Information("GameShift fully initialized. Monitoring for games.");
        }
        catch (Exception ex)
        {
            WriteDiag($"STARTUP EXCEPTION: {ex}");
            Log.Fatal(ex, "GameShift failed to start");
            WriteCrashLog("STARTUP", ex);
            MessageBox.Show(
                $"GameShift failed to start:\n\n{ex.Message}\n\nSee crash.log in %AppData%\\GameShift\\",
                "Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Shows the MainWindow (creating it if necessary) and navigates to the specified page.
    /// Replaces the old multi-window OnDashboardRequested / OnGameLibraryRequested / etc. pattern.
    /// </summary>
    private void ShowAndNavigate(Type pageType)
    {
        if (_mainWindow == null || !_mainWindow.IsLoaded)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Show();
        }
        else
        {
            _mainWindow.Show();
            _mainWindow.Activate();
        }

        _mainWindow.NavigateTo(pageType);
    }

    /// <summary>
    /// Checks for available updates and shows the UpdateWindow popup if one exists.
    /// Called during startup after MainWindow is shown. Never blocks or throws.
    /// Respects the user's "Skip This Version" preference.
    /// </summary>
    private async Task CheckAndShowUpdatePopupAsync(AppSettings settings)
    {
        try
        {
            // Check if an update was already downloaded (e.g., from a previous session)
            bool alreadyStaged = File.Exists(GameShift.Core.Updates.UpdateApplier.GetUpdateStagingPath());

            if (alreadyStaged)
            {
                // We have a staged update — check what version it is
                var updateInfo = await GameShift.Core.Updates.UpdateChecker.CheckForUpdateAsync();
                if (updateInfo != null)
                {
                    WriteDiag($"Step f3: Staged update found for v{updateInfo.LatestVersion}");
                    var updateWindow = new UpdateWindow(updateInfo, alreadyStaged: true);
                    if (_mainWindow != null) updateWindow.Owner = _mainWindow;
                    updateWindow.ShowDialog();
                    return;
                }
            }

            // Check GitHub for a newer release
            var update = await GameShift.Core.Updates.UpdateChecker.CheckForUpdateAsync();
            if (update == null)
            {
                WriteDiag("Step f3: No update available");
                return;
            }

            // Respect "Skip This Version" preference
            if (!string.IsNullOrEmpty(settings.SkippedUpdateVersion) &&
                update.LatestVersion == settings.SkippedUpdateVersion)
            {
                WriteDiag($"Step f3: Update v{update.LatestVersion} skipped by user");
                return;
            }

            WriteDiag($"Step f3: Update available v{update.LatestVersion}, showing popup");
            var popup = new UpdateWindow(update);
            if (_mainWindow != null) popup.Owner = _mainWindow;
            popup.ShowDialog();
        }
        catch (Exception ex)
        {
            // Update check failure never blocks startup
            Log.Debug(ex, "Update popup check failed (non-fatal)");
            WriteDiag($"Step f3: Update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles DPC spike events by showing a toast notification with driver info and fix suggestion.
    /// Shows a toast with offending driver name and specific fix description.
    /// The 30-second cooldown is enforced by DpcLatencyMonitor.
    /// </summary>
    private void OnDpcSpikeDetected(object? sender, DpcSpikeEventArgs e)
    {
        if (_trayManager == null) return;

        var driverInfo = e.DriverName != null ? e.DriverName : "unknown driver";
        var fixSuggestion = e.DriverName != null
            ? Services.DpcMon?.GetFixSuggestion(e.DriverName)
            : null;

        var message = $"DPC latency spike: {e.LatencyMicroseconds:F0}\u00B5s ({driverInfo})";
        if (fixSuggestion != null)
        {
            message += $"\n{fixSuggestion}";
        }

        _trayManager.ShowDpcNotification("DPC Latency Warning", message);
    }

    /// <summary>
    /// Handles the global hotkey press.
    /// Dispatches to the UI thread and invokes TrayIconManager.TogglePause().
    /// </summary>
    private void OnGlobalHotkeyPressed()
    {
        Dispatcher.Invoke(() =>
        {
            _trayManager?.TogglePause();

            // Show auto-dismissing toast feedback for hotkey press
            var isPaused = _trayManager?.IsPaused ?? false;
            var title = isPaused ? "Monitoring Paused" : "Monitoring Resumed";
            var message = isPaused
                ? "Game detection is paused. Press Ctrl+Shift+G to resume."
                : "Game detection is active. Optimizations will apply automatically.";
            _mainWindow?.ShowToast(title, message, TimeSpan.FromSeconds(3));
        });
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("GameShift shutting down normally");

        // Deactivate any active optimizations
        if (Services.Orchestrator?.IsOptimizing == true && Services.Engine != null)
        {
            try
            {
                await Services.Engine.DeactivateProfileAsync();
                Log.Information("Optimizations deactivated during shutdown");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to deactivate optimizations during shutdown");
            }
        }

        // Unsubscribe all event handlers from Detector before disposing
        EventWiringHelper.UnwireAll(Services, _eventSubs);

        // Dispose Game Profile manager
        Services.GameProfileMgr?.Dispose();

        // Stop Background Mode services
        Services.BackgroundMode?.Dispose();

        // Dispose optimization engine
        Services.Engine?.Dispose();

        // Stop game monitoring
        Services.Detector?.StopMonitoring();
        Services.Detector?.Dispose();

        // Dispose performance and network monitors (v2.3)
        Services.PerfMon?.Dispose();
        Services.PingMon?.Dispose();

        // Dispose temperature monitor (v2.3)
        Services.TempMon?.Dispose();

        // Dispose DPC trace engine
        Services.DpcTrace?.Dispose();

        // Unsubscribe and dispose DPC monitor
        if (Services.DpcMon != null)
        {
            Services.DpcMon.DpcSpikeDetected -= OnDpcSpikeDetected;
            Services.DpcMon.Dispose();
        }

        // Unregister global hotkey
        _hotkeyService?.Dispose();

        // Stop single-instance pipe server
        _singleInstancePipe?.Dispose();

        // Stop watchdog heartbeat — do this after deactivation so the watchdog sees
        // sessionActive=false before the heartbeat drops and doesn't trigger recovery
        _watchdogHeartbeat?.Dispose();

        // Dispose tray icon
        _trayManager?.Dispose();

        // Clean up lockfile
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var lockfilePath = Path.Combine(appDataPath, "GameShift", "active_session.json");

        try
        {
            if (File.Exists(lockfilePath))
            {
                SystemStateSnapshot.DeleteLockfile(lockfilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clean up lockfile during shutdown");
        }

        // Release single-instance mutex
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// Writes a diagnostic line to stderr and gameshift-diag.log.
    /// Uses the same format as Program.WriteDiag for consistent tracing.
    /// This traces OnStartup step-by-step — critical for diagnosing invisible-app issues
    /// where the app runs but produces no visible UI (e.g., tray icon fails on Win10).
    /// </summary>
    private static void WriteDiag(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] App.{message}";
        Console.Error.WriteLine(line);
        try
        {
            File.AppendAllText("gameshift-diag.log", line + Environment.NewLine);
        }
        catch { }
    }

    private static void WriteCrashLog(string type, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameShift");
            Directory.CreateDirectory(dir);
            var separator = "\n\n" + new string('=', 80) + "\n";
            var content = $"[{DateTime.Now}] {type} EXCEPTION:\n{ex}";
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                separator + content);
        }
        catch { }
    }
}
