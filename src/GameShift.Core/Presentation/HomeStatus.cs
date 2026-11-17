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
        var session = Session.Read(sessionRequestedUtc, lastOutcome, gameActionsInEffect);
        bool isOptimizing = session.IsOptimizing;
        bool quickOptimize = session.QuickOptimize;
        int applied = session.Applied;
        int failed = session.Failed;
        var current = session.Current;

        string? counts = null;
        if (session.StartedUtc is { } started)
        {
            counts = $"Since {Since(started, timeZone)} · {applied} applied";
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

    internal static string Since(DateTime startedUtc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc), timeZone)
            .ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>What the strip, its counts and its counters all read from one session outcome.</summary>
    internal readonly record struct Session(
        bool IsOptimizing, bool QuickOptimize, int Applied, int Failed, DateTime? StartedUtc, SessionOutcome? Current)
    {
        public static Session Read(DateTime? sessionRequestedUtc, SessionOutcome? lastOutcome, int gameActionsInEffect)
        {
            bool isOptimizing = sessionRequestedUtc != null;
            var current = CurrentSession.Select(lastOutcome, sessionRequestedUtc);

            int modulesApplied = current?.Modules.Count(m =>
                m.Kind is ModuleOutcomeKind.Applied or ModuleOutcomeKind.AlreadyApplied) ?? 0;
            int failed = current?.Modules.Count(m => m.Kind == ModuleOutcomeKind.Failed) ?? 0;

            // Quick Optimize runs an engine session without the orchestrator optimizing a game; like the
            // tray, it counts only while some of its changes are applied. Game actions come with a game session.
            bool quickOptimize = !isOptimizing && modulesApplied > 0;
            return new(isOptimizing, quickOptimize, modulesApplied + gameActionsInEffect, failed, current?.StartedUtc, current);
        }
    }

    private static string SessionHeadline(bool quickOptimize, string? gameName, SessionOutcome? current) =>
        quickOptimize ? "Quick Optimize is on" : GameHeadline(gameName, current?.GameName);

    private static string GameHeadline(string? gameName, string? outcomeGameName)
    {
        var name = string.IsNullOrWhiteSpace(gameName) ? outcomeGameName : gameName;
        return string.IsNullOrWhiteSpace(name) ? "Optimizing" : $"Optimizing {name}";
    }
}

/// <summary>One of the strip's three counters; <see cref="StatusKind"/> picks the icon.</summary>
public sealed record HomeCounter(int Count, string Label, ModuleStatusKind StatusKind)
{
    /// <summary>Applied and failed take their state colour only when there is something to count.</summary>
    public bool Highlight => Count > 0 && StatusKind is ModuleStatusKind.Applied or ModuleStatusKind.Failed;
}

/// <summary>
/// Home's status strip: a state chip, a title, a sub line with the session's start and elapsed time,
/// and three counters. Kind and tone are <see cref="HomeStatus"/>'s, so the two never disagree.
/// </summary>
/// <param name="Chip">Upper case: WPF has no text transform.</param>
/// <param name="Counters">Empty while a session runs without its own results yet: nothing is claimed.</param>
public sealed record HomeStrip(
    HomeStatusKind Kind, HomeStatusTone Tone, string Chip, string Title, string SubLine, IReadOnlyList<HomeCounter> Counters)
{
    internal const string IdleSubLine = "Starts automatically when a library game runs";
    internal const string PausedSubLine = "No game is optimized until you resume";

    /// <param name="modules">Home's module lines (<see cref="HomeSnapshot"/>), for off, not available, ready and paused.</param>
    /// <param name="nowUtc">For the elapsed time.</param>
    public static HomeStrip For(
        DateTime? sessionRequestedUtc, bool isPaused, string? gameName, SessionOutcome? lastOutcome,
        int gameActionsInEffect, IReadOnlyList<HomeModuleLine> modules, DateTime nowUtc, TimeZoneInfo timeZone)
    {
        var status = HomeStatus.For(sessionRequestedUtc, isPaused, gameName, lastOutcome, gameActionsInEffect, timeZone);
        var session = HomeStatus.Session.Read(sessionRequestedUtc, lastOutcome, gameActionsInEffect);
        int Lines(ModuleStatusKind kind) => modules.Count(m => m.Status.Kind == kind);

        switch (status.Kind)
        {
            case HomeStatusKind.Paused:
                var paused = session.Applied > 0
                    ? $"{TrayState.Changes(session.Applied)} still active · {PausedSubLine.ToLowerInvariant()}"
                    : PausedSubLine;
                return new(status.Kind, status.Tone, "PAUSED", "Paused", paused,
                [
                    new(Lines(ModuleStatusKind.Paused), "paused", ModuleStatusKind.Paused),
                    new(Lines(ModuleStatusKind.Off), "off", ModuleStatusKind.Off),
                    new(Lines(ModuleStatusKind.NotAvailable), "not available", ModuleStatusKind.NotAvailable),
                ]);

            case HomeStatusKind.Idle:
                return new(status.Kind, status.Tone, "IDLE", "Waiting for a game", IdleSubLine,
                [
                    new(Lines(ModuleStatusKind.Ready), "ready", ModuleStatusKind.Ready),
                    new(Lines(ModuleStatusKind.Off), "off", ModuleStatusKind.Off),
                    new(Lines(ModuleStatusKind.NotAvailable), "not available", ModuleStatusKind.NotAvailable),
                ]);
        }

        var title = session.QuickOptimize ? "Quick Optimize" : GameTitle(gameName, session.Current?.GameName);
        if (session.StartedUtc is not { } started)
            return new(status.Kind, status.Tone, "OPTIMIZING", title, HomeStatus.WaitingForResults, []);

        var sub = $"Since {HomeStatus.Since(started, timeZone)} · {Elapsed(nowUtc - started)} elapsed";
        return new(status.Kind, status.Tone, "OPTIMIZING", title, sub,
        [
            new(session.Applied, "applied", ModuleStatusKind.Applied),
            new(session.Failed, "failed", ModuleStatusKind.Failed),
            new(Lines(ModuleStatusKind.Off) + Lines(ModuleStatusKind.NotAvailable), "off or not available", ModuleStatusKind.Off),
        ]);
    }

    /// <summary>"00:42:17", "01:42:17", or "1 d 02:03:04" past a day.</summary>
    internal static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        var clock = string.Create(CultureInfo.InvariantCulture, $"{elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
        return elapsed.Days > 0 ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.Days} d {clock}") : clock;
    }

    private static string GameTitle(string? gameName, string? outcomeGameName)
    {
        var name = string.IsNullOrWhiteSpace(gameName) ? outcomeGameName : gameName;
        return string.IsNullOrWhiteSpace(name) ? RecentSessionLine.UnknownGame : name;
    }

    // The counters are a list, which a record compares by reference; the strip is refreshed every second.
    public bool Equals(HomeStrip? other) =>
        other is not null && Kind == other.Kind && Tone == other.Tone && Chip == other.Chip && Title == other.Title
        && SubLine == other.SubLine && Counters.SequenceEqual(other.Counters);

    public override int GetHashCode() => HashCode.Combine(Kind, Tone, Chip, Title, SubLine, Counters.Count);
}

/// <summary>Home's Pause button: its label follows the pause state and its tooltip names the hotkey when one is registered.</summary>
public sealed record HomePauseButton(string Label, string ToolTip)
{
    /// <param name="hotkey">The registered pause hotkey ("Ctrl+Shift+G"), or null when none is registered.</param>
    public static HomePauseButton For(bool isPaused, string? hotkey)
    {
        var tip = isPaused
            ? "Resume game detection. A game that is still running is optimized again."
            : "Pause game detection. A running game session ends.";
        if (!string.IsNullOrWhiteSpace(hotkey))
            tip += $" Hotkey: {hotkey}";
        return new(isPaused ? "Resume" : "Pause", tip);
    }
}
