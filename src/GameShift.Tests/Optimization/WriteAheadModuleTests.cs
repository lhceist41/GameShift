using System.ServiceProcess;
using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Optimization;

/// <summary>
/// ServiceSuppressor and PowerPlanSwitcher hand the journal their record before each change, so a
/// crash in the middle of an apply leaves a record that recovery replays. Services and power plans
/// are in-memory fakes and every journal lives in a temp folder.
/// </summary>
[Collection("ConfigState")]
public sealed class WriteAheadModuleTests : IDisposable
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    private readonly TempPath _temp = new();
    private readonly string? _previousSettingsPath = SettingsManager.SettingsFilePathOverride;
    private readonly List<OptimizationEngine> _engines = new();

    public WriteAheadModuleTests()
    {
        SettingsManager.SettingsFilePathOverride = _temp.GetFile("settings.json");
    }

    public void Dispose()
    {
        foreach (var engine in _engines)
            engine.Dispose();
        SettingsManager.SettingsFilePathOverride = _previousSettingsPath;
        _temp.Dispose();
    }

    private string JournalPath => _temp.GetFile("state.json");

    private OptimizationEngine NewEngine(params IOptimization[] optimizations)
    {
        var engine = new OptimizationEngine(optimizations, new JournalManager(JournalPath), Logger.None);
        _engines.Add(engine);
        return engine;
    }

    private SessionJournalData OnDisk() => new JournalManager(JournalPath).PeekJournal()!;

    private static GameProfile Profile() => new()
    {
        Id = "test",
        GameName = "TestGame",
        ExecutableName = "game.exe",
        ProcessId = 1234,
        SuppressServices = true,
        SwitchPowerPlan = true,
    };

    private static string[] ServicesIn(string record) =>
        JsonDocument.Parse(record).RootElement.GetProperty("services")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

    // ── ServiceSuppressor ────────────────────────────────────────────────

    private static ServiceSuppressor NewSuppressor(FakeServices services, params string[] targets)
    {
        var suppressor = new ServiceSuppressor
        {
            Services = services,
            Targets = _ => targets.Select(t => new ServiceSuppressor.ServiceInfo(t, t)).ToArray(),
        };
        suppressor.CanApply(new SystemContext { Profile = Profile(), Snapshot = new SystemStateSnapshot() });
        return suppressor;
    }

    [Fact]
    public void ServiceSuppressor_RecordsEachServiceAndItsRunningDependents_BeforeStoppingIt()
    {
        var services = new FakeServices();
        services.Add("A", "A1");
        services.Add("A1");
        services.Add("B");
        services.Add("Off");
        services.Status["Off"] = ServiceControllerStatus.Stopped;
        var recorder = new RecordingRecorder();
        var checkedStops = new List<string>();
        services.OnStop = name =>
        {
            var listed = ServicesIn(recorder.Records.Last());
            Assert.Contains(name, listed);
            Assert.All(services.RunningDependentsOf(name), d => Assert.Contains(d, listed));
            checkedStops.Add(name);
        };

        var applied = NewSuppressor(services, "A", "Off", "B").Apply(recorder);

        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.Equal(new[] { "A", "B" }, checkedStops);
        Assert.Equal(new[] { "A", "A1" }, ServicesIn(recorder.Records[0]));
        Assert.Equal(new[] { "A", "A1", "B" }, ServicesIn(recorder.Records[1]));
        Assert.Equal(2, recorder.Records.Count);
        Assert.Equal(recorder.Records.Last(), applied.OriginalValue);
    }

    [Fact]
    public void ServiceSuppressor_RecorderThatThrows_SkipsOnlyThatStop()
    {
        var services = new FakeServices();
        services.Add("A");
        services.Add("B", "B1");
        services.Add("B1");
        services.Add("C");
        var recorder = new RecordingRecorder { ThrowWhen = record => ServicesIn(record).Contains("B") };
        var suppressor = NewSuppressor(services, "A", "B", "C");

        var applied = suppressor.Apply(recorder);

        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.Equal(new[] { "A", "C" }, services.Stopped);
        Assert.Equal(ServiceControllerStatus.Running, services.Status["B"]);
        Assert.Equal(ServiceControllerStatus.Running, services.Status["B1"]);
        Assert.Equal(new[] { "A", "C" }, ServicesIn(applied.OriginalValue));
        Assert.Equal(recorder.Records.Last(), applied.OriginalValue);

        Assert.Equal(OptimizationState.Reverted, suppressor.Revert().State);
        Assert.Equal(new[] { "A", "C" }, services.Restarted);
    }

    // Each record replayed against the services as they were when it was written: what a crash
    // between that record and its stop leaves behind.
    [Fact]
    public void ServiceSuppressor_PartlyApplied_EachRecordRestartsExactlyWhatItLists()
    {
        var services = new FakeServices();
        services.Add("A", "A1");
        services.Add("A1");
        services.Add("B");
        services.Add("C");
        var recorder = new RecordingRecorder();
        var crashStates = new List<Dictionary<string, ServiceControllerStatus>>();
        services.OnStop = _ => crashStates.Add(new Dictionary<string, ServiceControllerStatus>(services.Status));

        NewSuppressor(services, "A", "B", "C").Apply(recorder);

        Assert.Equal(3, recorder.Records.Count);
        for (int i = 0; i < recorder.Records.Count; i++)
        {
            var crashed = new FakeServices(crashStates[i]);

            var result = new ServiceSuppressor { Services = crashed }.RevertFromRecord(recorder.Records[i]);

            Assert.Equal(OptimizationState.Reverted, result.State);
            Assert.Equal(ServicesIn(recorder.Records[i]), crashed.Restarted);
            Assert.All(crashed.Status.Values, s => Assert.Equal(ServiceControllerStatus.Running, s));
        }
    }

    [Fact]
    public void ServiceSuppressor_EveryApplyResult_CarriesTheModuleName()
    {
        var services = new FakeServices();
        services.Add("A");
        var withoutContext = new ServiceSuppressor { Services = services };

        Assert.Equal(withoutContext.Name, withoutContext.Apply(new RecordingRecorder()).Name);
        var suppressor = NewSuppressor(services, "A");
        Assert.Equal(suppressor.Name, suppressor.Apply(new RecordingRecorder()).Name);
    }

    [Fact]
    public async Task ServiceSuppressor_InTheEngine_IsJournaledBeforeEachStop_AndTheSessionEndRestartsIt()
    {
        var services = new FakeServices();
        services.Add("A", "A1");
        services.Add("A1");
        services.Add("B");
        var onDiskAtStop = new List<JournalEntry>();
        services.OnStop = _ => onDiskAtStop.Add(Assert.Single(OnDisk().Optimizations));
        var suppressor = new ServiceSuppressor
        {
            Services = services,
            Targets = _ => new[] { new ServiceSuppressor.ServiceInfo("A", "A"), new ServiceSuppressor.ServiceInfo("B", "B") },
        };
        var engine = NewEngine(suppressor);

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(2, onDiskAtStop.Count);
        Assert.All(onDiskAtStop, e => Assert.Equal((suppressor.Name, "Applied", true), (e.Name, e.State, e.InProgress)));
        Assert.Equal(new[] { "A", "A1" }, ServicesIn(onDiskAtStop[0].OriginalValue));
        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Applied", false), (entry.State, entry.InProgress));
        Assert.Equal(onDiskAtStop.Last().OriginalValue, entry.OriginalValue);
        Assert.Equal(new[] { "A", "A1", "B" }, ServicesIn(entry.OriginalValue));

        await engine.DeactivateProfileAsync();

        Assert.Equal(new[] { "A", "A1", "B" }, services.Restarted);
        Assert.Equal("Reverted", Assert.Single(OnDisk().Optimizations).State);
    }

    // ── PowerPlanSwitcher ────────────────────────────────────────────────

    private static PowerPlanSwitcher NewSwitcher(FakePowerPlans power, Func<Guid>? getActive = null) => new()
    {
        Powercfg = _ => throw new InvalidOperationException("powercfg is not used for records this version writes"),
        SetActiveScheme = power.SetActive,
        GetActiveScheme = getActive ?? (() => power.Active),
        SchemeExists = power.Exists,
        IsDualCcdX3D = () => false,
    };

    private static SystemContext Context(Guid activePlan) => new()
    {
        Profile = Profile(),
        Snapshot = new SystemStateSnapshot { OriginalPowerPlan = activePlan },
    };

    private static string PlanRecord(Guid original, Guid? target = null) => target == null
        ? JsonSerializer.Serialize(new { originalPlanGuid = original.ToString() })
        : JsonSerializer.Serialize(new { originalPlanGuid = original.ToString(), targetPlanGuid = target.Value.ToString() });

    [Fact]
    public void PowerPlan_RecordsTheSwitchBeforeMakingIt()
    {
        var power = new FakePowerPlans(Balanced, HighPerformance);
        var recorder = new RecordingRecorder();
        power.OnSetActive = plan =>
        {
            var record = JsonDocument.Parse(Assert.Single(recorder.Records)).RootElement;
            Assert.Equal(Balanced, Guid.Parse(record.GetProperty("originalPlanGuid").GetString()!));
            Assert.Equal(plan, Guid.Parse(record.GetProperty("targetPlanGuid").GetString()!));
        };
        var switcher = NewSwitcher(power);
        Assert.True(switcher.CanApply(Context(Balanced)));

        var applied = switcher.Apply(recorder);

        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.Equal(new[] { HighPerformance }, power.Activations);
        Assert.Equal(recorder.Records.Single(), applied.OriginalValue);
        Assert.Equal(PlanRecord(Balanced, HighPerformance), applied.OriginalValue);
    }

    [Fact]
    public void PowerPlan_NoSwitch_RecordsNothing()
    {
        var power = new FakePowerPlans(PowerSaver, HighPerformance, Balanced);
        var recorder = new RecordingRecorder();
        var switcher = NewSwitcher(power);

        Assert.False(switcher.CanApply(Context(PowerSaver)));
        Assert.Equal(OptimizationState.Failed, switcher.Apply(recorder).State);

        Assert.Empty(recorder.Records);
        Assert.Empty(power.Attempts);
    }

    [Fact]
    public void PowerPlan_EveryApplyResult_CarriesTheModuleName()
    {
        var notAllowed = NewSwitcher(new FakePowerPlans(PowerSaver, HighPerformance));
        notAllowed.CanApply(Context(PowerSaver));
        var refused = new FakePowerPlans(Balanced, HighPerformance);
        refused.Refuse.Add(HighPerformance);
        var refusing = NewSwitcher(refused);
        refusing.CanApply(Context(Balanced));
        var throwing = new PowerPlanSwitcher
        {
            SetActiveScheme = _ => throw new InvalidOperationException("power service unavailable"),
            SchemeExists = _ => true,
            IsDualCcdX3D = () => false,
        };
        throwing.CanApply(Context(Balanced));
        var applying = NewSwitcher(new FakePowerPlans(Balanced, HighPerformance));
        applying.CanApply(Context(Balanced));

        var results = new[] { notAllowed, refusing, throwing, applying }
            .Select(s => (s.Name, s.Apply(new RecordingRecorder()))).ToList();

        Assert.Equal(
            new[] { OptimizationState.Failed, OptimizationState.Failed, OptimizationState.Failed, OptimizationState.Applied },
            results.Select(r => r.Item2.State));
        Assert.All(results, r => Assert.Equal(r.Name, r.Item2.Name));
    }

    [Fact]
    public async Task PowerPlan_InTheEngine_IsJournaledBeforeTheSwitch_AndTheSessionEndSwitchesBack()
    {
        var power = new FakePowerPlans(Balanced, HighPerformance);
        JournalEntry? onDiskAtSwitch = null;
        power.OnSetActive = plan =>
        {
            if (plan == HighPerformance)
                onDiskAtSwitch = Assert.Single(OnDisk().Optimizations);
        };
        var switcher = NewSwitcher(power);
        var engine = NewEngine(new BalancedSnapshot(), switcher);

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal((switcher.Name, "Applied", true), (onDiskAtSwitch!.Name, onDiskAtSwitch.State, onDiskAtSwitch.InProgress));
        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Applied", false), (entry.State, entry.InProgress));
        Assert.Equal(onDiskAtSwitch.OriginalValue, entry.OriginalValue);
        Assert.Equal(PlanRecord(Balanced, HighPerformance), entry.OriginalValue);
        Assert.Equal(HighPerformance, power.Active);

        await engine.DeactivateProfileAsync();

        Assert.Equal(Balanced, power.Active);
        Assert.Equal("Reverted", Assert.Single(OnDisk().Optimizations).State);
    }

    // The switch failed, so nothing changed: the undo must not touch the plan or report leftovers.
    [Fact]
    public async Task PowerPlan_SwitchFails_UndoLeavesThePlanAlone_AndTheModuleIsPlainFailed()
    {
        var power = new FakePowerPlans(Balanced, HighPerformance);
        power.Refuse.Add(HighPerformance);
        var switcher = NewSwitcher(power);
        var engine = NewEngine(new BalancedSnapshot(), switcher);

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(new[] { HighPerformance }, power.Attempts);
        Assert.Equal(Balanced, power.Active);
        var outcome = engine.LastOutcome!.Modules.Single(m => m.ModuleName == switcher.Name);
        Assert.Equal(
            (ModuleOutcomeKind.Failed, "did not apply: PowerSetActiveScheme failed with error 2"),
            (outcome.Kind, outcome.Reason));
        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Reverted", false), (entry.State, entry.InProgress));
        Assert.Equal(0, engine.AppliedCount);
    }

    [Fact]
    public void PowerPlan_RecordOfASwitchThatNeverHappened_LeavesAnotherPlanActive()
    {
        var power = new FakePowerPlans(PowerSaver, Balanced, HighPerformance);

        var result = NewSwitcher(power).RevertFromRecord(PlanRecord(Balanced, HighPerformance));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Empty(power.Attempts);
        Assert.Equal(PowerSaver, power.Active);
    }

    [Fact]
    public void PowerPlan_RecordWithTheTargetActive_RestoresTheOriginal()
    {
        var power = new FakePowerPlans(HighPerformance, Balanced);

        var result = NewSwitcher(power).RevertFromRecord(PlanRecord(Balanced, HighPerformance));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(new[] { Balanced }, power.Activations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PowerPlan_ActivePlanUnreadable_RestoresTheOriginalAsBefore(bool throws)
    {
        var power = new FakePowerPlans(PowerSaver, Balanced, HighPerformance);
        var switcher = NewSwitcher(power, () => throws ? throw new InvalidOperationException("no power service") : Guid.Empty);

        var result = switcher.RevertFromRecord(PlanRecord(Balanced, HighPerformance));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(new[] { Balanced }, power.Activations);
    }

    [Fact]
    public void PowerPlan_RecordWithoutATarget_RestoresTheOriginalWithoutReadingTheActivePlan()
    {
        var power = new FakePowerPlans(PowerSaver, Balanced, HighPerformance);
        int reads = 0;
        var switcher = NewSwitcher(power, () =>
        {
            reads++;
            return power.Active;
        });

        var result = switcher.RevertFromRecord(PlanRecord(Balanced));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(new[] { Balanced }, power.Activations);
        Assert.Equal(0, reads);
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class RecordingRecorder : IApplyRecorder
    {
        public List<string> Records { get; } = new();

        public Func<string, bool>? ThrowWhen { get; init; }

        public void BeforeChange(string originalValueJson)
        {
            if (ThrowWhen?.Invoke(originalValueJson) == true)
                throw new IOException("journal unavailable");
            Records.Add(originalValueJson);
        }
    }

    /// <summary>
    /// The engine captures the real active plan into its session snapshot. Listed before the
    /// switcher, this pins that snapshot to Balanced and is itself never applied.
    /// </summary>
    private sealed class BalancedSnapshot : IOptimization, IJournaledOptimization
    {
        public string Name => "Balanced snapshot";
        public string Description => "Test fixture";
        public bool IsApplied => false;
        public bool IsAvailable => true;

        public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile) => throw new InvalidOperationException("journaled path only");
        public Task<bool> RevertAsync(SystemStateSnapshot snapshot) => throw new InvalidOperationException("journaled path only");

        public bool CanApply(SystemContext context)
        {
            context.Snapshot.OriginalPowerPlan = Balanced;
            return false;
        }

        public OptimizationResult Apply() => throw new InvalidOperationException("never applied");
        public OptimizationResult Revert() => throw new InvalidOperationException("never applied");
        public bool Verify() => false;
        public OptimizationResult RevertFromRecord(string originalValueJson) => throw new InvalidOperationException("never applied");
    }

    private sealed class FakeServices : ServiceSuppressor.IServiceControl
    {
        private readonly Dictionary<string, List<string>> _dependents = new(StringComparer.OrdinalIgnoreCase);

        public FakeServices()
        {
        }

        public FakeServices(Dictionary<string, ServiceControllerStatus> status)
        {
            foreach (var (name, state) in status)
                Status[name] = state;
        }

        public Dictionary<string, ServiceControllerStatus> Status { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Stopped { get; } = new();

        public List<string> Restarted { get; } = new();

        public Action<string>? OnStop { get; set; }

        public void Add(string name, params string[] dependents)
        {
            Status[name] = ServiceControllerStatus.Running;
            _dependents[name] = dependents.ToList();
        }

        public List<string> RunningDependentsOf(string serviceName) =>
            _dependents.TryGetValue(serviceName, out var dependents)
                ? dependents.Where(d => Status[d] == ServiceControllerStatus.Running).ToList()
                : new List<string>();

        public ServiceControllerStatus GetStatus(string serviceName) =>
            Status.TryGetValue(serviceName, out var status)
                ? status
                : throw new InvalidOperationException($"Service {serviceName} was not found.");

        public List<string> GetRunningDependents(string serviceName) => RunningDependentsOf(serviceName);

        public void StopAndWait(string serviceName, TimeSpan timeout)
        {
            OnStop?.Invoke(serviceName);
            foreach (var dependent in RunningDependentsOf(serviceName))
                Status[dependent] = ServiceControllerStatus.Stopped;
            Status[serviceName] = ServiceControllerStatus.Stopped;
            Stopped.Add(serviceName);
        }

        public bool TryRestart(string serviceName)
        {
            Restarted.Add(serviceName);
            Status[serviceName] = ServiceControllerStatus.Running;
            return true;
        }
    }

    private sealed class FakePowerPlans
    {
        private readonly HashSet<Guid> _plans;

        public FakePowerPlans(Guid active, params Guid[] others)
        {
            Active = active;
            _plans = new HashSet<Guid>(others) { active };
        }

        public Guid Active { get; private set; }

        public HashSet<Guid> Refuse { get; } = new();

        public List<Guid> Attempts { get; } = new();

        public List<Guid> Activations { get; } = new();

        public Action<Guid>? OnSetActive { get; set; }

        public bool Exists(Guid plan) => _plans.Contains(plan);

        public uint SetActive(Guid plan)
        {
            Attempts.Add(plan);
            OnSetActive?.Invoke(plan);
            if (!_plans.Contains(plan) || Refuse.Contains(plan))
                return 2;
            Active = plan;
            Activations.Add(plan);
            return 0;
        }
    }
}
