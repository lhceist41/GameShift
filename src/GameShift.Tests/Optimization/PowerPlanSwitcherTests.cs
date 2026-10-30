using System.Text.Json;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Tests.Optimization;

/// <summary>
/// The session overrides are written to the performance plan and restored onto it before the
/// original plan is reactivated, so the values restored must be the performance plan's own.
/// Runs against an in-memory powercfg; nothing reaches the real power scheme.
/// </summary>
public class PowerPlanSwitcherTests
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private const string BoostPolicy = "45bcc044-d885-43e2-8605-ee0ec6e96b59";

    private readonly FakePowerScheme _power = new(Balanced, Ultimate);

    private PowerPlanSwitcher NewSwitcher()
    {
        var switcher = new PowerPlanSwitcher { Powercfg = _power.Run, SetActiveScheme = _power.SetActive };
        switcher.CanApply(new SystemContext
        {
            Profile = new GameProfile { Id = "game", GameName = "Game" },
            Snapshot = new SystemStateSnapshot { OriginalPowerPlan = Balanced },
        });
        return switcher;
    }

    [Fact]
    public void Revert_RestoresThePerformancePlansOwnValues_AndLeavesTheOriginalPlanUntouched()
    {
        var switcher = NewSwitcher();

        Assert.Equal(OptimizationState.Applied, switcher.Apply().State);
        Assert.Equal(Ultimate, _power.Active);
        Assert.NotEmpty(_power.Writes);
        Assert.DoesNotContain(_power.Writes, w => w.Plan == Balanced);

        Assert.Equal(OptimizationState.Reverted, switcher.Revert().State);

        Assert.Equal(Balanced, _power.Active);
        Assert.DoesNotContain(_power.Writes, w => w.Plan == Balanced);
        Assert.Empty(_power.ChangedSinceStart());
    }

    [Fact]
    public void ValueThatCannotBeParsed_IsNeitherOverriddenNorJournaled()
    {
        _power.Unparseable.Add(BoostPolicy);
        var switcher = NewSwitcher();

        var applied = switcher.Apply();

        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.DoesNotContain(_power.Writes, w => w.Setting == BoostPolicy);
        Assert.Contains(_power.Writes, w => w.Setting != BoostPolicy);
        Assert.DoesNotContain(BoostPolicy, applied.OriginalValue);

        switcher.Revert();

        Assert.DoesNotContain(_power.Writes, w => w.Setting == BoostPolicy);
        Assert.Empty(_power.ChangedSinceStart());
    }

    [Fact]
    public void PlanSwitchedDuringSession_RevertWritesOnlyIntoThePlanTheValuesCameFrom()
    {
        var switcher = NewSwitcher();
        switcher.Apply();

        _power.SetActive(Balanced); // the user switches plans in Windows during the session
        switcher.Revert();

        Assert.Equal(Balanced, _power.Active);
        Assert.DoesNotContain(_power.Writes, w => w.Plan == Balanced);
        Assert.Empty(_power.ChangedSinceStart());
    }

    [Fact]
    public void JournalRevertAfterAPlanSwitch_WritesOnlyIntoThePerformancePlan()
    {
        var applied = NewSwitcher().Apply();
        _power.SetActive(Balanced);

        var recovery = new PowerPlanSwitcher { Powercfg = _power.Run, SetActiveScheme = _power.SetActive };
        Assert.Equal(OptimizationState.Reverted, recovery.RevertFromRecord(applied.OriginalValue).State);

        Assert.DoesNotContain(_power.Writes, w => w.Plan == Balanced);
        Assert.Empty(_power.ChangedSinceStart());
    }

    [Fact]
    public void JournalRecordWithoutTargetPlan_RestoresOntoTheActivePlan()
    {
        _power.SetActive(Ultimate);
        var record = JsonSerializer.Serialize(new
        {
            originalPlanGuid = Balanced.ToString(),
            subSettings = new[]
            {
                new { subGroup = "54533251-82be-4824-96c1-47b60b740d00", setting = BoostPolicy, acValue = 7, dcValue = 8 },
            },
        });

        new PowerPlanSwitcher { Powercfg = _power.Run, SetActiveScheme = _power.SetActive }.RevertFromRecord(record);

        Assert.Contains(_power.Writes, w => w.Plan == Ultimate && w.Setting == BoostPolicy && w.Slot == "AC" && w.Value == 7);
        Assert.DoesNotContain(_power.Writes, w => w.Plan == Balanced);
    }

    /// <summary>
    /// Two plans whose sub-settings start at different values per plan. Queries for a setting in
    /// <see cref="Unparseable"/> answer in a form the parser does not read (as a localized
    /// Windows would).
    /// </summary>
    private sealed class FakePowerScheme
    {
        private readonly Dictionary<Guid, int> _planBase;
        private readonly Dictionary<(Guid Plan, string Setting), (int Ac, int Dc)> _values = new();
        private readonly Dictionary<(Guid Plan, string Setting), (int Ac, int Dc)> _initial = new();

        public FakePowerScheme(Guid active, Guid other)
        {
            Active = active;
            _planBase = new Dictionary<Guid, int> { [active] = 50, [other] = 20 };
        }

        public Guid Active { get; private set; }

        public HashSet<string> Unparseable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(Guid Plan, string Setting, string Slot, int Value)> Writes { get; } = new();

        public IEnumerable<(Guid Plan, string Setting)> ChangedSinceStart() =>
            _values.Where(v => v.Value != _initial[v.Key]).Select(v => v.Key);

        public uint SetActive(Guid plan)
        {
            if (!_planBase.ContainsKey(plan))
                return 2;
            Active = plan;
            return 0;
        }

        public (bool success, string output) Run(string arguments)
        {
            var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "/query" when parts.Length == 2:
                    return (_planBase.ContainsKey(Guid.Parse(parts[1])), string.Empty);

                case "/query":
                    {
                        var (ac, dc) = Get(Plan(parts[1]), parts[3]);
                        return Unparseable.Contains(parts[3])
                            ? (true, $"    Aktueller Wechselstrom-Energieeinstellungsindex: 0x{ac:x8}\n    Aktueller Gleichstrom-Energieeinstellungsindex: 0x{dc:x8}")
                            : (true, $"    Current AC Power Setting Index: 0x{ac:x8}\n    Current DC Power Setting Index: 0x{dc:x8}");
                    }

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

        private (int Ac, int Dc) Get(Guid plan, string setting)
        {
            var key = (plan, setting);
            if (!_values.TryGetValue(key, out var value))
            {
                var seed = _planBase[plan] + _initial.Count;
                value = (seed, seed + 1);
                _values[key] = value;
                _initial[key] = value;
            }
            return value;
        }
    }
}
