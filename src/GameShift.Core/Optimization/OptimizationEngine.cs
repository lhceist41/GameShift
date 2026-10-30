using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Event args for when an optimization is applied.
/// </summary>
public class OptimizationAppliedEventArgs : EventArgs
{
    public IOptimization Optimization { get; }

    public OptimizationAppliedEventArgs(IOptimization optimization)
    {
        Optimization = optimization;
    }
}

/// <summary>
/// Event args for when an optimization is reverted.
/// </summary>
public class OptimizationRevertedEventArgs : EventArgs
{
    public IOptimization Optimization { get; }

    public OptimizationRevertedEventArgs(IOptimization optimization)
    {
        Optimization = optimization;
    }
}

/// <summary>
/// Orchestrates the lifecycle of system optimizations.
/// Handles state capture, LIFO revert, and graceful failure for optimizations.
/// Thread-safe for concurrent activate/deactivate calls.
/// </summary>
public class OptimizationEngine : IDisposable
{
    private readonly Stack<IOptimization> _appliedOptimizations;
    private SystemStateSnapshot? _snapshot;
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger _logger;
    private readonly List<IOptimization> _optimizations;
    private readonly JournalManager _journal;

    /// <summary>
    /// The session journal, shared with the detection layer so per-game GameActions can be recorded
    /// for crash recovery against the same in-memory session as the optimizations.
    /// </summary>
    internal JournalManager Journal => _journal;

    /// <summary>
    /// Number of optimizations currently applied (used by SessionTracker for session stats).
    /// Backed by a separate counter so cross-thread readers (SessionTracker, dashboard) get a
    /// consistent value without touching the Stack while it is mutated under the semaphore.
    /// </summary>
    public int AppliedCount => Volatile.Read(ref _appliedCount);
    private int _appliedCount;

    /// <summary>
    /// Fired when an optimization is successfully applied.
    /// Used by UI to update status indicators (Phase 6).
    /// </summary>
    public event EventHandler<OptimizationAppliedEventArgs>? OptimizationApplied;

    /// <summary>
    /// Fired when an optimization is successfully reverted.
    /// Used by UI to update status indicators (Phase 6).
    /// </summary>
    public event EventHandler<OptimizationRevertedEventArgs>? OptimizationReverted;

    /// <summary>
    /// Fired when an optimization fails to apply (returns false or throws).
    /// Used by UI to show failure counts on dashboard and session summary.
    /// </summary>
    public event EventHandler<OptimizationAppliedEventArgs>? OptimizationFailed;

    /// <summary>
    /// Creates a new OptimizationEngine with the specified optimizations.
    /// </summary>
    /// <param name="optimizations">Collection of optimizations to manage</param>
    public OptimizationEngine(IEnumerable<IOptimization> optimizations)
        : this(optimizations, new JournalManager())
    {
    }

    /// <summary>
    /// Creates an engine that records its sessions in <paramref name="journal"/>. Used by tests to
    /// keep the session journal out of %ProgramData%.
    /// </summary>
    internal OptimizationEngine(IEnumerable<IOptimization> optimizations, JournalManager journal)
    {
        _optimizations = optimizations.ToList();
        _appliedOptimizations = new Stack<IOptimization>();
        _semaphore = new SemaphoreSlim(1, 1); // Allow one thread at a time
        _logger = SettingsManager.Logger; // Use centralized logger from Phase 1
        _journal = journal;
    }

    /// <summary>
    /// Activates optimizations for the specified game profile.
    /// Captures system state snapshot before applying any changes.
    /// Failed optimizations are logged but don't block others.
    /// Thread-safe: Serializes with DeactivateProfileAsync via semaphore.
    /// </summary>
    /// <param name="profile">Game profile to activate</param>
    public async Task ActivateProfileAsync(GameProfile profile)
    {
        // Collect events to fire AFTER the semaphore is released, so subscriber
        // reentrancy (e.g. a handler that calls DeactivateProfileAsync) can't deadlock.
        var eventsToFire = new List<Action>();

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            _logger.Information("Activating profile for game: {GameName} (PID: {ProcessId})",
                profile.GameName, profile.ProcessId);

            // Capture system state BEFORE applying any optimization
            _snapshot = SystemStateSnapshot.Capture();
            _logger.Debug("System state snapshot captured at {CaptureTime}", _snapshot.CaptureTime);

            // Open session journal
            _journal.StartSession(profile);

            // Apply available optimizations in order
            int skippedCount = 0;
            foreach (var optimization in _optimizations.Where(o => o.IsAvailable))
            {
                // Guard against double-apply: if an optimization is already applied (e.g. the
                // manual "Optimize Now" path ran, then a game auto-launched), re-applying would
                // let modules that capture original state on Apply overwrite their true baseline
                // with already-modified values - corrupting revert. Skip and keep tracking intact.
                if (optimization.IsApplied || _appliedOptimizations.Contains(optimization))
                {
                    _logger.Information("Skipped (already applied): {OptimizationName}", optimization.Name);
                    continue;
                }

                if (!profile.IsOptimizationEnabled(optimization.Name))
                {
                    _logger.Information("Skipped (disabled in profile): {OptimizationName}", optimization.Name);
                    skippedCount++;
                    continue;
                }

                try
                {
                    _logger.Debug("Applying optimization: {OptimizationName}", optimization.Name);

                    bool success;

                    if (optimization is IJournaledOptimization journaled)
                    {
                        var context = new SystemContext { Profile = profile, Snapshot = _snapshot };
                        if (!journaled.CanApply(context))
                        {
                            _logger.Information("Skipped (CanApply returned false): {OptimizationName}", optimization.Name);
                            skippedCount++;
                            continue;
                        }

                        var result = journaled.Apply();
                        success = result.State == OptimizationState.Applied;

                        if (success)
                            _journal.RecordApplied(result);
                    }
                    else
                    {
                        success = await optimization.ApplyAsync(_snapshot, profile).ConfigureAwait(false);
                    }

                    if (success)
                    {
                        // Track for LIFO revert
                        _appliedOptimizations.Push(optimization);
                        Interlocked.Increment(ref _appliedCount);
                        _logger.Information("Successfully applied: {OptimizationName}", optimization.Name);

                        // Queue UI notification - fired outside semaphore below
                        var appliedOpt = optimization;
                        eventsToFire.Add(() => OptimizationApplied?.Invoke(this, new OptimizationAppliedEventArgs(appliedOpt)));
                    }
                    else
                    {
                        _logger.Warning("Optimization failed (returned false): {OptimizationName}",
                            optimization.Name);
                        var failedOpt = optimization;
                        eventsToFire.Add(() => OptimizationFailed?.Invoke(this, new OptimizationAppliedEventArgs(failedOpt)));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Optimization threw exception: {OptimizationName}",
                        optimization.Name);
                    var failedOpt = optimization;
                    eventsToFire.Add(() => OptimizationFailed?.Invoke(this, new OptimizationAppliedEventArgs(failedOpt)));
                }
            }

            _logger.Information("Profile activation complete. Applied {AppliedCount}, skipped {SkippedCount} (disabled in profile or not applicable).",
                _appliedCount, skippedCount);
        }
        finally
        {
            _semaphore.Release();
        }

        // Fire events OUTSIDE the semaphore to prevent subscriber-reentrancy deadlocks.
        foreach (var fire in eventsToFire)
        {
            try { fire(); }
            catch (Exception ex) { _logger.Warning(ex, "Optimization event subscriber threw"); }
        }
    }

    /// <summary>
    /// Deactivates the current profile, reverting all applied optimizations.
    /// Reverts in LIFO order (last-applied reverts first) for dependency safety.
    /// Failed reverts are logged but don't stop other reverts from being attempted.
    /// Thread-safe: Serializes with ActivateProfileAsync via semaphore.
    /// </summary>
    public async Task DeactivateProfileAsync()
    {
        // Collect events to fire AFTER the semaphore is released, so subscriber
        // reentrancy (e.g. a handler that calls ActivateProfileAsync) can't deadlock.
        var eventsToFire = new List<Action>();

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            // If the watchdog has already reverted our optimizations (because it
            // detected a heartbeat timeout or pipe disconnect), the registry/system
            // state has been restored under our feet. Re-running our own revert
            // would redundantly process no-op state and could race with anything
            // else reading the journal. Clear in-memory tracking and exit early.
            if (_journal.WasRecoveredDuringCurrentSession())
            {
                _logger.Information(
                    "[OptimizationEngine] Watchdog already recovered optimizations - skipping LIFO revert");

                _appliedOptimizations.Clear(); // Clear in-memory tracking
                Interlocked.Exchange(ref _appliedCount, 0);
                _snapshot = null;
                return;
            }

            _logger.Information("Deactivating profile. Reverting {Count} optimizations in LIFO order.",
                _appliedCount);

            // Revert in reverse order (LIFO via Stack)
            while (_appliedOptimizations.TryPop(out var optimization))
            {
                Interlocked.Decrement(ref _appliedCount);
                try
                {
                    _logger.Debug("Reverting optimization: {OptimizationName}", optimization.Name);

                    if (_snapshot == null)
                    {
                        _logger.Error("Cannot revert {OptimizationName}: snapshot is null (double-deactivate or activate failed)", optimization.Name);
                        continue;
                    }

                    bool success;

                    if (optimization is IJournaledOptimization journaled)
                    {
                        var result = journaled.Revert();
                        success = result.State == OptimizationState.Reverted;
                        _journal.RecordReverted(optimization.Name, result.State);
                    }
                    else
                    {
                        success = await optimization.RevertAsync(_snapshot).ConfigureAwait(false);
                    }

                    if (success)
                    {
                        _logger.Information("Successfully reverted: {OptimizationName}", optimization.Name);

                        // Queue UI notification - fired outside semaphore below
                        var revertedOpt = optimization;
                        eventsToFire.Add(() => OptimizationReverted?.Invoke(this, new OptimizationRevertedEventArgs(revertedOpt)));
                    }
                    else
                    {
                        _logger.Error("Revert failed (returned false): {OptimizationName}",
                            optimization.Name);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue reverting other optimizations
                    _logger.Error(ex, "Revert threw exception: {OptimizationName}", optimization.Name);
                }
            }

            // Close the journal - session ended cleanly
            _journal.EndSession();

            // Clear snapshot after all reverts complete
            _snapshot = null;
            _logger.Information("Profile deactivation complete.");
        }
        finally
        {
            _semaphore.Release();
        }

        // Fire events OUTSIDE the semaphore to prevent subscriber-reentrancy deadlocks.
        foreach (var fire in eventsToFire)
        {
            try { fire(); }
            catch (Exception ex) { _logger.Warning(ex, "Optimization event subscriber threw"); }
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
