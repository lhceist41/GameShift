using System;
using System.IO;
using System.Threading;
using System.Windows;
using GameShift.Core.BackgroundMode;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.GameProfiles;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.Journal;
using GameShift.Core.System;
using GameShift.Core.SystemTweaks;
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

    // Stored event handlers for proper unsubscription in OnExit
    private EventHandler<GameDetectedEventArgs>? _bgModeGameStarted;
    private EventHandler? _bgModeAllGamesStopped;
    private EventHandler<GameDetectedEventArgs>? _monitorPauseGameStarted;
    private EventHandler? _monitorResumeAllGamesStopped;
    private EventHandler<ProcessSpawnedEventArgs>? _markDirtyHandler;

    /// <summary>
    /// True when the tray icon was created successfully. When false (tray creation failed),
    /// closing the window exits the app instead of hiding to tray.
    /// </summary>
    internal static bool TrayAvailable { get; private set; }

    // ── Static service properties for page ViewModel construction ────────────────
    // Pages are instantiated by WPF UI NavigationView via parameterless constructors.
    // They access these static properties in their Loaded event to create ViewModels.

    /// <summary>Detection orchestrator — game detection, known games, optimization lifecycle.</summary>
    public static DetectionOrchestrator? Orchestrator { get; private set; }

    /// <summary>Optimization engine — applies/reverts optimization profiles.</summary>
    public static OptimizationEngine? Engine { get; private set; }

    /// <summary>Game detector — scans for active game processes.</summary>
    public static GameDetector? Detector { get; private set; }

    /// <summary>Profile manager — loads and saves per-game optimization profiles.</summary>
    public static ProfileManager? ProfileMgr { get; private set; }

    /// <summary>All optimization modules registered in the engine.</summary>
    public static IOptimization[]? Optimizations { get; private set; }

    /// <summary>VBS/HVCI toggle — optional, null if unavailable.</summary>
    public static VbsHvciToggle? VbsToggle { get; private set; }

    /// <summary>DPC latency monitor — optional, null if ETW unavailable.</summary>
    public static DpcLatencyMonitor? DpcMon { get; private set; }

    /// <summary>System performance monitor (CPU/RAM/GPU) — created at startup.</summary>
    public static SystemPerformanceMonitor? PerfMon { get; private set; }

    /// <summary>Network ping monitor — created at startup, started from dashboard.</summary>
    public static PingMonitor? PingMon { get; private set; }

    /// <summary>Session history store — persists gaming sessions to JSON.</summary>
    public static SessionHistoryStore? SessionStore { get; private set; }

    /// <summary>Session tracker — records gaming sessions via GameDetector events.</summary>
    public static SessionTracker? SessionTrk { get; private set; }

    /// <summary>Temperature monitor — CPU/GPU temps via LibreHardwareMonitor.</summary>
    public static TemperatureMonitor? TempMon { get; private set; }

    /// <summary>Consolidated hardware scan result for conditional game optimizations.</summary>
    public static HardwareScanResult? HardwareScan { get; private set; }

    /// <summary>Known driver database — loaded once at startup, cached.</summary>
    public static KnownDriverDatabase? DriverDb { get; private set; }

    /// <summary>Background Mode service — always-on system optimizations.</summary>
    public static BackgroundModeService? BackgroundMode { get; private set; }

    /// <summary>DPC trace engine — ETW per-driver DPC attribution.</summary>
    public static DpcTraceEngine? DpcTrace { get; private set; }

    /// <summary>DPC fix engine — applies/reverts DPC latency fixes.</summary>
    public static DpcFixEngine? DpcFix { get; private set; }

    /// <summary>System Tweaks manager — one-time registry optimizations.</summary>
    public static SystemTweaksManager? TweaksMgr { get; private set; }

    /// <summary>Game Profile manager — per-game optimization profiles.</summary>
    public static GameProfileManager? GameProfileMgr { get; private set; }

    /// <summary>Driver version tracker — detects installed drivers and checks for advisories.</summary>
    public static DriverVersionTracker? DriverTracker { get; private set; }

    /// <summary>Benchmark service — PresentMon-based frame time capture.</summary>
    public static BenchmarkService? Benchmark { get; private set; }

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
            var lockfilePath = Path.Combine(gameshiftPath, "active_session.json");

            // Ensure GameShift AppData directory exists
            if (!Directory.Exists(gameshiftPath))
            {
                Directory.CreateDirectory(gameshiftPath);
            }

            if (File.Exists(lockfilePath))
            {
                try
                {
                    var snapshot = SystemStateSnapshot.LoadFromLockfile(lockfilePath);

                    Log.Warning("Detected orphaned session lockfile - performing crash recovery");

                    if (snapshot != null)
                    {
                        // Recover processor idle disable state (if GameShift crashed with IDLEDISABLE=1)
                        if (snapshot.IdleDisableSchemeGuid != null)
                        {
                            Log.Information("Crash recovery: restoring processor idle state for scheme {Guid}",
                                snapshot.IdleDisableSchemeGuid);
                            GameShift.Core.Optimization.CpuParkingManager.CleanupStaleIdleDisable(
                                snapshot.IdleDisableSchemeGuid);
                        }

                        // Restore CPU parking settings
                        if (snapshot.CpuParkingSchemeGuid != null && snapshot.CpuParkingEntries.Count > 0)
                        {
                            Log.Information("Crash recovery: restoring CPU parking settings for scheme {Guid}",
                                snapshot.CpuParkingSchemeGuid);
                            GameShift.Core.Optimization.CpuParkingManager.CleanupStaleParkingState(
                                snapshot.CpuParkingSchemeGuid, snapshot.CpuParkingEntries);
                        }

                        // Re-enable scheduled tasks that were disabled during gaming
                        if (snapshot.DisabledScheduledTasks.Count > 0)
                        {
                            Log.Information("Crash recovery: re-enabling {Count} disabled scheduled tasks",
                                snapshot.DisabledScheduledTasks.Count);
                            GameShift.Core.Optimization.ScheduledTaskSuppressor.CleanupStaleDisabledTasks(
                                snapshot.DisabledScheduledTasks);
                        }

                        // Clean up IFEO PerfOptions entries
                        if (snapshot.IfeoEntries.Count > 0)
                        {
                            Log.Information("Crash recovery: cleaning up {Count} IFEO PerfOptions entries",
                                snapshot.IfeoEntries.Count);
                            SystemStateSnapshot.CleanupStaleIfeoEntries(snapshot);
                        }

                        // Restore registry values (GPU, visual effects, network optimizations)
                        if (snapshot.RegistryValues.Count > 0)
                        {
                            Log.Information("Crash recovery: restoring {Count} registry values",
                                snapshot.RegistryValues.Count);
                            RestoreCrashRecoveryRegistryValues(snapshot.RegistryValues);
                        }

                        // Restore Win32PrioritySeparation
                        if (snapshot.OriginalPrioritySeparation.HasValue)
                        {
                            Log.Information("Crash recovery: restoring Win32PrioritySeparation to {Value}",
                                snapshot.OriginalPrioritySeparation.Value);
                            try
                            {
                                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                                    @"SYSTEM\CurrentControlSet\Control\PriorityControl", writable: true);
                                key?.SetValue("Win32PrioritySeparation",
                                    snapshot.OriginalPrioritySeparation.Value,
                                    Microsoft.Win32.RegistryValueKind.DWord);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Crash recovery: failed to restore Win32PrioritySeparation");
                            }
                        }
                    }

                    MessageBox.Show(
                        "GameShift recovered from an unexpected shutdown. All settings have been restored.",
                        "Crash Recovery",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    SystemStateSnapshot.DeleteLockfile(lockfilePath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to perform crash recovery");
                    // Still continue to main window even if recovery fails
                }
            }
            else
            {
                Log.Information("No orphaned session detected, proceeding normally");
            }

            // Step b1.5: Clean up orphaned ETW DPC trace session from a previous crash
            try
            {
                var zombieSession = Microsoft.Diagnostics.Tracing.Session.TraceEventSession
                    .GetActiveSession("GameShift-DPC-Trace");
                if (zombieSession != null)
                {
                    zombieSession.Stop();
                    zombieSession.Dispose();
                    Log.Information("Cleaned up orphaned DPC monitoring ETW session");
                }
            }
            catch { /* Best-effort cleanup — session may not exist */ }

            // Step b2: Clean up leftover update artifacts from a previous auto-update
            GameShift.Core.Updates.UpdateApplier.CleanupPreviousUpdate();

            // Step c: Load settings and log startup
            WriteDiag("Step c: Loading settings...");
            var settings = SettingsManager.Load();
            Log.Information("GameShift started (Admin: {IsAdmin})", isAdmin);
            WriteDiag("Settings loaded OK");

            // Step c2: Apply startup registration from settings
            // Ensures the registry matches settings.json even on first launch
            StartupManager.SetStartWithWindows(settings.StartWithWindows);

            // Step c3: Check VBS/HVCI state (advisory — not an optimization)
            VbsToggle = new VbsHvciToggle();
            VbsToggle.CheckState();

            if (VbsToggle.ShouldShowBanner)
            {
                Log.Information("VBS/HVCI is enabled — dashboard banner will be shown");
            }

            // Step c4: Create DPC latency monitor (passive, not an IOptimization)
            DpcMon = new DpcLatencyMonitor();

            // Step c4b: Load known driver database and create DPC Doctor services
            DriverDb = KnownDriverDatabase.Load();
            DpcTrace = new DpcTraceEngine(DriverDb);
            DpcFix = new DpcFixEngine(settings, () => SettingsManager.Save(settings));

            // Step c5: Quick hardware detection for conditional game optimizations
            // Fast (~200ms): GPU vendor, hybrid CPU, laptop, HAGS, Riot paths.
            // Skips DPC baseline (3s) — that happens in full scan later.
            WriteDiag("Step c5: Quick hardware detection...");
            try
            {
                var hwScanner = new HardwareScanner();
                hwScanner.DetectHardwareQuick(VbsToggle);
                HardwareScan = hwScanner.Result;
                WriteDiag($"Hardware detected: GPU={HardwareScan?.GpuVendor}, Hybrid={HardwareScan?.IsHybridCpu}, Laptop={HardwareScan?.IsLaptop}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Quick hardware detection failed — conditional filtering disabled");
                WriteDiag($"Hardware detection FAILED: {ex.Message}");
            }

            // Step d: Wire core services
            WriteDiag("Step d: Wiring core services...");
            // Create all optimization modules (stored as static for page ViewModel construction)
            Optimizations = new IOptimization[]
            {
                new ServiceSuppressor(),         // 0 - v1
                new PowerPlanSwitcher(),         // 1 - v1
                new TimerResolutionManager(),    // 2 - v1
                new ProcessPriorityBooster(),    // 3 - v1
                new MemoryOptimizer(),           // 4 - v1
                new VisualEffectReducer(),       // 5 - v1
                new NetworkOptimizer(),          // 6 - v1
                new HybridCpuDetector(),         // 7 - v1
                new MpoToggle(),                 // 8 - v2
                new CompetitiveMode(),           // 9 - v2
                new GpuDriverOptimizer(),        // 10 - v2
                new ScheduledTaskSuppressor(),   // 11 - v3 (after ServiceSuppressor)
                new CpuParkingManager(),         // 12 - v3 (after PowerPlanSwitcher)
                new IoPriorityManager(),         // 13 - v4 (after GpuDriverOptimizer)
                new EfficiencyModeController()   // 14 - v4 (last — process-level, reverts first)
            };

            Engine = new OptimizationEngine(Optimizations);
            ProfileMgr = new ProfileManager();

            // Create library scanners
            var scanners = new ILibraryScanner[]
            {
                new SteamLibraryScanner(),
                new EpicLibraryScanner(),
                new GogLibraryScanner(),
                new XboxLibraryScanner()
            };

            Detector = new GameDetector(scanners);

            // Wire process spawn events to shared cache invalidation
            _markDirtyHandler = (_, _) => GameShift.Core.System.ProcessSnapshotService.MarkDirty();
            Detector.ProcessSpawned += _markDirtyHandler;

            var store = new KnownGamesStore();

            Orchestrator = new DetectionOrchestrator(
                Detector, Engine, store, scanners, ProfileMgr, DpcMon, HardwareScan);

            // v2.3: Create performance and network monitors
            PerfMon = new SystemPerformanceMonitor();
            PingMon = new PingMonitor();

            // v2.3: Create session history store and tracker
            SessionStore = new SessionHistoryStore();
            SessionStore.Load();
            SessionTrk = new SessionTracker(Detector!, DpcMon, Engine!, SessionStore);

            // v3: Create and start Background Mode service
            BackgroundMode = new BackgroundModeService();
            BackgroundMode.Start(Detector); // No-op if not enabled in settings
            WriteDiag($"BackgroundMode initialized (enabled={BackgroundMode.IsEnabled})");

            // v2.3: Create temperature monitor
            TempMon = new TemperatureMonitor();

            // Wire Background Mode gaming session hooks
            if (Detector != null && BackgroundMode != null)
            {
                _bgModeGameStarted = (_, _) => BackgroundMode.OnGamingStart();
                _bgModeAllGamesStopped = (object? _, EventArgs _2) => BackgroundMode.OnGamingStop();
                Detector.GameStarted += _bgModeGameStarted;
                Detector.AllGamesStopped += _bgModeAllGamesStopped;
            }

            // Pause dashboard monitors during gaming (they poll every 1-2s and the dashboard isn't visible)
            if (Detector != null)
            {
                _monitorPauseGameStarted = (_, _) => { PerfMon?.Pause(); PingMon?.Pause(); TempMon?.Pause(); };
                _monitorResumeAllGamesStopped = (object? _, EventArgs _2) => { PerfMon?.Resume(); PingMon?.Resume(); TempMon?.Resume(); };
                Detector.GameStarted += _monitorPauseGameStarted;
                Detector.AllGamesStopped += _monitorResumeAllGamesStopped;
            }

            // v6: Create driver version tracker and scan asynchronously
            DriverTracker = new DriverVersionTracker();
            _ = DriverTracker.ScanAndCheckAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Warning(t.Exception?.InnerException, "DriverVersionTracker background scan failed");
            }, TaskContinuationOptions.OnlyOnFaulted);
            WriteDiag("DriverVersionTracker initialized (scanning in background)");

            // v6: Create benchmark service
            Benchmark = new BenchmarkService();
            WriteDiag("BenchmarkService initialized");

            // v4: Create System Tweaks manager (stateless — just detects and applies)
            TweaksMgr = new SystemTweaksManager();
            WriteDiag("SystemTweaksManager initialized");

            // v4: Create and wire Game Profile manager
            GameProfileMgr = new GameProfileManager();
            if (Detector != null && GameProfileMgr != null)
            {
                Detector.GameStarted += GameProfileMgr.OnGameStarted;
                Detector.AllGamesStopped += GameProfileMgr.OnAllGamesStopped;
            }
            // Wire conflict resolution: GameProfiles → ProcessPriorityPersistence
            if (BackgroundMode?.ProcessPriority != null && GameProfileMgr != null)
            {
                BackgroundMode.ProcessPriority.GameProfileActiveProcesses = GameProfileMgr.ActiveSessionProcessNames;
            }
            WriteDiag($"GameProfileManager initialized (profiles={GameProfileMgr?.GetAllProfiles().Count})");

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
                if (Detector != null)
                {
                    _trayManager = new TrayIconManager(Orchestrator, Engine, Detector, settings, DpcMon, ProfileMgr);

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
            // Shown AFTER services and tray are wired (wizard's Scan button needs App.Detector),
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
            if (DpcMon != null)
            {
                DpcMon.DpcSpikeDetected += OnDpcSpikeDetected;
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
            await Orchestrator.InitializeAsync();

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
            ? DpcMon?.GetFixSuggestion(e.DriverName)
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
        if (Orchestrator?.IsOptimizing == true && Engine != null)
        {
            try
            {
                await Engine.DeactivateProfileAsync();
                Log.Information("Optimizations deactivated during shutdown");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to deactivate optimizations during shutdown");
            }
        }

        // Unsubscribe all event handlers from Detector before disposing
        if (Detector != null)
        {
            if (_bgModeGameStarted != null) Detector.GameStarted -= _bgModeGameStarted;
            if (_bgModeAllGamesStopped != null) Detector.AllGamesStopped -= _bgModeAllGamesStopped;
            if (_monitorPauseGameStarted != null) Detector.GameStarted -= _monitorPauseGameStarted;
            if (_monitorResumeAllGamesStopped != null) Detector.AllGamesStopped -= _monitorResumeAllGamesStopped;
            if (_markDirtyHandler != null) Detector.ProcessSpawned -= _markDirtyHandler;
            if (GameProfileMgr != null)
            {
                Detector.GameStarted -= GameProfileMgr.OnGameStarted;
                Detector.AllGamesStopped -= GameProfileMgr.OnAllGamesStopped;
            }
        }

        // Dispose Game Profile manager
        GameProfileMgr?.Dispose();

        // Stop Background Mode services
        BackgroundMode?.Dispose();

        // Dispose optimization engine
        Engine?.Dispose();

        // Stop game monitoring
        Detector?.StopMonitoring();
        Detector?.Dispose();

        // Dispose performance and network monitors (v2.3)
        PerfMon?.Dispose();
        PingMon?.Dispose();

        // Dispose temperature monitor (v2.3)
        TempMon?.Dispose();

        // Dispose DPC trace engine
        DpcTrace?.Dispose();

        // Unsubscribe and dispose DPC monitor
        if (DpcMon != null)
        {
            DpcMon.DpcSpikeDetected -= OnDpcSpikeDetected;
            DpcMon.Dispose();
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

    /// <summary>
    /// Restores registry values from a crash recovery snapshot.
    /// Each key is "{RegistryKeyPath}\{ValueName}" and the value is the original data.
    /// </summary>
    private static void RestoreCrashRecoveryRegistryValues(Dictionary<string, object> registryValues)
    {
        foreach (var (compositeKey, originalValue) in registryValues)
        {
            try
            {
                // Split composite key into key path and value name
                var lastSlash = compositeKey.LastIndexOf('\\');
                if (lastSlash < 0) continue;

                var keyPath = compositeKey[..lastSlash];
                var valueName = compositeKey[(lastSlash + 1)..];

                // Determine the root key
                Microsoft.Win32.RegistryKey? rootKey = null;
                string subKeyPath;

                if (keyPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
                {
                    rootKey = Microsoft.Win32.Registry.LocalMachine;
                    subKeyPath = keyPath["HKEY_LOCAL_MACHINE\\".Length..];
                }
                else if (keyPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
                {
                    rootKey = Microsoft.Win32.Registry.CurrentUser;
                    subKeyPath = keyPath["HKEY_CURRENT_USER\\".Length..];
                }
                else
                {
                    Log.Warning("Crash recovery: unrecognized registry root in key '{Key}'", keyPath);
                    continue;
                }

                using var key = rootKey.OpenSubKey(subKeyPath, writable: true);
                if (key == null)
                {
                    Log.Debug("Crash recovery: registry key '{Key}' no longer exists, skipping", keyPath);
                    continue;
                }

                // Restore the original value
                // originalValue comes from JSON deserialization and may be JsonElement
                if (originalValue is System.Text.Json.JsonElement jsonElement)
                {
                    switch (jsonElement.ValueKind)
                    {
                        case System.Text.Json.JsonValueKind.Number:
                            key.SetValue(valueName, jsonElement.GetInt32(), Microsoft.Win32.RegistryValueKind.DWord);
                            break;
                        case System.Text.Json.JsonValueKind.String:
                            key.SetValue(valueName, jsonElement.GetString() ?? "", Microsoft.Win32.RegistryValueKind.String);
                            break;
                        default:
                            Log.Debug("Crash recovery: unsupported JSON type for registry value '{Key}\\{Name}'", keyPath, valueName);
                            break;
                    }
                }
                else if (originalValue is int intVal)
                {
                    key.SetValue(valueName, intVal, Microsoft.Win32.RegistryValueKind.DWord);
                }
                else if (originalValue is string strVal)
                {
                    key.SetValue(valueName, strVal, Microsoft.Win32.RegistryValueKind.String);
                }

                Log.Debug("Crash recovery: restored registry value '{Key}\\{Name}'", keyPath, valueName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Crash recovery: failed to restore registry value '{Key}'", compositeKey);
            }
        }
    }

    private static void WriteCrashLog(string type, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameShift");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now}] {type} EXCEPTION:\n{ex}");
        }
        catch { }
    }
}
