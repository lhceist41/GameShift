using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Monitoring;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.System;
using GameShift.Core.Updates;
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

    // True only when THIS process created (and therefore owns) the single-instance mutex. A 2nd
    // instance gets createdNew == false, never owns it, and must NOT call ReleaseMutex() on exit.
    private static bool _ownsMutex;

    private TrayIconManager? _trayManager;
    private MainWindow? _mainWindow;
    private GlobalHotkeyService? _hotkeyService;
    private SingleInstancePipe? _singleInstancePipe;
    private WatchdogHeartbeatClient? _watchdogHeartbeat;

    // Set when a registered install was refused at startup; shown once the dashboard is up.
    private string? _installRefusedMessage;

    // Holds event subscriptions for clean unsubscription in OnExit
    private EventSubscriptions? _eventSubs;

    // Static OneTimeTipAction.TipTriggered subscription - held so OnExit can unsubscribe it; the
    // static event would otherwise keep this App's lambda (and the Dispatcher it captures) alive.
    private Action<string>? _tipTriggeredHandler;

    /// <summary>
    /// True when the tray icon was created successfully. When false (tray creation failed),
    /// closing the window exits the app instead of hiding to tray.
    /// </summary>
    internal static bool TrayAvailable { get; private set; }

    // ── Static service registry for page ViewModel construction ────────────────
    // Pages are instantiated by WPF UI NavigationView via parameterless constructors.
    // They access App.Services.* in their Loaded event to create ViewModels.

    /// <summary>Centralised service registry - replaces individual static properties.</summary>
    public static ServiceRegistry Services { get; } = new();

    /// <summary>
    /// Static constructor: registers global crash handlers as early as possible -
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
        _singleInstanceMutex = new Mutex(true, GameShift.Core.Verification.RevertVerificationRunner.SingleInstanceMutexName, out bool createdNew);
        _ownsMutex = createdNew; // we own the mutex only if we created it
        if (!createdNew)
        {
            WriteDiag("Single-instance check FAILED - another instance is running.");
            WriteDiag("Sending 'show' to first instance via named pipe...");
            SingleInstancePipe.SendShowMessage();
            Shutdown();
            return;
        }
        WriteDiag("Single-instance check passed");

        // WPF Dispatcher thread exception handler.
        // The AppDomain handler registered in static ctor covers pre-startup crashes.
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
                WriteDiag("Not admin - showing elevation dialog");
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

            // Step a2: A portable copy hands over to a trusted installed copy before it touches any state.
            if (HandOverToInstalledCopy())
            {
                Shutdown();
                return;
            }

            // Step b: Crash recovery check
            WriteDiag("Step b: Crash recovery check...");
            CrashRecoveryHandler.RecoverIfNeeded();

            ThemeService.Apply(SettingsManager.Load().AppTheme);

            // Step c: Show the window IMMEDIATELY with a loading overlay, before the heavy
            // service initialization. The user sees GameShift within a fraction of a second
            // instead of a blank screen while WMI / performance-counter / registry work runs.
            // The window stays responsive because the service construction itself happens on a
            // background thread (Step d) - the overlay's progress ring keeps spinning.
            WriteDiag("Step c: Showing MainWindow (loading state)...");
            _mainWindow = new MainWindow();
            _mainWindow.Show();
            WriteDiag("MainWindow shown (loading overlay visible)");

            // Step d: Build all core services OFF the UI thread so the window never freezes.
            // CreateAll constructs only plain, background-safe objects (no WPF UI elements), so it
            // is safe on a thread-pool thread; the tray icon and navigation stay on the UI thread.
            WriteDiag("Step d: Creating core services (background thread)...");
            var settings = await Task.Run(() => ServiceFactory.CreateAll(Services, WriteDiag));
            WriteDiag("Core services created");

            // Wire inter-service events (monitor pause, game profiles, etc.).
            // Back on the UI thread now that services exist.
            _eventSubs = EventWiringHelper.WireAll(Services);

            // Wire game tip notifications to snackbar toast (held in a field so OnExit can unsubscribe)
            _tipTriggeredHandler = message =>
            {
                Dispatcher.InvokeAsync(() => _mainWindow?.ShowToast("Game Tip", message, TimeSpan.FromSeconds(6)));
            };
            OneTimeTipAction.TipTriggered += _tipTriggeredHandler;

            WriteDiag("Core services wired OK");

            // Step e: Create tray icon (UI thread - builds a WPF NotifyIcon).
            // Wrapped in try/catch: tray icon registration can fail on some Win10 configurations
            // (e.g., pack URI resolution fails before Application is fully initialized).
            WriteDiag("Step e: Creating TrayIconManager...");
            try
            {
                if (Services.Detector != null)
                {
                    _trayManager = new TrayIconManager(Services.Orchestrator!, Services.Engine!, Services.Detector, settings, Services.DpcMon);

                    // Wire tray menu actions - show MainWindow and navigate to the requested page
                    _trayManager.DashboardRequested += () => ShowAndNavigate(typeof(DashboardPage));
                    _trayManager.GameLibraryRequested += () => ShowAndNavigate(typeof(GameLibraryPage));
                    _trayManager.ProfileEditorRequested += () => ShowAndNavigate(typeof(ProfilesPage));
                    _trayManager.SettingsRequested += () => ShowAndNavigate(typeof(SettingsPage));
                }
                WriteDiag("TrayIconManager created OK");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TrayIconManager failed to initialize - continuing without tray icon");
                WriteDiag($"TrayIconManager FAILED: {ex.Message}");
                WriteCrashLog("TRAY_INIT", ex);
            }

            // Hardcodet.NotifyIcon.Wpf works reliably on both Win10 and Win11.
            TrayAvailable = _trayManager != null;
            WriteDiag($"TrayAvailable={TrayAvailable}");

            // Step e2: First-run wizard check
            // Shown AFTER services and tray are wired (wizard's Scan button needs App.Services.Detector),
            // BEFORE navigating to the dashboard. The loading overlay sits behind the modal wizard.
            if (SettingsManager.IsFirstRun())
            {
                WriteDiag("Step e2: First launch detected - showing setup wizard...");
                var wizard = new FirstRunWizardWindow { Owner = _mainWindow };
                wizard.ShowDialog(); // Modal - blocks until wizard closes

                if (wizard.WizardCompleted)
                {
                    // Reload settings from what the wizard just saved
                    settings = SettingsManager.Load();
                    WriteDiag("Setup wizard completed - settings loaded");
                }
                else
                {
                    try
                    {
                        SettingsManager.RecordFirstRunSkipped();
                        settings = SettingsManager.Load();
                        WriteDiag("Setup wizard closed - defaults saved with Start with Windows off");
                    }
                    catch (Exception ex)
                    {
                        WriteDiag($"Setup wizard closed - saving defaults failed: {ex.Message}");
                    }
                }
            }

            // Step f: Services are ready - navigate to the dashboard and drop the loading overlay.
            // The window itself has been visible since Step c.
            WriteDiag("Step f: Navigating to dashboard...");
            _mainWindow.NavigateTo(typeof(DashboardPage));
            _mainWindow.HideLoadingOverlay();
            WriteDiag("Dashboard shown OK");

            // Step f1: Startup notices, shown one after another once the dashboard has rendered while
            // the rest of startup carries on. Later dialogs wait for them so none opens over another.
            var startupNotices = ShowStartupNoticesAsync();

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
                Log.Warning(ex, "Failed to register global hotkey - continuing without it");
                WriteDiag($"Global hotkey FAILED: {ex.Message}");
            }

            // Step f3: Check for updates and show popup if available
            WriteDiag("Step f3: Checking for updates...");
            var updatePopupShown = await CheckAndShowUpdatePopupAsync(settings, startupNotices);

            // Step f4: Install notices: a refused install, the old portable file, or the one-time install
            // offer. Skipped while an update popup was shown this start, which may already be shutting the
            // app down.
            if (!updatePopupShown)
                await ShowInstallNoticesAsync(startupNotices);

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

            // Start watchdog heartbeat - best-effort, silent if watchdog service not installed
            _watchdogHeartbeat = new WatchdogHeartbeatClient();
            _watchdogHeartbeat.Start();
            WriteDiag("Watchdog heartbeat client started");

            // The boot-recovery task is not registered from here. It runs its target as SYSTEM, and
            // the only target this code could name is a GameShift.Watchdog.exe next to the app, in a
            // folder standard users can often write to. Releases do not ship the watchdog, so this
            // could only ever register a planted file. It belongs with an admin-only watchdog install.

            // Shutdown mode depends on tray availability.
            // With tray: OnExplicitShutdown keeps app alive when window hides.
            // Without tray (Win10): OnMainWindowClose lets the app exit normally.
            ShutdownMode = TrayAvailable
                ? ShutdownMode.OnExplicitShutdown
                : ShutdownMode.OnMainWindowClose;

            // Step g: Initialize detection system asynchronously
            // Tray icon is already visible - this runs in background
            WriteDiag("Step g: Starting async initialization...");

            // Firewall rules an earlier version or a crash left behind. Started first so that a game
            // detected as monitoring starts waits for it; it runs in the background and never throws.
            _ = Services.Orchestrator!.RemoveLeftoverFirewallRulesAsync();

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
                $"GameShift failed to start:\n\n{ex.Message}\n\nSee crash.log in %ProgramData%\\GameShift\\logs\\",
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
    /// Shows the startup notices in a fixed order, each after the previous one is closed: crash
    /// recovery, the Background Mode removal, then the journal warnings. Runs once the dashboard has
    /// rendered, at <see cref="System.Windows.Threading.DispatcherPriority.Background"/>: waiting for
    /// ApplicationIdle could take a minute on a slow machine while startup work keeps the dispatcher
    /// busy. Never throws.
    /// </summary>
    private async Task ShowStartupNoticesAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to wait for the dashboard before the startup notices");
        }

        try { ShowStartupRecoveryNotice(); }
        catch (Exception ex) { Log.Warning(ex, "Failed to show the crash recovery notice"); }

        try { ShowBackgroundModeRetirementNotice(); }
        catch (Exception ex) { Log.Warning(ex, "Failed to show the Background Mode notice"); }

        CheckJournalWarnings();
    }

    /// <summary>
    /// Surfaces any boot-recovery warnings recorded by the watchdog in the session journal:
    /// (1) pending DPC fixes that required a reboot but haven't been acknowledged, and
    /// (2) a Windows Update that occurred between the last session and this boot.
    /// Never throws; failures are logged but non-fatal.
    /// </summary>
    private void CheckJournalWarnings()
    {
        try
        {
            var journal = Services.Journal;
            if (journal == null) return;

            var pendingFixes = journal.GetPendingRebootFixDescriptions();
            var buildWarning = journal.GetBuildChangedWarning();

            if (pendingFixes.Count == 0 && string.IsNullOrEmpty(buildWarning))
                return;

            if (pendingFixes.Count > 0)
            {
                var msg = "A previous DPC fix required a system reboot:\n\n" +
                          string.Join("\n", pendingFixes.Select(d => "  \u2022 " + d)) +
                          "\n\nHas the system been rebooted since?";

                var result = MessageBox.Show(
                    _mainWindow!,
                    msg,
                    "Pending DPC Fixes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    journal.ClearPendingRebootFixes();
                }
            }

            if (!string.IsNullOrEmpty(buildWarning))
            {
                MessageBox.Show(
                    _mainWindow!,
                    "Windows was updated since your last GameShift session:\n\n" + buildWarning +
                    "\n\nSome optimizations may need to be reapplied.",
                    "Windows Build Changed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                journal.ClearBuildChangedWarning();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to check journal warnings");
        }
    }

    /// <summary>
    /// Tells the user once what the Background Mode cleanup changed or could not change.
    /// </summary>
    private void ShowStartupRecoveryNotice()
    {
        var recovery = Services.StartupRecovery;
        var message = recovery?.Notice;
        if (recovery == null || string.IsNullOrEmpty(message)) return;

        var (title, icon) = recovery.Outcome switch
        {
            StartupRecoveryOutcome.Recovered => ("Changes Undone", MessageBoxImage.Information),
            StartupRecoveryOutcome.Untrusted => ("Session Record Ignored", MessageBoxImage.Warning),
            StartupRecoveryOutcome.FolderUnavailable => ("Crash Recovery Unavailable", MessageBoxImage.Warning),
            StartupRecoveryOutcome.Unreadable => ("Session Record Unreadable", MessageBoxImage.Warning),
            _ => ("Changes Not Fully Undone", MessageBoxImage.Warning),
        };

        MessageBox.Show(_mainWindow!, message, title, MessageBoxButton.OK, icon);
    }

    private void ShowBackgroundModeRetirementNotice()
    {
        var message = Services.BackgroundModeRetirementMessage;
        if (string.IsNullOrEmpty(message)) return;

        MessageBox.Show(
            _mainWindow!,
            message,
            "Background Mode Removed",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// Checks for available updates and shows the UpdateWindow popup if one exists.
    /// Called during startup after MainWindow is shown. Never blocks or throws.
    /// Respects the user's "Skip This Version" preference.
    /// The popup waits until <paramref name="startupNotices"/> have been closed.
    /// Returns true if the popup was shown.
    /// </summary>
    private async Task<bool> CheckAndShowUpdatePopupAsync(AppSettings settings, Task startupNotices)
    {
        try
        {
            // Check GitHub for a newer release
            var update = await GameShift.Core.Updates.UpdateChecker.CheckForUpdateAsync();
            if (update == null)
            {
                WriteDiag("Step f3: No update available");
                return false;
            }

            // Respect "Skip This Version" preference
            if (!string.IsNullOrEmpty(settings.SkippedUpdateVersion) &&
                update.LatestVersion == settings.SkippedUpdateVersion)
            {
                WriteDiag($"Step f3: Update v{update.LatestVersion} skipped by user");
                return false;
            }

            WriteDiag($"Step f3: Update available v{update.LatestVersion}, showing popup");
            await startupNotices;
            var popup = new UpdateWindow(update);
            if (_mainWindow != null) popup.Owner = _mainWindow;
            popup.ShowDialog();
            return true;
        }
        catch (Exception ex)
        {
            // Update check failure never blocks startup
            Log.Debug(ex, "Update popup check failed (non-fatal)");
            WriteDiag($"Step f3: Update check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A portable copy started while a trusted install is registered offers to start the installed copy
    /// and exits; on a logon start it exits without asking. Runs on the UI thread before the first await,
    /// where the single-instance mutex can still be released. Returns true when this copy is to exit.
    /// </summary>
    private bool HandOverToInstalledCopy()
    {
        StartupHandover handover;
        try
        {
            handover = InstallHandover.DecideAtStart(InstallationDetector.Current, Program.LaunchOptions.StartedAtLogon);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Install handover check failed - continuing as this copy");
            return false;
        }

        switch (handover.Kind)
        {
            case HandoverKind.ExitQuietly:
                WriteDiag("Step a2: Logon start of a portable copy while GameShift is installed - exiting");
                Log.Information("Logon start of a portable copy while a trusted install is registered - exiting quietly");
                return true;

            case HandoverKind.InstallRefused:
                Log.Warning("Registered install not used: {Reasons}", string.Join("; ", handover.Reasons ?? Array.Empty<string>()));
                _installRefusedMessage = handover.RefusedMessage;
                return false;

            case HandoverKind.OfferInstalledCopy:
                break;

            default:
                return false;
        }

        WriteDiag("Step a2: Portable copy while GameShift is installed - offering the installed copy");
        var answer = MessageBox.Show(
            "GameShift is installed on this PC. Start the installed copy instead? This copy then closes.\n\n" +
            "The installed copy uses the same settings and game library, and installs updates for you.",
            "GameShift Is Installed",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return false;

        var installedExe = handover.InstalledExePath!;

        // Released first: the installed copy would otherwise find it held, forward "show" to this
        // process and exit.
        ReleaseSingleInstanceMutex();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installedExe,
                WorkingDirectory = Path.GetDirectoryName(installedExe)!,
                UseShellExecute = false
            });
            Log.Information("Started the installed copy at {Path}", installedExe);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not start the installed copy at {Path}", installedExe);
            MessageBox.Show(
                "The installed copy could not be started. Start GameShift from the Start menu.",
                "GameShift Is Installed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return true;
    }

    private static void ReleaseSingleInstanceMutex()
    {
        if (_ownsMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* not owner / already released - nothing to do */ }
            _ownsMutex = false;
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    /// <summary>
    /// Once <paramref name="startupNotices"/> have been closed: says why a registered install was not
    /// used, names the portable file an install replaced, or offers a portable copy with nothing
    /// installed, once, to install GameShift. Never throws.
    /// </summary>
    private async Task ShowInstallNoticesAsync(Task startupNotices)
    {
        try
        {
            var install = InstallationDetector.Current;

            if (_installRefusedMessage != null)
            {
                await startupNotices;
                _mainWindow?.ShowToast("Installed Copy Not Used", _installRefusedMessage, TimeSpan.FromSeconds(10));
                return;
            }

            var recorded = SettingsManager.Load().MigratedPortableExePath;
            var note = InstallHandover.DecideOldCopyNote(install, recorded, File.Exists);
            if (note.ClearRecord)
                SettingsManager.Update(s => { if (s.MigratedPortableExePath == recorded) s.MigratedPortableExePath = null; });
            if (note.PathToMention != null)
            {
                await startupNotices;
                MessageBox.Show(
                    _mainWindow!,
                    "GameShift is now installed and installs its own updates. The copy you used before is no longer needed:\n\n" +
                    note.PathToMention + "\n\n" +
                    "You can delete that file. GameShift does not delete it for you.",
                    "Old Copy Can Be Deleted",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!InstallHandover.ShouldOfferInstall(install, SettingsManager.Load().InstallOfferShown, Program.LaunchOptions.StartedAtLogon))
                return;

            await startupNotices;
            SettingsManager.Update(s => s.InstallOfferShown = true);

            WriteDiag("Step f4: Portable copy - showing the one-time install offer");
            var result = MessageBox.Show(
                _mainWindow!,
                "GameShift now comes with an installer, and it is the recommended way to run it.\n\n" +
                "Installing puts GameShift in C:\\Program Files\\GameShift, where only administrators can " +
                "change its files, and installs future updates for you. Your settings and game library are kept.\n\n" +
                "Install GameShift now? This copy closes while the installer runs, and the installed copy starts " +
                "when it is done. This message is shown only once.",
                "Install GameShift",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
                _ = InstallFromPortableAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Install notices failed (non-fatal)");
        }
    }

    /// <summary>
    /// Installs GameShift through the installer of the latest release, staged in Program Files like a
    /// portable update, and exits once it runs. Not awaited by startup, so monitoring starts meanwhile.
    /// Never throws.
    /// </summary>
    private async Task InstallFromPortableAsync()
    {
        UpdateInfo? release = null;
        try
        {
            _mainWindow?.ShowToast("Installing GameShift",
                "Downloading the installer. GameShift closes and installs itself when the download is done.",
                TimeSpan.FromSeconds(8));

            release = await UpdateChecker.GetLatestReleaseAsync();
            var download = release == null
                ? new UpdateDownloadResult(null, "GameShift could not look up the latest release.")
                : await UpdateCoordinator.DownloadAsync(release, new Progress<double>(), CancellationToken.None);

            if (download.Staged == null)
            {
                ShowInstallFailed(download.FailureReason, release);
                return;
            }

            var exePath = Environment.ProcessPath;
            SettingsManager.Update(s => s.MigratedPortableExePath = exePath);
            if (UpdateCoordinator.Apply(download.Staged))
            {
                Shutdown();
                return;
            }

            SettingsManager.Update(s => { if (s.MigratedPortableExePath == exePath) s.MigratedPortableExePath = null; });
            ShowInstallFailed("The installer could not be started.", release);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Installing from the portable copy failed");
            ShowInstallFailed("Something went wrong while preparing the installer.", release);
        }
    }

    private void ShowInstallFailed(string? reason, UpdateInfo? release)
    {
        try
        {
            var answer = MessageBox.Show(
                _mainWindow!,
                "GameShift could not be installed from here. " + (reason ?? "The download failed or could not be verified.") +
                "\n\nOpen the release page to download the installer yourself?",
                "Install GameShift",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = release?.ReleaseUrl ?? UpdateAssetSelector.LatestInstallerUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show the install failure");
        }
    }

    /// <summary>
    /// Handles DPC spike events by showing a toast notification with the DPC load.
    /// The 30-second cooldown is enforced by DpcLatencyMonitor.
    /// </summary>
    private void OnDpcSpikeDetected(object? sender, DpcSpikeEventArgs e)
    {
        if (_trayManager == null) return;

        var message = $"Drivers used {DpcLoad.Format(e.LatencyMicroseconds)} of CPU time for deferred procedure calls (DPC load). Latency Doctor can find the driver.";

        _trayManager.ShowDpcNotification("High DPC Load", message);
    }

    /// <summary>
    /// Handles the global hotkey press: toggles pause through TrayIconManager and, once the change
    /// has taken effect, shows the resulting state. Runs on the UI thread, since the hotkey arrives
    /// through the main window's message hook.
    /// </summary>
    private async void OnGlobalHotkeyPressed()
    {
        if (_trayManager == null) return;

        var isPaused = await _trayManager.TogglePauseAsync();

        try
        {
            // Show auto-dismissing toast feedback for hotkey press
            var title = isPaused ? "Monitoring Paused" : "Monitoring Resumed";
            var message = isPaused
                ? "Game detection is paused. Press Ctrl+Shift+G to resume."
                : "Game detection is active. Optimizations will apply automatically.";
            _mainWindow?.ShowToast(title, message, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show the pause toggle toast");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("GameShift shutting down normally");

        // Unsubscribe the static tip-toast handler so it does not outlive the App instance.
        if (_tipTriggeredHandler != null)
        {
            OneTimeTipAction.TipTriggered -= _tipTriggeredHandler;
            _tipTriggeredHandler = null;
        }

        // Release long-lived page ViewModel event subscriptions (DashboardViewModel,
        // etc.) BEFORE disposing core services so their handlers do not fire during
        // service teardown. The DashboardPage's Unloaded handler uses the lightweight
        // StopTimers path for navigate-away; shutdown requires a full Cleanup.
        try
        {
            _mainWindow?.CleanupLongLivedPageViewModels();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clean up page ViewModels during shutdown");
        }

        // End any active session: a game session's game-specific actions and the engine's
        // optimizations, or a Quick Optimize session.
        // Block synchronously - async void can be killed by the framework before
        // the await completes, leaving optimizations un-reverted.
        // Stop game monitoring FIRST so a game-close event can't kick off a concurrent revert that
        // holds the engine semaphore while we block on it below.
        Services.Detector?.StopMonitoring();

        if (Services.Orchestrator != null)
        {
            try
            {
                // Run on a thread-pool thread so async continuations don't post back to the (blocked)
                // UI dispatcher - avoids a sync-over-async deadlock that would hang the app on exit.
                Task.Run(() => Services.Orchestrator.ShutdownAsync()).GetAwaiter().GetResult();
                Log.Information("Optimization session ended for shutdown");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to end the optimization session during shutdown");
            }
        }

        // Unsubscribe all event handlers from Detector before disposing
        EventWiringHelper.UnwireAll(Services, _eventSubs);

        // Clean up DetectionOrchestrator event subscriptions
        Services.Orchestrator?.Cleanup();

        // Dispose session tracker (unsubscribes from Detector events)
        Services.SessionTrk?.Dispose();

        // Dispose optimization engine
        Services.Engine?.Dispose();

        // Stop game monitoring
        Services.Detector?.StopMonitoring();
        Services.Detector?.Dispose();

        // Dispose performance and network monitors (v2.3)
        Services.PerfMon?.Dispose();
        Services.PingMon?.Dispose();

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

        // Stop watchdog heartbeat - do this after deactivation so the watchdog sees
        // sessionActive=false before the heartbeat drops and doesn't trigger recovery
        _watchdogHeartbeat?.Dispose();

        // Dispose tray icon
        _trayManager?.Dispose();

        // Release the single-instance mutex only if THIS instance owns it. A 2nd instance signals the
        // first and exits without owning it - calling ReleaseMutex() there throws ApplicationException
        // ("synchronization method called from an unsynchronized block") and crashes the exit path.
        if (_ownsMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* not owner / already released - nothing to do */ }
        }
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// Writes a diagnostic line to stderr and gameshift-diag.log.
    /// Uses the same format and file as Program.WriteDiag for consistent tracing: the admin-only logs
    /// folder, never the working directory or the user-writable %AppData%\GameShift.
    /// This traces OnStartup step-by-step - critical for diagnosing invisible-app issues
    /// where the app runs but produces no visible UI (e.g., tray icon fails on Win10).
    /// </summary>
    private static void WriteDiag(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] App.{message}";
        Console.Error.WriteLine(line);
        try
        {
            if (SettingsManager.IsLogsFolderReady())
                File.AppendAllText(Path.Combine(SettingsManager.GetLogsPath(), "gameshift-diag.log"), line + Environment.NewLine);
        }
        catch { }
    }

    private static void WriteCrashLog(string type, Exception? ex)
    {
        try
        {
            if (!SettingsManager.IsLogsFolderReady())
                return;
            var separator = "\n\n" + new string('=', 80) + "\n";
            var content = $"[{DateTime.Now}] {type} EXCEPTION:\n{ex}";
            File.AppendAllText(
                Path.Combine(SettingsManager.GetLogsPath(), "crash.log"),
                separator + content);
        }
        catch { }
    }
}
