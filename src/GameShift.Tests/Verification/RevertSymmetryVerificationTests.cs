using System.Security.Principal;
using GameShift.Core.Optimization;
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

    // ── Coverage closure ─────────────────────────────────────────────────────

    /// <summary>
    /// Session optimizations intentionally NOT exercised by the harness, with the reason.
    /// Anything new must be added to the harness engine list or listed here deliberately.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedFromHarness = new(StringComparer.Ordinal)
    {
        ["VbsHvciToggle"] = "reboot-scoped security setting, user-initiated, not part of a session",
        ["CoreIsolationManager"] = "reboot-scoped core reservation, user-initiated, not part of a session",
    };

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

        var result = await RevertVerificationRunner.RunAsync(Logger.None);

        _output.WriteLine(result.FormatReport());

        Assert.True(result.Error == null, "harness error: " + result.Error);
        Assert.True(result.AppliedChangeCount >= 5,
            $"only {result.AppliedChangeCount} probed changes during the session; the run was not meaningful");
        Assert.True(result.Failures.Count == 0,
            "persistent residue after revert:" + Environment.NewLine + result.FormatReport());
    }
}
