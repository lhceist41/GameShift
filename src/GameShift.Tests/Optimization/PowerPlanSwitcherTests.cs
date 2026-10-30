using System.Text.Json;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Tests.Optimization;

/// <summary>
/// The switcher moves only from Balanced to High Performance, and never on a dual-CCD X3D. It does
/// that through the power scheme API alone; powercfg is left for journal records of earlier
/// versions, whose sub-setting overrides must still be restored onto the plan they came from.
/// Runs against an in-memory power scheme; nothing reaches the real one.
/// </summary>
public class PowerPlanSwitcherTests
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string BoostPolicy = "45bcc044-d885-43e2-8605-ee0ec6e96b59";

    private FakePowerScheme _power = new(Balanced, HighPerformance);
    private readonly List<string> _powercfgCalls = new();
    private bool _dualCcdX3D;

    private PowerPlanSwitcher NewSwitcher() => new()
    {
        Powercfg = args =>
        {
            _powercfgCalls.Add(args);
            return _power.Run(args);
        },
        SetActiveScheme = plan => _power.SetActive(plan),
        GetActiveScheme = () => _power.Active,
        SchemeExists = plan => _power.Exists(plan),
        IsDualCcdX3D = () => _dualCcdX3D,
    };

    private static SystemContext Context(Guid activePlan) => new()
    {
        Profile = new GameProfile { Id = "game", GameName = "Game" },
        Snapshot = new SystemStateSnapshot { OriginalPowerPlan = activePlan },
    };

    // ── The switch decision ───────────────────────────────────────────

    [Fact]
    public void ShouldSwitch_FromBalancedOnACpuThatIsNotADualCcdX3D()
    {
        Assert.True(PowerPlanSwitcher.ShouldSwitch(Balanced, () => false));
    }

    [Fact]
    public void ShouldSwitch_NotOnADualCcdX3D()
    {
        Assert.False(PowerPlanSwitcher.ShouldSwitch(Balanced, () => true));
    }

    public static TheoryData<Guid> PlansOtherThanBalanced => new() { HighPerformance, Ultimate, PowerSaver, Guid.Empty };

    [Theory]
    [MemberData(nameof(PlansOtherThanBalanced))]
    public void ShouldSwitch_NeverFromAnotherPlan_AndDoesNotReadTheCpu(Guid activePlan)
    {
        Assert.False(PowerPlanSwitcher.ShouldSwitch(
            activePlan, () => throw new InvalidOperationException("topology read for a plan that is not switched")));
    }

    // ── Apply and revert ──────────────────────────────────────────────

    [Fact]
    public void FromBalanced_SwitchesToHighPerformanceAndBack_WithoutPowercfg()
    {
        var switcher = NewSwitcher();

        Assert.True(switcher.CanApply(Context(Balanced)));
        var applied = switcher.Apply();

        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.Equal(HighPerformance, _power.Active);
        var record = JsonDocument.Parse(applied.OriginalValue).RootElement;
        Assert.Equal(Balanced, Guid.Parse(record.GetProperty("originalPlanGuid").GetString()!));
        Assert.False(record.TryGetProperty("subSettings", out _));

        Assert.Equal(OptimizationState.Reverted, switcher.Revert().State);

        Assert.Equal(Balanced, _power.Active);
        Assert.Empty(_powercfgCalls);
    }

    public static TheoryData<Guid> OtherExistingPlans => new() { HighPerformance, Ultimate, PowerSaver };

    [Theory]
    [MemberData(nameof(OtherExistingPlans))]
    public void AnotherActivePlan_IsLeftAlone(Guid activePlan)
    {
        _power = new FakePowerScheme(activePlan, HighPerformance, Balanced);
        var switcher = NewSwitcher();

        Assert.False(switcher.CanApply(Context(activePlan)));
        Assert.Equal(OptimizationState.Failed, switcher.Apply().State);
        Assert.Empty(_power.Activations);
    }

    [Fact]
    public void ActivePlanThatCannotBeRead_IsLeftAlone()
    {
        var switcher = new PowerPlanSwitcher
        {
            SetActiveScheme = plan => _power.SetActive(plan),
            GetActiveScheme = () => Guid.Empty,
            SchemeExists = plan => _power.Exists(plan),
            IsDualCcdX3D = () => false,
        };

        Assert.False(switcher.CanApply(Context(Guid.Empty)));
        Assert.Empty(_power.Activations);
    }

    [Fact]
    public void DualCcdX3D_StaysOnBalanced()
    {
        _dualCcdX3D = true;
        var switcher = NewSwitcher();

        Assert.False(switcher.CanApply(Context(Balanced)));
        Assert.Empty(_power.Activations);
    }

    [Fact]
    public void WithoutAHighPerformancePlan_StaysOnBalanced()
    {
        _power = new FakePowerScheme(Balanced);
        var switcher = NewSwitcher();

        Assert.False(switcher.CanApply(Context(Balanced)));
        Assert.Empty(_power.Activations);
    }

    [Fact]
    public void ActivationFails_ReportsFailedAndIsNotTracked()
    {
        var switcher = NewSwitcher();
        Assert.True(switcher.CanApply(Context(Balanced)));
        _power.Refuse.Add(HighPerformance);

        var applied = switcher.Apply();

        Assert.Equal(OptimizationState.Failed, applied.State);
        Assert.False(switcher.IsApplied);
        Assert.Equal(Balanced, _power.Active);
    }

    [Fact]
    public void SnapshotWithoutAPlan_FallsBackToTheActivePlan()
    {
        var switcher = NewSwitcher();

        Assert.True(switcher.CanApply(Context(Guid.Empty)));
        Assert.Equal(OptimizationState.Applied, switcher.Apply().State);
    }

    // ── Journal records ───────────────────────────────────────────────

    [Fact]
    public void RecordThisVersionWrites_RestoresBalancedWithoutPowercfg()
    {
        var switcher = NewSwitcher();
        switcher.CanApply(Context(Balanced));
        var applied = switcher.Apply();

        var recovery = NewSwitcher();
        Assert.Equal(OptimizationState.Reverted, recovery.RevertFromRecord(applied.OriginalValue).State);

        Assert.Equal(Balanced, _power.Active);
        Assert.Empty(_powercfgCalls);
    }

    [Fact]
    public void EarlierVersionRecordWithOverrides_RestoresThemOntoThePerformancePlanAndReactivatesTheOriginal()
    {
        _power = new FakePowerScheme(Ultimate, Balanced);
        var record = JsonSerializer.Serialize(new
        {
            originalPlanGuid = Balanced.ToString(),
            targetPlanGuid = Ultimate.ToString(),
            subSettings = new[]
            {
                new { subGroup = SubProcessor, setting = BoostPolicy, acValue = 2, dcValue = 3 },
            },
        });

        var result = NewSwitcher().RevertFromRecord(record);

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Contains(_power.Writes, w => w.Plan == Ultimate && w.Setting == BoostPolicy && w.Slot == "AC" && w.Value == 2);
        Assert.Contains(_power.Writes, w => w.Plan == Ultimate && w.Setting == BoostPolicy && w.Slot == "DC" && w.Value == 3);
        Assert.DoesNotContain(_power.Writes, w => w.Plan == Balanced);
        Assert.Equal(Balanced, _power.Active);
    }

    [Fact]
    public void JournalRecordWithoutTargetPlan_RestoresOntoTheActivePlan()
    {
        _power = new FakePowerScheme(Ultimate, Balanced);
        var record = JsonSerializer.Serialize(new
        {
            originalPlanGuid = Balanced.ToString(),
            subSettings = new[]
            {
                new { subGroup = SubProcessor, setting = BoostPolicy, acValue = 7, dcValue = 8 },
            },
        });

        NewSwitcher().RevertFromRecord(record);

        Assert.Contains(_power.Writes, w => w.Plan == Ultimate && w.Setting == BoostPolicy && w.Slot == "AC" && w.Value == 7);
        Assert.DoesNotContain(_power.Writes, w => w.Plan == Balanced);
    }

    /// <summary>
    /// Plans that exist, each starting with different sub-setting values. <see cref="Refuse"/> makes
    /// activation of a plan fail as the API would.
    /// </summary>
    private sealed class FakePowerScheme
    {
        private readonly HashSet<Guid> _plans;
        private readonly Dictionary<(Guid Plan, string Setting), (int Ac, int Dc)> _values = new();

        public FakePowerScheme(Guid active, params Guid[] others)
        {
            Active = active;
            _plans = new HashSet<Guid>(others) { active };
        }

        public Guid Active { get; private set; }

        public HashSet<Guid> Refuse { get; } = new();

        public List<Guid> Activations { get; } = new();

        public List<(Guid Plan, string Setting, string Slot, int Value)> Writes { get; } = new();

        public bool Exists(Guid plan) => _plans.Contains(plan);

        public uint SetActive(Guid plan)
        {
            if (!_plans.Contains(plan) || Refuse.Contains(plan))
                return 2;
            Active = plan;
            Activations.Add(plan);
            return 0;
        }

        public (bool success, string output) Run(string arguments)
        {
            var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "/setacvalueindex":
                    {
                        var plan = Plan(parts[1]);
                        var value = int.Parse(parts[4]);
                        _values[(plan, parts[3])] = (value, Get(plan, parts[3]).Dc);
                        Writes.Add((plan, parts[3], "AC", value));
                        return (true, string.Empty);
                    }

                case "/setdcvalueindex":
                    {
                        var plan = Plan(parts[1]);
                        var value = int.Parse(parts[4]);
                        _values[(plan, parts[3])] = (Get(plan, parts[3]).Ac, value);
                        Writes.Add((plan, parts[3], "DC", value));
                        return (true, string.Empty);
                    }

                case "/setactive":
                    return (true, string.Empty);

                default:
                    return (false, string.Empty);
            }
        }

        private Guid Plan(string scheme) => scheme == "SCHEME_CURRENT" ? Active : Guid.Parse(scheme);

        private (int Ac, int Dc) Get(Guid plan, string setting) =>
            _values.TryGetValue((plan, setting), out var value) ? value : (50, 51);
    }
}
