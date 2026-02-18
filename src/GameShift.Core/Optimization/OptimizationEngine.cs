using GameShift.Core.Config;
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
public class OptimizationEngine
{
    private readonly Stack<IOptimization> _appliedOptimizations;
    private SystemStateSnapshot? _snapshot;
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger _logger;
    private readonly List<IOptimization> _optimizations;

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
    /// Creates a new OptimizationEngine with the specified optimizations.
    /// </summary>
    /// <param name="optimizations">Collection of optimizations to manage</param>
    public OptimizationEngine(IEnumerable<IOptimization> optimizations)
    {
        _optimizations = optimizations.ToList();
        _appliedOptimizations = new Stack<IOptimization>();
        _semaphore = new SemaphoreSlim(1, 1); // Allow one thread at a time
        _logger = SettingsManager.Logger; // Use centralized logger from Phase 1
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

                try
                {
                    _logger.Debug("Applying optimization: {OptimizationName}", optimization.Name);

                    bool success = await optimization.ApplyAsync(_snapshot, profile);

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
                        // Log warning but continue with other optimizations
                        _logger.Warning("Optimization failed (returned false): {OptimizationName}",
                            optimization.Name);
                    }
                }
                catch (Exception ex)
                {
                    // Catch exceptions from misbehaving optimizations
                    _logger.Warning(ex, "Optimization threw exception: {OptimizationName}",
                        optimization.Name);
                }
            }

            _logger.Information("Profile activation complete. Applied {AppliedCount}, skipped {SkippedCount} (disabled in profile).",
                _appliedOptimizations.Count, skippedCount);
        }
        finally
        {
            _semaphore.Release();
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

                    bool success = await optimization.RevertAsync(_snapshot!);

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

            // Clear snapshot after all reverts complete
            _snapshot = null;
            _logger.Information("Profile deactivation complete.");
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
