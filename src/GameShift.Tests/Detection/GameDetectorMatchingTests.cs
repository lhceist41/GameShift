using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GameShift.Core.Detection;
using GameShift.Core.GameProfiles;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Detection;

/// <summary>
/// Regression harness for <see cref="GameDetector"/> process matching and active-game lifecycle.
///
/// These tests drive the real production handlers (<see cref="GameDetector.OnProcessStarted"/>,
/// <see cref="GameDetector.OnProcessStopped"/>, <see cref="GameDetector.ReconcileActiveGamesOnce"/>)
/// with synthetic event data, so matching logic is never re-implemented in the test. Monitoring is
/// never started and the process table is never enumerated: the only real-process interactions are
/// the deliberately controlled current-process and known-nonexistent-PID liveness cases.
///
/// All synthetic <see cref="ProcessStartEventData.ImageFileName"/> values are rooted, which is what
/// keeps <see cref="GameDetector.OnProcessStarted"/> on its ETW path and away from
/// <see cref="Process.GetProcessById(int)"/>.
/// </summary>
public class GameDetectorMatchingTests
{
    private static GameDetector CreateDetector(params GameInfo[] knownGames)
    {
        var detector = new GameDetector(Array.Empty<ILibraryScanner>());
        foreach (var game in knownGames)
            detector.AddKnownGame(game);
        return detector;
    }

    private static ProcessStartEventData Started(int processId, string rootedImagePath)
    {
        Assert.True(Path.IsPathRooted(rootedImagePath), "Synthetic start paths must be rooted.");
        return new ProcessStartEventData
        {
            ProcessId = processId,
            ImageFileName = rootedImagePath,
            ParentProcessId = 0,
            Timestamp = DateTime.UtcNow
        };
    }

    private static ProcessStopEventData Stopped(int processId) => new()
    {
        ProcessId = processId,
        Timestamp = DateTime.UtcNow
    };

    /// <summary>
    /// Records the three lifecycle events so assertions can check both count and payload.
    /// </summary>
    private sealed class EventRecorder
    {
        public List<GameDetectedEventArgs> Started { get; } = new();
        public List<GameDetectedEventArgs> Stopped { get; } = new();
        public List<ProcessSpawnedEventArgs> Spawned { get; } = new();
        public int AllStoppedCount { get; private set; }

        public EventRecorder(GameDetector detector)
        {
            detector.GameStarted += (_, e) => Started.Add(e);
            detector.GameStopped += (_, e) => Stopped.Add(e);
            detector.ProcessSpawned += (_, e) => Spawned.Add(e);
            detector.AllGamesStopped += (_, _) => AllStoppedCount++;
        }
    }

    /// <summary>
    /// Finds a PID that provably does not exist, so the liveness sweep takes its
    /// "no such process -> definitely gone" branch. Probes specific IDs far above the practical
    /// Windows PID range; it never calls <see cref="Process.GetProcesses()"/> and never inspects
    /// arbitrary real processes.
    /// </summary>
    private static int FindNonexistentProcessId()
    {
        for (int candidate = 0x7FFF_FFF0; candidate > 0x7FFF_FF00; candidate -= 4)
        {
            try
            {
                using var probe = Process.GetProcessById(candidate);
            }
            catch (ArgumentException)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No nonexistent PID could be found for the liveness test.");
    }

    /// <summary>Name (without extension) of the process hosting this test run.</summary>
    private static string CurrentProcessName()
    {
        using var self = Process.GetCurrentProcess();
        return self.ProcessName;
    }

    // ---------------------------------------------------------------------------------------
    // Permanent invariants
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A1: Steam-style launcher entries carry an install directory but no executable path. The game
    /// must still be matched by install-directory containment, keep the launcher's identity, and
    /// report the actual running executable. The shared spawn feed must also fire for a matched
    /// process, not only for the unmatched process covered by A17.
    /// </summary>
    [Fact]
    public void OnProcessStarted_ExecutableUnderInstallDirectory_RaisesGameStartedWithScannerIdentity()
    {
        var game = new GameInfo
        {
            Id = "steam_620",
            GameName = "Portal 2",
            ExecutablePath = "",
            InstallDirectory = @"C:\SteamLibrary\steamapps\common\Portal 2",
            LauncherSource = "Steam",
            LauncherId = "620"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        const string runningPath = @"C:\SteamLibrary\steamapps\common\Portal 2\bin\portal2.exe";
        detector.OnProcessStarted(Started(4100, runningPath));

        var started = Assert.Single(events.Started);
        Assert.Equal("steam_620", started.GameId);
        Assert.Equal("Portal 2", started.GameName);
        Assert.Equal("Steam", started.LauncherSource);
        Assert.Equal(runningPath, started.ExecutablePath);
        Assert.Equal(4100, started.ProcessId);

        // ProcessSpawned is raised before the game-matching filter, so a successful match must not
        // swallow it: ProcessPriorityPersistence and ProcessSnapshotService need every start.
        var spawned = Assert.Single(events.Spawned);
        Assert.Equal(4100, spawned.ProcessId);
        Assert.Equal("portal2.exe", spawned.ProcessName);

        var active = Assert.Single(detector.GetActiveGames());
        Assert.Equal(4100, active.Key);
        Assert.Equal("steam_620", active.Value.Id);
    }

    /// <summary>
    /// A2: Install-directory containment must be a real directory boundary. A sibling directory that
    /// merely shares the name prefix (TF2 vs TF2Mods) is a different install and must not match.
    /// </summary>
    [Fact]
    public void OnProcessStarted_SiblingDirectoryWithSharedPrefix_DoesNotMatch()
    {
        var game = new GameInfo
        {
            Id = "steam_440",
            GameName = "Team Fortress 2",
            ExecutablePath = "",
            InstallDirectory = @"C:\SteamLibrary\steamapps\common\TF2",
            LauncherSource = "Steam",
            LauncherId = "440"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(4200, @"C:\SteamLibrary\steamapps\common\TF2Mods\modrunner.exe"));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Single(detector.GetKnownGames()); // no runtime entry appended either
    }

    /// <summary>
    /// A3: Exact executable-path matching (the fallback for entries with no install directory) is
    /// case-insensitive, and it is exact - another executable in the same directory is not the game.
    /// </summary>
    [Fact]
    public void OnProcessStarted_ExactExecutablePathMatch_IsCaseInsensitive()
    {
        var game = new GameInfo
        {
            Id = "gog_1207658930",
            GameName = "Rocket Game",
            ExecutablePath = @"C:\Games\ExactMatch\Rocket.exe",
            InstallDirectory = "",
            LauncherSource = "GOG",
            LauncherId = "1207658930"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(4300, @"c:\games\exactmatch\ROCKET.EXE"));

        var started = Assert.Single(events.Started);
        Assert.Equal("gog_1207658930", started.GameId);

        // A different executable in the same directory is not covered by an exact-path entry.
        detector.OnProcessStarted(Started(4301, @"C:\Games\ExactMatch\Editor.exe"));

        Assert.Single(events.Started);
        Assert.Single(detector.GetActiveGames());
    }

    /// <summary>
    /// A4: Platform stubs, anti-cheat launchers, installers and crash reporters live inside game
    /// install directories. Matching one as "the game" would optimize the wrong process and trigger
    /// a premature revert when it exits, so they must be rejected even under a known install path.
    /// </summary>
    [Theory]
    [InlineData("start_protected_game.exe")]
    [InlineData("EasyAntiCheat.exe")]
    [InlineData("BEService.exe")]
    [InlineData("setup.exe")]
    [InlineData("crashpad_handler.exe")]
    [InlineData("UECrashReporter.exe")]
    public void OnProcessStarted_NonGameHelperUnderInstallDirectory_IsRejected(string helperExeName)
    {
        const string installDir = @"C:\SteamLibrary\steamapps\common\HelperHost";
        var game = new GameInfo
        {
            Id = "steam_9001",
            GameName = "Helper Host",
            ExecutablePath = "",
            InstallDirectory = installDir,
            LauncherSource = "Steam",
            LauncherId = "9001"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(4400, Path.Combine(installDir, helperExeName)));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());

        // Positive control: the real game executable in the same directory still matches, proving
        // the rejection above is about the helper name and not about the directory.
        detector.OnProcessStarted(Started(4401, Path.Combine(installDir, "HelperHost.exe")));

        var started = Assert.Single(events.Started);
        Assert.Equal("steam_9001", started.GameId);
        Assert.Equal(4401, started.ProcessId);
    }

    /// <summary>
    /// A5: With no launcher match, an executable whose name is a built-in profile process name is
    /// adopted under a deterministic built-in identity, and a runtime known-game record is created
    /// from the observed path so later launches match directly.
    /// </summary>
    [Fact]
    public void OnProcessStarted_UnknownExeMatchingBuiltInProcessName_CreatesDeterministicBuiltInIdentity()
    {
        var builtIn = BuiltInProfiles.ApexLegends();
        var exeName = builtIn.ProcessNames.First();
        const string installDir = @"C:\Standalone\Apex";
        var runningPath = Path.Combine(installDir, exeName);

        using var detector = CreateDetector();
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(4500, runningPath));

        var expectedId = GameInfo.GenerateId("builtin", builtIn.Id);
        var started = Assert.Single(events.Started);
        Assert.Equal(expectedId, started.GameId);
        Assert.Equal(builtIn.DisplayName, started.GameName);
        Assert.Equal("BuiltIn", started.LauncherSource);
        Assert.Equal(runningPath, started.ExecutablePath);

        var created = Assert.Single(detector.GetKnownGames());
        Assert.Equal(expectedId, created.Id);
        Assert.Equal(runningPath, created.ExecutablePath);
        Assert.Equal(installDir, created.InstallDirectory);
        Assert.Equal("BuiltIn", created.LauncherSource);
    }

    /// <summary>
    /// A6: The built-in process-name fallback is a last resort. When a launcher already knows the
    /// title, the launcher identity must win so profile lookup stays stable, and no duplicate
    /// built-in record may be appended.
    /// </summary>
    [Fact]
    public void OnProcessStarted_KnownGameTakesPrecedenceOverBuiltInProfile()
    {
        var builtIn = BuiltInProfiles.ApexLegends();
        var exeName = builtIn.ProcessNames.First();
        const string installDir = @"C:\SteamLibrary\steamapps\common\Apex Legends";

        var launcherGame = new GameInfo
        {
            Id = "steam_1172470",
            GameName = "Apex Legends",
            ExecutablePath = "",
            InstallDirectory = installDir,
            LauncherSource = "Steam",
            LauncherId = "1172470"
        };

        using var detector = CreateDetector(launcherGame);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(4600, Path.Combine(installDir, exeName)));

        var started = Assert.Single(events.Started);
        Assert.Equal("steam_1172470", started.GameId);
        Assert.Equal("Steam", started.LauncherSource);

        var known = Assert.Single(detector.GetKnownGames());
        Assert.Equal("steam_1172470", known.Id);
        Assert.DoesNotContain(detector.GetKnownGames(), g => g.LauncherSource == "BuiltIn");
    }

    /// <summary>
    /// A7: The startup rundown and a live start event can both deliver the same PID. Tracking and
    /// the GameStarted notification must be idempotent so downstream optimization is applied once.
    /// </summary>
    [Fact]
    public void OnProcessStarted_SamePidTwice_RaisesGameStartedOnce()
    {
        var game = new GameInfo
        {
            Id = "steam_730",
            GameName = "Counter-Strike 2",
            ExecutablePath = "",
            InstallDirectory = @"C:\SteamLibrary\steamapps\common\CS2",
            LauncherSource = "Steam",
            LauncherId = "730"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        var start = Started(4700, @"C:\SteamLibrary\steamapps\common\CS2\game\bin\cs2.exe");
        detector.OnProcessStarted(start);
        detector.OnProcessStarted(start);

        Assert.Single(events.Started);
        Assert.Single(detector.GetActiveGames());
    }

    /// <summary>
    /// A8: AllGamesStopped is the revert signal. It must fire only when the LAST tracked game exits,
    /// otherwise optimizations would be reverted while another game is still running.
    /// </summary>
    [Fact]
    public void OnProcessStopped_LastTrackedGame_RaisesAllGamesStoppedOnceAndNotBefore()
    {
        var first = new GameInfo
        {
            Id = "steam_1",
            GameName = "First",
            InstallDirectory = @"C:\Games\First",
            LauncherSource = "Steam",
            LauncherId = "1"
        };
        var second = new GameInfo
        {
            Id = "steam_2",
            GameName = "Second",
            InstallDirectory = @"C:\Games\Second",
            LauncherSource = "Steam",
            LauncherId = "2"
        };

        using var detector = CreateDetector(first, second);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(4800, @"C:\Games\First\first.exe"));
        detector.OnProcessStarted(Started(4801, @"C:\Games\Second\second.exe"));
        Assert.Equal(2, events.Started.Count);

        detector.OnProcessStopped(Stopped(4800));
        Assert.Single(events.Stopped);
        Assert.Equal(0, events.AllStoppedCount);
        Assert.Single(detector.GetActiveGames());

        detector.OnProcessStopped(Stopped(4801));
        Assert.Equal(2, events.Stopped.Count);
        Assert.Equal(1, events.AllStoppedCount);
        Assert.Empty(detector.GetActiveGames());
    }

    /// <summary>
    /// A9: Stop events arrive for every process on the machine. An untracked PID must be inert -
    /// in particular it must never produce a spurious AllGamesStopped revert.
    /// </summary>
    [Fact]
    public void OnProcessStopped_UntrackedPid_RaisesNothing()
    {
        var game = new GameInfo
        {
            Id = "steam_3",
            GameName = "Tracked",
            InstallDirectory = @"C:\Games\Tracked",
            LauncherSource = "Steam",
            LauncherId = "3"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(4900, @"C:\Games\Tracked\tracked.exe"));
        detector.OnProcessStopped(Stopped(4999));

        Assert.Empty(events.Stopped);
        Assert.Equal(0, events.AllStoppedCount);
        Assert.Single(detector.GetActiveGames());
    }

    /// <summary>
    /// A10: The liveness sweep exists because the WMI fallback can drop a stop event. When the PID
    /// no longer exists at all, the tracked game must be released and the revert signal must fire.
    /// </summary>
    [Fact]
    public void ReconcileActiveGamesOnce_ProcessGone_RaisesGameStoppedAndAllGamesStopped()
    {
        int deadPid = FindNonexistentProcessId();

        var game = new GameInfo
        {
            Id = "steam_4",
            GameName = "Ghost",
            ExecutablePath = @"C:\Games\Ghost\ghost.exe",
            InstallDirectory = @"C:\Games\Ghost",
            LauncherSource = "Steam",
            LauncherId = "4"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(deadPid, @"C:\Games\Ghost\ghost.exe"));
        Assert.Single(detector.GetActiveGames());

        detector.ReconcileActiveGamesOnce();

        var stopped = Assert.Single(events.Stopped);
        Assert.Equal(deadPid, stopped.ProcessId);
        Assert.Equal("steam_4", stopped.GameId);
        Assert.Equal(1, events.AllStoppedCount);
        Assert.Empty(detector.GetActiveGames());
    }

    /// <summary>
    /// A11: The sweep must never revert a game that is actually running. A live PID whose image name
    /// matches the tracked executable stays tracked.
    /// </summary>
    [Fact]
    public void ReconcileActiveGamesOnce_ProcessAliveWithMatchingImageName_KeepsGame()
    {
        var hostExePath = Path.Combine(@"C:\Games\LiveHost", CurrentProcessName() + ".exe");

        var game = new GameInfo
        {
            Id = "steam_5",
            GameName = "Live Host",
            ExecutablePath = hostExePath,
            InstallDirectory = "",
            LauncherSource = "Steam",
            LauncherId = "5"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(Environment.ProcessId, hostExePath));
        Assert.Single(detector.GetActiveGames());

        detector.ReconcileActiveGamesOnce();

        Assert.Empty(events.Stopped);
        Assert.Equal(0, events.AllStoppedCount);
        Assert.Single(detector.GetActiveGames());
    }

    /// <summary>
    /// A12: A live PID that now belongs to a differently-named image means the game exited and the
    /// PID was recycled. The stale tracking entry must be dropped and the revert signal must fire.
    /// </summary>
    [Fact]
    public void ReconcileActiveGamesOnce_PidReusedByDifferentImage_DropsGame()
    {
        const string exePath = @"C:\Games\Recycled\NotTheTestHost.exe";

        var game = new GameInfo
        {
            Id = "steam_6",
            GameName = "Recycled",
            ExecutablePath = exePath,
            InstallDirectory = "",
            LauncherSource = "Steam",
            LauncherId = "6"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(Environment.ProcessId, exePath));
        Assert.Single(detector.GetActiveGames());

        detector.ReconcileActiveGamesOnce();

        var stopped = Assert.Single(events.Stopped);
        Assert.Equal("steam_6", stopped.GameId);
        Assert.Equal(1, events.AllStoppedCount);
        Assert.Empty(detector.GetActiveGames());
    }

    /// <summary>
    /// A14: Liveness must never be decided optimistically. A denied or transient process lookup means
    /// "unknown", which the sweep must treat as alive so a running game is never reverted; only a
    /// missing PID (ArgumentException) counts as gone.
    /// </summary>
    [Fact]
    public void IsTrackedGameGone_AccessDeniedProbe_TreatsGameAsAlive()
    {
        var game = new GameInfo
        {
            Id = "steam_7",
            GameName = "Protected",
            ExecutablePath = @"C:\Games\Protected\protected.exe",
            LauncherSource = "Steam",
            LauncherId = "7"
        };
        var active = new ActiveGame(game, "protected.exe", @"C:\Games\Protected\protected.exe");

        Assert.False(GameDetector.IsTrackedGameGone(1234, active, _ => throw new Win32Exception(5)));
        Assert.True(GameDetector.IsTrackedGameGone(1234, active, _ => throw new ArgumentException("no such process")));

        // The probe result is what drives the PID-reuse comparison.
        Assert.False(GameDetector.IsTrackedGameGone(1234, active, _ => "protected"));
        Assert.True(GameDetector.IsTrackedGameGone(1234, active, _ => "somethingelse"));
    }

    /// <summary>
    /// A14b: The sweep compares against Process.ProcessName, which drops only a trailing ".exe". A
    /// live image with another extension must still count as the same process, or the sweep would
    /// release a running game and revert its optimizations underneath it.
    /// </summary>
    [Fact]
    public void IsTrackedGameGone_NonExeImage_IsAliveUnderItsFullName()
    {
        var game = new GameInfo
        {
            Id = "steam_42",
            GameName = "Foo",
            ExecutablePath = "",
            InstallDirectory = @"C:\SteamLibrary\steamapps\common\Foo",
            LauncherSource = "Steam",
            LauncherId = "42"
        };
        var active = new ActiveGame(game, "Foo.bin", @"C:\SteamLibrary\steamapps\common\Foo\Foo.bin");

        Assert.False(GameDetector.IsTrackedGameGone(7000, active, _ => "Foo.bin"));
        Assert.True(GameDetector.IsTrackedGameGone(7000, active, _ => "Foo"));
    }

    /// <summary>
    /// A17: ProcessSpawned is the shared spawn feed for ProcessPriorityPersistence and
    /// ProcessSnapshotService. It fires for every start, before game matching, and carries the
    /// filename only - not the full path.
    /// </summary>
    [Fact]
    public void ProcessSpawned_RaisedForEveryStart_WithFileNameOnly()
    {
        using var detector = CreateDetector();
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(5100, @"C:\Windows\System32\notepad.exe"));

        var spawned = Assert.Single(events.Spawned);
        Assert.Equal(5100, spawned.ProcessId);
        Assert.Equal("notepad.exe", spawned.ProcessName);
        Assert.Empty(events.Started);
    }

    /// <summary>
    /// A18: The same title can be reported by more than one launcher scanner. The aggregate must
    /// hold one entry per game ID, and the first scanner to report an ID owns it.
    /// </summary>
    [Fact]
    public void ScanLibraries_DeduplicatesByIdAndKeepsFirstScannerWins()
    {
        var firstDuplicate = new GameInfo
        {
            Id = "steam_100",
            GameName = "Duplicate (first scanner)",
            InstallDirectory = @"C:\First\Duplicate",
            LauncherSource = "FirstLauncher",
            LauncherId = "100"
        };
        var secondDuplicate = new GameInfo
        {
            Id = "steam_100",
            GameName = "Duplicate (second scanner)",
            InstallDirectory = @"C:\Second\Duplicate",
            LauncherSource = "SecondLauncher",
            LauncherId = "100"
        };
        var uniqueFromFirst = new GameInfo { Id = "steam_101", GameName = "Only In First", LauncherSource = "FirstLauncher" };
        var uniqueFromSecond = new GameInfo { Id = "steam_102", GameName = "Only In Second", LauncherSource = "SecondLauncher" };

        var scannerA = new FakeLibraryScanner("FirstLauncher", true, firstDuplicate, uniqueFromFirst);
        var scannerB = new FakeLibraryScanner("SecondLauncher", true, secondDuplicate, uniqueFromSecond);

        using var detector = new GameDetector(new ILibraryScanner[] { scannerA, scannerB });
        detector.ScanLibraries();

        var known = detector.GetKnownGames();
        Assert.Equal(3, known.Count);

        var deduped = Assert.Single(known, g => g.Id == "steam_100");
        Assert.Equal("Duplicate (first scanner)", deduped.GameName);
        Assert.Equal(@"C:\First\Duplicate", deduped.InstallDirectory);
    }

    /// <summary>
    /// A19: A launcher that is not installed must not be scanned at all - scanning it would hit
    /// registry/filesystem paths that do not exist on this machine.
    /// </summary>
    [Fact]
    public void ScanLibraries_SkipsScannerReportingNotInstalled()
    {
        var absent = new FakeLibraryScanner(
            "AbsentLauncher",
            isInstalled: false,
            new GameInfo { Id = "steam_200", GameName = "Never Added", LauncherSource = "AbsentLauncher" })
        {
            ThrowOnScan = true
        };
        var present = new FakeLibraryScanner(
            "PresentLauncher",
            isInstalled: true,
            new GameInfo { Id = "steam_201", GameName = "Added", LauncherSource = "PresentLauncher" });

        using var detector = new GameDetector(new ILibraryScanner[] { absent, present });
        detector.ScanLibraries();

        Assert.False(absent.ScanCalled);
        Assert.True(present.ScanCalled);
        var known = Assert.Single(detector.GetKnownGames());
        Assert.Equal("steam_201", known.Id);
    }

    /// <summary>
    /// A20: Removal is by identity, not by display name. Two launchers can list the same title, and
    /// removing one must leave the other both stored and detectable.
    /// </summary>
    [Fact]
    public void RemoveKnownGame_RemovesOnlyMatchingId_LeavesSameTitleFromOtherLauncher()
    {
        var steamCopy = new GameInfo
        {
            Id = "steam_300",
            GameName = "Shared Title",
            InstallDirectory = @"C:\SteamLibrary\steamapps\common\Shared Title",
            LauncherSource = "Steam",
            LauncherId = "300"
        };
        var epicCopy = new GameInfo
        {
            Id = "epic_shared",
            GameName = "Shared Title",
            InstallDirectory = @"C:\Epic\Shared Title",
            LauncherSource = "Epic",
            LauncherId = "shared"
        };

        using var detector = CreateDetector(steamCopy, epicCopy);
        var events = new EventRecorder(detector);

        detector.RemoveKnownGame("steam_300");

        var remaining = Assert.Single(detector.GetKnownGames());
        Assert.Equal("epic_shared", remaining.Id);

        // The removed identity no longer matches...
        detector.OnProcessStarted(Started(5200, @"C:\SteamLibrary\steamapps\common\Shared Title\shared.exe"));
        Assert.Empty(events.Started);

        // ...while the same-titled entry from the other launcher still does.
        detector.OnProcessStarted(Started(5201, @"C:\Epic\Shared Title\shared.exe"));
        var started = Assert.Single(events.Started);
        Assert.Equal("epic_shared", started.GameId);
    }

    /// <summary>
    /// A13: Launcher entries (Steam/Xbox) carry an install directory but no executable path. The
    /// PID-reuse comparison therefore cannot use the persistent record - it uses the executable
    /// actually observed for that PID. When the PID now belongs to a different image, the stale
    /// entry must be dropped and the revert signal must fire, exactly as it does for entries that
    /// happen to carry a path.
    ///
    /// Before the runtime-evidence repair there was no expected name for these entries at all, so a
    /// missed stop event kept them tracked forever and their optimizations never reverted.
    /// </summary>
    [Fact]
    public void ReconcileActiveGamesOnce_LauncherGameWithEmptyExecutablePath_DetectsPidReuse()
    {
        var game = new GameInfo
        {
            Id = "steam_8",
            GameName = "Launcher Only",
            ExecutablePath = "",
            InstallDirectory = @"C:\SteamLibrary\steamapps\common\Launcher Only",
            LauncherSource = "Steam",
            LauncherId = "8"
        };

        using var detector = CreateDetector(game);
        var events = new EventRecorder(detector);

        // The observed image name (launcheronly.exe) is not this test host's process name, so the
        // live PID below is a PID-reuse observation.
        detector.OnProcessStarted(
            Started(Environment.ProcessId, @"C:\SteamLibrary\steamapps\common\Launcher Only\launcheronly.exe"));
        Assert.Single(detector.GetActiveGames());

        detector.ReconcileActiveGamesOnce();

        var stopped = Assert.Single(events.Stopped);
        Assert.Equal("steam_8", stopped.GameId);
        Assert.Equal(Environment.ProcessId, stopped.ProcessId);
        Assert.Equal(1, events.AllStoppedCount);
        Assert.Empty(detector.GetActiveGames());

        // The persistent record is untouched by the runtime observation.
        Assert.Equal("", Assert.Single(detector.GetKnownGames()).ExecutablePath);
    }

    /// <summary>
    /// A15: The built-in fallback record is identified by its stable generated built-in ID, so
    /// launching the same title from a second location reuses the one record instead of appending a
    /// duplicate. Without this the known-games list grew without bound across sessions.
    ///
    /// The first non-empty observed path is kept; a later observation does not rewrite it, so the
    /// record does not thrash between install locations.
    /// </summary>
    [Fact]
    public void OnProcessStarted_BuiltInFallbackFromDifferentDirectories_KeepsOneRecordByStableId()
    {
        var builtIn = BuiltInProfiles.ApexLegends();
        var exeName = builtIn.ProcessNames.First();
        var expectedId = GameInfo.GenerateId("builtin", builtIn.Id);
        var firstPath = Path.Combine(@"C:\Install A", exeName);
        var secondPath = Path.Combine(@"C:\Install B", exeName);

        using var detector = CreateDetector();
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(5300, firstPath));
        detector.OnProcessStarted(Started(5301, secondPath));

        Assert.Equal(2, events.Started.Count);
        Assert.All(events.Started, e => Assert.Equal(expectedId, e.GameId));

        // Each event still reports the path actually observed for its own PID.
        Assert.Equal(firstPath, events.Started[0].ExecutablePath);
        Assert.Equal(secondPath, events.Started[1].ExecutablePath);

        // ...while exactly one known-game record exists for that built-in ID.
        var record = Assert.Single(detector.GetKnownGames(), g => g.Id == expectedId);
        Assert.Equal(firstPath, record.ExecutablePath);
    }

    /// <summary>
    /// A16: javaw.exe is the Minecraft Java process name and simultaneously the process name of
    /// every other Java desktop application, so the name alone is not evidence of Minecraft. An
    /// unrelated JVM must not be claimed as a game - matching one optimizes the wrong process and
    /// triggers a premature revert when it exits.
    ///
    /// ACCEPTED LOSS: this removes standalone Minecraft Java auto-detection. Minecraft is still
    /// matched when a launcher scanner knows its install directory, when its exact executable path
    /// is known, or when it is added manually; an uncorrelated javaw.exe is not.
    /// </summary>
    [Fact]
    public void OnProcessStarted_AmbiguousBuiltInProcessName_DoesNotMatchUnrelatedProcess()
    {
        using var detector = CreateDetector();
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(Started(5400, @"C:\Program Files\Eclipse Adoptium\jdk-21\bin\javaw.exe"));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Empty(detector.GetKnownGames()); // no runtime built-in record appended either

        // The spawn feed is unaffected - it fires before game matching.
        var spawned = Assert.Single(events.Spawned);
        Assert.Equal("javaw.exe", spawned.ProcessName);
    }

    // ---------------------------------------------------------------------------------------
    // Temporary current-behavior characterizations
    //
    // The tests below pin down what current master DOES, not what it SHOULD do. They are here so a
    // later repair gate changes them deliberately and visibly. Each states the intended flip.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A21 - TEMPORARY CHARACTERIZATION of current behavior; this is not correct behavior.
    ///
    /// Removing a game only drops the launcher record. If the title also matches a built-in profile,
    /// the next launch is re-detected under the built-in identity, so removal does not hold.
    ///
    /// FUTURE FLIP: once removal records persistent suppression, this test must assert the title is
    /// NOT re-detected after removal.
    /// </summary>
    [Fact]
    public void RemoveKnownGame_ThenBuiltInTitleLaunches_IsRedetectedUnderBuiltInIdentity()
    {
        var builtIn = BuiltInProfiles.ApexLegends();
        var exeName = builtIn.ProcessNames.First();
        const string installDir = @"C:\SteamLibrary\steamapps\common\Apex Legends";

        var launcherGame = new GameInfo
        {
            Id = "steam_1172470",
            GameName = "Apex Legends",
            ExecutablePath = "",
            InstallDirectory = installDir,
            LauncherSource = "Steam",
            LauncherId = "1172470"
        };

        using var detector = CreateDetector(launcherGame);
        var events = new EventRecorder(detector);

        detector.RemoveKnownGame("steam_1172470");
        Assert.Empty(detector.GetKnownGames());

        detector.OnProcessStarted(Started(5500, Path.Combine(installDir, exeName)));

        // Current behavior: removal is not suppression - the title comes back as a built-in.
        var started = Assert.Single(events.Started);
        Assert.Equal(GameInfo.GenerateId("builtin", builtIn.Id), started.GameId);
        Assert.Equal("BuiltIn", started.LauncherSource);
    }
}
