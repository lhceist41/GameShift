using System;
using System.IO;
using System.Threading.Tasks;
using GameShift.Core.BackgroundMode;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.GameProfiles;
using GameShift.Core.Journal;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Core.SystemTweaks;
using Serilog;

namespace GameShift.App.Services;

/// <summary>
/// Creates and wires all core services during application startup.
/// Populates the <see cref="ServiceRegistry"/> with fully-initialized instances.
/// </summary>
public static class ServiceFactory
{
    /// <summary>
    /// Creates all core services, assigns them to <paramref name="services"/>,
    /// and returns the loaded <see cref="AppSettings"/>.
    /// </summary>
    /// <param name="services">The service registry to populate.</param>
    /// <param name="writeDiag">Diagnostic trace callback for startup logging.</param>
    /// <returns>The loaded application settings.</returns>
    public static AppSettings CreateAll(ServiceRegistry services, Action<string> writeDiag)
    {
        // Load settings and log startup
        writeDiag("Step c: Loading settings...");
        var settings = SettingsManager.Load();
        Log.Information("GameShift started (Admin: true)");
        var install = GameShift.Core.Updates.InstallationDetector.Current;
        Log.Information("Install mode: {Mode} (registered install folder: {InstallFolder})",
            install.Mode, install.RegisteredInstallDirectory ?? "none");
        writeDiag("Settings loaded OK");

        // Reconcile startup registration without spawning schtasks.exe on every launch.
        // Only (re)registers when the scheduled task is missing or points at a stale exe path.
        StartupManager.ReconcileStartupRegistration(settings.StartWithWindows);

        // Check VBS/HVCI state (advisory)
        services.VbsToggle = new VbsHvciToggle();
        services.VbsToggle.CheckState();

        if (services.VbsToggle.ShouldShowBanner)
        {
            Log.Information("VBS/HVCI is enabled -- dashboard banner will be shown");
        }

        // Journal reader for the App layer - surfaces boot-recovery warnings (pending
        // reboot-required DPC fixes, Windows Update since last session) on startup.
        // Separate instance from the one owned by OptimizationEngine; safe because
        // JournalManager serializes through a static lock and writes atomically.
        services.Journal = new JournalManager();
        var journal = services.Journal.LoadJournal();

        RetireBackgroundMode(services, settings, journal?.SessionActive == true, writeDiag);

        // Create DPC latency monitor (passive, not an IOptimization)
        services.DpcMon = new DpcLatencyMonitor();

        // Load known driver database and create DPC Doctor services
        services.DriverDb = KnownDriverDatabase.Load();
        services.DpcTrace = new DpcTraceEngine(services.DriverDb);
        services.DpcFix = new DpcFixEngine();

        // Quick hardware detection for conditional game optimizations
        writeDiag("Step c5: Quick hardware detection...");
        try
        {
            var hwScanner = new HardwareScanner();
            hwScanner.DetectHardwareQuick(services.VbsToggle);
            services.HardwareScan = hwScanner.Result;
            writeDiag($"Hardware detected: GPU={services.HardwareScan?.GpuVendor}, Hybrid={services.HardwareScan?.IsHybridCpu}, Laptop={services.HardwareScan?.IsLaptop}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Quick hardware detection failed -- conditional filtering disabled");
            writeDiag($"Hardware detection FAILED: {ex.Message}");
        }

        // Wire core services
        writeDiag("Step d: Wiring core services...");

        // Create all optimization modules
        services.Optimizations = new IOptimization[]
        {
            new ServiceSuppressor(),         // 0
            new PowerPlanSwitcher(),         // 1
            new TimerResolutionManager(),    // 2
            new ProcessPriorityBooster(),    // 3
            new HybridCpuDetector(),         // 4
            new EfficiencyModeController(),  // 5 (process-level)
            new CpuSchedulingOptimizer()     // 6 (E-core routing + HighQoS)
        };

        services.Engine = new OptimizationEngine(services.Optimizations);
        services.ProfileMgr = new ProfileManager();

        // Create library scanners
        var scanners = new ILibraryScanner[]
        {
            new SteamLibraryScanner(),
            new EpicLibraryScanner(),
            new GogLibraryScanner(),
            new XboxLibraryScanner()
        };

        services.Detector = new GameDetector(scanners);

        var store = new KnownGamesStore();

        services.Orchestrator = new DetectionOrchestrator(
            services.Detector, services.Engine, store, scanners, services.ProfileMgr, services.DpcMon, services.HardwareScan);

        // Performance and network monitors
        services.PerfMon = new SystemPerformanceMonitor();
        services.PingMon = new PingMonitor();

        // Session history store and tracker
        services.SessionStore = new SessionHistoryStore();
        services.SessionStore.Load();
        services.SessionTrk = new SessionTracker(services.Detector!, services.DpcMon, services.Engine!, services.SessionStore);

        // System Tweaks manager
        services.TweaksMgr = new SystemTweaksManager();
        writeDiag("SystemTweaksManager initialized");

        // Game Profile manager
        services.GameProfileMgr = new GameProfileManager();
        writeDiag($"GameProfileManager initialized (profiles={services.GameProfileMgr?.GetAllProfiles().Count})");

        writeDiag("Core services wired OK");

        return settings;
    }

    /// <summary>
    /// Runs the one-time Background Mode cleanup. It runs before any optimization session can
    /// start, so nothing GameShift applies this run can be mistaken for a leftover.
    /// </summary>
    private static void RetireBackgroundMode(ServiceRegistry services, AppSettings settings, bool journalSessionActive, Action<string> writeDiag)
    {
        try
        {
            var settingsFile = Path.Combine(SettingsManager.GetAppDataPath(), "settings.json");
            var result = BackgroundModeRetirement.Run(
                settings.BackgroundModeRetired,
                settings.BackgroundMode,
                journalSessionActive,
                new WindowsPowerPlanStore(),
                new WindowsScheduledTaskStore(),
                () =>
                {
                    // On a fresh install, creating settings.json here would skip the first-run
                    // wizard; with nothing of Background Mode's to find, the step just runs again.
                    if (File.Exists(settingsFile))
                        SettingsManager.Update(s => s.BackgroundModeRetired = true);
                },
                Log.Logger);
            services.BackgroundModeRetirementMessage = result.Message;
            writeDiag($"Background Mode cleanup: completed={result.Completed}, message={result.Message != null}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Background Mode cleanup failed");
        }
    }
}
