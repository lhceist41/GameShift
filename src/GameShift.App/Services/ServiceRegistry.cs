using GameShift.Core.BackgroundMode;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.GameProfiles;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.SystemTweaks;

namespace GameShift.App.Services;

public class ServiceRegistry
{
    public DetectionOrchestrator? Orchestrator { get; set; }
    public OptimizationEngine? Engine { get; set; }
    public GameDetector? Detector { get; set; }
    public ProfileManager? ProfileMgr { get; set; }
    public IOptimization[]? Optimizations { get; set; }
    public VbsHvciToggle? VbsToggle { get; set; }
    public DpcLatencyMonitor? DpcMon { get; set; }
    public SystemPerformanceMonitor? PerfMon { get; set; }
    public PingMonitor? PingMon { get; set; }
    public SessionHistoryStore? SessionStore { get; set; }
    public SessionTracker? SessionTrk { get; set; }
    public TemperatureMonitor? TempMon { get; set; }
    public HardwareScanResult? HardwareScan { get; set; }
    public KnownDriverDatabase? DriverDb { get; set; }
    public BackgroundModeService? BackgroundMode { get; set; }
    public DpcTraceEngine? DpcTrace { get; set; }
    public DpcFixEngine? DpcFix { get; set; }
    public SystemTweaksManager? TweaksMgr { get; set; }
    public GameProfileManager? GameProfileMgr { get; set; }
    public DriverVersionTracker? DriverTracker { get; set; }
    public BenchmarkService? Benchmark { get; set; }
}
