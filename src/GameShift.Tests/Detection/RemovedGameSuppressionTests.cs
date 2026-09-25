using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.GameProfiles;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Detection;

/// <summary>
/// Removal through <see cref="DetectionOrchestrator"/>: a removed launcher game must not come back
/// through a built-in profile, in the same session or after a restart. Runs against an isolated
/// app-data root, never the real %AppData%\GameShift store.
/// </summary>
[Collection("ConfigState")]
public sealed class RemovedGameSuppressionTests : IDisposable
{
    private const string InstallDir = @"D:\SteamLibrary\steamapps\common\Apex Legends";

    private static readonly string ApexExe =
        Path.Combine(InstallDir, BuiltInProfiles.ApexLegends().ProcessNames.First());

    private readonly TempPath _temp = new();
    private readonly string? _previousSettingsPath = SettingsManager.SettingsFilePathOverride;

    public RemovedGameSuppressionTests()
    {
        SettingsManager.SettingsFilePathOverride = _temp.GetFile("settings.json");
    }

    public void Dispose()
    {
        SettingsManager.SettingsFilePathOverride = _previousSettingsPath;
        _temp.Dispose();
    }

    private static GameInfo SteamApex() => new()
    {
        Id = "steam_1172470",
        GameName = "Apex Legends",
        ExecutablePath = "",
        InstallDirectory = InstallDir,
        LauncherSource = "Steam",
        LauncherId = "1172470"
    };

    private static ProcessStartEventData Started(int processId, string path) => new()
    {
        ProcessId = processId,
        ImageFileName = path,
        ParentProcessId = 0,
        Timestamp = DateTime.UtcNow
    };

    /// <summary>
    /// Starts a library the way the app does (store loaded from disk, launcher scanned, detector
    /// synced) without starting process monitoring, then runs <paramref name="test"/>.
    /// </summary>
    private static void WithLibrary(
        GameInfo[] scannedGames,
        Action<DetectionOrchestrator, GameDetector, List<GameDetectedEventArgs>> test)
    {
        var scanners = new ILibraryScanner[] { new FakeLibraryScanner("Steam", true, scannedGames) };
        using var detector = new GameDetector(scanners);
        using var engine = new OptimizationEngine(Array.Empty<IOptimization>());
        var orchestrator = new DetectionOrchestrator(
            detector, engine, new KnownGamesStore(), scanners, new ProfileManager());
        var started = new List<GameDetectedEventArgs>();
        detector.GameStarted += (_, e) => started.Add(e);
        try
        {
            orchestrator.LoadAndSyncLibrary();
            test(orchestrator, detector, started);
        }
        finally
        {
            orchestrator.Cleanup();
        }
    }

    [Fact]
    public void RemoveGame_LauncherGame_IsNotRedetectedThroughItsBuiltInProfile()
    {
        WithLibrary(new[] { SteamApex() }, (orchestrator, detector, started) =>
        {
            detector.OnProcessStarted(Started(9000, ApexExe));
            Assert.Equal("steam_1172470", Assert.Single(started).GameId);

            Assert.True(orchestrator.RemoveGame("steam_1172470"));
            detector.OnProcessStarted(Started(9001, ApexExe));

            Assert.Single(started);
        });
    }

    [Fact]
    public void LoadAndSyncLibrary_AfterRestart_RemovedGameStaysSuppressed()
    {
        WithLibrary(new[] { SteamApex() }, (orchestrator, _, _) =>
            Assert.True(orchestrator.RemoveGame("steam_1172470")));

        // The launcher still reports the game; a new session must keep it out and suppressed.
        WithLibrary(new[] { SteamApex() }, (orchestrator, detector, started) =>
        {
            Assert.Empty(orchestrator.GetKnownGames());
            detector.OnProcessStarted(Started(9100, ApexExe));
            Assert.Empty(started);
        });
    }

    [Fact]
    public void RemoveGame_ManualGame_DoesNotSuppressItsFolder()
    {
        var folder = Directory.CreateDirectory(_temp.GetFile("Apex")).FullName;
        var exe = Path.Combine(folder, "r5apex.exe");
        File.WriteAllText(exe, "placeholder - not an executable");

        WithLibrary(Array.Empty<GameInfo>(), (orchestrator, detector, started) =>
        {
            var manual = Assert.IsType<GameInfo>(orchestrator.AddManualGame(exe));
            Assert.True(orchestrator.RemoveGame(manual.Id));

            detector.OnProcessStarted(Started(9200, exe));

            Assert.Equal(
                GameInfo.GenerateId("builtin", BuiltInProfiles.ApexLegends().Id),
                Assert.Single(started).GameId);
        });
    }
}
