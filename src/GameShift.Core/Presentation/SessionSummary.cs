using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

public enum SessionSummaryTone
{
    Neutral,
    Success,
    Warning,
}

/// <param name="StillActive">Modules whose revert failed, with the reason, in revert order.</param>
public sealed record SessionSummary(SessionSummaryTone Tone, string Text, IReadOnlyList<string> StillActive)
{
    internal const string RecoveryText =
        "Crash recovery ran during this session. GameShift cannot tell which of its changes recovery undid.";

    internal const string NothingToUndoText = "No changes were active, so there was nothing to undo.";

    /// <summary>
    /// The end-of-session text, built only from the deactivation's own revert results. Null for an
    /// outcome that has not ended (an activation outcome is not a summary of anything undone).
    /// </summary>
    public static SessionSummary? For(SessionOutcome? deactivation)
    {
        if (deactivation is not { EndedUtc: not null })
            return null;

        // Recovery's per-module results are not part of the outcome, so nothing is claimed about them.
        if (deactivation.RevertedByRecovery)
            return new(SessionSummaryTone.Warning, RecoveryText, Array.Empty<string>());

        // Counted from Reverts only: after Quick Optimize then a game, Modules lists the earlier
        // changes as AlreadyApplied while Reverts holds every change that was on the stack.
        int total = deactivation.Reverts.Count;
        if (total == 0)
            return new(SessionSummaryTone.Neutral, NothingToUndoText, Array.Empty<string>());

        var failed = deactivation.Reverts.Where(r => r.Kind != ModuleRevertKind.Reverted).ToList();
        if (failed.Count == 0)
        {
            var text = total == 1 ? "The change was undone." : $"All {total} changes were undone.";
            return new(SessionSummaryTone.Success, text, Array.Empty<string>());
        }

        var stillActive = failed
            .Select(r => r.Reason is { Length: > 0 } reason
                ? $"{r.ModuleName}: {ModuleStatus.Sentence(reason)}"
                : r.ModuleName)
            .ToArray();
        return new(SessionSummaryTone.Warning,
            $"{total - failed.Count} of {total} undone, {failed.Count} still active.",
            stillActive);
    }
}
