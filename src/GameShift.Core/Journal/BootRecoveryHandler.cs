using GameShift.Core.Detection;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Journal;

/// <summary>
/// Implements the --boot-recovery logic for GameShift.Watchdog.
///
/// Run once at system startup (via a Windows Scheduled Task with a 30-second delay).
/// Performs two independent checks regardless of order:
///
///   1. Crash recovery — if <c>sessionActive == true</c> in the journal, a previous
///      GameShift session did not exit cleanly (BSOD, power loss, hard kill).
///      Reverts all <c>Applied</c> optimizations via <see cref="WatchdogRevertEngine"/>
///      and marks the session inactive.
///
///   2. Windows Update detection — compares <c>windowsBuild</c> recorded in the journal
///      against the live <c>CurrentBuildNumber.UBR</c> from the registry. If different,
///      logs a warning and records the build change in the journal so the main app can
///      surface a re-verification prompt on next launch.
/// </summary>
public static class BootRecoveryHandler
{
    /// <summary>
    /// Runs the full boot-recovery sequence and returns when done.
    /// All failures are caught and logged — never throws.
    /// </summary>
    public static void Run(ILogger logger)
    {
        logger = logger.ForContext(typeof(BootRecoveryHandler));
        logger.Information("[BootRecovery] Boot recovery started");

        try
        {
            // Clean up any orphaned ETW session from a crashed GameShift instance.
            // Must happen before journal check — the ETW session is a system resource
            // independent of the journal's sessionActive flag.
            EtwProcessMonitor.CleanupStaleSession(logger);

            var journal = new JournalManager();
            var journalData = journal.LoadJournal();

            if (journalData == null)
            {
                logger.Information("[BootRecovery] No journal found — nothing to recover");
                return;
            }

            logger.Information(
                "[BootRecovery] Journal loaded (sessionActive={Active}, build={Build}, game={Game})",
                journalData.SessionActive,
                journalData.WindowsBuild,
                journalData.ActiveGame?.Name ?? "<none>");

            // ── 1. Crash recovery ─────────────────────────────────────────────

            if (journalData.SessionActive)
            {
                logger.Warning(
                    "[BootRecovery] Session was active — possible crash or power loss. " +
                    "Reverting {Count} Applied optimization(s) for game '{Game}'",
                    journalData.Optimizations.Count(e => e.State == nameof(OptimizationState.Applied)),
                    journalData.ActiveGame?.Name ?? "<unknown>");

                var revertEngine = new WatchdogRevertEngine(logger);
                revertEngine.RevertFromJournal(journalData, journal);
                // RevertFromJournal calls journal.EndSession() which saves sessionActive=false
            }
            else
            {
                logger.Information("[BootRecovery] Session was already inactive — no crash recovery needed");
            }

            // ── 2. Windows Update detection ───────────────────────────────────
            // Always check, even when no crash occurred. A Windows Update can silently
            // undo registry-backed optimizations set by a previous session.

            var buildInJournal = journalData.WindowsBuild;
            var currentBuild = ReadCurrentBuildString();

            if (string.IsNullOrEmpty(buildInJournal) || string.IsNullOrEmpty(currentBuild))
            {
                logger.Debug("[BootRecovery] Build strings unavailable — skipping update check");
            }
            else if (!string.Equals(buildInJournal, currentBuild, StringComparison.Ordinal))
            {
                logger.Warning(
                    "[BootRecovery] Windows build changed since last session: " +
                    "{OldBuild} → {NewBuild}. A Windows Update occurred. " +
                    "Persistent registry optimizations should be re-verified on next launch.",
                    buildInJournal,
                    currentBuild);

                journal.RecordBuildChanged(buildInJournal, currentBuild);

                logger.Information(
                    "[BootRecovery] Build-changed warning written to journal for main app to surface");
            }
            else
            {
                logger.Information("[BootRecovery] Windows build unchanged ({Build}) — no update detected",
                    currentBuild);
            }

            logger.Information("[BootRecovery] Boot recovery complete");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[BootRecovery] Unhandled exception during boot recovery");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current Windows build as "CurrentBuildNumber.UBR" from the registry.
    /// Matches the format written by <see cref="JournalManager"/> at session start.
    /// </summary>
    internal static string ReadCurrentBuildString()
    {
        try
        {
            const string regPath =
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

            var build = Registry.GetValue(regPath, "CurrentBuildNumber", "0")?.ToString() ?? "0";
            var ubr   = Registry.GetValue(regPath, "UBR", "0")?.ToString() ?? "0";
            return $"{build}.{ubr}";
        }
        catch
        {
            return string.Empty;
        }
    }
}
