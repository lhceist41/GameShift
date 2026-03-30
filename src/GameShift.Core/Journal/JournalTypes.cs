using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Core.Journal;

/// <summary>
/// Represents the lifecycle state of a journaled optimization entry.
/// </summary>
public enum OptimizationState
{
    Pending,
    Applied,
    Reverted,
    Failed,
    VerifyFailed
}

/// <summary>
/// Immutable result returned by Apply() and Revert().
/// Carries the serialized original and applied values for deterministic revert
/// and the final state of the operation.
/// </summary>
public record OptimizationResult(
    string Name,
    string OriginalValue,
    string AppliedValue,
    OptimizationState State,
    string? ErrorMessage = null
);

/// <summary>
/// Context passed to IJournaledOptimization.CanApply().
/// Carries the game profile and current system snapshot.
/// </summary>
public class SystemContext
{
    public required GameProfile Profile { get; init; }
    public required SystemStateSnapshot Snapshot { get; init; }
}

/// <summary>
/// Extended optimization interface that supports the state journal (command pattern).
/// Implementations return structured OptimizationResult records so that original
/// and applied values can be persisted for deterministic revert after a crash.
///
/// Named IJournaledOptimization to avoid collision with the existing IOptimization
/// async interface. OptimizationEngine checks for this interface at runtime and
/// routes through the journal path when present.
/// </summary>
public interface IJournaledOptimization
{
    /// <summary>Display name, must match IOptimization.Name on the same class.</summary>
    string Name { get; }

    /// <summary>
    /// Pre-flight check. Store the context for use in Apply().
    /// Return false to skip this optimization without counting it as a failure.
    /// </summary>
    bool CanApply(SystemContext context);

    /// <summary>
    /// Apply the optimization synchronously.
    /// Returns an OptimizationResult with serialized original and applied state.
    /// </summary>
    OptimizationResult Apply();

    /// <summary>
    /// Revert the optimization synchronously.
    /// Returns an OptimizationResult reflecting the restored state.
    /// </summary>
    OptimizationResult Revert();

    /// <summary>
    /// Confirms the applied change is still in effect (e.g., registry value not reverted externally).
    /// Returns false if the change has been undone by another process or a Windows Update.
    /// </summary>
    bool Verify();

    /// <summary>
    /// Called by the watchdog recovery path to revert without any live instance state.
    /// <paramref name="originalValueJson"/> is the <see cref="OptimizationResult.OriginalValue"/>
    /// string persisted in the journal at apply time.
    /// Implementations must parse this JSON and restore the system to the original state.
    /// </summary>
    OptimizationResult RevertFromRecord(string originalValueJson);
}
