using GameShift.Core.Detection;
using GameShift.Core.Optimization;
using Serilog;

namespace GameShift.Core.Journal;

/// <summary>
/// Reverts optimizations recorded in a <see cref="SessionJournalData"/> without needing
/// live process state. Used by the watchdog service and boot-recovery task.
///
/// Game actions are reverted first, newest first, through <see cref="GameActionRecovery"/>.
/// Optimizations follow in LIFO order (last-applied first) by iterating the
/// journal's Optimizations list in reverse, matching each entry to a known
/// <see cref="IJournaledOptimization"/> implementation and calling
/// <see cref="IJournaledOptimization.RevertFromRecord"/>.
/// </summary>
public class WatchdogRevertEngine
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Func<IJournaledOptimization>> _factories;

    // Registry of optimization factories keyed by Name.
    // Add entries here as more optimizations migrate to IJournaledOptimization.
    // Internal (not private) so JournaledOptimizationContractTests can assert the closure
    // invariant: every IJournaledOptimization implementor has a factory here and vice versa.
    internal static readonly Dictionary<string, Func<IJournaledOptimization>> DefaultFactories =
        new(StringComparer.Ordinal)
        {
            [MpoToggle.OptimizationId] = () => new MpoToggle(),
            [VisualEffectReducer.OptimizationId] = () => new VisualEffectReducer(),
            [ScheduledTaskSuppressor.OptimizationId] = () => new ScheduledTaskSuppressor(),
            [NetworkOptimizer.OptimizationId] = () => new NetworkOptimizer(),
            [PowerPlanSwitcher.OptimizationId] = () => new PowerPlanSwitcher(),
            [CpuParkingManager.OptimizationId] = () => new CpuParkingManager(),
            [ProcessPriorityBooster.OptimizationId] = () => new ProcessPriorityBooster(),
            [HybridCpuDetector.OptimizationId] = () => new HybridCpuDetector(),
            [TimerResolutionManager.OptimizationId] = () => new TimerResolutionManager(),
            [SessionSystemTweaksOptimizer.OptimizationId] = () => new SessionSystemTweaksOptimizer(),
            [ServiceSuppressor.OptimizationId] = () => new ServiceSuppressor(),
            [GpuDriverOptimizer.OptimizationId] = () => new GpuDriverOptimizer(),
            [CompetitiveMode.OptimizationId] = () => new CompetitiveMode(),
        };

    private readonly Func<string, string, ILogger, GameActionRevertResult> _revertGameAction;
    private readonly Action<ILogger> _cleanupStaleTraceSession;
    private readonly Action _resetBackgroundProcesses;

    public WatchdogRevertEngine(ILogger logger)
        : this(logger, factories: null, gameActionReverter: null,
            EtwProcessMonitor.CleanupStaleSession, () => BackgroundProcessTargets.ResetDemotedProcessesToDefaults())
    {
    }

    /// <summary>
    /// Test-only constructor that allows overriding the factory dictionary so that
    /// revert-from-record can be routed to mock implementations of
    /// <see cref="IJournaledOptimization"/> rather than the real ones (which touch
    /// the registry, services, processes, etc.), and the game-action reverter likewise.
    /// The trace-session cleanup and the background process reset do nothing unless passed,
    /// so no test reaches real processes or ETW sessions.
    /// </summary>
    internal WatchdogRevertEngine(
        ILogger logger,
        Dictionary<string, Func<IJournaledOptimization>>? factories,
        Func<string, string, ILogger, GameActionRevertResult>? gameActionReverter = null,
        Action<ILogger>? cleanupStaleTraceSession = null,
        Action? resetBackgroundProcesses = null)
    {
        _logger = logger.ForContext<WatchdogRevertEngine>();
        _factories = factories ?? DefaultFactories;
        _revertGameAction = gameActionReverter ?? GameActionRecovery.Revert;
        _cleanupStaleTraceSession = cleanupStaleTraceSession ?? (_ => { });
        _resetBackgroundProcesses = resetBackgroundProcesses ?? (() => { });
    }

    /// <summary>
    /// Reverts the journal's game actions, newest first, and then every <c>Applied</c>
    /// optimization in LIFO order: the order a live session end undoes them in. Entries already
    /// marked <c>Reverted</c> are skipped, so a replay that was cut short can run again. One record
    /// that fails or throws does not stop the rest. After reverting, marks the journal session as
    /// inactive via <paramref name="journal"/>.
    /// </summary>
    /// <param name="beforeEndSession">Runs with the result while the session is still marked active.</param>
    public JournalRevertResult RevertFromJournal(
        SessionJournalData journalData, JournalManager journal, Action<JournalRevertResult>? beforeEndSession = null)
    {
        // Clean up any orphaned ETW session from the crashed GameShift instance
        _cleanupStaleTraceSession(_logger);

        var items = new List<JournalRevertItem>();

        // Revert per-game actions (firewall rules, Defender exclusions, registry overrides). These
        // have no factory - they're undone generically from their self-describing journal records.
        if (journalData.GameActions.Count > 0)
        {
            _logger.Information(
                "[WatchdogRevertEngine] {Count} GameAction(s) to revert", journalData.GameActions.Count);
            foreach (var ga in journalData.GameActions.AsEnumerable().Reverse())
            {
                GameActionRevertResult result;
                try
                {
                    result = _revertGameAction(ga.Kind, ga.Payload, _logger);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[WatchdogRevertEngine] Exception reverting GameAction '{Kind}'", ga.Kind);
                    result = new GameActionRevertResult(RevertOutcome.Failed, ex.Message);
                }

                items.Add(new JournalRevertItem(IsGameAction: true, ga.Kind, result.Outcome, result.Message));
            }
        }

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
            if (!_factories.TryGetValue(entry.Name, out var factory))
            {
                _logger.Warning(
                    "[WatchdogRevertEngine] No factory registered for '{Name}' - skipping",
                    entry.Name);
                items.Add(new JournalRevertItem(
                    IsGameAction: false, entry.Name, RevertOutcome.Skipped, "No recovery path for this optimization"));
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
                items.Add(new JournalRevertItem(
                    IsGameAction: false, entry.Name,
                    result.State == OptimizationState.Reverted ? RevertOutcome.Reverted : RevertOutcome.Failed,
                    result.ErrorMessage));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[WatchdogRevertEngine] Exception reverting '{Name}'", entry.Name);
                items.Add(new JournalRevertItem(IsGameAction: false, entry.Name, RevertOutcome.Failed, ex.Message));
            }
        }

        // Reset any background processes left demoted by the crashed session. These tweaks (memory/IO
        // priority, EcoQoS, CPU-set) are volatile and have no per-PID journal, but a long-lived process
        // can stay demoted until it restarts - re-resolve by name and write OS defaults.
        _resetBackgroundProcesses();

        // Stamp the journal with the recovery time so the main app's
        // DeactivateProfileAsync can detect that the watchdog has already
        // reverted and skip its own redundant LIFO revert.
        journal.RecordRecoveryTimestamp(DateTime.UtcNow);

        var revertResult = new JournalRevertResult(items);
        beforeEndSession?.Invoke(revertResult);

        // Mark session inactive so boot recovery won't trigger again
        journal.EndSession();

        _logger.Information("[WatchdogRevertEngine] Recovery complete - session marked inactive");
        return revertResult;
    }
}
