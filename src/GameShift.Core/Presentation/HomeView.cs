using System.Globalization;
using GameShift.Core.Config;
using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

/// <summary>One module on Home's "This session" list with its badge.</summary>
public sealed record HomeModuleLine(string ModuleName, ModuleStatus Status)
{
    /// <summary>The state word in upper case: WPF has no text transform.</summary>
    public string StateWord => Status.Word.ToUpperInvariant();
}

/// <summary>
/// Home's short session list: at most <see cref="MaxRows"/> lines in the snapshot's order (failed
/// first), a "+ N more" line for the rest and a link to the full list on During games.
/// </summary>
/// <param name="MoreText">"+ 9 more", or null when every line is shown.</param>
public sealed record HomeSessionList(string Header, IReadOnlyList<HomeModuleLine> Shown, string? MoreText, string LinkText)
{
    public const int MaxRows = 8;

    /// <param name="idle">Nothing runs and nothing is paused: the list shows what the next session will do.</param>
    public static HomeSessionList For(IReadOnlyList<HomeModuleLine> lines, bool idle)
    {
        var shown = lines.Take(MaxRows).ToList();
        int more = lines.Count - shown.Count;
        return new(
            idle ? "NEXT SESSION" : "THIS SESSION",
            shown,
            more > 0 ? string.Create(CultureInfo.InvariantCulture, $"+ {more} more") : null,
            lines.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"All {lines.Count} in During games") : "Open During games");
    }

    // A record compares the list by reference; Home redraws only on a real change.
    public bool Equals(HomeSessionList? other) =>
        other is not null && Header == other.Header && MoreText == other.MoreText && LinkText == other.LinkText
        && Shown.SequenceEqual(other.Shown);

    public override int GetHashCode() => HashCode.Combine(Header, MoreText, LinkText, Shown.Count);
}

/// <summary>Home's status strip and its "This session" list, read together so they never disagree.</summary>
public sealed record HomeSnapshot(HomeStatus Status, HomeStrip Strip, IReadOnlyList<HomeModuleLine> Modules)
{
    /// <param name="sessionRequestedUtc">
    /// Read before <paramref name="lastOutcome"/>: a session that ends between the two reads then
    /// still counts as running, so the next refresh corrects the strip instead of none coming.
    /// </param>
    /// <param name="gameName">The game being optimized, when known; else the outcome's game name is used.</param>
    /// <param name="nowUtc">For the strip's elapsed time.</param>
    public static HomeSnapshot Read(
        IReadOnlyList<SessionModuleInput> modules,
        Func<DateTime?> sessionRequestedUtc,
        Func<SessionOutcome?> lastOutcome,
        bool isPaused,
        string? gameName,
        int gameActionsInEffect,
        DateTime nowUtc,
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
        var strip = HomeStrip.For(requested, isPaused, gameName, outcome, gameActionsInEffect, lines, nowUtc, timeZone);
        return new(status, strip, lines);
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
