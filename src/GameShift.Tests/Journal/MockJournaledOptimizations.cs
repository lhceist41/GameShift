using GameShift.Core.Journal;

namespace GameShift.Tests.Journal;

/// <summary>
/// Mock IJournaledOptimization used to verify <see cref="WatchdogRevertEngine"/>
/// behavior without touching the real registry/services/processes that the
/// production optimization implementations modify.
/// </summary>
public class MockJournaledOptimization : IJournaledOptimization
{
    public string Name { get; set; } = "MockOpt";

    /// <summary>Arguments passed to every RevertFromRecord call, in order.</summary>
    public List<string> RevertFromRecordCalls { get; } = new();

    /// <summary>When true, RevertFromRecord throws to simulate a reverter failure.</summary>
    public bool ShouldThrow { get; set; }

    /// <summary>The state to report on the OptimizationResult returned from RevertFromRecord.</summary>
    public OptimizationState ResultState { get; set; } = OptimizationState.Reverted;

    public bool CanApply(SystemContext context) => true;

    public OptimizationResult Apply() =>
        new(Name, string.Empty, string.Empty, OptimizationState.Applied);

    public OptimizationResult Revert() =>
        new(Name, string.Empty, string.Empty, OptimizationState.Reverted);

    public bool Verify() => true;

    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        RevertFromRecordCalls.Add(originalValueJson);
        if (ShouldThrow) throw new InvalidOperationException("Mock failure");
        return new OptimizationResult(Name, string.Empty, string.Empty, ResultState);
    }
}

/// <summary>
/// Mock IJournaledOptimization that appends its name to a shared sequence list
/// whenever RevertFromRecord is invoked. Lets tests verify the revert order
/// across multiple distinct optimization names in a single list.
/// </summary>
public class RecordingMockJournaledOptimization : IJournaledOptimization
{
    private readonly List<string> _sequence;

    public RecordingMockJournaledOptimization(string name, List<string> sequence)
    {
        Name = name;
        _sequence = sequence;
    }

    public string Name { get; }

    public bool CanApply(SystemContext context) => true;

    public OptimizationResult Apply() =>
        new(Name, string.Empty, string.Empty, OptimizationState.Applied);

    public OptimizationResult Revert() =>
        new(Name, string.Empty, string.Empty, OptimizationState.Reverted);

    public bool Verify() => true;

    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        _sequence.Add(Name);
        return new OptimizationResult(Name, string.Empty, string.Empty, OptimizationState.Reverted);
    }
}
