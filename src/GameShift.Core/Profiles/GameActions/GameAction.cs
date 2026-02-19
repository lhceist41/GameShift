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
    /// Must not throw — log errors and return gracefully.
    /// </summary>
    public abstract void Apply(SystemStateSnapshot snapshot);

    /// <summary>
    /// Reverts this game-specific action.
    /// Called BEFORE IOptimization.Revert during profile deactivation.
    /// Must not throw — log errors and return gracefully.
    /// </summary>
    public abstract void Revert(SystemStateSnapshot snapshot);
}
