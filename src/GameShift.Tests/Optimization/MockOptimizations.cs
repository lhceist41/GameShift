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
    /// Optional callback invoked when RevertAsync is called.
    /// Used for LIFO order verification.
    /// </summary>
    public Action? OnRevert { get; set; }

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        ApplyCallCount++;
        IsApplied = true;
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
