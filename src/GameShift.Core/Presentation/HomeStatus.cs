using System.Globalization;
using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

public enum HomeStatusKind
{
    Idle,
    Optimizing,
    Paused,
}

/// <summary>How the headline is coloured: active only while a session runs with nothing failed.</summary>
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

    /// <param name="isOptimizing">A game session runs (orchestrator).</param>
    /// <param name="isPaused">Monitoring is paused (orchestrator).</param>
    /// <param name="gameName">The game the orchestrator is optimizing, when known.</param>
    /// <param name="current">The current session's activation outcome (see <see cref="CurrentSession.Select"/>), or null.</param>
    /// <param name="timeZone">Converts the session start for "Since 21:14".</param>
    public static HomeStatus For(
        bool isOptimizing, bool isPaused, string? gameName, SessionOutcome? current, TimeZoneInfo timeZone)
    {
        if (current is { EndedUtc: not null })
            current = null;

        // Quick Optimize runs an engine session without the orchestrator optimizing a game.
        bool quickOptimize = !isOptimizing && current != null;

        if (!isOptimizing && !quickOptimize)
            return isPaused
                ? new(HomeStatusKind.Paused, HomeStatusTone.Neutral, "Paused", null)
                : new(HomeStatusKind.Idle, HomeStatusTone.Neutral, "Waiting for a game", null);

        // Without this session's own results nothing is claimed: no counts, no active colour.
        if (current is not { StartedUtc: { } started })
            return new(HomeStatusKind.Optimizing, HomeStatusTone.Neutral, GameHeadline(gameName, null), WaitingForResults);

        int applied = current.Modules.Count(m =>
            m.Kind is ModuleOutcomeKind.Applied or ModuleOutcomeKind.AlreadyApplied);
        int failed = current.Modules.Count(m => m.Kind == ModuleOutcomeKind.Failed);

        var since = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(started, DateTimeKind.Utc), timeZone)
            .ToString("HH:mm", CultureInfo.InvariantCulture);
        var counts = $"Since {since} · {applied} applied";
        if (failed > 0)
            counts += $" · {failed} failed";

        if (quickOptimize && isPaused)
            return new(HomeStatusKind.Paused, HomeStatusTone.Neutral, "Paused", counts);

        var headline = quickOptimize ? "Quick Optimize is on" : GameHeadline(gameName, current.GameName);
        return new(HomeStatusKind.Optimizing, failed > 0 ? HomeStatusTone.Warning : HomeStatusTone.Active, headline, counts);
    }

    private static string GameHeadline(string? gameName, string? outcomeGameName)
    {
        var name = string.IsNullOrWhiteSpace(gameName) ? outcomeGameName : gameName;
        return string.IsNullOrWhiteSpace(name) ? "Optimizing" : $"Optimizing {name}";
    }
}
