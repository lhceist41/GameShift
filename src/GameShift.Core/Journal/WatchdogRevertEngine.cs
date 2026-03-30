using GameShift.Core.Optimization;
using Serilog;

namespace GameShift.Core.Journal;

/// <summary>
/// Reverts optimizations recorded in a <see cref="SessionJournalData"/> without needing
/// live process state. Used by the watchdog service and boot-recovery task.
///
/// Optimizations are reverted in LIFO order (last-applied first) by iterating the
/// journal's Optimizations list in reverse, matching each entry to a known
/// <see cref="IJournaledOptimization"/> implementation and calling
/// <see cref="IJournaledOptimization.RevertFromRecord"/>.
/// </summary>
public class WatchdogRevertEngine
{
    private readonly ILogger _logger;

    // Registry of optimization factories keyed by Name.
    // Add entries here as more optimizations migrate to IJournaledOptimization.
    private static readonly Dictionary<string, Func<IJournaledOptimization>> Factories =
        new(StringComparer.Ordinal)
        {
            [MpoToggle.OptimizationId] = () => new MpoToggle(),
        };

    public WatchdogRevertEngine(ILogger logger)
    {
        _logger = logger.ForContext<WatchdogRevertEngine>();
    }

    /// <summary>
    /// Reverts all <c>Applied</c> optimizations in the journal in LIFO order.
    /// Skips entries whose name has no registered factory (logs a warning).
    /// After reverting, marks the journal session as inactive via <paramref name="journal"/>.
    /// </summary>
    public void RevertFromJournal(SessionJournalData journalData, JournalManager journal)
    {
        var toRevert = journalData.Optimizations
            .AsEnumerable()
            .Reverse()
            .Where(e => e.State == nameof(OptimizationState.Applied))
            .ToList();

        _logger.Information(
            "[WatchdogRevertEngine] {Count} Applied optimization(s) to revert for game '{Game}'",
            toRevert.Count,
            journalData.ActiveGame?.Name ?? "<unknown>");

        foreach (var entry in toRevert)
        {
            if (!Factories.TryGetValue(entry.Name, out var factory))
            {
                _logger.Warning(
                    "[WatchdogRevertEngine] No factory registered for '{Name}' — skipping",
                    entry.Name);
                continue;
            }

            try
            {
                _logger.Information("[WatchdogRevertEngine] Reverting '{Name}'", entry.Name);
                var opt = factory();
                var result = opt.RevertFromRecord(entry.OriginalValue);
                _logger.Information(
                    "[WatchdogRevertEngine] '{Name}' → {State}{Error}",
                    entry.Name,
                    result.State,
                    result.ErrorMessage != null ? $": {result.ErrorMessage}" : string.Empty);

                journal.RecordReverted(entry.Name, result.State);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[WatchdogRevertEngine] Exception reverting '{Name}'", entry.Name);
            }
        }

        // Mark session inactive so boot recovery won't trigger again
        journal.EndSession();

        _logger.Information("[WatchdogRevertEngine] Recovery complete — session marked inactive");
    }
}
