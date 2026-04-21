using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Tests.Optimization;

/// <summary>
/// Mock optimization that always succeeds.
/// Used for testing OptimizationEngine apply/revert lifecycle.
/// </summary>
public class MockOptimizationSuccess : IOptimization
{
    public string Name { get; set; } = "MockSuccess";
    public string Description { get; set; } = "Mock optimization for testing";
    public bool IsApplied { get; private set; }
    public bool IsAvailable { get; set; } = true;

    public int ApplyCallCount { get; private set; }
    public int RevertCallCount { get; private set; }

    /// <summary>
    /// Captures the snapshot reference passed to ApplyAsync.
    /// Used to verify the engine captures system state before applying optimizations.
    /// </summary>
    public SystemStateSnapshot? LastSnapshotReceived { get; private set; }

    /// <summary>
    /// Optional callback invoked when ApplyAsync is called.
    /// </summary>
    public Action? OnApply { get; set; }

    /// <summary>
    /// Optional callback invoked when RevertAsync is called.
    /// Used for LIFO order verification.
    /// </summary>
    public Action? OnRevert { get; set; }

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        LastSnapshotReceived = snapshot;
        ApplyCallCount++;
        IsApplied = true;
        OnApply?.Invoke();
        return Task.FromResult(true);
    }

    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        RevertCallCount++;
        IsApplied = false;
        OnRevert?.Invoke();
        return Task.FromResult(true);
    }
}

/// <summary>
/// Mock optimization that throws exception on apply.
/// Used for testing graceful failure handling.
/// </summary>
public class MockOptimizationFailure : IOptimization
{
    public string Name { get; set; } = "MockFailure";
    public string Description { get; set; } = "Mock optimization that fails";
    public bool IsApplied { get; private set; }
    public bool IsAvailable { get; set; } = true;

    public int ApplyCallCount { get; private set; }

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        ApplyCallCount++;
        throw new InvalidOperationException("Simulated failure");
    }

    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        IsApplied = false;
        return Task.FromResult(true);
    }
}

/// <summary>
/// Mock optimization with IsAvailable = false.
/// Used for testing that engine skips unavailable optimizations.
/// </summary>
public class MockOptimizationUnavailable : IOptimization
{
    public string Name { get; set; } = "MockUnavailable";
    public string Description { get; set; } = "Mock optimization that's unavailable";
    public bool IsApplied { get; private set; }
    public bool IsAvailable { get; set; } = false;

    public int ApplyCallCount { get; private set; }

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        ApplyCallCount++;
        // Should never be called - if it is, fail the test
        throw new InvalidOperationException("Unavailable optimization should not be applied!");
    }

    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        IsApplied = false;
        return Task.FromResult(true);
    }
}

/// <summary>
/// Mock optimization that detects Apply/Revert interleaving.
/// Tracks concurrent Apply and Revert invocations to verify that
/// OptimizationEngine serializes access via its internal semaphore.
/// </summary>
public class MockOptimizationWithInterleaveDetection : IOptimization
{
    private int _activeApplyCount;
    private int _activeRevertCount;
    private readonly object _lock = new();

    public string Name { get; set; } = "MockInterleaveDetection";
    public string Description { get; set; } = "Mock optimization that detects concurrent Apply/Revert";
    public bool IsApplied { get; private set; }
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Maximum number of simultaneously-active Apply+Revert invocations observed.
    /// Should remain at 1 if the engine serializes correctly.
    /// </summary>
    public int MaxInterleaveObserved { get; private set; }

    /// <summary>
    /// True if Apply and Revert were ever observed running simultaneously.
    /// Indicates the engine's semaphore serialization failed.
    /// </summary>
    public bool InterleaveDetected { get; private set; }

    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        lock (_lock)
        {
            _activeApplyCount++;
            if (_activeRevertCount > 0) InterleaveDetected = true;
            MaxInterleaveObserved = Math.Max(MaxInterleaveObserved, _activeApplyCount + _activeRevertCount);
        }

        await Task.Delay(10); // Simulate work to widen the race window

        lock (_lock)
        {
            _activeApplyCount--;
            IsApplied = true;
        }
        return true;
    }

    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        lock (_lock)
        {
            _activeRevertCount++;
            if (_activeApplyCount > 0) InterleaveDetected = true;
            MaxInterleaveObserved = Math.Max(MaxInterleaveObserved, _activeApplyCount + _activeRevertCount);
        }

        await Task.Delay(10); // Simulate work to widen the race window

        lock (_lock)
        {
            _activeRevertCount--;
            IsApplied = false;
        }
        return true;
    }
}
