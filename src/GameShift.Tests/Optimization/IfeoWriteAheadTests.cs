using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Microsoft.Win32;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Optimization;

/// <summary>
/// ProcessPriorityBooster and HybridCpuDetector hand the journal their IFEO record before they
/// create a key or write a value, so a crash in between leaves a record that recovery replays. Every
/// key lives in a throwaway HKCU tree that stands in for HKLM; every journal lives in a temp folder.
/// </summary>
[Collection("ConfigState")]
public sealed class IfeoWriteAheadTests : IDisposable
{
    private const RecoveryHive Hklm = RecoveryHive.LocalMachine;
    private const string IfeoBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string Exe = "game.exe";
    private const string ExeKey = IfeoBase + @"\" + Exe;
    private const string PerfOptions = ExeKey + @"\PerfOptions";
    private const string Priority = "CpuPriorityClass";
    private const string Affinity = "CpuAffinityMask";

    // P-cores on logical processors 0 and 1, so pinning them is the mask 0b11.
    private const int PinnedMask = 3;

    private readonly TestRecoveryRegistry _registry = new();
    private readonly TempPath _temp = new();
    private readonly string? _previousSettingsPath = SettingsManager.SettingsFilePathOverride;
    private readonly List<OptimizationEngine> _engines = new();

    public IfeoWriteAheadTests()
    {
        SettingsManager.SettingsFilePathOverride = _temp.GetFile("settings.json");
    }

    public void Dispose()
    {
        foreach (var engine in _engines)
            engine.Dispose();
        SettingsManager.SettingsFilePathOverride = _previousSettingsPath;
        _temp.Dispose();
        _registry.Dispose();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private string JournalPath => _temp.GetFile("state.json");

    private SessionJournalData OnDisk() => new JournalManager(JournalPath).PeekJournal()!;

    private OptimizationEngine NewEngine(params IOptimization[] optimizations)
    {
        var engine = new OptimizationEngine(optimizations, new JournalManager(JournalPath), Logger.None);
        _engines.Add(engine);
        return engine;
    }

    private static GameProfile Profile(string exe = Exe, int processId = 1234) => new()
    {
        Id = "test",
        GameName = "TestGame",
        ExecutableName = exe,
        ProcessId = processId,
        AntiCheat = AntiCheatType.EasyAntiCheat,
        BoostProcessPriority = true,
        PinGameCores = true,
    };

    private static CpuTopology HybridTopology() => new()
    {
        PerformanceCores =
        {
            new CpuCore { CpuSetId = 256, LogicalProcessorIndex = 0 },
            new CpuCore { CpuSetId = 257, LogicalProcessorIndex = 1 },
        },
        EfficiencyCores = { new CpuCore { CpuSetId = 258, LogicalProcessorIndex = 2, EfficiencyClass = 1 } },
    };

    private ProcessPriorityBooster NewBooster(IfeoRegistry? ifeo = null, GameProfile? profile = null)
    {
        var booster = new ProcessPriorityBooster
        {
            Ifeo = ifeo ?? _registry.Ifeo(),
            RecoveryRegistry = _registry.Registry,
            Processes = new FakeProcessPriorities(),
        };
        booster.CanApply(new SystemContext { Profile = profile ?? Profile(), Snapshot = new SystemStateSnapshot() });
        return booster;
    }

    // No live process: an anti-cheat profile goes straight to the IFEO fallback.
    private HybridCpuDetector NewDetector(IfeoRegistry? ifeo = null, GameProfile? profile = null)
    {
        var detector = new HybridCpuDetector
        {
            Ifeo = ifeo ?? _registry.Ifeo(),
            RecoveryRegistry = _registry.Registry,
            Topology = HybridTopology(),
        };
        detector.CanApply(new SystemContext { Profile = profile ?? Profile(processId: 0), Snapshot = new SystemStateSnapshot() });
        return detector;
    }

    private void Seed(string path, params (string Name, int Value)[] values)
    {
        using var key = _registry.CreateKey(Hklm, path);
        foreach (var (name, value) in values)
            key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private object? Value(string name, string path = PerfOptions) => _registry.Value(Hklm, path, name);

    private bool Exists(string path)
    {
        using var key = _registry.Open(Hklm, path);
        return key != null;
    }

    private static (string KeyPath, string? Originals) SingleIfeoEntry(string record)
    {
        var entry = Assert.Single(JsonDocument.Parse(record).RootElement.GetProperty("ifeoKeys").EnumerateArray());
        var originals = entry.GetProperty("originalValuesJson");
        return (entry.GetProperty("keyPath").GetString()!,
                originals.ValueKind == JsonValueKind.Null ? null : originals.GetString());
    }

    /// <summary>An IFEO registry whose every write first checks that the recorder holds a record naming the key.</summary>
    private IfeoRegistry CheckedIfeo(RecordingRecorder recorder, List<string> writes, string perfOptions = PerfOptions) =>
        _registry.Ifeo(beforeWrite: write =>
        {
            Assert.NotEmpty(recorder.Records);
            Assert.Equal(perfOptions, SingleIfeoEntry(recorder.Records.Last()).KeyPath);
            writes.Add(write);
        });

    private static IfeoRegistry FailAfter(TestRecoveryRegistry registry, string valueName) =>
        registry.Ifeo(afterWrite: write =>
        {
            if (write == $"set value {valueName}")
                throw new IOException("write reported failure");
        });

    /// <summary>Seeds PerfOptions as the test starts it and returns the originals the module must record.</summary>
    private string? SeedStart(string state, string valueName, int original)
    {
        switch (state)
        {
            case "absent":
                return null;
            case "empty":
                Seed(PerfOptions);
                return "{}";
            default:
                Seed(PerfOptions, (valueName, original));
                return JsonSerializer.Serialize(new Dictionary<string, int> { [valueName] = original });
        }
    }

    // ── ProcessPriorityBooster ───────────────────────────────────────────

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("with value")]
    public void Booster_RecordsTheKeyBeforeAnyWrite_AndTheResultIsTheLastRecord(string start)
    {
        var expectedOriginals = SeedStart(start, Priority, 2);
        var recorder = new RecordingRecorder();
        var writes = new List<string>();

        var applied = NewBooster(CheckedIfeo(recorder, writes)).Apply(recorder);

        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.Equal(new[] { $"create key {ExeKey}", $"create key {PerfOptions}", $"set value {Priority}" }, writes);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(record, applied.OriginalValue);
        Assert.Equal((PerfOptions, expectedOriginals), SingleIfeoEntry(record));
        Assert.Equal(3, Value(Priority));
    }

    [Theory]
    [InlineData(1234)]
    [InlineData(0)]
    public void Booster_RecorderThatThrows_WritesNothingAndFails(int processId)
    {
        var writes = new List<string>();
        var booster = NewBooster(_registry.Ifeo(beforeWrite: writes.Add), Profile(processId: processId));

        var applied = booster.Apply(new RecordingRecorder { Throws = true });

        Assert.Equal(OptimizationState.Failed, applied.State);
        Assert.Equal(ProcessPriorityBooster.OptimizationId, applied.Name);
        Assert.Contains("journal unavailable", applied.ErrorMessage);
        Assert.Empty(writes);
        Assert.False(Exists(ExeKey));
        Assert.False(booster.IsApplied);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("with value")]
    public async Task Booster_WriteThatFailsAfterTheIntent_IsUndoneByTheEngine(string start)
    {
        SeedStart(start, Priority, 2);
        var booster = NewBooster(FailAfter(_registry, Priority));
        var engine = NewEngine(booster);

        await engine.ActivateProfileAsync(Profile());

        Assert.Equal(start == "absent" ? null : 2, Value(Priority));
        Assert.Equal(start != "absent", Exists(PerfOptions));
        var outcome = Assert.Single(engine.LastOutcome!.Modules);
        Assert.Equal(ModuleOutcomeKind.Failed, outcome.Kind);
        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Reverted", false), (entry.State, entry.InProgress));
        Assert.Equal(0, engine.AppliedCount);
    }

    [Fact]
    public void Booster_EveryApplyResultCarriesItsName()
    {
        var results = new List<OptimizationResult>
        {
            new ProcessPriorityBooster { Ifeo = _registry.Ifeo() }.Apply(new RecordingRecorder()),
            NewBooster().Apply(new RecordingRecorder { Throws = true }),
            NewBooster(FailAfter(_registry, Priority)).Apply(new RecordingRecorder()),
            NewBooster(FailAfter(_registry, Priority), Profile(processId: 0)).Apply(new RecordingRecorder()),
            NewBooster().Apply(new RecordingRecorder()),
            NewBooster(profile: Profile(exe: "")).Apply(new RecordingRecorder()),
        };

        Assert.Contains(results, r => r.State == OptimizationState.Applied);
        Assert.Contains(results, r => r.State == OptimizationState.Failed);
        Assert.All(results, r => Assert.Equal(ProcessPriorityBooster.OptimizationId, r.Name));
    }

    // ── HybridCpuDetector ────────────────────────────────────────────────

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("with value")]
    public void Detector_RecordsTheKeyBeforeAnyWrite_AndTheResultIsTheLastRecord(string start)
    {
        var expectedOriginals = SeedStart(start, Affinity, 15);
        var recorder = new RecordingRecorder();
        var writes = new List<string>();

        var applied = NewDetector(CheckedIfeo(recorder, writes)).Apply(recorder);

        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.Equal(new[] { $"create key {ExeKey}", $"create key {PerfOptions}", $"set value {Affinity}" }, writes);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(record, applied.OriginalValue);
        Assert.Equal((PerfOptions, expectedOriginals), SingleIfeoEntry(record));
        Assert.Equal(PinnedMask, Value(Affinity));
    }

    [Fact]
    public void Detector_RecorderThatThrows_WritesNothingAndFails()
    {
        var writes = new List<string>();
        var detector = NewDetector(_registry.Ifeo(beforeWrite: writes.Add));

        var applied = detector.Apply(new RecordingRecorder { Throws = true });

        Assert.Equal(OptimizationState.Failed, applied.State);
        Assert.Equal(HybridCpuDetector.OptimizationId, applied.Name);
        Assert.Contains("journal unavailable", applied.ErrorMessage);
        Assert.Empty(writes);
        Assert.False(Exists(ExeKey));
        Assert.False(detector.IsApplied);
    }

    [Fact]
    public async Task Detector_WriteThatFailsAfterTheIntent_IsUndoneByTheEngine()
    {
        var detector = NewDetector(FailAfter(_registry, Affinity));
        var engine = NewEngine(detector);

        await engine.ActivateProfileAsync(Profile(processId: 0));

        Assert.False(Exists(PerfOptions));
        var outcome = Assert.Single(engine.LastOutcome!.Modules);
        Assert.Equal(ModuleOutcomeKind.Failed, outcome.Kind);
        var entry = Assert.Single(OnDisk().Optimizations);
        Assert.Equal(("Reverted", false), (entry.State, entry.InProgress));
        Assert.Null(SingleIfeoEntry(entry.OriginalValue).Originals);
        Assert.False(detector.IsApplied);
    }

    // A mask recorded for one game must not become the "original" of the next one.
    [Fact]
    public void Detector_SecondApply_DoesNotCarryTheFirstGamesMask()
    {
        const string ExeB = "other.exe";
        const string PerfOptionsB = IfeoBase + @"\" + ExeB + @"\PerfOptions";
        Seed(PerfOptions, (Affinity, 15));
        var detector = NewDetector();

        var first = detector.Apply(new RecordingRecorder());
        Assert.Equal("""{"CpuAffinityMask":15}""", SingleIfeoEntry(first.OriginalValue).Originals);
        Assert.Equal(OptimizationState.Reverted, detector.Revert().State);
        Assert.Equal(15, Value(Affinity));

        // ProcessPriorityBooster runs first and has already created the second game's PerfOptions.
        Seed(PerfOptionsB, (Priority, 3));
        detector.CanApply(new SystemContext { Profile = Profile(exe: ExeB, processId: 0), Snapshot = new SystemStateSnapshot() });
        var recorder = new RecordingRecorder();
        var second = detector.Apply(recorder);

        Assert.Equal(OptimizationState.Applied, second.State);
        Assert.Equal((PerfOptionsB, "{}"), SingleIfeoEntry(Assert.Single(recorder.Records)));
        Assert.Equal(recorder.Records.Last(), second.OriginalValue);
        Assert.Equal(OptimizationState.Reverted, detector.Revert().State);
        Assert.Null(Value(Affinity, PerfOptionsB));
        Assert.Equal(3, Value(Priority, PerfOptionsB));

        // The replay of the same record leaves no mask either.
        Assert.Equal(OptimizationState.Applied, detector.Apply(new RecordingRecorder()).State);
        Assert.Equal(OptimizationState.Reverted, new HybridCpuDetector { RecoveryRegistry = _registry.Registry }
            .RevertFromRecord(second.OriginalValue).State);
        Assert.Null(Value(Affinity, PerfOptionsB));
        Assert.Equal(3, Value(Priority, PerfOptionsB));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Detector_PreExistingEmptyPerfOptions_SurvivesReplayAndLiveRevert(bool replay)
    {
        Seed(PerfOptions);
        var detector = NewDetector();
        var applied = detector.Apply(new RecordingRecorder());
        Assert.Equal(PinnedMask, Value(Affinity));

        var reverted = replay
            ? new HybridCpuDetector { RecoveryRegistry = _registry.Registry }.RevertFromRecord(applied.OriginalValue)
            : detector.Revert();

        Assert.Equal(OptimizationState.Reverted, reverted.State);
        Assert.True(Exists(PerfOptions));
        Assert.Null(Value(Affinity));
    }

    [Fact]
    public void Detector_EveryApplyResultCarriesItsName()
    {
        var noPinning = Profile(processId: 0);
        noPinning.PinGameCores = false;
        var results = new List<OptimizationResult>
        {
            new HybridCpuDetector { Ifeo = _registry.Ifeo(), Topology = HybridTopology() }.Apply(new RecordingRecorder()),
            NewDetectorWithoutTopology().Apply(new RecordingRecorder()),
            NewDetector().Apply(new RecordingRecorder { Throws = true }),
            NewDetector(FailAfter(_registry, Affinity)).Apply(new RecordingRecorder()),
            NewDetector(profile: noPinning).Apply(new RecordingRecorder()),
            NewDetector().Apply(new RecordingRecorder()),
            NewDetector(profile: Profile(exe: "", processId: 0)).Apply(new RecordingRecorder()),
        };

        Assert.Contains(results, r => r.State == OptimizationState.Applied);
        Assert.Contains(results, r => r.State == OptimizationState.Failed);
        Assert.All(results, r => Assert.Equal(HybridCpuDetector.OptimizationId, r.Name));
    }

    private HybridCpuDetector NewDetectorWithoutTopology()
    {
        var detector = new HybridCpuDetector { Ifeo = _registry.Ifeo(), Topology = null };
        detector.CanApply(new SystemContext { Profile = Profile(processId: 0), Snapshot = new SystemStateSnapshot() });
        return detector;
    }

    // ── Both modules on one image ────────────────────────────────────────

    // A crash after both applies: recovery replays the journal newest first, through fresh instances.
    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("with values")]
    public async Task BoosterThenDetector_ReplayedNewestFirst_LeavesPerfOptionsAsItWas(string start)
    {
        if (start == "empty")
            Seed(PerfOptions);
        else if (start == "with values")
            Seed(PerfOptions, (Priority, 1), (Affinity, 15));
        var engine = NewEngine(NewBooster(), NewDetector());

        await engine.ActivateProfileAsync(Profile(processId: 0));

        Assert.Equal(3, Value(Priority));
        Assert.Equal(PinnedMask, Value(Affinity));
        var entries = OnDisk().Optimizations;
        Assert.Equal(new[] { ProcessPriorityBooster.OptimizationId, HybridCpuDetector.OptimizationId }, entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.Equal(("Applied", false), (e.State, e.InProgress)));

        foreach (var entry in Enumerable.Reverse(entries))
        {
            IJournaledOptimization fresh = entry.Name == HybridCpuDetector.OptimizationId
                ? new HybridCpuDetector { RecoveryRegistry = _registry.Registry }
                : new ProcessPriorityBooster { RecoveryRegistry = _registry.Registry };
            Assert.Equal(OptimizationState.Reverted, fresh.RevertFromRecord(entry.OriginalValue).State);
        }

        Assert.Equal(start != "absent", Exists(PerfOptions));
        Assert.Equal(start == "with values" ? 1 : null, Value(Priority));
        Assert.Equal(start == "with values" ? 15 : null, Value(Affinity));
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class RecordingRecorder : IApplyRecorder
    {
        public List<string> Records { get; } = new();

        public bool Throws { get; init; }

        public void BeforeChange(string originalValueJson)
        {
            if (Throws)
                throw new IOException("journal unavailable");
            Records.Add(originalValueJson);
        }
    }
}
