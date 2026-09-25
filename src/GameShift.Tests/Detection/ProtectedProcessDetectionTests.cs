using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameShift.Core.Detection;
using GameShift.Core.GameProfiles;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Detection;

/// <summary>
/// Detector-level contract suite for the protected-process detection repair.
///
/// A kernel-anti-cheat game (Apex/EAC) can produce a process start whose image path GameShift is
/// not allowed to read. The old code resolved such starts through <c>Process.MainModule</c>, which
/// cannot distinguish "already exited" from "alive but unreadable", so a dead or unproven process
/// could register a game and apply optimizations - and a bare filename could be emitted through the
/// event's path field.
///
/// These tests drive the real production handlers with synthetic events and a scripted
/// <see cref="IProcessProbeBackend"/>, so the dead/unreadable/unknown branches are exercised without
/// starting monitoring, enumerating the process table, or running a real game.
/// </summary>
public class ProtectedProcessDetectionTests
{
    private const int ErrorAccessDenied = 5;

    /// <summary>Apex process name used throughout; it is one of Apex's real built-in names.</summary>
    private const string ApexProcessName = "r5apex.exe";

    /// <summary>The name the live process table reports for an Apex process (no extension).</summary>
    private static string ApexLiveName => Path.GetFileNameWithoutExtension(ApexProcessName);

    private static string ApexBuiltInId => GameInfo.GenerateId("builtin", BuiltInProfiles.ApexLegends().Id);

    /// <summary>
    /// Live process-name probe stand-in: reports <paramref name="processName"/> for every PID, so a
    /// test states outright what the PID's current occupant is called. This is the seam the
    /// name-only corroboration step reads; it is NOT the sweep's probe, which stays on the real
    /// process table.
    /// </summary>
    private static Func<int, string> LiveNameIs(string processName) => _ => processName;

    /// <summary>
    /// Default live-name probe for tests that must never reach the corroboration step. It raises the
    /// same "no such process" signal the real lookup does, so a test that unexpectedly DOES reach it
    /// fails closed instead of passing on a fabricated name.
    /// </summary>
    private static readonly Func<int, string> NoLiveProcess =
        _ => throw new ArgumentException("no process with that ID");

    private static GameDetector CreateDetector(ScriptedProcessProbeBackend backend, params GameInfo[] knownGames) =>
        CreateDetector(backend, NoLiveProcess, knownGames);

    private static GameDetector CreateDetector(
        ScriptedProcessProbeBackend backend,
        Func<int, string> liveProcessNameProbe,
        params GameInfo[] knownGames)
    {
        var detector = new GameDetector(
            Array.Empty<ILibraryScanner>(),
            new ProcessProbe(backend),
            liveProcessNameProbe);
        foreach (var game in knownGames)
            detector.AddKnownGame(game);
        return detector;
    }

    /// <summary>A WMI-shaped start event: filename only, no path. This is the repaired route.</summary>
    private static ProcessStartEventData PathlessStart(int processId, string fileName)
    {
        Assert.False(Path.IsPathRooted(fileName), "Pathless start events must carry a bare filename.");
        return new ProcessStartEventData
        {
            ProcessId = processId,
            ImageFileName = fileName,
            ParentProcessId = 0,
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>An ETW-shaped start event: full rooted image path.</summary>
    private static ProcessStartEventData RootedStart(int processId, string rootedPath)
    {
        Assert.True(Path.IsPathRooted(rootedPath), "Rooted start events must carry a full path.");
        return new ProcessStartEventData
        {
            ProcessId = processId,
            ImageFileName = rootedPath,
            ParentProcessId = 0,
            Timestamp = DateTime.UtcNow
        };
    }

    private static ProcessStopEventData Stopped(int processId) => new()
    {
        ProcessId = processId,
        Timestamp = DateTime.UtcNow
    };

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

    /// <summary>Backend script for "alive, but the image path cannot be read".</summary>
    private static ScriptedProcessProbeBackend AliveNoPathBackend() =>
        new ScriptedProcessProbeBackend()
            .OpenSucceeds()
            .WaitReturns(NativeInterop.WAIT_TIMEOUT)
            .PathQueryReturns(null);

    // ---------------------------------------------------------------------------------------
    // Dead / unproven starts must never register anything
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Contract 6: a pathless start whose PID is already dead must not register a game. This is the
    /// core failure the repair exists to prevent - a process that exited between the start event and
    /// the resolution attempt would previously still have been adopted by name.
    /// </summary>
    [Fact]
    public void OnProcessStarted_PathlessGoneProcess_RegistersNothing()
    {
        var backend = new ScriptedProcessProbeBackend()
            .OpenSucceeds()
            .WaitReturns(NativeInterop.WAIT_OBJECT_0);

        using var detector = CreateDetector(backend);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(6100, ApexProcessName));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Empty(detector.GetKnownGames()); // no built-in record created either

        // The shared spawn feed is upstream of matching and still fires.
        Assert.Equal(ApexProcessName, Assert.Single(events.Spawned).ProcessName);
    }

    /// <summary>
    /// Contract 7: an unclassifiable pathless start registers nothing. Uncertainty must never be
    /// promoted to liveness: an access-denied or transient observation says nothing about whether
    /// the process is running, so adopting it would optimize for a process that may not exist.
    /// </summary>
    [Fact]
    public void OnProcessStarted_PathlessUnknownProcess_RegistersNothing()
    {
        var backend = new ScriptedProcessProbeBackend()
            .OpenFails(ErrorAccessDenied)
            .ExistenceIs(PidExistence.Unknown);

        using var detector = CreateDetector(backend);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(6101, ApexProcessName));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Empty(detector.GetKnownGames());
    }

    // ---------------------------------------------------------------------------------------
    // The narrow name-only fallback
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Contract 8: a pathless start whose PID is independently PROVEN alive may be matched on the
    /// event's process name alone - but only for a built-in profile that opted in. Apex is the only
    /// one, because both of its process names are exclusive to it.
    /// </summary>
    [Fact]
    public void OnProcessStarted_ApexPathlessAliveNoPath_MatchesBuiltInApex()
    {
        using var detector = CreateDetector(AliveNoPathBackend(), LiveNameIs(ApexLiveName));
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(6200, ApexProcessName));

        var started = Assert.Single(events.Started);
        Assert.Equal(ApexBuiltInId, started.GameId);
        Assert.Equal(BuiltInProfiles.ApexLegends().DisplayName, started.GameName);
        Assert.Equal("BuiltIn", started.LauncherSource);
        Assert.Equal(6200, started.ProcessId);

        var active = Assert.Single(detector.GetActiveGames());
        Assert.Equal(ApexBuiltInId, active.Value.Id);
    }

    /// <summary>
    /// Contract 9: the event emitted for a name-only match tells the truth about what was observed.
    /// The path is EMPTY, never the filename: path-consuming subscribers (Defender exclusions, IFEO
    /// image targeting, profile ExecutablePath backfill) treat "" as their fail-closed guard, and a
    /// filename smuggled through the path field would defeat it. The name travels in ProcessName.
    /// </summary>
    [Fact]
    public void OnProcessStarted_ApexPathlessAliveNoPath_EmitsEmptyPathAndObservedProcessName()
    {
        using var detector = CreateDetector(AliveNoPathBackend(), LiveNameIs(ApexLiveName));
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(6201, ApexProcessName));

        var started = Assert.Single(events.Started);
        Assert.Equal(string.Empty, started.ExecutablePath);
        Assert.Equal(ApexProcessName, started.ProcessName);

        // The runtime built-in record carries no fabricated location either.
        var record = Assert.Single(detector.GetKnownGames());
        Assert.Equal(ApexBuiltInId, record.Id);
        Assert.Equal("", record.ExecutablePath);
        Assert.Equal("", record.InstallDirectory);
    }

    /// <summary>
    /// Contract 9b: a kernel short name can arrive truncated (r5apex_dx12.exe as r5apex_dx12.ex).
    /// The match and the recorded evidence come from the live process-table name, so the DX12
    /// client is still recognised and the liveness sweep later compares like with like.
    /// </summary>
    [Fact]
    public void OnProcessStarted_TruncatedEventName_MatchesApexOnLiveName()
    {
        using var detector = CreateDetector(AliveNoPathBackend(), LiveNameIs("r5apex_dx12"));
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(6202, "r5apex_dx12.ex"));

        var started = Assert.Single(events.Started);
        Assert.Equal(ApexBuiltInId, started.GameId);
        Assert.Equal("r5apex_dx12.exe", started.ProcessName);

        var record = Assert.Single(detector.GetKnownGames());
        var active = new ActiveGame(record, started.ProcessName, null);
        Assert.False(GameDetector.IsTrackedGameGone(6202, active, LiveNameIs("r5apex_dx12")));
    }

    /// <summary>
    /// Contract 9c: the name-only route has no path to test against a removed game's folder, so it
    /// uses the evidence there is: a removed launcher game's folder that holds an executable of that
    /// name suppresses the match. A removed folder without one does not.
    /// </summary>
    [Fact]
    public void OnProcessStarted_NameOnly_SuppressedOnlyWhenARemovedFolderHoldsTheExecutable()
    {
        using var removedApex = new TempPath();
        File.WriteAllText(removedApex.GetFile(ApexProcessName), "placeholder - not an executable");
        using var removedOther = new TempPath();

        using var suppressed = CreateDetector(AliveNoPathBackend(), LiveNameIs(ApexLiveName));
        suppressed.SuppressInstallDirectory(removedApex.Path);
        var suppressedEvents = new EventRecorder(suppressed);
        suppressed.OnProcessStarted(PathlessStart(6300, ApexProcessName));
        Assert.Empty(suppressedEvents.Started);

        using var unaffected = CreateDetector(AliveNoPathBackend(), LiveNameIs(ApexLiveName));
        unaffected.SuppressInstallDirectory(removedOther.Path);
        var events = new EventRecorder(unaffected);
        unaffected.OnProcessStarted(PathlessStart(6301, ApexProcessName));
        Assert.Equal(ApexBuiltInId, Assert.Single(events.Started).GameId);
    }

    /// <summary>
    /// Contract 10: a name-only match still gets full liveness reconciliation, because the observed
    /// process name is recorded as runtime evidence. When the PID later belongs to a different
    /// image, the sweep detects the reuse and releases the game so optimizations revert.
    ///
    /// Uses this test process's own PID: its real image name is not r5apex.exe, which is exactly the
    /// PID-reuse shape.
    /// </summary>
    [Fact]
    public void ReconcileActiveGamesOnce_NameOnlyMatch_DetectsPidReuseFromObservedName()
    {
        // The registration-time corroboration is satisfied; the SWEEP still uses the real process
        // table, where this PID is the test host and not r5apex - which is the reuse shape.
        using var detector = CreateDetector(AliveNoPathBackend(), LiveNameIs(ApexLiveName));
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(Environment.ProcessId, ApexProcessName));
        Assert.Single(detector.GetActiveGames());

        detector.ReconcileActiveGamesOnce();

        var stopped = Assert.Single(events.Stopped);
        Assert.Equal(ApexBuiltInId, stopped.GameId);
        Assert.Equal(Environment.ProcessId, stopped.ProcessId);
        Assert.Equal(1, events.AllStoppedCount);
        Assert.Empty(detector.GetActiveGames());

        // The stop event reports the same runtime evidence the start event did.
        Assert.Equal(string.Empty, stopped.ExecutablePath);
        Assert.Equal(ApexProcessName, stopped.ProcessName);
    }

    /// <summary>
    /// Contract 11: the startup rundown has no live start event behind it, so it has no trustworthy
    /// observed process name - only a snapshot name that may already be stale. An alive-but-
    /// unreadable process must therefore be skipped there, even for an opted-in profile.
    /// </summary>
    [Fact]
    public void ScanRunningProcess_AliveNoPath_DoesNotNameOnlyMatch()
    {
        using var detector = CreateDetector(AliveNoPathBackend());
        var events = new EventRecorder(detector);

        Assert.Null(detector.ScanRunningProcess(6300));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Empty(detector.GetKnownGames());
    }

    /// <summary>
    /// Contract 12: identity is never bridged by display name. A launcher or manual entry titled
    /// "Apex Legends" is a different identity from the built-in profile, so a name-only built-in
    /// match must not adopt it (which would attach the wrong profile and the wrong launcher source).
    /// </summary>
    [Fact]
    public void OnProcessStarted_NameOnlyMatch_DoesNotAdoptSameDisplayNameLauncherOrManualRecord()
    {
        var launcherGame = new GameInfo
        {
            Id = "steam_1172470",
            GameName = "Apex Legends",
            ExecutablePath = "",
            InstallDirectory = @"C:\SteamLibrary\steamapps\common\Apex Legends",
            LauncherSource = "Steam",
            LauncherId = "1172470"
        };
        var manualGame = new GameInfo
        {
            Id = GameInfo.GenerateId("manual", "Apex Legends"),
            GameName = "Apex Legends",
            ExecutablePath = @"D:\Elsewhere\Apex\r5apex.exe",
            InstallDirectory = @"D:\Elsewhere\Apex",
            LauncherSource = "Manual"
        };

        using var detector = CreateDetector(AliveNoPathBackend(), LiveNameIs(ApexLiveName), launcherGame, manualGame);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(6400, ApexProcessName));

        var started = Assert.Single(events.Started);
        Assert.Equal(ApexBuiltInId, started.GameId);
        Assert.Equal("BuiltIn", started.LauncherSource);

        // All three identities coexist; neither pre-existing record was mutated or absorbed.
        Assert.Equal(3, detector.GetKnownGames().Count);
        Assert.Equal("", Assert.Single(detector.GetKnownGames(), g => g.Id == "steam_1172470").ExecutablePath);
        Assert.Equal(
            @"D:\Elsewhere\Apex\r5apex.exe",
            Assert.Single(detector.GetKnownGames(), g => g.Id == manualGame.Id).ExecutablePath);
    }

    /// <summary>
    /// Contract 15: javaw.exe produces no pathless detection under current production data, even
    /// when the process is proven alive and its name corroborated. javaw.exe is shared by every
    /// Java application, so proving that SOME process is alive says nothing about which program it
    /// is.
    ///
    /// SCOPE OF THIS TEST - it does NOT uniquely pin the pathless ambiguity branch. Minecraft is not
    /// opted into the name-only fallback, so removing the AmbiguousProcessNames check from
    /// MatchProcessByNameOnly would leave this outcome unchanged (no eligible profile claims the
    /// name). What is genuinely pinned here is the end-to-end outcome plus the data assertion that
    /// "javaw.exe" is on the ambiguous list. The DISCRIMINATING test for the ambiguity block is the
    /// readable-route A16 in GameDetectorMatchingTests, which fails if the check is removed.
    /// </summary>
    [Fact]
    public void OnProcessStarted_PathlessJavawStart_RegistersNothing()
    {
        Assert.Contains("javaw.exe", GameDetector.AmbiguousProcessNames);

        // Corroboration succeeds, so the rejection is reached through the name-only gate itself
        // rather than short-circuited earlier.
        using var detector = CreateDetector(AliveNoPathBackend(), LiveNameIs("javaw"));
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(6500, "javaw.exe"));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Empty(detector.GetKnownGames());
    }

    /// <summary>
    /// Contract 21: a helper-shaped pathless start (platform stub, anti-cheat launcher, crash
    /// reporter) produces no detection, even when the process is proven alive and its name
    /// corroborated. The non-game-helper filter is applied on this route as well.
    ///
    /// SCOPE OF THIS TEST - it does NOT uniquely pin the helper-filter branch. None of these names
    /// belongs to any built-in profile's ProcessNames, and no name-only-eligible built-in uses a
    /// helper name, so removing the IsNonGameHelper check from MatchProcessByNameOnly would leave
    /// this outcome unchanged. What is pinned is the current fail-closed outcome for helper-like
    /// pathless events. Pinning the branch itself would require a synthetic eligible profile, which
    /// would mean weakening the production rule that eligibility is read from BuiltInProfiles only.
    /// </summary>
    [Theory]
    [InlineData("start_protected_game.exe")]
    [InlineData("EasyAntiCheat.exe")]
    [InlineData("crashpad_handler.exe")]
    public void OnProcessStarted_PathlessHelperName_RegistersNothing(string helperExeName)
    {
        // Corroboration succeeds, so the rejection is reached through the name-only gate itself.
        using var detector = CreateDetector(
            AliveNoPathBackend(),
            LiveNameIs(Path.GetFileNameWithoutExtension(helperExeName)));
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(6600, helperExeName));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Empty(detector.GetKnownGames());
    }

    /// <summary>
    /// Contract 27: the AllowNameOnlyFallback opt-in is what confines the name-only route to Apex,
    /// and this is the test that pins that code path.
    ///
    /// Overwatch.exe is a real built-in game process name, unique among built-ins, not a helper and
    /// not on the ambiguous list - so with the process proven alive and its name corroborated, the
    /// ONLY thing left stopping a name-only match is that its profile did not opt in. Deleting the
    /// AllowNameOnlyFallback check from MatchProcessByNameOnly makes this test fail.
    ///
    /// Contracts 19 and 20 pin the DATA (Apex is the sole opt-in; eligible names are unambiguous).
    /// This pins the CODE that reads it.
    /// </summary>
    [Fact]
    public void OnProcessStarted_PathlessNonOptedInBuiltInName_RegistersNothing()
    {
        const string notOptedIn = "Overwatch.exe";

        // Guard the premise. If the profile data ever changed, this test would silently stop
        // discriminating, so assert exactly what makes it a valid probe of the opt-in gate.
        var claimant = Assert.Single(
            BuiltInProfiles.GetAll(),
            p => p.ProcessNames.Contains(notOptedIn, StringComparer.OrdinalIgnoreCase));
        Assert.False(claimant.AllowNameOnlyFallback);
        Assert.False(GameDetector.AmbiguousProcessNames.Contains(notOptedIn));

        using var detector = CreateDetector(
            AliveNoPathBackend(),
            LiveNameIs(Path.GetFileNameWithoutExtension(notOptedIn)));
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(7300, notOptedIn));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Empty(detector.GetKnownGames());
    }

    /// <summary>
    /// Contract 28: the probe proves liveness for whatever process owns the PID at probe time, not
    /// for the process the start event described. If the event's process exited and the PID was
    /// reused before the probe ran, the event's name describes a dead process while the liveness
    /// belongs to the new occupant, so the name must not be admitted as identity.
    ///
    /// This is the exact shape that would otherwise register: probe says AliveNoPath, name is Apex's.
    /// Removing the corroboration step makes this test fail.
    /// </summary>
    [Fact]
    public void OnProcessStarted_PathlessAliveNoPathButPidNowRunsAnotherImage_RegistersNothing()
    {
        using var detector = CreateDetector(AliveNoPathBackend(), LiveNameIs("notepad"));
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(7400, ApexProcessName));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Empty(detector.GetKnownGames());

        // The shared spawn feed is upstream of matching and still fires.
        Assert.Equal(ApexProcessName, Assert.Single(events.Spawned).ProcessName);
    }

    /// <summary>
    /// Contract 29: an unreadable live name is uncertainty, not corroboration. When the PID's
    /// current process name cannot be obtained at all - the PID vanished, or the lookup failed for
    /// any other reason - the name-only route fails closed instead of falling back on the event's
    /// unverified name.
    /// </summary>
    [Theory]
    [InlineData(true)]  // ArgumentException: the documented "no process with that ID" signal
    [InlineData(false)] // anything else the lookup can raise
    public void OnProcessStarted_PathlessAliveNoPathButLiveNameUnreadable_RegistersNothing(bool pidVanished)
    {
        static string Vanished(int _) => throw new ArgumentException("no process with that ID");
        static string Transient(int _) => throw new InvalidOperationException("transient lookup failure");

        Func<int, string> failingProbe = pidVanished ? new Func<int, string>(Vanished) : Transient;

        using var detector = CreateDetector(AliveNoPathBackend(), failingProbe);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(7500, ApexProcessName));

        Assert.Empty(events.Started);
        Assert.Empty(detector.GetActiveGames());
        Assert.Empty(detector.GetKnownGames());
    }

    // ---------------------------------------------------------------------------------------
    // Name-only eligibility is a security decision, not a preference
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Contract 19: Apex is the ONLY built-in profile allowed to match on name alone. Widening this
    /// set is what would turn the fallback from a targeted repair into a general false-positive
    /// source, so the restriction is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void BuiltInProfiles_OnlyApexOptsIntoNameOnlyFallback()
    {
        var eligible = BuiltInProfiles.GetAll().Where(p => p.AllowNameOnlyFallback).ToList();

        var only = Assert.Single(eligible);
        Assert.Equal(BuiltInProfiles.ApexLegends().Id, only.Id);
        Assert.True(BuiltInProfiles.ApexLegends().AllowNameOnlyFallback);
    }

    /// <summary>
    /// Contract 20: every process name that can be matched on name alone must be usable as an
    /// identity by itself - non-empty, not on the ambiguous list, and not shared with any other
    /// built-in profile. A shared name would make the gate resolve to the wrong game (or, for two
    /// eligible profiles, fail closed and silently detect nothing).
    /// </summary>
    [Fact]
    public void BuiltInProfiles_NameOnlyEligibleProcessNamesAreUnambiguous()
    {
        var all = BuiltInProfiles.GetAll();
        var eligible = all.Where(p => p.AllowNameOnlyFallback).ToList();
        Assert.NotEmpty(eligible);

        foreach (var profile in eligible)
        {
            Assert.NotEmpty(profile.ProcessNames);

            foreach (var name in profile.ProcessNames)
            {
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.False(
                    GameDetector.AmbiguousProcessNames.Contains(name),
                    $"{name} is name-only eligible but also listed as ambiguous.");

                // Not claimed by any other built-in profile, eligible or not.
                var otherClaimants = all
                    .Where(p => p.Id != profile.Id)
                    .Where(p => p.ProcessNames.Any(pn => string.Equals(pn, name, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Id)
                    .ToList();

                Assert.Empty(otherClaimants);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Readable observations and the event contract
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Contract 17: a readable (ETW) start still reports the full rooted path AND a populated
    /// process name. The readable route is untouched by this repair and must not consult the probe
    /// at all - the scripted backend here is empty, so any probe call would break the match.
    /// </summary>
    [Fact]
    public void OnProcessStarted_ReadableStart_EmitsRootedPathAndProcessName()
    {
        var backend = new ScriptedProcessProbeBackend();
        var game = new GameInfo
        {
            Id = "steam_620",
            GameName = "Portal 2",
            ExecutablePath = "",
            InstallDirectory = @"C:\SteamLibrary\steamapps\common\Portal 2",
            LauncherSource = "Steam",
            LauncherId = "620"
        };

        using var detector = CreateDetector(backend, game);
        var events = new EventRecorder(detector);

        const string runningPath = @"C:\SteamLibrary\steamapps\common\Portal 2\bin\portal2.exe";
        detector.OnProcessStarted(RootedStart(6700, runningPath));

        var started = Assert.Single(events.Started);
        Assert.Equal(runningPath, started.ExecutablePath);
        Assert.True(Path.IsPathRooted(started.ExecutablePath));
        Assert.Equal("portal2.exe", started.ProcessName);

        Assert.Empty(backend.RequestedAccess); // readable route never probes
    }

    /// <summary>
    /// Contract 18: a real launcher match still outranks the built-in name fallback. The built-in
    /// identity is a last resort, so a title the launcher already knows keeps the launcher's stable
    /// identity (and therefore its saved profile) even though its executable name is Apex's.
    /// </summary>
    [Fact]
    public void OnProcessStarted_ReadableInstallDirectoryMatch_BeatsApexBuiltInIdentity()
    {
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

        using var detector = CreateDetector(new ScriptedProcessProbeBackend(), launcherGame);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(RootedStart(6800, Path.Combine(installDir, ApexProcessName)));

        var started = Assert.Single(events.Started);
        Assert.Equal("steam_1172470", started.GameId);
        Assert.Equal("Steam", started.LauncherSource);
        Assert.Equal(ApexProcessName, started.ProcessName);

        // No competing built-in record was created.
        Assert.Equal("steam_1172470", Assert.Single(detector.GetKnownGames()).Id);
    }

    /// <summary>
    /// Contract 22: the active-game view stays a projection of stable game identities, and it is a
    /// snapshot - a caller iterating it cannot be disturbed by a concurrent process stop, and a held
    /// reference does not silently keep reporting a game that has exited.
    /// </summary>
    [Fact]
    public void GetActiveGames_ReturnsStableIdentitiesAndIsASnapshot()
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

        using var detector = CreateDetector(new ScriptedProcessProbeBackend(), game);

        detector.OnProcessStarted(RootedStart(6900, @"C:\SteamLibrary\steamapps\common\CS2\game\bin\cs2.exe"));

        var snapshot = detector.GetActiveGames();
        var tracked = Assert.Single(snapshot);
        Assert.Equal(6900, tracked.Key);
        Assert.Equal("steam_730", tracked.Value.Id);
        Assert.Same(game, tracked.Value); // the scanned identity itself, not a runtime copy

        detector.OnProcessStopped(Stopped(6900));

        Assert.Single(snapshot);              // the earlier snapshot is unaffected
        Assert.Empty(detector.GetActiveGames()); // a fresh read reflects the exit
    }

    // ---------------------------------------------------------------------------------------
    // Built-in record path handling
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Contract 23: an unreadable observation must never erase a real path that a previous readable
    /// observation established. Losing it would break install-directory matching and any action that
    /// needs the game's real location.
    /// </summary>
    [Fact]
    public void BuiltInRecord_UnreadableObservationAfterReadable_KeepsKnownPath()
    {
        const string realPath = @"C:\Origin Games\Apex\r5apex.exe";

        using var detector = CreateDetector(AliveNoPathBackend(), LiveNameIs(ApexLiveName));
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(RootedStart(7000, realPath));   // readable first
        detector.OnProcessStarted(PathlessStart(7001, ApexProcessName)); // then unreadable

        Assert.Equal(2, events.Started.Count);

        var record = Assert.Single(detector.GetKnownGames());
        Assert.Equal(ApexBuiltInId, record.Id);
        Assert.Equal(realPath, record.ExecutablePath);
        Assert.Equal(@"C:\Origin Games\Apex", record.InstallDirectory);

        // The unreadable observation is still reported truthfully on its own event.
        Assert.Equal(string.Empty, events.Started[1].ExecutablePath);
        Assert.Equal(ApexProcessName, events.Started[1].ProcessName);
    }

    /// <summary>
    /// Contract 24: the reverse order fills the gap. A record created from an unreadable observation
    /// starts with no location; the first readable observation supplies one, so later launches can be
    /// matched by install directory instead of relying on the fallback again.
    /// </summary>
    [Fact]
    public void BuiltInRecord_ReadableObservationAfterUnreadable_FillsEmptyPath()
    {
        const string realPath = @"C:\Origin Games\Apex\r5apex.exe";

        using var detector = CreateDetector(AliveNoPathBackend(), LiveNameIs(ApexLiveName));

        detector.OnProcessStarted(PathlessStart(7100, ApexProcessName)); // unreadable first
        var beforeFill = Assert.Single(detector.GetKnownGames());
        Assert.Equal("", beforeFill.ExecutablePath);

        detector.OnProcessStarted(RootedStart(7101, realPath)); // then readable

        var record = Assert.Single(detector.GetKnownGames());
        Assert.Equal(ApexBuiltInId, record.Id);
        Assert.Equal(realPath, record.ExecutablePath);
        Assert.Equal(@"C:\Origin Games\Apex", record.InstallDirectory);
    }

    /// <summary>
    /// A pathless start whose PID resolves to a real rooted path takes the ordinary readable route:
    /// the probe upgraded the observation, so full path matching applies and no name-only
    /// concession is needed.
    /// </summary>
    [Fact]
    public void OnProcessStarted_PathlessButResolvable_MatchesOnResolvedPath()
    {
        const string installDir = @"C:\SteamLibrary\steamapps\common\Portal 2";
        var game = new GameInfo
        {
            Id = "steam_620",
            GameName = "Portal 2",
            ExecutablePath = "",
            InstallDirectory = installDir,
            LauncherSource = "Steam",
            LauncherId = "620"
        };
        var runningPath = Path.Combine(installDir, "portal2.exe");

        var backend = new ScriptedProcessProbeBackend()
            .OpenSucceeds()
            .WaitReturns(NativeInterop.WAIT_TIMEOUT)
            .PathQueryReturns(runningPath);

        using var detector = CreateDetector(backend, game);
        var events = new EventRecorder(detector);

        detector.OnProcessStarted(PathlessStart(7200, "portal2.exe"));

        var started = Assert.Single(events.Started);
        Assert.Equal("steam_620", started.GameId);
        Assert.Equal(runningPath, started.ExecutablePath);
        Assert.Equal("portal2.exe", started.ProcessName);
    }

    // ---------------------------------------------------------------------------------------
    // Runtime-evidence invariants
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Contract 30: a tracked game cannot exist without the process name observed for its PID.
    ///
    /// That name is the only thing the liveness sweep compares against, so an empty one would make
    /// IsTrackedGameGone answer "still alive" forever: a missed stop event would then keep the game -
    /// and its optimizations - tracked for the rest of the session, which is precisely the leak A13
    /// closed. The invariant is enforced at construction so that no call site, present or future,
    /// can create that state.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ActiveGame_WithoutObservedProcessName_CannotBeConstructed(string? observedProcessName)
    {
        var game = new GameInfo
        {
            Id = "steam_1",
            GameName = "Anything",
            InstallDirectory = @"C:\Games\Anything",
            LauncherSource = "Steam"
        };

        Assert.Throws<ArgumentException>(
            () => new ActiveGame(game, observedProcessName!, @"C:\Games\Anything\anything.exe"));
    }
}
