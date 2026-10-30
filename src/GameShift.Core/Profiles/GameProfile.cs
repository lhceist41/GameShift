using System.Text.Json;
using System.Text.Json.Serialization;
using GameShift.Core.Optimization;

namespace GameShift.Core.Profiles;

/// <summary>
/// Type of the retired <see cref="GameProfile.Intensity"/> value, which nothing reads any more.
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
public class GameProfile : IJsonOnDeserializing
{
    /// <summary>The profile format this version writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Format of the saved profile. A profile written before versioning has no value and reads as 0;
    /// a profile created in code is current.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

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

    // ── Per-optimization toggles ──────────────────────────────────────

    /// <summary>
    /// Whether to suppress non-essential Windows services during gameplay.
    /// Maps to "Windows Service Suppressor" optimization.
    /// </summary>
    public bool SuppressServices { get; set; } = true;

    /// <summary>
    /// Whether to switch from Balanced to the High Performance power plan during gameplay.
    /// Maps to "Ultimate Performance Power Plan" optimization.
    /// </summary>
    public bool SwitchPowerPlan { get; set; } = true;

    /// <summary>
    /// Whether to request a 1 ms system timer resolution during gameplay. Advanced, off by default.
    /// Maps to "System Timer Resolution Manager" optimization.
    /// </summary>
    public bool SetTimerResolution { get; set; } = false;

    /// <summary>
    /// Whether to boost the game process priority during gameplay.
    /// Maps to "Process Priority Booster" optimization.
    /// </summary>
    public bool BoostProcessPriority { get; set; } = true;

    /// <summary>
    /// Whether to pin the game process to performance cores only on hybrid CPUs.
    /// Maps to "Hybrid CPU Optimizer" optimization. Set together with
    /// <see cref="PinToVCacheCcd"/> through <see cref="PinGameCores"/>.
    /// </summary>
    public bool UsePerformanceCoresOnly { get; set; } = false;

    // ── v4 Process Intelligence toggles ─────────────────────────────

    /// <summary>
    /// Whether to put background apps in efficiency mode during gameplay. Drives
    /// "Efficiency Mode Control" and the QoS half of "CPU Scheduling Optimizer".
    /// Gracefully skips on Windows 10.
    /// Default true - reduces background CPU competition with the game.
    /// </summary>
    public bool EnableEfficiencyMode { get; set; } = true;

    // ── v5 Platform Coverage toggles ───────────────────────────────

    /// <summary>
    /// Whether to pin the game process to the V-Cache CCD on AMD X3D processors with two CCDs.
    /// Maps to V-Cache CCD pinning in "Hybrid CPU Optimizer". Set together with
    /// <see cref="UsePerformanceCoresOnly"/> through <see cref="PinGameCores"/>.
    /// </summary>
    public bool PinToVCacheCcd { get; set; } = false;

    /// <summary>
    /// The one "pin the game to P-cores or the V-Cache CCD" switch. Advanced, off by default.
    /// Reads on when either pinning flag is on; setting it sets both.
    /// </summary>
    [JsonIgnore]
    public bool PinGameCores
    {
        get => UsePerformanceCoresOnly || PinToVCacheCcd;
        set
        {
            UsePerformanceCoresOnly = value;
            PinToVCacheCcd = value;
        }
    }

    // ── v2 DPC Monitoring toggles ────────────────────────────────────

    /// <summary>
    /// Whether to enable DPC latency monitoring during gameplay for this game.
    /// When enabled, DpcLatencyMonitor samples every 500ms and alerts on spikes.
    /// Default true -- monitoring is passive and low-overhead.
    /// </summary>
    public bool EnableDpcMonitoring { get; set; } = true;

    /// <summary>
    /// DPC load threshold for spike alerts, in the stored DpcLoad scale (percent x 10000).
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
    /// Default None - most games have no kernel-level anti-cheat.
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
    /// NOT serialized to profile JSON - actions are determined at runtime from executable name.
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

    // ── Retired toggles ───────────────────────────────────────────────
    // Nothing reads these any more. They stay so a profile saved by this version still carries the
    // user's values if an earlier version reads it.

    public OptimizationIntensity Intensity { get; set; } = OptimizationIntensity.Casual;
    public bool OptimizeMemory { get; set; } = true;
    public bool ReduceVisualEffects { get; set; } = true;
    public bool OptimizeNetwork { get; set; } = true;
    public bool ManageIoPriority { get; set; } = true;
    public bool ManageMemoryPriority { get; set; } = true;
    public bool EmptyWorkingSets { get; set; } = true;
    public int HardMinWorkingSetMB { get; set; } = 2048;
    public bool SuppressTier2Services { get; set; } = true;
    public bool SuppressScheduledTasks { get; set; } = true;
    public bool SuppressDefenderScheduledScan { get; set; } = false;
    public bool UnparkCpuCores { get; set; } = true;
    public bool DisableProcessorIdle { get; set; } = false;
    public bool DisableMpo { get; set; } = false;
    public bool EnableCompetitiveMode { get; set; } = false;
    public bool SuspendDiscordOverlay { get; set; } = true;
    public bool SuspendSteamOverlay { get; set; } = true;
    public bool SuspendNvidiaOverlay { get; set; } = true;
    public bool KillWidgets { get; set; } = true;
    public bool EnableGpuOptimization { get; set; } = true;
    public bool ForceMaxPerformancePowerMode { get; set; } = true;
    public bool OptimizeShaderCache { get; set; } = true;
    public bool EnableLowLatencyMode { get; set; } = true;
    public int MemoryThresholdMB { get; set; } = 0;

    // ── Factory methods ───────────────────────────────────────────────

    /// <summary>
    /// Creates the default profile with standard optimization settings.
    /// Timer resolution and core pinning are off; everything else is on.
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

    /// <summary>
    /// Returns a deep copy of this profile. Used so the shared cached default profile can be
    /// handed out without callers' per-session mutations (ProcessId, anti-cheat backfill,
    /// executable info) leaking back into the cache and bleeding across games.
    /// All persisted state round-trips through JSON; the runtime-only [JsonIgnore]
    /// GameSpecificActions list is copied by reference so the clone stays faithful.
    /// </summary>
    public GameProfile Clone()
    {
        var json = JsonSerializer.Serialize(this);
        var clone = JsonSerializer.Deserialize<GameProfile>(json) ?? CreateDefault();
        clone.GameSpecificActions = new List<GameActions.GameAction>(GameSpecificActions);
        return clone;
    }

    void IJsonOnDeserializing.OnDeserializing() => SchemaVersion = 0;

    /// <summary>
    /// Brings a profile saved by an earlier version to the current format. Returns true when it
    /// changed, so the caller saves it once.
    /// </summary>
    public bool UpgradeSchema()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
            return false;

        // Version 0 stored every flag, so a deliberate "on" cannot be told from the old default.
        // Timer resolution and core pinning are off until the user turns them on again.
        SetTimerResolution = false;
        PinGameCores = false;
        SchemaVersion = CurrentSchemaVersion;
        return true;
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
            HybridCpuDetector.OptimizationId => PinGameCores,
            EfficiencyModeController.OptimizationId => EnableEfficiencyMode,
            _ => true // Unknown optimizations are enabled by default
        };
    }
}
