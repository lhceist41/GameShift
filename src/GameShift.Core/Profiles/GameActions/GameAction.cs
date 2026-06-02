using GameShift.Core.Detection;
using GameShift.Core.System;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Base class for game-specific actions applied alongside optimization profiles.
/// Unlike IOptimization, GameActions are synchronous and game-specific (not global system optimizations).
/// Apply/Revert lifecycle mirrors IOptimization but is simpler: no async, no profile parameter.
/// </summary>
public abstract class GameAction
{
    /// <summary>Display name for logging and UI.</summary>
    public abstract string Name { get; }

    /// <summary>Tier: 1=auto-apply, 2=user-enables, 3=info/tip only.</summary>
    public virtual int Tier => 1;

    /// <summary>Short impact description for UI display.</summary>
    public virtual string Impact => "";

    /// <summary>Human-readable condition text (e.g., "AMD GPU only").</summary>
    public virtual string Condition => "";

    /// <summary>Whether this action depends on hardware configuration.</summary>
    public virtual bool IsConditional => false;

    /// <summary>
    /// Returns true if this action should be applied given the hardware context.
    /// Override for hardware-dependent actions. Default: always true.
    /// </summary>
    public virtual bool IsHardwareMatch(HardwareScanResult hw) => true;

    /// <summary>
    /// Applies this game-specific action.
    /// Called AFTER IOptimization.Apply completes during profile activation.
    /// Must not throw - log errors and return gracefully.
    /// </summary>
    public abstract void Apply(SystemStateSnapshot snapshot);

    /// <summary>
    /// Reverts this game-specific action.
    /// Called BEFORE IOptimization.Revert during profile deactivation.
    /// Must not throw - log errors and return gracefully.
    /// </summary>
    public abstract void Revert(SystemStateSnapshot snapshot);

    /// <summary>
    /// Returns a self-describing record of how to undo this action's PERSISTENT state after a
    /// crash (registry value, firewall rule, Defender exclusion), or null if there is nothing
    /// crash-recoverable. Called by the orchestrator after a successful Apply and written to the
    /// journal so the watchdog can revert it without reconstructing this instance.
    /// </summary>
    public virtual GameActionRevertRecord? GetCrashRevertRecord() => null;
}

/// <summary>
/// Self-describing revert record for a <see cref="GameAction"/>. <see cref="Kind"/> selects the
/// undo path (<c>registry</c> / <c>firewall</c> / <c>defender</c>); <see cref="Payload"/> is
/// kind-specific JSON. Persisted via <c>GameActionJournalEntry</c> for crash recovery.
/// </summary>
public sealed record GameActionRevertRecord(string Kind, string Payload);
