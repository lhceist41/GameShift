using System.Diagnostics;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Microsoft.Win32;
using Serilog.Core;

namespace GameShift.Tests.Detection;

/// <summary>
/// A saved profile can name another process name of the same built-in game than the one running,
/// for example a Deadlock profile saved when the game was project8.exe. The session uses the
/// observed name only when the built-in table lists both names for one game, and only for the
/// session's copy of the profile. The engine records the executable name every optimization
/// receives and, for the Deadlock cases, runs the real priority booster against an in-memory process
/// table (Deadlock's VAC takes its live path, which never touches the registry). Profiles and
/// journals live in a temp folder.
/// </summary>
[Collection("ConfigState")]
public sealed class DetectionOrchestratorSavedProfileTests : IAsyncLifetime
{
    private const int GamePid = 6100;
    private const string DeadlockId = "steam_1422450";
    private const string DeadlockBin = @"C:\Games\Deadlock\game\bin\win64";
    private const string ApexId = "steam_1172470";
    private const string ApexDir = @"C:\Games\Apex";
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private readonly TempPath _temp = new();
    private readonly string? _previousSettingsPath = SettingsManager.SettingsFilePathOverride;
    private readonly Dictionary<int, string> _running = new();
    private readonly FakeProcessPriorities _processes = new();
    private readonly ExecutableNameRecorder _recorder = new();
    private readonly ProfileManager _profiles;
    private readonly ILibraryScanner[] _scanners;
    private readonly GameDetector _detector;
    private OptimizationEngine? _engine;
    private DetectionOrchestrator? _orchestrator;

    public DetectionOrchestratorSavedProfileTests()
    {
        SettingsManager.SettingsFilePathOverride = _temp.GetFile("settings.json");
        _profiles = new ProfileManager(ProfilesDirectory);

        _scanners = new ILibraryScanner[]
        {
            new FakeLibraryScanner("Steam", true,
                new GameInfo
                {
                    Id = DeadlockId, GameName = "Deadlock", InstallDirectory = @"C:\Games\Deadlock",
                    LauncherSource = "Steam", LauncherId = "1422450"
                },
                new GameInfo
                {
                    Id = ApexId, GameName = "Apex Legends", InstallDirectory = ApexDir,
                    LauncherSource = "Steam", LauncherId = "1172470"
                })
        };

        _detector = new GameDetector(_scanners, ProcessProbe.Default, sweepProcessNameProbe: RunningProcessName);
    }

    private string ProfilesDirectory => _temp.GetFile("profiles");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_orchestrator != null)
        {
            await _orchestrator.WhenIdleAsync();
            _orchestrator.Cleanup();
        }
        _engine?.Dispose();
        _detector.Dispose();
        SettingsManager.SettingsFilePathOverride = _previousSettingsPath;
        _temp.Dispose();
    }

    [Fact]
    public async Task SavedProject8_RunningAsDeadlockExe_TheModulesGetTheObservedName_AndTheGameIsBoosted()
    {
        SaveProfile(DeadlockId, "project8.exe");
        await StartOrchestratorAsync(withBooster: true);

        await StartGameAsync(Path.Combine(DeadlockBin, "deadlock.exe"));

        Assert.Equal("deadlock.exe", Assert.Single(_recorder.Names));
        Assert.Equal(new[] { (GamePid, ProcessPriorityClass.High) }, _processes.Writes);
    }

    [Fact]
    public async Task SavedDeadlockExe_RunningAsProject8_TheModulesGetTheObservedName()
    {
        SaveProfile(DeadlockId, "deadlock.exe");
        await StartOrchestratorAsync(withBooster: true);

        await StartGameAsync(Path.Combine(DeadlockBin, "project8.exe"));

        Assert.Equal("project8.exe", Assert.Single(_recorder.Names));
        Assert.Equal(new[] { (GamePid, ProcessPriorityClass.High) }, _processes.Writes);
    }

    // A name the table does not list for this game, such as one the user typed or another game's,
    // never widens what counts as the game: the booster keeps skipping the process it does not expect.
    [Theory]
    [InlineData("DeadlockWrapper.exe")]
    [InlineData("cs2.exe")]
    public async Task SavedNameNotOfTheSameBuiltInGame_IsKept(string savedName)
    {
        SaveProfile(DeadlockId, savedName);
        await StartOrchestratorAsync(withBooster: true);

        await StartGameAsync(Path.Combine(DeadlockBin, "deadlock.exe"));

        Assert.Equal(savedName, Assert.Single(_recorder.Names));
        Assert.Empty(_processes.Writes);
    }

    [Fact]
    public async Task TheRefresh_IsNotSaved()
    {
        var file = SaveProfile(DeadlockId, "project8.exe");
        var bytes = File.ReadAllBytes(file);
        var written = File.GetLastWriteTimeUtc(file);
        var files = Directory.GetFiles(ProfilesDirectory);
        await StartOrchestratorAsync(withBooster: true);

        await StartGameAsync(Path.Combine(DeadlockBin, "deadlock.exe"));
        await StopGameAsync();

        Assert.Equal("deadlock.exe", Assert.Single(_recorder.Names));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Equal(files, Directory.GetFiles(ProfilesDirectory));
        Assert.Equal("project8.exe", _profiles.GetProfileForGame(DeadlockId).ExecutableName);
    }

    /// <summary>
    /// An Apex session that crashed under r5apex.exe journaled the IFEO key it wrote. A later session
    /// runs as r5apex_dx12.exe and refreshes the saved name; undoing the crashed session still
    /// reverts the journaled r5apex.exe key and leaves the r5apex_dx12.exe key alone. Apex's anti-cheat
    /// would send a live booster to the IFEO path in HKLM, so this session's engine has only the
    /// recorder; both keys live in a throwaway HKCU tree.
    /// </summary>
    [Fact]
    public async Task IfeoRevertOfAnEarlierSession_TargetsItsJournaledKey_AfterTheRefresh()
    {
        const string earlierKey = Ifeo + @"\r5apex.exe\PerfOptions";
        const string currentKey = Ifeo + @"\r5apex_dx12.exe\PerfOptions";
        using var registry = new TestRecoveryRegistry();
        using (var key = registry.CreateKey(RecoveryHive.LocalMachine, earlierKey))
            key.SetValue("CpuPriorityClass", 3, RegistryValueKind.DWord);
        using (var key = registry.CreateKey(RecoveryHive.LocalMachine, currentKey))
            key.SetValue("CpuPriorityClass", 3, RegistryValueKind.DWord);

        var crashedJournal = new SessionJournalData
        {
            SessionActive = true,
            ActiveGame = new ActiveGameInfo { Name = "Apex Legends", Executable = "r5apex.exe", Pid = 4200 },
            Optimizations =
            {
                new JournalEntry
                {
                    Name = ProcessPriorityBooster.OptimizationId,
                    State = nameof(OptimizationState.Applied),
                    OriginalValue = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        ifeoKeys = new[] { new { keyPath = earlierKey, originalValuesJson = (string?)null } }
                    })
                }
            }
        };

        SaveProfile(ApexId, "r5apex.exe");
        await StartOrchestratorAsync(withBooster: false);
        await StartGameAsync(Path.Combine(ApexDir, "r5apex_dx12.exe"));
        Assert.Equal("r5apex_dx12.exe", Assert.Single(_recorder.Names));

        var factories = new Dictionary<string, Func<IJournaledOptimization>>
        {
            [ProcessPriorityBooster.OptimizationId] = () => new ProcessPriorityBooster { RecoveryRegistry = registry.Registry }
        };
        var result = new WatchdogRevertEngine(Logger.None, factories)
            .RevertFromJournal(crashedJournal, new JournalManager(_temp.GetFile("crashed.json")));

        Assert.True(result.AllReverted);
        Assert.Null(registry.Value(RecoveryHive.LocalMachine, earlierKey, "CpuPriorityClass"));
        Assert.Equal(3, registry.Value(RecoveryHive.LocalMachine, currentKey, "CpuPriorityClass"));
    }

    private Task StartOrchestratorAsync(bool withBooster)
    {
        var optimizations = withBooster
            ? new IOptimization[] { _recorder, new ProcessPriorityBooster { Processes = _processes } }
            : new IOptimization[] { _recorder };
        _engine = new OptimizationEngine(optimizations, new JournalManager(_temp.GetFile("state.json")));
        _orchestrator = new DetectionOrchestrator(
            _detector, _engine, new KnownGamesStore(), _scanners, _profiles,
            dpcMonitor: null,
            hardwareScan: null,
            gameActionsFor: _ => new List<GameAction>(),
            startMonitoring: () => { },
            stopMonitoring: () => { },
            removeLeftoverFirewallRules: () => Array.Empty<string>());
        return _orchestrator.InitializeAsync();
    }

    private string SaveProfile(string gameId, string executableName)
    {
        var profile = GameProfile.CreateDefault();
        profile.Id = gameId;
        profile.GameName = gameId;
        profile.ExecutableName = executableName;
        _profiles.SaveProfile(profile);
        return Path.Combine(ProfilesDirectory, gameId + ".json");
    }

    private async Task StartGameAsync(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        _running[GamePid] = name;
        _processes.Add(GamePid, name, ProcessPriorityClass.Normal);
        _detector.OnProcessStarted(new ProcessStartEventData
        {
            ProcessId = GamePid,
            ImageFileName = path,
            ParentProcessId = 0,
            Timestamp = DateTime.UtcNow
        });
        await _orchestrator!.WhenIdleAsync();
    }

    private async Task StopGameAsync()
    {
        _running.Remove(GamePid);
        _detector.OnProcessStopped(new ProcessStopEventData { ProcessId = GamePid, Timestamp = DateTime.UtcNow });
        await _orchestrator!.WhenIdleAsync();
    }

    private string RunningProcessName(int processId) =>
        _running.TryGetValue(processId, out var name)
            ? name
            : throw new ArgumentException("No process with that ID is running.");

    /// <summary>Records the executable name of each profile it is applied with; changes nothing.</summary>
    private sealed class ExecutableNameRecorder : IOptimization
    {
        public List<string> Names { get; } = new();

        public string Name => "Executable name recorder";

        public string Description => "Records the profile's executable name";

        public bool IsApplied { get; private set; }

        public bool IsAvailable => true;

        public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
        {
            Names.Add(profile.ExecutableName);
            IsApplied = true;
            return Task.FromResult(true);
        }

        public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
        {
            IsApplied = false;
            return Task.FromResult(true);
        }
    }
}
