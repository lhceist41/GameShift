using GameShift.Core.Config;
using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

/// <summary>The state a Latency Doctor fix shows.</summary>
public enum FixStatusKind
{
    Active,
    RestartPending,
    NotActive,
    Failed,
}

/// <summary>What choosing the fix does next.</summary>
public enum FixAction
{
    None,
    Apply,
    Undo,
}

public enum FixAttemptKind
{
    ApplyFailed,
    UndoFailed,
    Undone,
}

/// <summary>
/// The last thing this app run tried with a fix. Kept in memory only: the ledger records a fix
/// whose failed apply was kept for undo exactly like an applied one.
/// </summary>
/// <param name="Reason">The engine's message, for a failure.</param>
/// <param name="RequiresRestart">For <see cref="FixAttemptKind.Undone"/>: the undo takes effect only after a restart.</param>
/// <param name="RecordedBefore">For <see cref="FixAttemptKind.ApplyFailed"/>: the fix was already recorded when the apply started.</param>
public sealed record FixAttempt(FixAttemptKind Kind, string? Reason, bool RequiresRestart, DateTime AtUtc, bool RecordedBefore = false);

/// <param name="Reason">Shown under the state; null when the state says it all.</param>
public sealed record FixStatus(FixStatusKind Kind, string? Reason, FixAction Action)
{
    public string Word => Kind switch
    {
        FixStatusKind.Active => "Active",
        FixStatusKind.RestartPending => "Restart pending",
        FixStatusKind.Failed => "Failed",
        _ => "Not active",
    };

    internal const string RestartToFinishApply = "Restart Windows to finish turning it on.";
    internal const string RestartToFinishUndo = "Restart Windows to finish turning it off.";
    internal const string TurnedOffOutside = "It was turned off outside GameShift. Choose it to clear GameShift's record.";
    internal const string AlreadyOnOutside = "Already on in Windows. GameShift did not turn it on, so there is nothing to undo here.";

    /// <summary>
    /// The state of one fix from its ledger entry, what this app run saw, the live setting where one
    /// can be read, and when Windows last started.
    /// </summary>
    /// <param name="recorded">The fix's entry in <see cref="AppSettings.AppliedDpcFixes"/>, or null.</param>
    /// <param name="liveActive">The setting as Windows has it now; null when it cannot be read.</param>
    /// <param name="lastAttempt">The last apply or undo of this fix in this app run, or null.</param>
    public static FixStatus For(AppliedDpcFix? recorded, bool? liveActive, FixAttempt? lastAttempt, DateTime bootTimeUtc)
    {
        // The engine refuses an apply of a fix it already recorded and changes nothing, so the
        // refusal says nothing about the fix's state.
        if (lastAttempt is { Kind: FixAttemptKind.ApplyFailed, RecordedBefore: true })
            lastAttempt = null;

        if (recorded != null)
        {
            // A failed apply that is still recorded left part of its change in place: offer the undo.
            if (lastAttempt is { Kind: FixAttemptKind.ApplyFailed or FixAttemptKind.UndoFailed })
                return new(FixStatusKind.Failed, ReasonFor(lastAttempt), FixAction.Undo);

            if (recorded.RequiresReboot && AppliedSinceBoot(recorded, bootTimeUtc))
                return new(FixStatusKind.RestartPending, RestartToFinishApply, FixAction.Undo);

            // A reboot fix's setting is written at once, so a live reading that disagrees means
            // something else changed it back.
            if (liveActive == false)
                return new(FixStatusKind.NotActive, TurnedOffOutside, FixAction.Undo);

            return new(FixStatusKind.Active, null, FixAction.Undo);
        }

        if (lastAttempt?.Kind == FixAttemptKind.ApplyFailed)
            return new(FixStatusKind.Failed, ReasonFor(lastAttempt), FixAction.Apply);

        if (lastAttempt is { Kind: FixAttemptKind.Undone, RequiresRestart: true } undone && undone.AtUtc >= bootTimeUtc)
            return new(FixStatusKind.RestartPending, RestartToFinishUndo, FixAction.Apply);

        // Nothing recorded to undo: offering an undo would fail with "Fix not found".
        if (liveActive == true)
            return new(FixStatusKind.Active, AlreadyOnOutside, FixAction.None);

        return new(FixStatusKind.NotActive, null, FixAction.Apply);
    }

    /// <summary>
    /// True when the fix was applied during the current boot. An entry without a time, written by
    /// an older version, reads back as year 1 and so counts as applied before it.
    /// </summary>
    public static bool AppliedSinceBoot(AppliedDpcFix recorded, DateTime bootTimeUtc) =>
        recorded.AppliedAt.ToUniversalTime() >= bootTimeUtc;

    /// <summary>When Windows started this boot (see <see cref="MemoryIntegrityStatus.BootTimeUtc"/>).</summary>
    public static DateTime CurrentBootTimeUtc() => MemoryIntegrityStatus.BootTimeUtc();

    private static string ReasonFor(FixAttempt attempt)
    {
        var reason = string.IsNullOrWhiteSpace(attempt.Reason) ? "No reason was given" : attempt.Reason;
        var text = ModuleStatus.Sentence(reason);
        return attempt.Kind == FixAttemptKind.UndoFailed ? "Undo failed: " + text : text;
    }
}
