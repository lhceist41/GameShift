namespace GameShift.Core.SystemTweaks;

/// <summary>
/// Defines the contract for a one-time system optimization tweak.
/// Each tweak detects current state, can apply/revert, and tracks whether it requires reboot.
/// </summary>
public interface ISystemTweak
{
    /// <summary>Display name for this tweak.</summary>
    string Name { get; }

    /// <summary>Human-readable description of what this tweak does.</summary>
    string Description { get; }

    /// <summary>Category for UI grouping (e.g., "Windows Gaming", "GPU", "CPU Scheduling").</summary>
    string Category { get; }

    /// <summary>Whether this tweak requires a system reboot to take effect.</summary>
    bool RequiresReboot { get; }

    /// <summary>
    /// Detects whether the optimization is currently active on the system.
    /// Does NOT check whether GameShift applied it — just the current state.
    /// </summary>
    bool DetectIsApplied();

    /// <summary>
    /// Applies the optimization. Returns a JSON string of original values for revert.
    /// Returns null if already applied or if apply fails.
    /// </summary>
    string? Apply();

    /// <summary>
    /// Reverts the optimization using stored original values.
    /// </summary>
    /// <param name="originalValuesJson">JSON from the Apply() return value.</param>
    /// <returns>True if revert succeeded.</returns>
    bool Revert(string? originalValuesJson);
}
