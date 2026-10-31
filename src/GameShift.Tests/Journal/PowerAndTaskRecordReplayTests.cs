using GameShift.Core.BackgroundMode;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Journal records of the scheduled task and core parking modules, in the shape 3.9 wrote them,
/// replayed against an in-memory task store and powercfg. Only the recorded GameShift targets are
/// acted on, only canonical GUIDs and parsed numbers reach powercfg, and the result is Failed
/// whenever something was not restored.
/// </summary>
public class PowerAndTaskRecordReplayTests
{
    // ── Scheduled tasks ───────────────────────────────────────────────

    private const string Defrag = @"\Microsoft\Windows\Defrag\ScheduledDefrag";
    private const string Appraiser = @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser";
    private const string WinSat = @"\Microsoft\Windows\Maintenance\WinSAT";

    private sealed class FakeTaskStore : IScheduledTaskStore
    {
        public Dictionary<string, ScheduledTaskState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> WillNotEnable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Touched { get; } = new();

        public ScheduledTaskState GetState(string path)
        {
            Touched.Add(path);
            return States.TryGetValue(path, out var state) ? state : ScheduledTaskState.NotFound;
        }

        public bool Enable(string path)
        {
            Touched.Add(path);
            if (WillNotEnable.Contains(path))
                return false;
            States[path] = ScheduledTaskState.Enabled;
            return true;
        }
    }

    private static string TaskRecord(params string[] paths) =>
        System.Text.Json.JsonSerializer.Serialize(new { disabledTasks = paths });

    [Fact]
    public void TaskRecord_ReEnablesTheDisabledTasks_AndNamesThem()
    {
        var store = new FakeTaskStore();
        store.States[Defrag] = ScheduledTaskState.Disabled;
        store.States[Appraiser] = ScheduledTaskState.Enabled;

        var result = new ScheduledTaskSuppressor { Tasks = store }.RevertFromRecord(TaskRecord(Defrag, Appraiser, WinSat));

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(ScheduledTaskState.Enabled, store.States[Defrag]);
        Assert.Equal($"Re-enabled scheduled tasks: {Defrag}", result.ErrorMessage);
    }

    [Theory]
    [InlineData(ScheduledTaskState.Unreadable)]
    [InlineData(ScheduledTaskState.Disabled)]
    public void TaskRecord_TaskNotReEnabled_IsFailed(ScheduledTaskState state)
    {
        var store = new FakeTaskStore();
        store.States[Defrag] = state;
        store.WillNotEnable.Add(Defrag);

        var result = new ScheduledTaskSuppressor { Tasks = store }.RevertFromRecord(TaskRecord(Defrag));

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Contains($"Could not re-enable scheduled tasks: {Defrag}", result.ErrorMessage);
    }

    // Earlier code passed the path into a schtasks argument string, where a quote added switches.
    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag"" /tr ""C:\Users\Public\x.exe")]
    [InlineData(@"\Microsoft\Windows\Some Other Task")]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag\..\..\Other")]
    public void TaskRecord_AnyOtherTask_IsRefusedAndNeverReachesTheStore(string path)
    {
        var store = new FakeTaskStore();
        store.States[Defrag] = ScheduledTaskState.Disabled;

        var result = new ScheduledTaskSuppressor { Tasks = store }.RevertFromRecord(TaskRecord(path, Defrag));

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.All(store.Touched, touched => Assert.Equal(Defrag, touched));
        Assert.Contains("Refused 1 journal entry", result.ErrorMessage);
        Assert.DoesNotContain(path, result.ErrorMessage);
    }

    // ── Core parking ──────────────────────────────────────────────────

    private const string Scheme = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string Processor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string CpMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string IdleStateMax = "9943e905-9a30-4ec1-9b99-44dd3b76f7a2";

    // The shape 3.9 serialized, with the originals as decimal strings and one value not captured.
    private const string ParkingRecord = """
        {"schemeGuid":"381b4222-f694-41f0-9685-ff5bb260df2e",
         "entries":[{"settingGuid":"0cc5b647-c1df-4637-891a-dec35c318583","settingName":"CPMINCORES","originalAc":"5","originalDc":null}],
         "idleDisableApplied":true,
         "cStateOriginals":[{"settingGuid":"9943e905-9a30-4ec1-9b99-44dd3b76f7a2","settingName":"IDLESTATEMAX (C1 max depth)","originalAc":"-1","originalDc":"0"}]}
        """;

    [Fact]
    public void ParkingRecord_RestoresEachValueThroughPowercfg()
    {
        var calls = new List<string>();

        var result = new CpuParkingManager { Powercfg = args => { calls.Add(args); return true; } }
            .RevertFromRecord(ParkingRecord);

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(
            new[]
            {
                $"/setacvalueindex {Scheme} {Processor} {CpMinCores} 5",
                $"/setacvalueindex {Scheme} {Processor} {IdleStateMax} -1",
                $"/setdcvalueindex {Scheme} {Processor} {IdleStateMax} 0",
                $"-attributes {Processor} {IdleStateMax} +ATTRIB_HIDE",
                $"/setactive {Scheme}",
            },
            calls);
    }

    [Fact]
    public void ParkingRecord_PowercfgFailure_IsFailed()
    {
        var result = new CpuParkingManager { Powercfg = args => !args.Contains(CpMinCores, StringComparison.Ordinal) }
            .RevertFromRecord(ParkingRecord);

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.Contains(CpMinCores, result.ErrorMessage);
    }

    [Theory]
    [InlineData("""{"schemeGuid":"SCHEME_CURRENT /hibernate off","entries":[]}""")]
    [InlineData("""{"schemeGuid":"381b4222-f694-41f0-9685-ff5bb260df2e","entries":[{"settingGuid":"0cc5b647-c1df-4637-891a-dec35c318583 /x","originalAc":"5"}]}""")]
    [InlineData("""{"schemeGuid":"381b4222-f694-41f0-9685-ff5bb260df2e","entries":[{"settingGuid":"0cc5b647-c1df-4637-891a-dec35c318583","originalAc":"5 /hibernate off"}]}""")]
    [InlineData("""{"schemeGuid":"381b4222-f694-41f0-9685-ff5bb260df2e","entries":[{"settingGuid":"0cc5b647-c1df-4637-891a-dec35c318583","originalAc":5}]}""")]
    public void ParkingRecord_AnythingButGuidsAndNumbers_NeverReachesPowercfg(string record)
    {
        var calls = new List<string>();

        var result = new CpuParkingManager { Powercfg = args => { calls.Add(args); return true; } }
            .RevertFromRecord(record);

        Assert.Equal(OptimizationState.Failed, result.State);
        Assert.All(calls, call => Assert.Equal($"/setactive {Scheme}", call));
    }
}
