using System.Text.Json.Serialization;
using GameShift.Core.Optimization;

namespace GameShift.Core.Profiles;

/// <summary>
/// Controls the aggressiveness of system optimizations for a game.
/// Competitive: full aggressive optimizations (ProcessorIdleDisable, 0.5ms timer, MPO disable).
/// Casual: gentler optimizations (no idle disable, 1ms timer, no MPO disable).
/// </summary>
public enum OptimizationIntensity
{
    Competitive = 0,
    Casual = 1
}

/// <summary>
/// Represents a per-game optimization profile with toggle properties for each optimization.
/// Each game can have different optimizations enabled/disabled, persisted as JSON files.
/// Supports per-game profiles and a default profile for unmatched games.
/// </summary>
public class GameProfile
{
    /// <summary>
    /// Unique identifier for this profile.
    /// Matches the game's ID from GameInfo (e.g. "steam_12345", "manual_cyberpunk2077").
    /// "default" for the default profile.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the game.
    /// Example: "Cyberpunk 2077"
    /// </summary>
    public string GameName { get; set; } = string.Empty;

    /// <summary>
    /// Process ID of the running game instance.
    /// Zero if game is not currently running.
    /// </summary>
    public int ProcessId { get; set; }

    /// <summary>
    /// Optimization intensity tier for this game.
    /// Competitive: aggressive optimizations (idle disable, 0.5ms timer, MPO disable).
    /// Casual: gentler optimizations suitable for RPGs, open-world games.
    /// Default: Casual (safe default for unknown games).
    /// </summary>
    public OptimizationIntensity Intensity { get; set; } = OptimizationIntensity.Casual;

    // ── Per-optimization toggles ──────────────────────────────────────

    /// <summary>
    /// Whether to suppress non-essential Windows services during gameplay.
    /// Maps to "Windows Service Suppressor" optimization.
    /// </summary>
    public bool SuppressServices { get; set; } = true;

    /// <summary>
    /// Whether to switch to the Ultimate Performance power plan during gameplay.
    /// Maps to "Ultimate Performance Power Plan" optimization.
    /// </summary>
    public bool SwitchPowerPlan { get; set; } = true;

    /// <summary>
    /// Whether to set the system timer to high resolution during gameplay.
    /// Maps to "System Timer Resolution Manager" optimization.
    /// </summary>
    public bool SetTimerResolution { get; set; } = true;

    /// <summary>
    /// Whether to boost the game process priority during gameplay.
    /// Maps to "Process Priority Booster" optimization.
    /// </summary>
    public bool BoostProcessPriority { get; set; } = true;

    /// <summary>
    /// Whether to optimize memory by trimming working sets during gameplay.
    /// Maps to "Memory Optimizer" optimization.
    /// </summary>
    public bool OptimizeMemory { get; set; } = true;

    /// <summary>
    /// Whether to reduce visual effects (transparency, animations) during gameplay.
    /// Maps to "Visual Effect Reducer" optimization.
    /// </summary>
    public bool ReduceVisualEffects { get; set; } = true;

    /// <summary>
    /// Whether to optimize network settings (Nagle, throttling) during gameplay.
    /// Maps to "Network Optimizer" optimization.
    /// </summary>
    public bool OptimizeNetwork { get; set; } = true;

    /// <summary>
    /// Whether to pin the game process to performance cores only on hybrid CPUs.
    /// Maps to "Hybrid CPU Optimizer" optimization.
    /// Default false — most games benefit from all cores; enable only for specific titles.
    /// </summary>
    public bool UsePerformanceCoresOnly { get; set; } = false;

    // ── v4 Process Intelligence toggles ─────────────────────────────

    /// <summary>
    /// Whether to lower I/O priority of background processes during gameplay.
    /// Maps to "I/O Priority Management" optimization.
    /// Default true — reduces disk contention from background processes.
    /// </summary>
    public bool ManageIoPriority { get; set; } = true;

    /// <summary>
    /// Whether to apply Windows 11 Efficiency Mode to background processes during gameplay.
    /// Maps to "Efficiency Mode Control" optimization.
    /// Constrains background processes to E-cores on hybrid CPUs.
    /// Gracefully skips on Windows 10.
    /// Default true — reduces background CPU competition with the game.
    /// </summary>
    public bool EnableEfficiencyMode { get; set; } = true;

    /// <summary>
    /// Whether to lower memory priority of background processes during gameplay.
    /// Sub-toggle of OptimizeMemory — only active when memory optimization is enabled.
    /// Causes OS to preferentially evict background process pages under memory pressure.
    /// Default true — protects game memory pages from eviction.
    /// </summary>
    public bool ManageMemoryPriority { get; set; } = true;

    /// <summary>
    /// Whether to call EmptyWorkingSet on background processes at session start.
    /// Moves background process pages to the standby list without destroying game assets.
    /// Sub-toggle of OptimizeMemory — only active when ManageMemoryPriority is enabled.
    /// Default true.
    /// </summary>
    public bool EmptyWorkingSets { get; set; } = true;

    /// <summary>
    /// Hard minimum working set size for the game process, in MB.
    /// The memory manager will not trim game pages below this amount.
    /// 0 = disabled. Default 2048 (2 GB) — protects game pages under system pressure.
    /// </summary>
    public int HardMinWorkingSetMB { get; set; } = 2048;

    // ── v5 Platform Coverage toggles ───────────────────────────────

    /// <summary>
    /// Whether to pin the game process to the V-Cache CCD on AMD X3D processors.
    /// Maps to V-Cache CCD pinning in "Hybrid CPU Optimizer".
    /// Default true — auto-detected; no effect on non-X3D CPUs.
    /// </summary>
    public bool PinToVCacheCcd { get; set; } = true;

    /// <summary>
    /// Whether to suppress Tier 2 (medium-impact) Windows services during gameplay.
    /// Sub-toggle of SuppressServices — only active when service suppression is enabled.
    /// Default true — Tier 2 services (Print Spooler, Fax, etc.) are safe for gaming PCs.
    /// </summary>
    public bool SuppressTier2Services { get; set; } = true;

    // ── v3 New Module toggles ────────────────────────────────────────

    /// <summary>
    /// Whether to disable resource-heavy Windows scheduled tasks during gameplay.
    /// Maps to "Scheduled Task Suppression" optimization.
    /// Default true — safe to disable telemetry, defrag, and update tasks during gaming.
    /// </summary>
    public bool SuppressScheduledTasks { get; set; } = true;

    /// <summary>
    /// Whether to also suppress Windows Defender scheduled scan during gameplay.
    /// Sub-toggle of SuppressScheduledTasks — only active when task suppression is enabled.
    /// Default false — user must opt in. Defender scans will resume when gaming session ends.
    /// </summary>
    public bool SuppressDefenderScheduledScan { get; set; } = false;

    /// <summary>
    /// Whether to unpark all CPU cores during gameplay.
    /// Maps to "CPU Core Unparking" optimization.
    /// Default true — prevents core unparking latency that causes micro-stutters.
    /// </summary>
    public bool UnparkCpuCores { get; set; } = true;

    /// <summary>
    /// Whether to disable processor idle (force C0 state) during gameplay.
    /// Eliminates C-state transition latency but doubles idle power consumption.
    /// Sub-toggle of UnparkCpuCores — only active when CPU core unparking is enabled.
    /// Default true — most impactful single latency setting for gaming.
    /// Laptop users or those with thermal concerns may want to disable this.
    /// </summary>
    public bool DisableProcessorIdle { get; set; } = true;

    // ── v2 Competitive Gaming toggles ────────────────────────────────

    /// <summary>
    /// Whether to disable Multiplane Overlay (MPO) during gameplay.
    /// MPO can cause frame pacing issues, especially on multi-monitor setups with mismatched refresh rates.
    /// Maps to "MPO Toggle" optimization.
    /// Default false — opt-in only, as MPO is fine for most single-monitor setups.
    /// </summary>
    public bool DisableMpo { get; set; } = false;

    /// <summary>
    /// Master toggle for Competitive Mode optimizations.
    /// When enabled, suspends overlay processes (Discord, Steam, NVIDIA) and kills non-essential GPU consumers.
    /// Maps to "Competitive Mode" optimization.
    /// Default false — opt-in only, as overlay suspension is disruptive for casual gaming.
    /// </summary>
    public bool EnableCompetitiveMode { get; set; } = false;

    /// <summary>
    /// Whether to suspend Discord overlay renderer during Competitive Mode.
    /// Sub-toggle of EnableCompetitiveMode — only active when CompetitiveMode is enabled.
    /// Default true — suspend by default when competitive mode is active.
    /// </summary>
    public bool SuspendDiscordOverlay { get; set; } = true;

    /// <summary>
    /// Whether to suspend Steam overlay (GameOverlayUI.exe) during Competitive Mode.
    /// Sub-toggle of EnableCompetitiveMode — only active when CompetitiveMode is enabled.
    /// Default true — suspend by default when competitive mode is active.
    /// </summary>
    public bool SuspendSteamOverlay { get; set; } = true;

    /// <summary>
    /// Whether to suspend NVIDIA overlay (NVIDIA Share/nvcontainer) during Competitive Mode.
    /// Sub-toggle of EnableCompetitiveMode — only active when CompetitiveMode is enabled.
    /// Default true — suspend by default when competitive mode is active.
    /// </summary>
    public bool SuspendNvidiaOverlay { get; set; } = true;

    /// <summary>
    /// Whether to kill Windows Widgets (Widgets.exe, WidgetService.exe) during Competitive Mode.
    /// Sub-toggle of EnableCompetitiveMode — only active when CompetitiveMode is enabled.
    /// Default true — kill by default when competitive mode is active.
    /// </summary>
    public bool KillWidgets { get; set; } = true;

    // ── v2 GPU Optimization toggles ────────────────────────────────

    /// <summary>
    /// Master toggle for GPU driver optimizations during gameplay.
    /// When enabled, applies vendor-specific registry optimizations (NVIDIA/AMD).
    /// Maps to "GPU Driver Optimizer" optimization.
    /// Default true — enabled by default for backwards compatibility.
    /// </summary>
    public bool EnableGpuOptimization { get; set; } = true;

    /// <summary>
    /// Whether to force maximum performance power mode for the GPU during gameplay.
    /// NVIDIA: Power management mode prefer maximum performance (DRS advisory).
    /// AMD: Applied via driver registry settings.
    /// Default true — most competitive gamers want maximum GPU performance.
    /// </summary>
    public bool ForceMaxPerformancePowerMode { get; set; } = true;

    /// <summary>
    /// Whether to optimize GPU shader cache settings during gameplay.
    /// NVIDIA: Sets shader cache to 16GB. AMD: Enables shader cache.
    /// Default true — larger shader cache reduces stutter from shader compilation.
    /// </summary>
    public bool OptimizeShaderCache { get; set; } = true;

    /// <summary>
    /// Whether to enable low latency mode during gameplay.
    /// NVIDIA: Low Latency Mode Ultra. AMD: Standard Anti-Lag (NOT Anti-Lag+).
    /// Default true — reduced input latency is critical for competitive gaming.
    /// </summary>
    public bool EnableLowLatencyMode { get; set; } = true;

    // ── v2 DPC Monitoring toggles ────────────────────────────────────

    /// <summary>
    /// Whether to enable DPC latency monitoring during gameplay for this game.
    /// When enabled, DpcLatencyMonitor samples every 500ms and alerts on spikes.
    /// Default true -- monitoring is passive and low-overhead.
    /// </summary>
    public bool EnableDpcMonitoring { get; set; } = true;

    /// <summary>
    /// DPC latency threshold in microseconds for spike alerts.
    /// When a sample exceeds this value, DpcSpikeDetected fires (with 30s cooldown).
    /// 0 means use the global AppSettings.DefaultDpcThresholdMicroseconds value.
    /// Default 0 -- use global setting.
    /// </summary>
    public int DpcThresholdMicroseconds { get; set; } = 0;

    // ── Anti-Cheat metadata ────────────────────────────────────────

    /// <summary>
    /// The anti-cheat system used by this game.
    /// Determines optimization strategy (runtime API vs IFEO registry fallback)
    /// and VBS/HVCI safety gating.
    /// Default None — most games have no kernel-level anti-cheat.
    /// </summary>
    public AntiCheatType AntiCheat { get; set; } = AntiCheatType.None;

    /// <summary>
    /// Whether this game's anti-cheat blocks runtime SetPriorityClass/SetProcessAffinityMask calls,
    /// requiring IFEO registry-based fallback for priority and affinity.
    /// True for EAC, BattlEye, RICOCHET, and TencentACE titles.
    /// </summary>
    [JsonIgnore]
    public bool RequiresIfeoFallback => AntiCheat is
        AntiCheatType.EasyAntiCheat or
        AntiCheatType.BattlEye or
        AntiCheatType.Ricochet or
        AntiCheatType.TencentACE;

    // ── v2 Game-Specific Actions ───────────────────────────────────

    /// <summary>
    /// Runtime-only game-specific actions populated by CompetitivePresets.
    /// NOT serialized to profile JSON — actions are determined at runtime from executable name.
    /// This avoids polymorphic serialization complexity and keeps profile JSON backwards-compatible.
    /// </summary>
    [JsonIgnore]
    public List<GameActions.GameAction> GameSpecificActions { get; set; } = new();

    // ── Game identity properties ──────────────────────────────────────

    /// <summary>
    /// Executable file name for process matching.
    /// Example: "Cyberpunk2077.exe"
    /// </summary>
    public string ExecutableName { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the game executable.
    /// Example: "D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe"
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Source launcher that detected this game.
    /// Values: "Steam", "Epic", "GOG", "Manual"
    /// </summary>
    public string LauncherSource { get; set; } = string.Empty;

    // ── Custom overrides ──────────────────────────────────────────────

    /// <summary>
    /// Custom list of service names to pause for this game.
    /// Null means use the default service list from global settings.
    /// Non-null overrides the default list entirely.
    /// </summary>
    public List<string>? CustomServicesToPause { get; set; }

    /// <summary>
    /// Per-game memory threshold override in megabytes.
    /// 0 means use the global AppSettings.MemoryThresholdMB value.
    /// Non-zero overrides the global setting for this specific game.
    /// </summary>
    public int MemoryThresholdMB { get; set; } = 0;

    // ── Factory methods ───────────────────────────────────────────────

    /// <summary>
    /// Creates the default profile with standard optimization settings.
    /// All optimizations enabled except UsePerformanceCoresOnly.
    /// Used when no per-game profile exists.
    /// </summary>
    /// <returns>A default GameProfile instance with Id="default"</returns>
    public static GameProfile CreateDefault()
    {
        return new GameProfile
        {
            Id = "default",
            GameName = "Default Profile"
        };
    }

    // ── Query methods ─────────────────────────────────────────────────

    /// <summary>
    /// Checks whether a specific optimization is enabled in this profile.
    /// Maps optimization display names to their corresponding toggle properties.
    /// Unknown optimization names return true (enabled by default).
    /// </summary>
    /// <param name="optimizationName">Display name of the optimization (e.g. "Memory Optimizer")</param>
    /// <returns>True if the optimization is enabled in this profile</returns>
    public bool IsOptimizationEnabled(string optimizationName)
    {
        return optimizationName switch
        {
            ServiceSuppressor.OptimizationId => SuppressServices,
            PowerPlanSwitcher.OptimizationId => SwitchPowerPlan,
            TimerResolutionManager.OptimizationId => SetTimerResolution,
            ProcessPriorityBooster.OptimizationId => BoostProcessPriority,
            MemoryOptimizer.OptimizationId => OptimizeMemory,
            VisualEffectReducer.OptimizationId => ReduceVisualEffects,
            NetworkOptimizer.OptimizationId => OptimizeNetwork,
            HybridCpuDetector.OptimizationId => UsePerformanceCoresOnly,
            MpoToggle.OptimizationId => DisableMpo,
            CompetitiveMode.OptimizationId => EnableCompetitiveMode,
            GpuDriverOptimizer.OptimizationId => EnableGpuOptimization,
            ScheduledTaskSuppressor.OptimizationId => SuppressScheduledTasks,
            CpuParkingManager.OptimizationId => UnparkCpuCores,
            IoPriorityManager.OptimizationId => ManageIoPriority,
            EfficiencyModeController.OptimizationId => EnableEfficiencyMode,
            _ => true // Unknown optimizations are enabled by default
        };
    }
}
