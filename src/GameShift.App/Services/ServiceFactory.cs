using System;
using System.Threading.Tasks;
using GameShift.Core.BackgroundMode;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.GameProfiles;
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
        writeDiag("Settings loaded OK");

        // Apply startup registration from settings
        StartupManager.SetStartWithWindows(settings.StartWithWindows);

        // Check VBS/HVCI state (advisory)
        services.VbsToggle = new VbsHvciToggle();
        services.VbsToggle.CheckState();

        if (services.VbsToggle.ShouldShowBanner)
        {
            Log.Information("VBS/HVCI is enabled -- dashboard banner will be shown");
        }

        // Create DPC latency monitor (passive, not an IOptimization)
        services.DpcMon = new DpcLatencyMonitor();

        // Load known driver database and create DPC Doctor services
        services.DriverDb = KnownDriverDatabase.Load();
        services.DpcTrace = new DpcTraceEngine(services.DriverDb);
        services.DpcFix = new DpcFixEngine(settings, () => SettingsManager.Save(settings));

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
            new EfficiencyModeController(),  // 14 - v4 (process-level)
            new CpuSchedulingOptimizer(),    // 15 - v4 (E-core routing + HighQoS)
            new SessionSystemTweaksOptimizer() // 16 - v4 (last)
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

        // Background Mode service
        services.BackgroundMode = new BackgroundModeService();
        services.BackgroundMode.Start(services.Detector);
        writeDiag($"BackgroundMode initialized (enabled={services.BackgroundMode.IsEnabled})");

        // Temperature monitor
        services.TempMon = new TemperatureMonitor();

        // Driver version tracker (scan asynchronously)
        services.DriverTracker = new DriverVersionTracker();
        _ = services.DriverTracker.ScanAndCheckAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                Log.Warning(t.Exception?.InnerException, "DriverVersionTracker background scan failed");
        }, TaskContinuationOptions.OnlyOnFaulted);
        writeDiag("DriverVersionTracker initialized (scanning in background)");

        // Benchmark service
        services.Benchmark = new BenchmarkService();
        writeDiag("BenchmarkService initialized");

        // System Tweaks manager
        services.TweaksMgr = new SystemTweaksManager();
        writeDiag("SystemTweaksManager initialized");

        // Game Profile manager
        services.GameProfileMgr = new GameProfileManager();

        // Wire conflict resolution: GameProfiles -> ProcessPriorityPersistence
        if (services.BackgroundMode?.ProcessPriority != null && services.GameProfileMgr != null)
        {
            services.BackgroundMode.ProcessPriority.GameProfileActiveProcesses = services.GameProfileMgr.ActiveSessionProcessNames;
        }
        writeDiag($"GameProfileManager initialized (profiles={services.GameProfileMgr?.GetAllProfiles().Count})");

        writeDiag("Core services wired OK");

        return settings;
    }
}
