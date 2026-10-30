using System.Diagnostics;
using GameShift.Core.Optimization;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Optimization;

/// <summary>
/// ProBalance against an in-memory process table: samples are driven by hand, so no timer runs
/// and no real process is touched.
/// </summary>
public sealed class ProBalanceServiceTests : IDisposable
{
    private const int GameA = 100;
    private const int GameB = 200;
    private const int Encoder = 300;
    private const int Launcher = 400;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly FakeProcessPriorities _priorities = new();
    private readonly Dictionary<int, (string Name, TimeSpan Cpu, bool Busy)> _table = new();
    private readonly HashSet<int> _trackedGames = new();
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private bool _gameListUnreadable;
    private readonly ProBalanceService _service;

    public ProBalanceServiceTests()
    {
        _service = new ProBalanceService(Sample, _priorities, () => _now, _ => new NoTimer());
        _service.ProtectSessionGames(() =>
            _gameListUnreadable ? throw new InvalidOperationException("detector busy") : _trackedGames.ToArray());
    }

    public void Dispose() => _service.Dispose();

    private void AddProcess(int pid, string name, bool busy, ProcessPriorityClass priority = ProcessPriorityClass.Normal)
    {
        _table[pid] = (name, TimeSpan.Zero, busy);
        _priorities.Add(pid, name, priority);
    }

    private IReadOnlyList<ProBalanceService.ProcessCpuSample> Sample() =>
        _table.Select(p => new ProBalanceService.ProcessCpuSample(p.Key, p.Value.Name, p.Value.Cpu)).ToList();

    /// <summary>Advances time by one interval; busy processes use half of every core.</summary>
    private void Tick(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            _now += Interval;
            foreach (var pid in _table.Keys.ToList())
            {
                var p = _table[pid];
                if (p.Busy)
                    _table[pid] = p with { Cpu = p.Cpu + Interval * Environment.ProcessorCount / 2 };
            }
            _service.SampleOnce();
        }
    }

    [Fact]
    public void SecondGameRunningAlongsideTheFirst_IsNeverRestrained()
    {
        AddProcess(GameA, "GameA", busy: true);
        AddProcess(GameB, "GameB", busy: true);
        AddProcess(Encoder, "encoder", busy: true);
        _trackedGames.UnionWith(new[] { GameA, GameB });

        _service.Start(GameA, "GameA.exe");
        Tick(6);

        Assert.DoesNotContain(_priorities.Writes, w => w.Pid == GameB);
        Assert.Equal(ProcessPriorityClass.Normal, _priorities.PriorityOf(GameB));
        Assert.Equal(ProcessPriorityClass.BelowNormal, _priorities.PriorityOf(Encoder));
    }

    [Fact]
    public void ProcessRestrainedBeforeItWasTrackedAsAGame_GetsItsPriorityBack()
    {
        AddProcess(GameA, "GameA", busy: false);
        AddProcess(GameB, "GameB", busy: true);
        _trackedGames.Add(GameA);

        _service.Start(GameA, "GameA.exe");
        Tick(4);
        Assert.Equal(ProcessPriorityClass.BelowNormal, _priorities.PriorityOf(GameB));

        _trackedGames.Add(GameB);
        Tick();

        Assert.Equal(ProcessPriorityClass.Normal, _priorities.PriorityOf(GameB));
    }

    [Fact]
    public void ProcessAlreadyLoweredByAnotherWriter_IsNotRestoredToLoweredOnStop()
    {
        AddProcess(Launcher, "Battle.net", busy: true, ProcessPriorityClass.BelowNormal);

        _service.Start(GameA, "GameA.exe");
        Tick(6);
        // The launcher demotion's owner restores it before the session's ProBalance stops.
        _priorities.SetByOtherWriter(Launcher, ProcessPriorityClass.Normal);
        _service.Stop();

        Assert.Empty(_priorities.Writes);
        Assert.Equal(ProcessPriorityClass.Normal, _priorities.PriorityOf(Launcher));
    }

    [Fact]
    public void SecondStart_RestoresWhatTheFirstRunRestrained()
    {
        AddProcess(Encoder, "encoder", busy: true, ProcessPriorityClass.AboveNormal);

        _service.Start(0, string.Empty); // Quick Optimize, no game
        Tick(4);
        Assert.Equal(ProcessPriorityClass.BelowNormal, _priorities.PriorityOf(Encoder));

        _service.Start(GameA, "GameA.exe"); // a game starts afterwards

        Assert.Equal(ProcessPriorityClass.AboveNormal, _priorities.PriorityOf(Encoder));
    }

    [Fact]
    public void UnreadableGameList_SkipsTheSampleInsteadOfRestraining()
    {
        AddProcess(GameB, "GameB", busy: true);
        _gameListUnreadable = true;

        _service.Start(GameA, "GameA.exe");
        Tick(6);

        Assert.Empty(_priorities.Writes);
    }

    [Fact]
    public void RestrainedPidReusedByAnotherProcess_IsNotRestoredOnStop()
    {
        AddProcess(GameA, "GameA", busy: false);
        AddProcess(Encoder, "encoder", busy: true);
        _service.Start(GameA, "GameA.exe");
        Tick(4);
        Assert.Equal(ProcessPriorityClass.BelowNormal, _priorities.PriorityOf(Encoder));

        AddProcess(Encoder, "browser", busy: false, ProcessPriorityClass.High);
        _service.Stop();

        Assert.Equal(ProcessPriorityClass.High, _priorities.PriorityOf(Encoder));
    }

    [Fact]
    public void RestrainedPidReusedByAnotherProcess_IsNotRestoredWhenItGoesQuiet()
    {
        AddProcess(GameA, "GameA", busy: false);
        AddProcess(Encoder, "encoder", busy: true);
        _service.Start(GameA, "GameA.exe");
        Tick(4);
        var writesWhileEncoderRan = _priorities.Writes.Count;

        AddProcess(Encoder, "browser", busy: false, ProcessPriorityClass.High);
        Tick(6);

        Assert.Equal(writesWhileEncoderRan, _priorities.Writes.Count);
        Assert.Equal(ProcessPriorityClass.High, _priorities.PriorityOf(Encoder));
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose() { }
    }
}
