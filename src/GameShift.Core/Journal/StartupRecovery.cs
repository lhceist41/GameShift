using System.Globalization;
using GameShift.Core.Optimization;
using Serilog;

namespace GameShift.Core.Journal;

public enum StartupRecoveryOutcome
{
    /// <summary>No journal, an unreadable or unknown-version one, or the last session ended normally.</summary>
    NothingToDo,

    /// <summary>Every recorded change of the session was undone.</summary>
    Recovered,

    /// <summary>The session was replayed but some records failed or were skipped.</summary>
    Partial,

    /// <summary>The journal could have been written by a standard user; nothing in it was acted on.</summary>
    Untrusted,

    /// <summary>Earlier replays did not finish, or the attempt could not be recorded; nothing was replayed.</summary>
    GaveUp,

    /// <summary>The journal folder could not be made a plain folder; nothing in it was read.</summary>
    FolderUnavailable
}

/// <param name="Items">One entry per record, replayed or left; empty when nothing was read from a trusted journal.</param>
/// <param name="GameName">From a trusted journal only.</param>
/// <param name="SessionStarted">From a trusted journal only.</param>
/// <param name="JournalFolder">The refused folder, for <see cref="StartupRecoveryOutcome.FolderUnavailable"/> only.</param>
public sealed record StartupRecoveryResult(
    bool WasSessionActive,
    StartupRecoveryOutcome Outcome,
    IReadOnlyList<JournalRevertItem> Items,
    string? GameName = null,
    DateTimeOffset? SessionStarted = null,
    string? EvidencePath = null,
    string? JournalFolder = null)
{
    /// <summary>What to tell the user once, or null when there is nothing to say.</summary>
    public string? Notice => StartupRecovery.DescribeForUser(this);
}

/// <summary>
/// Undoes the changes of a session that did not end normally (a crash, a kill, a cut-off sign-out
/// or a power loss), at the next start of the app and before anything can start a new session. It
/// acts only on a journal that <see cref="JournalTrust"/> accepts; every record is checked again by
/// its own revert path.
/// </summary>
public static class StartupRecovery
{
    internal const int MaxAttempts = 2;

    /// <param name="journal">A fresh instance for the journal file; it holds the loaded session afterwards.</param>
    public static StartupRecoveryResult Run(
        JournalManager journal, Func<string, JournalTrustResult> trust, WatchdogRevertEngine engine, ILogger logger) =>
        Run(journal, trust, engine, logger, JournalTrust.Repair);

    /// <param name="repairFolder">Seam for the owner and permission repair of an untrusted folder.</param>
    internal static StartupRecoveryResult Run(
        JournalManager journal,
        Func<string, JournalTrustResult> trust,
        WatchdogRevertEngine engine,
        ILogger logger,
        Func<string, ILogger, bool> repairFolder)
    {
        bool wasActive = false;
        SessionJournalData? trusted = null;
        try
        {
            // The folder may be a link to one a standard user controls, so nothing is read through
            // it. Whether the last session ended is then unknown, so it counts as not ended.
            if (journal.FolderRefusal is { } refusal)
            {
                var folder = Path.GetDirectoryName(journal.JournalPath);
                logger.Warning("[StartupRecovery] {Folder} could not be used ({Reason}); nothing in it was read and crash recovery is off for this run",
                    folder, refusal);
                return new StartupRecoveryResult(
                    WasSessionActive: true, StartupRecoveryOutcome.FolderUnavailable, Array.Empty<JournalRevertItem>(),
                    JournalFolder: folder);
            }

            // Look first without adopting the file, so an untrusted journal never becomes this
            // instance's state (a later metadata save would write it back under the journal name).
            var peeked = journal.PeekJournal();
            if (peeked == null)
                return Nothing(wasActive);

            wasActive = peeked.SessionActive;
            if (!wasActive)
            {
                journal.LoadJournal();
                return Nothing(wasActive);
            }

            if (peeked.Version != 2)
            {
                logger.Warning("[StartupRecovery] Journal version {Version} is not one this version replays; leaving it", peeked.Version);
                return Nothing(wasActive);
            }

            JournalTrustResult verdict;
            try
            {
                verdict = trust(journal.JournalPath);
            }
            catch (Exception ex)
            {
                verdict = JournalTrustResult.Untrusted($"the trust check failed: {ex.Message}");
            }

            if (!verdict.Trusted)
                return SetAsideUntrusted(journal.JournalPath, verdict, logger, repairFolder);

            // Read again now that the file is known to be writable only by administrators.
            trusted = journal.LoadJournal();
            if (trusted == null || !trusted.SessionActive || trusted.Version != 2)
            {
                trusted = null;
                return Nothing(wasActive);
            }

            logger.Information(
                "[StartupRecovery] The last session ({Game}) did not end normally; undoing its recorded changes (attempt {Attempt})",
                trusted.ActiveGame?.Name ?? "<unknown>", trusted.RecoveryAttempts + 1);

            if (trusted.RecoveryAttempts >= MaxAttempts)
            {
                logger.Error("[StartupRecovery] {Attempts} earlier attempts did not finish; not replaying again", trusted.RecoveryAttempts);
                return GiveUp(journal, trusted, logger);
            }

            // Persisted before replaying, so a replay that takes the process down counts.
            if (!journal.RecordRecoveryAttempt())
            {
                logger.Error("[StartupRecovery] Could not record the attempt; not replaying, so a failing replay cannot repeat on every start");
                return GiveUp(journal, trusted, logger);
            }

            string? evidence = null;
            JournalRevertResult replay;
            try
            {
                replay = engine.RevertFromJournal(trusted, journal, result =>
                {
                    if (!result.AllReverted)
                        evidence = journal.SaveEvidenceCopy(EvidenceSuffix("unrecovered"));
                });
            }
            catch (Exception ex)
            {
                // The session stays active, so the next start tries again until the attempt limit.
                logger.Error(ex, "[StartupRecovery] Replaying the journal stopped with an error");
                evidence ??= journal.SaveEvidenceCopy(EvidenceSuffix("unrecovered"));
                return Result(wasActive, StartupRecoveryOutcome.Partial, Array.Empty<JournalRevertItem>(), trusted, evidence);
            }

            var outcome = replay.AllReverted ? StartupRecoveryOutcome.Recovered : StartupRecoveryOutcome.Partial;
            logger.Information(
                "[StartupRecovery] {Outcome}: {Reverted} of {Total} record(s) undone{Evidence}",
                outcome, replay.Items.Count(i => i.Outcome == RevertOutcome.Reverted), replay.Items.Count,
                evidence != null ? $", records kept in {evidence}" : string.Empty);
            return Result(wasActive, outcome, replay.Items, trusted, evidence);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[StartupRecovery] Recovery failed");
            return wasActive
                ? Result(wasActive, StartupRecoveryOutcome.Partial, Array.Empty<JournalRevertItem>(), trusted, evidence: null)
                : Nothing(wasActive);
        }
    }

    private static StartupRecoveryResult Nothing(bool wasActive) =>
        new(wasActive, StartupRecoveryOutcome.NothingToDo, Array.Empty<JournalRevertItem>());

    private static StartupRecoveryResult Result(
        bool wasActive, StartupRecoveryOutcome outcome, IReadOnlyList<JournalRevertItem> items,
        SessionJournalData? trusted, string? evidence) =>
        new(wasActive, outcome, items, trusted?.ActiveGame?.Name, trusted?.ActiveGame?.StartTime ?? trusted?.Timestamp, evidence);

    private static StartupRecoveryResult SetAsideUntrusted(
        string journalPath, JournalTrustResult verdict, ILogger logger, Func<string, ILogger, bool> repairFolder)
    {
        logger.Warning("[StartupRecovery] The journal at {Path} is not trusted ({Reasons}); nothing in it was acted on",
            journalPath, string.Join("; ", verdict.Reasons));

        var newName = $"state.{EvidenceSuffix("untrusted")}.json";
        bool setAside = false;
        try
        {
            setAside = WindowsJournalSecurity.RenameInPlace(journalPath, newName);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[StartupRecovery] Could not rename the untrusted journal");
        }

        // Only once the record is set aside: an Administrators-owned file that sat in a folder a user
        // could write would pass the trust check after the folder is repaired.
        var folder = Path.GetDirectoryName(journalPath);
        if (setAside && folder != null)
        {
            logger.Information("[StartupRecovery] Kept the untrusted journal as {Name}", newName);
            repairFolder(folder, logger);
        }
        else
        {
            logger.Warning("[StartupRecovery] The untrusted journal could not be set aside; its folder was not repaired");
        }

        return new StartupRecoveryResult(WasSessionActive: true, StartupRecoveryOutcome.Untrusted, Array.Empty<JournalRevertItem>());
    }

    private static StartupRecoveryResult GiveUp(JournalManager journal, SessionJournalData trusted, ILogger logger)
    {
        var pending = trusted.GameActions.AsEnumerable().Reverse()
            .Select(ga => new JournalRevertItem(IsGameAction: true, ga.Kind, RevertOutcome.Skipped, "Not attempted"))
            .Concat(trusted.Optimizations.AsEnumerable().Reverse()
                .Where(e => e.State == nameof(OptimizationState.Applied))
                .Select(e => new JournalRevertItem(IsGameAction: false, e.Name, RevertOutcome.Skipped, "Not attempted")))
            .ToList();

        var evidence = journal.SaveEvidenceCopy(EvidenceSuffix("unrecovered"));
        journal.EndSession();
        logger.Warning("[StartupRecovery] Session ended without replaying {Count} record(s){Evidence}",
            pending.Count, evidence != null ? $"; records kept in {evidence}" : string.Empty);
        return Result(wasActive: true, StartupRecoveryOutcome.GaveUp, pending, trusted, evidence);
    }

    private static string EvidenceSuffix(string kind) =>
        $"{kind}-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}";

    // ── What the user is told ─────────────────────────────────────────────────

    internal const string UntrustedNotice =
        "GameShift found a session record it could not verify and did not act on it.";

    /// <summary>Formatted with the journal folder.</summary>
    internal const string FolderUnavailableNotice =
        "GameShift could not use its data folder ({0}) because something changed it or is holding it open. " +
        "Crash recovery is off until it can, and nothing from an earlier session was undone. " +
        "Restart Windows, then start GameShift again.";

    internal const string NotUndoneAdvice = "GameShift kept a copy of that session's record, and its log lists what is left.";

    internal const string TaskReEnabledAdvice =
        "Each was re-enabled because GameShift may have left it disabled; if you had disabled it yourself, disable it again.";

    /// <summary>
    /// The one notice for a recovery. Names come only from fixed labels for the records' kinds, and
    /// task paths only from the scheduled-task result, which lists allowlisted paths; never other
    /// text from the file.
    /// </summary>
    internal static string? DescribeForUser(StartupRecoveryResult result)
    {
        if (result.Outcome == StartupRecoveryOutcome.NothingToDo)
            return null;
        if (result.Outcome == StartupRecoveryOutcome.Untrusted)
            return UntrustedNotice;
        if (result.Outcome == StartupRecoveryOutcome.FolderUnavailable)
            return string.Format(CultureInfo.InvariantCulture, FolderUnavailableNotice, result.JournalFolder);

        var game = string.IsNullOrWhiteSpace(result.GameName) ? "a game session" : result.GameName;
        var when = result.SessionStarted is { } started
            ? $" ({started.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})"
            : string.Empty;
        var opening = $"GameShift did not close normally during {game}{when}.";

        var notUndone = result.Items
            .Where(i => i.Outcome != RevertOutcome.Reverted)
            .Select(LabelFor)
            .Distinct()
            .ToList();
        if (notUndone.Count == 0 && result.Outcome != StartupRecoveryOutcome.Recovered)
            notUndone.Add("some of the recorded changes");

        var parts = new List<string>
        {
            result.Outcome switch
            {
                StartupRecoveryOutcome.Recovered => $"{opening} It has now undone the changes it made then.",
                StartupRecoveryOutcome.Partial => $"{opening} It has undone what it could.",
                _ => $"{opening} It could not undo those changes automatically.",
            },
        };

        if (notUndone.Count > 0)
            parts.Add($"Some changes could not be undone: {string.Join(", ", notUndone)}. {NotUndoneAdvice}");

        // Only the parts that list allowlisted task paths; an error text could quote the record.
        var taskLines = result.Items
            .Where(i => !i.IsGameAction && i.Name == ScheduledTaskSuppressor.OptimizationId && i.Message != null)
            .SelectMany(i => i.Message!.Split(". "))
            .ToList();
        var reEnabled = taskLines.FirstOrDefault(l => l.StartsWith(ScheduledTaskSuppressor.ReEnabledPrefix, StringComparison.Ordinal));
        var notReEnabled = taskLines.FirstOrDefault(l => l.StartsWith(ScheduledTaskSuppressor.NotReEnabledPrefix, StringComparison.Ordinal));
        if (reEnabled != null)
            parts.Add($"{reEnabled}. {TaskReEnabledAdvice}");
        if (notReEnabled != null)
            parts.Add($"{notReEnabled}.");

        return string.Join("\n\n", parts);
    }

    private static string LabelFor(JournalRevertItem item)
    {
        if (item.IsGameAction)
        {
            return item.Name switch
            {
                "registry" => "a registry value changed for the game",
                "firewall" => "a firewall rule added for the game",
                "defender" => "a Windows Security exclusion added for the game",
                "suspend" => "an app paused for the game",
                _ => "a game change from an earlier version",
            };
        }

        return WatchdogRevertEngine.DefaultFactories.ContainsKey(item.Name)
            ? item.Name
            : "a setting from an earlier version";
    }
}
