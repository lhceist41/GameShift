using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.System;
using GameShift.Tests.Optimization;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Detection;

/// <summary>
/// Every way a session ends goes through the orchestrator's one teardown: the last game exiting,
/// pause, exit, and a revert from the dashboard. A preset game's actions here are fakes that only
/// record calls, the engine applies a mock optimization and keeps its journal in a temp folder, and
/// starting or stopping process monitoring only counts calls. Nothing here reaches Defender, the
/// firewall, PowerShell or a process monitor, or writes to the registry; while a session is active
/// the engine still reads the Windows build number and watches its registry values, read-only, as it
/// does in <see cref="OptimizationEngineTests"/>.
/// </summary>
[Collection("ConfigState")]
public sealed class DetectionOrchestratorSessionTests : IAsyncLifetime
{
    private static readonly string PresetPath =
        Path.Combine(@"C:\Games\Overwatch\_retail_", CompetitivePresets.Overwatch2Exe);
    private const string OtherPath = @"C:\Games\Other\other.exe";

    private static readonly string[] SessionApplied =
        { "apply engine", "apply Defender exclusion", "apply Firewall rule" };
    private static readonly string[] SessionReverted =
        { "revert Firewall rule", "revert Defender exclusion", "revert engine" };

    private readonly TempPath _temp = new();
    private readonly string? _previousSettingsPath = SettingsManager.SettingsFilePathOverride;

    // Every apply and revert in order, game actions and engine alike.
    private readonly List<string> _calls = new();

    // PIDs the liveness sweep finds running, with the process name it reports for them.
    private readonly Dictionary<int, string> _running = new();

    // One set per time the orchestrator asked for the preset game's actions.
    private readonly List<List<FakeGameAction>> _actionSets = new();

    private readonly MockOptimizationSuccess _optimization = new() { Name = "Mock optimization" };

    // Applied after _optimization. Unavailable, and so skipped, unless a test makes it throw.
    private readonly AvailabilityCheckOptimization _laterOptimization = new();
    private readonly GameDetector _detector;
    private readonly OptimizationEngine _engine;
    private readonly DetectionOrchestrator _orchestrator;
    private int _monitoringStarts;
    private int _monitoringStops;
    private int _leftoverCleanups;

    public DetectionOrchestratorSessionTests()
    {
        SettingsManager.SettingsFilePathOverride = _temp.GetFile("settings.json");

        // Background Mode's ProBalance would otherwise start with the session, on a timer that
        // lowers the priority of real processes.
        SettingsManager.Update(s => s.BackgroundMode = new BackgroundModeSettings { ProBalanceEnabled = false });

        _optimization.OnApply = () => _calls.Add("apply engine");
        _optimization.OnRevert = () => _calls.Add("revert engine");

        var scanners = new ILibraryScanner[]
        {
            new FakeLibraryScanner("Steam", true,
                new GameInfo
                {
                    Id = "steam_ow", GameName = "Overwatch 2", InstallDirectory = @"C:\Games\Overwatch",
                    LauncherSource = "Steam", LauncherId = "ow"
                },
                new GameInfo
                {
                    Id = "steam_other", GameName = "Other", InstallDirectory = @"C:\Games\Other",
                    LauncherSource = "Steam", LauncherId = "other"
                })
        };

        _detector = new GameDetector(scanners, ProcessProbe.Default, sweepProcessNameProbe: RunningProcessName);
        _engine = new OptimizationEngine(
            new IOptimization[] { _optimization, _laterOptimization }, new JournalManager(JournalPath));
        _orchestrator = new DetectionOrchestrator(
            _detector, _engine, new KnownGamesStore(), scanners, new ProfileManager(),
            dpcMonitor: null,
            hardwareScan: null,
            gameActionsFor: PresetGameActions,
            startMonitoring: () => _monitoringStarts++,
            stopMonitoring: () => _monitoringStops++,
            removeLeftoverFirewallRules: () =>
            {
                Interlocked.Increment(ref _leftoverCleanups);
                return Array.Empty<string>();
            });
    }

    private string JournalPath => _temp.GetFile("state.json");

    public Task InitializeAsync() => _orchestrator.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _orchestrator.WhenIdleAsync();
        _orchestrator.Cleanup();
        _engine.Dispose();
        _detector.Dispose();
        SettingsManager.SettingsFilePathOverride = _previousSettingsPath;
        _temp.Dispose();
    }

    [Fact]
    public async Task LastGameExiting_RevertsGameActionsInReverseOrderThenTheEngine()
    {
        await StartGameAsync(5000, PresetPath);
        await StartGameAsync(5001, OtherPath);
        Assert.True(_orchestrator.IsOptimizing);
        Assert.Equal(SessionApplied, _calls);

        // Another game is still running, so the session continues.
        await StopGameAsync(5000);
        Assert.True(_orchestrator.IsOptimizing);
        Assert.Equal(SessionApplied, _calls);

        await StopGameAsync(5001);

        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(SessionApplied.Concat(SessionReverted), _calls);
    }

    [Fact]
    public async Task Shutdown_MidGame_RevertsGameActionsAndTheEngine_AndStartsNoSessionAfterwards()
    {
        await StartGameAsync(5000, PresetPath);

        await _orchestrator.ShutdownAsync();

        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(SessionApplied.Concat(SessionReverted), _calls);
        Assert.Equal(1, _monitoringStops);

        // A start event still in flight while GameShift exits must not start a session.
        await StartGameAsync(5001, OtherPath);
        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(1, _optimization.ApplyCallCount);
    }

    [Fact]
    public async Task Pause_MidGame_EndsTheSessionAndStartsNoSessionWhilePaused()
    {
        await StartGameAsync(5000, PresetPath);

        await _orchestrator.PauseAsync();

        Assert.True(_orchestrator.IsPaused);
        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(1, _monitoringStops);
        Assert.Equal(SessionApplied.Concat(SessionReverted), _calls);

        // A start event still in flight while paused must not start a session.
        await StartGameAsync(5001, OtherPath);
        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(1, _optimization.ApplyCallCount);
        Assert.Single(_actionSets);
    }

    /// <summary>
    /// A start event that arrives while the pause is still reverting waits for the session lock. When
    /// it gets the lock, no session is active any more, so it must be the pause that stops it.
    /// </summary>
    [Fact]
    public async Task Pause_StartArrivingDuringItsTeardown_StartsNoSession()
    {
        await StartGameAsync(5000, PresetPath);

        using var teardownReached = new ManualResetEventSlim();
        using var finishTeardown = new ManualResetEventSlim();
        var firstToRevert = _actionSets[0][^1];
        firstToRevert.DuringRevert = () =>
        {
            teardownReached.Set();
            finishTeardown.Wait(TimeSpan.FromSeconds(10));
        };

        var pause = Task.Run(() => _orchestrator.PauseAsync());
        Assert.True(teardownReached.Wait(TimeSpan.FromSeconds(10)));

        _running[5001] = Path.GetFileNameWithoutExtension(OtherPath);
        _detector.OnProcessStarted(Started(5001, OtherPath));
        finishTeardown.Set();
        await pause;
        await _orchestrator.WhenIdleAsync();

        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(1, _optimization.ApplyCallCount);
    }

    [Fact]
    public async Task Resume_GameStillRunning_StartsItsSessionAgain()
    {
        await StartGameAsync(5000, PresetPath);
        await _orchestrator.PauseAsync();

        await _orchestrator.ResumeAsync();

        Assert.False(_orchestrator.IsPaused);
        Assert.True(_orchestrator.IsOptimizing);
        Assert.Equal(2, _monitoringStarts); // initialization, then resume
        Assert.Equal(2, _optimization.ApplyCallCount);
        Assert.Equal(2, _actionSets.Count);
        Assert.All(_actionSets[1], action => Assert.Equal(1, action.Applied));

        // The resumed session is the one the game's exit ends, its own actions included.
        await StopGameAsync(5000);

        Assert.False(_orchestrator.IsOptimizing);
        Assert.All(_actionSets[1], action => Assert.Equal(1, action.Reverted));
        Assert.Equal(2, _optimization.RevertCallCount);
    }

    [Fact]
    public async Task Resume_GameStartedWhilePaused_IsOptimizedFromItsRememberedStart()
    {
        await _orchestrator.PauseAsync();
        await StartGameAsync(5000, PresetPath);
        Assert.Empty(_actionSets);

        await _orchestrator.ResumeAsync();

        Assert.True(_orchestrator.IsOptimizing);
        Assert.Equal(SessionApplied, _calls);
    }

    [Fact]
    public async Task Resume_GameExitedWhilePaused_IsReleasedAndNotOptimized()
    {
        await StartGameAsync(5000, PresetPath);
        await _orchestrator.PauseAsync();

        // Nothing reports the exit while paused; the process is simply gone.
        _running.Remove(5000);
        var stopped = new List<int>();
        _detector.GameStopped += (_, e) => stopped.Add(e.ProcessId);

        await _orchestrator.ResumeAsync();
        await _orchestrator.WhenIdleAsync();

        Assert.Equal(new[] { 5000 }, stopped);
        Assert.Empty(_detector.GetActiveGames());
        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(1, _optimization.ApplyCallCount);
        Assert.Single(_actionSets);
        Assert.Equal(2, _monitoringStarts);
    }

    /// <summary>
    /// Quick Optimize runs through the engine with no game session. Pausing monitoring leaves it
    /// alone, and the dashboard's revert still reverts it through the engine.
    /// </summary>
    [Fact]
    public async Task QuickOptimize_SurvivesPause_AndEndSessionRevertsItThroughTheEngine()
    {
        var profile = GameProfile.CreateDefault();
        profile.GameName = "Quick Optimize";
        await _engine.ActivateProfileAsync(profile);
        Assert.False(_orchestrator.IsOptimizing);

        await _orchestrator.PauseAsync();
        await _orchestrator.ResumeAsync();
        Assert.True(_optimization.IsApplied);

        await _orchestrator.EndSessionAsync();

        Assert.False(_optimization.IsApplied);
        Assert.Equal(new[] { "apply engine", "revert engine" }, _calls);
        Assert.Empty(_actionSets);
    }

    [Fact]
    public async Task EndSession_DuringAGame_RevertsItsGameActions_AndTheGameExitRevertsNothingAgain()
    {
        await StartGameAsync(5000, PresetPath);

        await _orchestrator.EndSessionAsync();

        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(SessionApplied.Concat(SessionReverted), _calls);

        await StopGameAsync(5000);

        Assert.Equal(SessionApplied.Concat(SessionReverted), _calls);
    }

    [Fact]
    public async Task EndSession_DuringAGame_PauseAndResumeDoNotOptimizeItAgain()
    {
        await StartGameAsync(5000, PresetPath);
        await _orchestrator.EndSessionAsync();

        await _orchestrator.PauseAsync();
        await _orchestrator.ResumeAsync();

        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(1, _optimization.ApplyCallCount);
        Assert.Single(_actionSets);

        // Starting the game again is a new start, which is optimized.
        await StopGameAsync(5000);
        await StartGameAsync(5001, PresetPath);
        Assert.True(_orchestrator.IsOptimizing);
        Assert.Equal(2, _optimization.ApplyCallCount);
    }

    /// <summary>
    /// The pause hotkey pressed twice in quick succession: the second press waits for the first and
    /// then resumes, rather than also deciding to pause from the state it read before.
    /// </summary>
    [Fact]
    public async Task TogglePause_TwoQuickToggles_PauseThenResume()
    {
        await StartGameAsync(5000, PresetPath);

        var results = await Task.WhenAll(_orchestrator.TogglePauseAsync(), _orchestrator.TogglePauseAsync());

        Assert.Equal(new[] { false, true }, results.OrderBy(paused => paused));
        Assert.False(_orchestrator.IsPaused);
        Assert.True(_orchestrator.IsOptimizing);
        Assert.Equal(2, _optimization.ApplyCallCount);
    }

    [Fact]
    public async Task Shutdown_RevertsQuickOptimize()
    {
        var profile = GameProfile.CreateDefault();
        profile.GameName = "Quick Optimize";
        await _engine.ActivateProfileAsync(profile);

        await _orchestrator.ShutdownAsync();

        Assert.False(_optimization.IsApplied);
        Assert.Equal(new[] { "apply engine", "revert engine" }, _calls);
    }

    /// <summary>
    /// Ending a session that never started must not touch the journal: an earlier run's journal that
    /// still records an active session is what crash recovery reverts from.
    /// </summary>
    [Fact]
    public async Task NoSession_EndingPausingAndExiting_LeaveAnEarlierRunsJournalUntouched()
    {
        File.WriteAllText(JournalPath, System.Text.Json.JsonSerializer.Serialize(
            new SessionJournalData { SessionActive = true, GameShiftVersion = "earlier" }));
        var before = File.ReadAllBytes(JournalPath);

        await _orchestrator.EndSessionAsync();
        await _orchestrator.PauseAsync();
        await _orchestrator.ResumeAsync();
        await _orchestrator.ShutdownAsync();

        Assert.Equal(before, File.ReadAllBytes(JournalPath));
        Assert.Equal(0, _optimization.RevertCallCount);
    }

    /// <summary>
    /// An optimization that throws part-way through activation leaves the ones before it applied.
    /// The session still counts as active, so the game's exit reverts them.
    /// </summary>
    [Fact]
    public async Task ActivationFailingPartWay_IsTornDownWhenTheGameExits()
    {
        _laterOptimization.Throw = true;

        await StartGameAsync(5000, PresetPath);

        Assert.True(_optimization.IsApplied);
        Assert.True(_orchestrator.IsOptimizing);

        await StopGameAsync(5000);

        Assert.False(_optimization.IsApplied);
        Assert.False(_orchestrator.IsOptimizing);
        Assert.Equal(new[] { "apply engine", "revert engine" }, _calls);
    }

    [Fact]
    public async Task LeftoverFirewallRuleCleanup_RunsWithoutASession_ButNotDuringOneOrAfterExit()
    {
        await _orchestrator.RemoveLeftoverFirewallRulesAsync();
        Assert.Equal(1, _leftoverCleanups);

        // A running session's firewall rule is its own.
        await StartGameAsync(5000, PresetPath);
        await _orchestrator.RemoveLeftoverFirewallRulesAsync();
        Assert.Equal(1, _leftoverCleanups);

        await _orchestrator.ShutdownAsync();
        await _orchestrator.RemoveLeftoverFirewallRulesAsync();
        Assert.Equal(1, _leftoverCleanups);
    }

    private async Task StartGameAsync(int processId, string path)
    {
        _running[processId] = Path.GetFileNameWithoutExtension(path);
        _detector.OnProcessStarted(Started(processId, path));
        await _orchestrator.WhenIdleAsync();
    }

    private async Task StopGameAsync(int processId)
    {
        _running.Remove(processId);
        _detector.OnProcessStopped(new ProcessStopEventData { ProcessId = processId, Timestamp = DateTime.UtcNow });
        await _orchestrator.WhenIdleAsync();
    }

    private static ProcessStartEventData Started(int processId, string path) => new()
    {
        ProcessId = processId,
        ImageFileName = path,
        ParentProcessId = 0,
        Timestamp = DateTime.UtcNow
    };

    /// <summary>The liveness sweep's view of the process table: only <see cref="_running"/>.</summary>
    private string RunningProcessName(int processId) =>
        _running.TryGetValue(processId, out var name)
            ? name
            : throw new ArgumentException("No process with that ID is running.");

    private List<GameAction> PresetGameActions(string executableName)
    {
        var set = new List<FakeGameAction>
        {
            new("Defender exclusion", _calls),
            new("Firewall rule", _calls)
        };
        _actionSets.Add(set);
        return new List<GameAction>(set);
    }

    /// <summary>
    /// Unavailable, or, with <see cref="Throw"/> set, failing its availability check, which the
    /// engine makes outside its per-optimization error handling.
    /// </summary>
    private sealed class AvailabilityCheckOptimization : IOptimization
    {
        public bool Throw { get; set; }

        public string Name => "Availability check";

        public string Description => "Never applied";

        public bool IsApplied => false;

        public bool IsAvailable => Throw ? throw new InvalidOperationException("Availability check failed") : false;

        public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile) => Task.FromResult(false);

        public Task<bool> RevertAsync(SystemStateSnapshot snapshot) => Task.FromResult(false);
    }

    /// <summary>Records its applies and reverts; changes nothing.</summary>
    private sealed class FakeGameAction : GameAction
    {
        private readonly string _name;
        private readonly List<string> _calls;

        public FakeGameAction(string name, List<string> calls)
        {
            _name = name;
            _calls = calls;
        }

        public override string Name => _name;

        public int Applied { get; private set; }

        public int Reverted { get; private set; }

        /// <summary>Runs inside <see cref="Revert"/>, after the revert is recorded.</summary>
        public Action? DuringRevert { get; set; }

        public override void Apply(SystemStateSnapshot snapshot)
        {
            Applied++;
            _calls.Add("apply " + _name);
        }

        public override void Revert(SystemStateSnapshot snapshot)
        {
            Reverted++;
            _calls.Add("revert " + _name);
            DuringRevert?.Invoke();
        }
    }
}
