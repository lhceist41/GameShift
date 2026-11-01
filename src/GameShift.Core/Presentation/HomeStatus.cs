using System.Globalization;
using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

public enum HomeStatusKind
{
    Idle,
    Optimizing,
    Paused,
}

/// <summary>How the headline is coloured: active only while changes are applied and none failed.</summary>
public enum HomeStatusTone
{
    Neutral,
    Active,
    Warning,
}

/// <param name="CountsLine">"Since 21:14 · 5 applied · 1 failed", a plain line when the counts are not known, or null.</param>
public sealed record HomeStatus(HomeStatusKind Kind, HomeStatusTone Tone, string Headline, string? CountsLine)
{
    internal const string WaitingForResults = "Waiting for the results of this session";

    /// <summary>The tray's words for a session that changed nothing.</summary>
    internal const string NoChangesActive = "No changes active";

    /// <param name="sessionRequestedUtc">
    /// <see cref="Detection.DetectionOrchestrator.SessionRequestedUtc"/>, read once: non-null while a
    /// game session runs. Taking both the session flag and the current results from this one value
    /// keeps them from disagreeing.
    /// </param>
    /// <param name="isPaused">Monitoring is paused (orchestrator).</param>
    /// <param name="gameName">The game the orchestrator is optimizing, when known.</param>
    /// <param name="lastOutcome"><see cref="OptimizationEngine.LastOutcome"/>.</param>
    /// <param name="gameActionsInEffect"><see cref="Detection.DetectionOrchestrator.GameActionsInEffect"/>.</param>
    /// <param name="timeZone">Converts the session start for "Since 21:14".</param>
    public static HomeStatus For(
        DateTime? sessionRequestedUtc, bool isPaused, string? gameName, SessionOutcome? lastOutcome,
        int gameActionsInEffect, TimeZoneInfo timeZone)
    {
        bool isOptimizing = sessionRequestedUtc != null;
        var current = CurrentSession.Select(lastOutcome, sessionRequestedUtc);

        int modulesApplied = current?.Modules.Count(m =>
            m.Kind is ModuleOutcomeKind.Applied or ModuleOutcomeKind.AlreadyApplied) ?? 0;
        int failed = current?.Modules.Count(m => m.Kind == ModuleOutcomeKind.Failed) ?? 0;

        // Quick Optimize runs an engine session without the orchestrator optimizing a game; like the
        // tray, it counts only while some of its changes are applied. Game actions come with a game session.
        bool quickOptimize = !isOptimizing && modulesApplied > 0;
        int applied = modulesApplied + gameActionsInEffect;

        string? counts = null;
        if (current is { StartedUtc: { } started })
        {
            var since = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(started, DateTimeKind.Utc), timeZone)
                .ToString("HH:mm", CultureInfo.InvariantCulture);
            counts = $"Since {since} · {applied} applied";
            if (failed > 0)
                counts += $" · {failed} failed";
        }

        // The orchestrator sets paused before it ends the game session, so paused is checked first.
        // Counts stay only for Quick Optimize, whose changes pausing leaves applied; the headline names
        // any change still in effect, also a game session's until its revert has run.
        if (isPaused)
        {
            var headline = applied > 0 ? $"Paused, {TrayState.Changes(applied)} active" : "Paused";
            return new(HomeStatusKind.Paused, HomeStatusTone.Neutral, headline, quickOptimize ? counts : null);
        }

        if (!isOptimizing && !quickOptimize)
            return new(HomeStatusKind.Idle, HomeStatusTone.Neutral, "Waiting for a game", null);

        // Without this session's own results nothing is claimed: no counts, no active colour.
        if (counts == null)
            return new(HomeStatusKind.Optimizing, HomeStatusTone.Neutral, GameHeadline(gameName, null), WaitingForResults);

        if (failed > 0)
            return new(HomeStatusKind.Optimizing, HomeStatusTone.Warning, SessionHeadline(quickOptimize, gameName, current), counts);

        if (applied == 0)
            return new(HomeStatusKind.Optimizing, HomeStatusTone.Neutral, NoChangesActive, counts);

        return new(HomeStatusKind.Optimizing, HomeStatusTone.Active, SessionHeadline(quickOptimize, gameName, current), counts);
    }

    private static string SessionHeadline(bool quickOptimize, string? gameName, SessionOutcome? current) =>
        quickOptimize ? "Quick Optimize is on" : GameHeadline(gameName, current?.GameName);

    private static string GameHeadline(string? gameName, string? outcomeGameName)
    {
        var name = string.IsNullOrWhiteSpace(gameName) ? outcomeGameName : gameName;
        return string.IsNullOrWhiteSpace(name) ? "Optimizing" : $"Optimizing {name}";
    }
}
