using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Detection;

[Collection("ConfigState")]
public sealed class KnownGamesStoreTests : IDisposable
{
    private readonly TempPath _temp = new();
    private readonly string? _previousSettingsPath = SettingsManager.SettingsFilePathOverride;

    public KnownGamesStoreTests()
    {
        SettingsManager.SettingsFilePathOverride = _temp.GetFile("settings.json");
    }

    public void Dispose()
    {
        SettingsManager.SettingsFilePathOverride = _previousSettingsPath;
        _temp.Dispose();
    }

    [Fact]
    public void RemoveGame_LauncherGame_StaysHiddenAfterReloadAndScan()
    {
        var removed = new GameInfo { Id = "steam_1", GameName = "Removed", LauncherSource = "Steam" };
        var retained = new GameInfo { Id = "epic_2", GameName = "Retained", LauncherSource = "Epic" };
        var store = new KnownGamesStore();
        store.MergeScannedGames(new[] { removed, retained });

        Assert.True(store.RemoveGame(removed.Id));
        store.MergeScannedGames(new[] { removed, retained });
        Assert.Equal(retained.Id, Assert.Single(store.GetAllGames()).Id);
        Assert.Equal(new[] { removed.Id }, JsonSerializer.Deserialize<string[]>(
            File.ReadAllText(_temp.GetFile("ignored_games.json"))));

        var reloaded = new KnownGamesStore();
        reloaded.Load();
        reloaded.MergeScannedGames(new[] { removed, retained });

        Assert.Equal(retained.Id, Assert.Single(reloaded.GetAllGames()).Id);
        Assert.Equal(retained.Id, Assert.Single(JsonSerializer.Deserialize<List<GameInfo>>(
            File.ReadAllText(_temp.GetFile("known_games.json")))!).Id);
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Fact]
    public void RemoveGame_IgnoreSaveFails_ReturnsFalseAndRollsBackKnownGames()
    {
        var removed = new GameInfo { Id = "steam_1", LauncherSource = "Steam" };
        var retained = new GameInfo { Id = "epic_2", LauncherSource = "Epic" };
        var ignoredPath = _temp.GetFile("ignored_games.json");
        File.WriteAllText(ignoredPath, "[\"gog_3\"]");
        var store = new KnownGamesStore();
        store.Load();
        store.MergeScannedGames(new[] { removed, retained });
        var knownPath = _temp.GetFile("known_games.json");
        var originalKnown = File.ReadAllText(knownPath);
        var originalIgnored = File.ReadAllText(ignoredPath);

        using (var blocked = new FileStream(ignoredPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.RemoveGame(removed.Id));
        }

        Assert.Equal(new[] { removed.Id, retained.Id }, store.GetAllGames().Select(game => game.Id));
        Assert.Equal(originalKnown, File.ReadAllText(knownPath));
        Assert.Equal(originalIgnored, File.ReadAllText(ignoredPath));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));

        var reloaded = new KnownGamesStore();
        reloaded.Load();
        Assert.Equal(new[] { removed.Id, retained.Id }, reloaded.GetAllGames().Select(game => game.Id));
        Assert.True(store.RemoveGame(removed.Id));
        store.MergeScannedGames(new[] { removed, retained, new GameInfo { Id = "gog_3", LauncherSource = "GOG" } });
        Assert.Equal(retained.Id, Assert.Single(store.GetAllGames()).Id);
        Assert.Equal(new[] { "gog_3", removed.Id }, JsonSerializer.Deserialize<string[]>(File.ReadAllText(ignoredPath)));
    }

    [Fact]
    public void RemoveGame_ManualGame_DoesNotCreateIgnoreEntryAndCanBeAddedAgain()
    {
        var executablePath = _temp.GetFile("manual-game.exe");
        File.WriteAllText(executablePath, string.Empty);
        var store = new KnownGamesStore();
        var game = Assert.IsType<GameInfo>(store.AddManualGame(executablePath));

        Assert.True(store.RemoveGame(game.Id));

        Assert.Empty(store.GetAllGames());
        Assert.False(File.Exists(_temp.GetFile("ignored_games.json")));
        var reloaded = new KnownGamesStore();
        reloaded.Load();
        Assert.Empty(reloaded.GetAllGames());
        Assert.Equal(game.Id, Assert.IsType<GameInfo>(reloaded.AddManualGame(executablePath)).Id);
        Assert.False(File.Exists(_temp.GetFile("ignored_games.json")));
    }

    [Theory]
    [InlineData("known_games.json")]
    [InlineData("ignored_games.json")]
    public void OrchestratorRemoveGame_StoreSaveFails_LeavesDetectorUntouched(string blockedFile)
    {
        var game = new GameInfo { Id = "steam_1", LauncherSource = "Steam" };
        var store = new KnownGamesStore();
        store.MergeScannedGames(new[] { game });
        File.WriteAllText(_temp.GetFile("ignored_games.json"), "[]");
        using var detector = new GameDetector(Array.Empty<ILibraryScanner>());
        detector.AddKnownGame(game);
        using var engine = new OptimizationEngine(Array.Empty<IOptimization>());
        var orchestrator = new DetectionOrchestrator(
            detector, engine, store, Array.Empty<ILibraryScanner>(), new ProfileManager());
        try
        {
            using (var blocked = new FileStream(_temp.GetFile(blockedFile), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(orchestrator.RemoveGame(game.Id));
                Assert.Same(game, Assert.Single(detector.GetKnownGames()));
                Assert.Same(game, Assert.Single(store.GetAllGames()));
            }

            Assert.True(orchestrator.RemoveGame(game.Id));
            Assert.Empty(detector.GetKnownGames());
            Assert.Empty(store.GetAllGames());
        }
        finally
        {
            orchestrator.Cleanup();
        }
    }

    [Fact]
    public void MergeScannedGames_ReplacesFileWithoutChangingAnOpenReaderSnapshot()
    {
        var original = new GameInfo { Id = "steam_1", GameName = "Before", LauncherSource = "Steam" };
        var store = new KnownGamesStore();
        store.MergeScannedGames(new[] { original });
        var knownPath = _temp.GetFile("known_games.json");
        var originalJson = File.ReadAllText(knownPath);
        using var snapshot = new FileStream(knownPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        store.MergeScannedGames(new[]
        {
            new GameInfo { Id = original.Id, GameName = "After", LauncherSource = "Steam" }
        });

        Assert.Equal("After", Assert.Single(JsonSerializer.Deserialize<List<GameInfo>>(
            File.ReadAllText(knownPath))!).GameName);
        using var reader = new StreamReader(snapshot);
        Assert.Equal(originalJson, reader.ReadToEnd());
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }
}
