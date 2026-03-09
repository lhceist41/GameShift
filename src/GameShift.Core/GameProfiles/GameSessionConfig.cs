using System.Diagnostics;
using GameShift.Core.Optimization;

namespace GameShift.Core.GameProfiles;

/// <summary>
/// Represents a per-game session configuration for process-level optimizations.
/// This is separate from GameShift.Core.Profiles.GameProfile which handles IOptimization toggles.
/// This class handles process priority, CPU affinity, launcher demotion, and BG mode overrides.
/// </summary>
public class GameSessionConfig
{
    /// <summary>Unique profile ID (e.g., "overwatch2").</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name (e.g., "Overwatch 2").</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Process names to match (e.g., ["Overwatch.exe"]).</summary>
    public string[] ProcessNames { get; set; } = Array.Empty<string>();

    /// <summary>Launcher process names to demote (e.g., ["Battle.net.exe"]).</summary>
    public string[] LauncherProcessNames { get; set; } = Array.Empty<string>();

    /// <summary>Whether this profile is enabled.</summary>
    public bool Enabled { get; set; } = true;

    // -- Session optimizations (applied on game start, reverted on exit) --

    /// <summary>Priority to set on the game process.</summary>
    public ProcessPriorityClass GamePriority { get; set; } = ProcessPriorityClass.High;

    /// <summary>Priority to set on launcher processes. Null = don't change.</summary>
    public ProcessPriorityClass? LauncherPriority { get; set; }

    /// <summary>CPU affinity mask. Null = all cores (or auto-calculated for hybrid).</summary>
    public long? AffinityMask { get; set; }

    /// <summary>If true, auto-calculate P-core-only affinity on Intel hybrid CPUs.</summary>
    public bool IntelHybridPCoreOnly { get; set; }

    // -- Background Mode escalation overrides --

    /// <summary>Override standby list threshold during gaming. Null = use default.</summary>
    public int? GamingStandbyThresholdMB { get; set; }

    /// <summary>Override free memory threshold during gaming. Null = use default.</summary>
    public int? GamingFreeMemoryThresholdMB { get; set; }

    // -- System Tweaks references --

    /// <summary>System tweak class names this profile recommends.</summary>
    public string[] RecommendedTweaks { get; set; } = Array.Empty<string>();

    // -- Anti-Cheat --

    /// <summary>Anti-cheat system used by this game (for IFEO fallback and VBS gating).</summary>
    public AntiCheatType AntiCheat { get; set; } = AntiCheatType.None;

    // -- Notes --

    /// <summary>Profile-specific notes shown in UI.</summary>
    public string[] Notes { get; set; } = Array.Empty<string>();
}
