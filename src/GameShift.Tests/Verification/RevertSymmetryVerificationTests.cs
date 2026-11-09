using System.Security.Principal;
using GameShift.Core.Optimization;
using GameShift.Core.System;
using GameShift.Core.Verification;
using Serilog.Core;
using Xunit;
using Xunit.Abstractions;

namespace GameShift.Tests.Verification;

/// <summary>
/// Tests for the revert-symmetry verification harness.
///
/// The comparison tests are pure. The coverage test is a closure contract in the same spirit as
/// the watchdog factory test: a new IOptimization added to GameShift.Core must either join the
/// harness engine list or be explicitly excluded here with a reason, so the trust-contract
/// verification can never silently lose coverage.
///
/// The full cycle test mutates REAL system state (then reverts it), so it is double-gated:
/// it only runs when GAMESHIFT_VERIFY_REVERT=1 AND the process is elevated. CI sets the variable
/// on an ephemeral runner; a plain local `dotnet test` reports it as skipped-by-gate and changes
/// nothing.
/// </summary>
public class RevertSymmetryVerificationTests
{
    private readonly ITestOutputHelper _output;

    public RevertSymmetryVerificationTests(ITestOutputHelper output) => _output = output;

    // ── ProbeComparison (pure) ───────────────────────────────────────────────

    [Fact]
    public void Compare_IdenticalProbes_ReportsNoDifferences()
    {
        var a = new StateProbe();
        a.Items["reg:HKLM\\X\\V"] = "dword:1";
        var b = new StateProbe();
        b.Items["reg:HKLM\\X\\V"] = "dword:1";

        Assert.Empty(ProbeComparison.Compare(a, b));
    }

    [Fact]
    public void Compare_ChangedRemovedAndAddedItems_AreAllReported()
    {
        var before = new StateProbe();
        before.Items["reg:HKLM\\X\\Changed"] = "dword:1";
        before.Items["reg:HKLM\\X\\Removed"] = "sz:old";

        var after = new StateProbe();
        after.Items["reg:HKLM\\X\\Changed"] = "dword:2";
        after.Items["reg:HKLM\\X\\Added"] = "sz:new";

        var diffs = ProbeComparison.Compare(before, after);

        Assert.Equal(3, diffs.Count);
        Assert.Contains(diffs, d => d.Key == "reg:HKLM\\X\\Changed" && d.Before == "dword:1" && d.After == "dword:2");
        Assert.Contains(diffs, d => d.Key == "reg:HKLM\\X\\Removed" && d.After == ProbeComparison.Absent);
        Assert.Contains(diffs, d => d.Key == "reg:HKLM\\X\\Added" && d.Before == ProbeComparison.Absent);
    }

    [Fact]
    public void Compare_KeyLookupIsCaseInsensitive_ValueComparisonIsExact()
    {
        var before = new StateProbe();
        before.Items["svc:status:SysMain"] = "Running";
        var after = new StateProbe();
        after.Items["svc:status:sysmain"] = "running";

        var diffs = ProbeComparison.Compare(before, after);

        Assert.Single(diffs); // same key despite casing, but the value text differs
    }

    // ── Residual classification policy (pure) ────────────────────────────────

    [Fact]
    public void ClassifyResidual_TouchedDemandStartServiceStatus_IsDriftNotResidue()
    {
        // The WdiSystemHost case: a Manual service idle-exits when its driver service stops.
        // Windows owns demand-start process lifetime, so this must not fail the contract.
        var before = new StateProbe();
        before.Items["svc:start:WdiSystemHost"] = "Manual";
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "svc:status:WdiSystemHost" };
        var residual = new List<StateDifference> { new("svc:status:WdiSystemHost", "Running", "Stopped") };

        var (failures, drift) = RevertVerificationRunner.ClassifyResidual(before, touched, residual);

        Assert.Empty(failures);
        Assert.Single(drift);
    }

    [Fact]
    public void ClassifyResidual_AutomaticServiceAndRegistry_RemainContractualFailures()
    {
        var before = new StateProbe();
        before.Items["svc:start:SysMain"] = "Automatic";
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "svc:status:SysMain" };
        var residual = new List<StateDifference>
        {
            new("svc:status:SysMain", "Running", "Stopped"),          // touched + automatic: failure
            new("svc:status:SomeOther", "Running", "Stopped"),        // untouched: drift
            new("reg:HKLM\\X\\V", "dword:1", "dword:5"),              // registry: always failure
            new("svc:start:SysMain", "Automatic", "Manual"),          // start type: always failure
        };

        var (failures, drift) = RevertVerificationRunner.ClassifyResidual(before, touched, residual);

        Assert.Equal(3, failures.Count);
        Assert.Single(drift);
        Assert.Equal("svc:status:SomeOther", drift[0].Key);
    }

    // ── Coverage closure ─────────────────────────────────────────────────────

    /// <summary>
    /// Session optimizations intentionally NOT exercised by the harness, with the reason.
    /// Anything new must be added to the harness engine list or listed here deliberately.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedFromHarness = new(StringComparer.Ordinal)
    {
        ["VbsHvciToggle"] = "reboot-scoped security setting, user-initiated, not part of a session",
        ["CoreIsolationManager"] = "reboot-scoped core reservation, user-initiated, not part of a session",
        ["MpoToggle"] = RevertOnly,
        ["VisualEffectReducer"] = RevertOnly,
        ["ScheduledTaskSuppressor"] = RevertOnly,
        ["NetworkOptimizer"] = RevertOnly,
        ["CpuParkingManager"] = RevertOnly,
        ["GpuDriverOptimizer"] = RevertOnly,
        ["CompetitiveMode"] = RevertOnly,
        ["SessionSystemTweaksOptimizer"] = RevertOnly,
    };

    private const string RevertOnly = "revert-only: replays journals written by earlier versions";

    [Fact]
    public void EverySessionOptimization_IsCoveredByTheHarness_OrExplicitlyExcluded()
    {
        var coreImplementors = typeof(IOptimization).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IOptimization).IsAssignableFrom(t))
            .ToList();

        var harnessTypes = RevertVerificationRunner.CreateEngineModules()
            .Select(m => m.GetType())
            .ToHashSet();

        var uncovered = coreImplementors
            .Where(t => !harnessTypes.Contains(t) && !ExcludedFromHarness.ContainsKey(t.Name))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.True(uncovered.Count == 0,
            "IOptimization implementor(s) not exercised by the revert-symmetry harness and not "
            + "explicitly excluded: " + string.Join(", ", uncovered)
            + ". Add them to RevertVerificationRunner.CreateEngineModules() or to the exclusion "
            + "list in this test with a reason.");

        // Sanity in the other direction: the harness only runs real Core implementors.
        foreach (var type in harnessTypes)
            Assert.Contains(type, coreImplementors);
    }

    // ── Full cycle (gated) ───────────────────────────────────────────────────

    [Fact]
    public async Task FullSessionCycle_LeavesNoPersistentResidue()
    {
        if (Environment.GetEnvironmentVariable("GAMESHIFT_VERIFY_REVERT") != "1")
        {
            _output.WriteLine("Gated: set GAMESHIFT_VERIFY_REVERT=1 (elevated) to run the live cycle.");
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            _output.WriteLine("Gated: process is not elevated; the live cycle needs administrator rights.");
            return;
        }

        var planBefore = SystemStateSnapshot.Capture().OriginalPowerPlan;
        var result = await RevertVerificationRunner.RunAsync(Logger.None);

        _output.WriteLine($"power plan before the session: {planBefore}");
        _output.WriteLine(result.FormatReport());

        Assert.True(result.Error == null, "harness error: " + result.Error);
        var notMeaningful = WhyTheRunIsNotMeaningful(planBefore, result.AppliedChanges);
        Assert.True(notMeaningful == null, notMeaningful);
        Assert.True(result.Failures.Count == 0,
            "persistent residue after revert:" + Environment.NewLine + result.FormatReport());
    }

    private static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    // A residue-free revert only proves something when the session changed probed state. From
    // Balanced the session must also switch the plan, so the plan revert is part of the proof.
    private static string? WhyTheRunIsNotMeaningful(Guid planBefore, IReadOnlyCollection<StateDifference> appliedChanges)
    {
        if (appliedChanges.Count == 0)
            return "no probed changes during the session; the run was not meaningful";

        if (planBefore == BalancedPlan
            && !appliedChanges.Any(d => string.Equals(d.Key, "pcfg:active-scheme", StringComparison.OrdinalIgnoreCase)))
            return $"the power plan switch from Balanced did not happen ({appliedChanges.Count} other probed changes); "
                + "the plan revert was not exercised";

        return null;
    }

    [Fact]
    public void MeaningfulRun_NoChanges_IsRejected()
    {
        var problem = WhyTheRunIsNotMeaningful(Guid.Empty, Array.Empty<StateDifference>());

        Assert.NotNull(problem);
        Assert.Contains("no probed changes", problem);
    }

    [Fact]
    public void MeaningfulRun_FromBalancedWithoutThePlanSwitch_IsRejected()
    {
        var changes = new[] { new StateDifference("svc:status:UsoSvc", "Running", "Stopped") };

        var problem = WhyTheRunIsNotMeaningful(BalancedPlan, changes);

        Assert.NotNull(problem);
        Assert.Contains("power plan switch from Balanced did not happen", problem);
    }

    [Fact]
    public void MeaningfulRun_FromBalancedWithThePlanSwitch_IsAccepted()
    {
        var changes = new[]
        {
            new StateDifference("pcfg:active-scheme", "381b4222-f694-41f0-9685-ff5bb260df2e", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
        };

        Assert.Null(WhyTheRunIsNotMeaningful(BalancedPlan, changes));
    }

    // Any other plan is left alone by design, so one probed change is enough there.
    [Fact]
    public void MeaningfulRun_FromHighPerformanceWithOneChange_IsAccepted()
    {
        var highPerformance = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        var changes = new[] { new StateDifference("svc:status:UsoSvc", "Running", "Stopped") };

        Assert.Null(WhyTheRunIsNotMeaningful(highPerformance, changes));
    }

    // ── Single-instance mutex ────────────────────────────────────────────────

    // The harness writes the real journal; while it runs, the app must not start and replay the
    // probe session, and while the app runs, the harness must not start. A local name keeps the
    // test away from the real app's mutex.
    [Fact]
    public void InstanceMutex_IsTakenOnceAndFreedOnDispose()
    {
        var name = $"Local\\GameShiftTests_{Guid.NewGuid():N}";

        using (var first = RevertVerificationRunner.TryTakeInstanceMutex(name))
        {
            Assert.NotNull(first);
            Assert.Null(RevertVerificationRunner.TryTakeInstanceMutex(name));
        }

        using var again = RevertVerificationRunner.TryTakeInstanceMutex(name);
        Assert.NotNull(again);
    }

    // The mutex is held first, so the run refuses before it probes or changes anything.
    [Fact]
    public async Task RunAsync_WhileTheAppHoldsTheMutex_RefusesWithoutRunning()
    {
        var name = $"Local\\GameShiftTests_{Guid.NewGuid():N}";
        using var app = new Mutex(initiallyOwned: true, name, out bool created);
        Assert.True(created);

        var result = await RevertVerificationRunner.RunAsync(Logger.None, name);

        Assert.False(result.Passed);
        Assert.NotNull(result.Error);
        Assert.Equal(0, result.BeforeItemCount);
        Assert.Empty(result.AppliedChanges);
    }
}
