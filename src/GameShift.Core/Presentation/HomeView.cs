using System.Globalization;
using GameShift.Core.Config;
using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

/// <summary>One module on Home's "This session" list with its badge.</summary>
public sealed record HomeModuleLine(string ModuleName, ModuleStatus Status);

/// <summary>Home's status strip and its "This session" list, read together so they never disagree.</summary>
public sealed record HomeSnapshot(HomeStatus Status, IReadOnlyList<HomeModuleLine> Modules)
{
    /// <param name="sessionRequestedUtc">
    /// Read before <paramref name="lastOutcome"/>: a session that ends between the two reads then
    /// still counts as running, so the next refresh corrects the strip instead of none coming.
    /// </param>
    /// <param name="gameName">The game being optimized, when known; else the outcome's game name is used.</param>
    public static HomeSnapshot Read(
        IReadOnlyList<SessionModuleInput> modules,
        Func<DateTime?> sessionRequestedUtc,
        Func<SessionOutcome?> lastOutcome,
        bool isPaused,
        string? gameName,
        int gameActionsInEffect,
        TimeZoneInfo timeZone)
    {
        var requested = sessionRequestedUtc();
        var outcome = lastOutcome();
        var status = HomeStatus.For(requested, isPaused, gameName, outcome, gameActionsInEffect, timeZone);
        var current = CurrentSession.Select(outcome, requested);

        var lines = modules
            .Select(m => new HomeModuleLine(
                m.ModuleName, ModuleStatus.For(m.ModuleName, m.IsAvailable, m.EnabledInProfile, isPaused, current)))
            .OrderBy(line => Rank(line.Status.Kind))
            .ToList();
        return new(status, lines);
    }

    /// <summary>Failed first, then applied, then what may still apply, then off and not available.</summary>
    internal static int Rank(ModuleStatusKind kind) => kind switch
    {
        ModuleStatusKind.Failed => 0,
        ModuleStatusKind.Applied => 1,
        ModuleStatusKind.Ready or ModuleStatusKind.Paused => 2,
        ModuleStatusKind.Off => 3,
        _ => 4,
    };
}

/// <param name="Length">Null when the history has no length for the session.</param>
/// <param name="Counts">
/// The engine's changes still active when the game closed (no game actions; 0 after an earlier
/// end or pause). The history records no failed count, so none is shown.
/// </param>
public sealed record RecentSessionLine(string Game, string When, string? Length, string Counts)
{
    internal const string UnknownGame = "Unknown game";

    public static RecentSessionLine For(GameSession session)
    {
        var game = string.IsNullOrWhiteSpace(session.GameName) ? UnknownGame : session.GameName;
        var when = session.StartTime.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
        return new(game, when, LengthText(session.Duration), $"{TrayState.Changes(session.OptimizationsApplied)} active when the game closed");
    }

    internal static string? LengthText(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return null;
        if (duration < TimeSpan.FromMinutes(1))
            return "Under 1 min";

        int totalMinutes = (int)duration.TotalMinutes;
        if (totalMinutes < 60)
            return string.Create(CultureInfo.InvariantCulture, $"{totalMinutes} min");
        return string.Create(CultureInfo.InvariantCulture, $"{totalMinutes / 60} h {totalMinutes % 60} min");
    }
}
