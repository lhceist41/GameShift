using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Core.Optimization;

/// <summary>
/// Defines the contract for all system optimizations.
/// Each optimization must be reversible and track its applied state.
/// Foundation for the modular optimization system.
/// </summary>
public interface IOptimization
{
    /// <summary>
    /// Display name for this optimization.
    /// Example: "Windows Service Suppressor"
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Human-readable description of what this optimization does.
    /// Example: "Temporarily stops non-essential Windows services"
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Indicates whether this optimization is currently applied.
    /// Must be false initially, true after successful ApplyAsync, false after RevertAsync.
    /// </summary>
    bool IsApplied { get; }

    /// <summary>
    /// Indicates whether this optimization can be applied on the current system.
    /// Example: PowerPlanSwitcher might return false if Ultimate Performance plan doesn't exist.
    /// OptimizationEngine skips optimizations where IsAvailable returns false.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Applies this optimization to the system.
    /// Must record original state in the provided snapshot before making changes.
    /// Returns true on success, false on failure (non-throwing).
    ///
    /// CRITICAL: Do NOT throw exceptions from this method. Return false instead.
    /// The engine logs warnings for failures but continues applying other optimizations.
    /// </summary>
    /// <param name="snapshot">Snapshot to record original state for later revert</param>
    /// <param name="profile">Game profile containing process info and settings</param>
    /// <returns>True if optimization applied successfully, false otherwise</returns>
    Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile);

    /// <summary>
    /// Reverts this optimization, restoring system to original state.
    /// Must attempt revert even if the original ApplyAsync partially failed.
    /// Returns true on success, false on failure (non-throwing).
    ///
    /// The engine calls this in LIFO order (last-applied reverts first).
    /// CRITICAL: Do NOT throw exceptions from this method. Return false instead.
    /// </summary>
    /// <param name="snapshot">Snapshot containing original state to restore</param>
    /// <returns>True if revert succeeded, false otherwise</returns>
    Task<bool> RevertAsync(SystemStateSnapshot snapshot);
}
