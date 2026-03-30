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
    /// Number of optimizations currently applied (used by SessionTracker for session stats).
    /// </summary>
    public int AppliedCount => _appliedOptimizations.Count;

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
    {
        _optimizations = optimizations.ToList();
        _appliedOptimizations = new Stack<IOptimization>();
        _semaphore = new SemaphoreSlim(1, 1); // Allow one thread at a time
        _logger = SettingsManager.Logger; // Use centralized logger from Phase 1
        _journal = new JournalManager();
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
        await _semaphore.WaitAsync();
        try
        {
            _logger.Information("Activating profile for game: {GameName} (PID: {ProcessId})",
                profile.GameName, profile.ProcessId);

            // Capture system state BEFORE applying any optimization
            _snapshot = SystemStateSnapshot.Capture();
            _logger.Debug("System state snapshot captured at {CaptureTime}", _snapshot.CaptureTime);

            // Open session journal
            _journal.StartSession(profile);

            // Load BackgroundMode settings once for all optimizations
            var bgExclusions = BuildBackgroundModeExclusions();

            // Apply available optimizations in order
            int skippedCount = 0;
            foreach (var optimization in _optimizations.Where(o => o.IsAvailable))
            {
                if (!profile.IsOptimizationEnabled(optimization.Name))
                {
                    _logger.Information("Skipped (disabled in profile): {OptimizationName}", optimization.Name);
                    skippedCount++;
                    continue;
                }

                if (bgExclusions.Contains(optimization.Name))
                {
                    _logger.Information("Skipped (handled by Background Mode): {OptimizationName}", optimization.Name);
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
                        success = await optimization.ApplyAsync(_snapshot, profile);
                    }

                    if (success)
                    {
                        // Track for LIFO revert
                        _appliedOptimizations.Push(optimization);
                        _logger.Information("Successfully applied: {OptimizationName}", optimization.Name);

                        // Notify UI
                        OptimizationApplied?.Invoke(this, new OptimizationAppliedEventArgs(optimization));
                    }
                    else
                    {
                        _logger.Warning("Optimization failed (returned false): {OptimizationName}",
                            optimization.Name);
                        OptimizationFailed?.Invoke(this, new OptimizationAppliedEventArgs(optimization));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Optimization threw exception: {OptimizationName}",
                        optimization.Name);
                    OptimizationFailed?.Invoke(this, new OptimizationAppliedEventArgs(optimization));
                }
            }

            _logger.Information("Profile activation complete. Applied {AppliedCount}, skipped {SkippedCount} (disabled in profile or Background Mode).",
                _appliedOptimizations.Count, skippedCount);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Builds a set of optimization names that are already handled by Background Mode.
    /// When Background Mode is active and a specific service is enabled, the corresponding
    /// session optimization is excluded to avoid double-handling.
    /// </summary>
    private static HashSet<string> BuildBackgroundModeExclusions()
    {
        var exclusions = new HashSet<string>(StringComparer.Ordinal);

        var settings = SettingsManager.Load();
        if (settings.BackgroundMode?.Enabled != true)
            return exclusions;

        var bg = settings.BackgroundMode;

        if (bg.StandbyListCleanerEnabled)
            exclusions.Add(MemoryOptimizer.OptimizationId);

        // Always exclude session power plan switching when Background Mode is active —
        // Background Mode owns power plan management via PowerPlanManager
        exclusions.Add(PowerPlanSwitcher.OptimizationId);

        if (bg.TimerResolutionEnabled)
            exclusions.Add(TimerResolutionManager.OptimizationId);

        return exclusions;
    }

    /// <summary>
    /// Deactivates the current profile, reverting all applied optimizations.
    /// Reverts in LIFO order (last-applied reverts first) for dependency safety.
    /// Failed reverts are logged but don't stop other reverts from being attempted.
    /// Thread-safe: Serializes with ActivateProfileAsync via semaphore.
    /// </summary>
    public async Task DeactivateProfileAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            _logger.Information("Deactivating profile. Reverting {Count} optimizations in LIFO order.",
                _appliedOptimizations.Count);

            // Revert in reverse order (LIFO via Stack)
            while (_appliedOptimizations.TryPop(out var optimization))
            {
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
                        success = await optimization.RevertAsync(_snapshot);
                    }

                    if (success)
                    {
                        _logger.Information("Successfully reverted: {OptimizationName}", optimization.Name);

                        // Notify UI
                        OptimizationReverted?.Invoke(this, new OptimizationRevertedEventArgs(optimization));
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

            // Close the journal — session ended cleanly
            _journal.EndSession();

            // Clear snapshot after all reverts complete
            _snapshot = null;
            _logger.Information("Profile deactivation complete.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
