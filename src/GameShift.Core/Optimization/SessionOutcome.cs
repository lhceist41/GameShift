namespace GameShift.Core.Optimization;

/// <summary>
/// What activation did with one module.
/// </summary>
public enum ModuleOutcomeKind
{
    Applied,
    Failed,

    /// <summary>Turned off in the game profile.</summary>
    SkippedDisabled,

    /// <summary>The module's own pre-flight check (CanApply) declined it.</summary>
    NotApplicable,

    /// <summary>Not available on this system (IsAvailable is false).</summary>
    Unavailable,

    /// <summary>Already applied, so it was left as it was.</summary>
    AlreadyApplied,
}

/// <param name="Reason">Why it failed; null for the other kinds, whose kind says it all.</param>
public sealed record ModuleOutcome(string ModuleName, ModuleOutcomeKind Kind, string? Reason, DateTime TimeUtc);

/// <summary>
/// What deactivation did with one applied module.
/// </summary>
public enum ModuleRevertKind
{
    Reverted,
    RevertFailed,
}

/// <param name="Reason">Why the revert failed; null when it was reverted.</param>
public sealed record ModuleRevert(string ModuleName, ModuleRevertKind Kind, string? Reason, DateTime TimeUtc);

/// <summary>
/// One activation and, once it ended, its deactivation.
/// </summary>
/// <param name="StartedUtc">When the activation started; null when deactivation ran without one.</param>
/// <param name="Modules">Every module in apply order, including the ones that were not applied.</param>
/// <param name="Reverts">Reverts in the order they ran (LIFO); empty until deactivation.</param>
/// <param name="EndedUtc">When deactivation finished; null for an activation outcome.</param>
/// <param name="RevertedByRecovery">
/// Crash recovery ran during this session, so deactivation reverted nothing itself; per-module
/// results of the recovery are not part of this outcome.
/// </param>
public sealed record SessionOutcome(
    DateTime? StartedUtc,
    string? GameName,
    IReadOnlyList<ModuleOutcome> Modules,
    IReadOnlyList<ModuleRevert> Reverts,
    DateTime? EndedUtc,
    bool RevertedByRecovery);

public class SessionOutcomeEventArgs : EventArgs
{
    public SessionOutcome Outcome { get; }

    public SessionOutcomeEventArgs(SessionOutcome outcome)
    {
        Outcome = outcome;
    }
}
