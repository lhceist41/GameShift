using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

/// <summary>The badge a module shows.</summary>
public enum ModuleStatusKind
{
    Applied,
    Failed,
    Off,
    NotAvailable,
    Ready,
    Paused,
}

/// <param name="Reason">Shown under the badge; set for Failed and for a module the session declined.</param>
public sealed record ModuleStatus(ModuleStatusKind Kind, string? Reason)
{
    public string Word => Kind switch
    {
        ModuleStatusKind.Applied => "Applied",
        ModuleStatusKind.Failed => "Failed",
        ModuleStatusKind.Off => "Off",
        ModuleStatusKind.NotAvailable => "Not available",
        ModuleStatusKind.Paused => "Paused",
        _ => "Ready",
    };

    internal const string DoesNotApplyReason = "Does not apply to this game or system.";

    /// <summary>
    /// The badge for one module, from its own result in the current session, never from position.
    /// </summary>
    /// <param name="current">The current session's activation outcome (see <see cref="CurrentSession.Select"/>), or null when none runs.</param>
    /// <param name="isPaused">Monitoring is paused; a Quick Optimize session can still be running.</param>
    public static ModuleStatus For(
        string moduleName, bool isAvailable, bool enabledInProfile, bool isPaused, SessionOutcome? current)
    {
        if (!isAvailable)
            return new(ModuleStatusKind.NotAvailable, null);

        // An ended outcome is the previous session's: its results are not this module's state now.
        var outcome = current is { EndedUtc: null }
            ? current.Modules.LastOrDefault(m => m.ModuleName == moduleName)
            : null;

        if (outcome != null)
        {
            switch (outcome.Kind)
            {
                case ModuleOutcomeKind.Applied:
                case ModuleOutcomeKind.AlreadyApplied:
                    return new(ModuleStatusKind.Applied, null);
                case ModuleOutcomeKind.Failed:
                    return new(ModuleStatusKind.Failed, Sentence(outcome.Reason ?? "did not apply"));
                case ModuleOutcomeKind.SkippedDisabled:
                    return new(ModuleStatusKind.Off, null);
                case ModuleOutcomeKind.NotApplicable:
                    return new(ModuleStatusKind.NotAvailable, DoesNotApplyReason);
                case ModuleOutcomeKind.Unavailable:
                    return new(ModuleStatusKind.NotAvailable, null);
            }
        }

        if (!enabledInProfile)
            return new(ModuleStatusKind.Off, null);

        return new(isPaused ? ModuleStatusKind.Paused : ModuleStatusKind.Ready, null);
    }

    /// <summary>The engine's reasons ("did not apply: access denied") as a sentence for the UI.</summary>
    internal static string Sentence(string reason)
    {
        var text = reason.Trim();
        if (text.Length == 0)
            return text;
        text = char.ToUpperInvariant(text[0]) + text[1..];
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
    }
}
