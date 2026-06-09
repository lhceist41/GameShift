using System.Reflection;
using GameShift.Core.Journal;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Contract tests for the crash-recovery trust chain. These lock in the invariants that make
/// the journal + watchdog design safe:
///
/// 1. Closure: every <see cref="IJournaledOptimization"/> implementor in GameShift.Core is
///    registered in <see cref="WatchdogRevertEngine.DefaultFactories"/> and vice versa. A module
///    missing its factory would journal state the watchdog can never revert after a crash; a
///    dangling factory is a dead registration.
/// 2. Identity: each factory builds an instance whose Name matches its registration key, so the
///    journal entry written at apply time routes back to the same module at recovery time.
/// 3. Resilience: RevertFromRecord must never throw and never claim success when handed corrupt
///    journal data. The watchdog runs after a crash; garbage in the journal must degrade to a
///    logged failure, not a recovery abort or a false "Reverted".
/// </summary>
public class JournaledOptimizationContractTests
{
    private static List<Type> CoreImplementors() =>
        typeof(IJournaledOptimization).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IJournaledOptimization).IsAssignableFrom(t))
            .ToList();

    private static string? OptimizationIdOf(Type type) =>
        type.GetField("OptimizationId", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            ?.GetRawConstantValue() as string;

    [Fact]
    public void EveryJournaledOptimization_DeclaresAPublicOptimizationId()
    {
        var missing = CoreImplementors()
            .Where(t => string.IsNullOrWhiteSpace(OptimizationIdOf(t)))
            .Select(t => t.Name)
            .ToList();

        Assert.True(missing.Count == 0,
            "IJournaledOptimization implementor(s) without a public const string OptimizationId: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void EveryJournaledOptimization_HasAWatchdogFactory_AndEveryFactoryHasAnImplementor()
    {
        var implementorIds = CoreImplementors()
            .Select(OptimizationIdOf)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var factoryKeys = WatchdogRevertEngine.DefaultFactories.Keys.ToHashSet(StringComparer.Ordinal);

        var missingFactories = implementorIds.Except(factoryKeys).OrderBy(s => s).ToList();
        var danglingFactories = factoryKeys.Except(implementorIds).OrderBy(s => s).ToList();

        Assert.True(missingFactories.Count == 0,
            "Journaled module(s) with NO watchdog factory (unrecoverable after a crash): "
            + string.Join(", ", missingFactories)
            + ". Register them in WatchdogRevertEngine.DefaultFactories.");

        Assert.True(danglingFactories.Count == 0,
            "Watchdog factory key(s) with no IJournaledOptimization implementor: "
            + string.Join(", ", danglingFactories));
    }

    [Fact]
    public void EveryWatchdogFactory_BuildsAnInstanceWhoseNameMatchesItsKey()
    {
        var mismatches = new List<string>();

        foreach (var (key, factory) in WatchdogRevertEngine.DefaultFactories)
        {
            var instance = factory();
            if (!string.Equals(instance.Name, key, StringComparison.Ordinal))
                mismatches.Add($"key '{key}' built {instance.GetType().Name} with Name '{instance.Name}'");
        }

        Assert.True(mismatches.Count == 0,
            "Factory key/Name mismatch (journal entries would not route back to the module): "
            + string.Join("; ", mismatches));
    }

    [Theory]
    [InlineData("{ this is not valid json")]
    [InlineData("")]
    [InlineData("null")]
    public void RevertFromRecord_OnCorruptJournalData_NeverThrows(string corruptJson)
    {
        var violations = new List<string>();

        foreach (var (key, factory) in WatchdogRevertEngine.DefaultFactories)
        {
            var instance = factory();
            var exception = Record.Exception(() => instance.RevertFromRecord(corruptJson));

            if (exception != null)
                violations.Add($"'{key}' threw {exception.GetType().Name}");
        }

        Assert.True(violations.Count == 0,
            $"RevertFromRecord threw for input '{corruptJson}' (recovery must degrade, not abort): "
            + string.Join("; ", violations));
    }

    // The empty string is deliberately NOT in this theory: HybridCpuDetector documents
    // empty/whitespace OriginalValue as "applied via the volatile CPU-Sets path, nothing
    // persistent to revert", for which Reverted is the honest answer. Invalid JSON and a
    // literal JSON null can never come from a healthy Apply (every module journals at
    // minimum "{}" or "[]"), so claiming Reverted on them is a false success.
    [Theory]
    [InlineData("{ this is not valid json")]
    [InlineData("null")]
    public void RevertFromRecord_OnUndecodableJournalData_NeverClaimsSuccess(string corruptJson)
    {
        var violations = new List<string>();

        foreach (var (key, factory) in WatchdogRevertEngine.DefaultFactories)
        {
            var instance = factory();
            var result = instance.RevertFromRecord(corruptJson);

            if (result.State == OptimizationState.Reverted)
                violations.Add($"'{key}' claimed Reverted");
        }

        Assert.True(violations.Count == 0,
            $"RevertFromRecord claimed success on undecodable input '{corruptJson}': "
            + string.Join("; ", violations));
    }
}
