using System.IO;
using System.Linq;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Detection;

/// <summary>
/// Regression harness for <see cref="KnownGamesStore"/>: the persistent identity list that survives
/// restarts and decides which titles GameShift will optimize. These tests run against an isolated
/// app-data root via the internal <see cref="SettingsManager.SettingsFilePathOverride"/>, so they are
/// serialized with the other config-state tests and never touch the real %AppData%\GameShift store.
/// </summary>
[Collection("ConfigState")]
public class KnownGamesStoreTests
{
    private static GameInfo ScannerGame(string id, string name, string installDir, string launcher = "Steam") => new()
    {
        Id = id,
        GameName = name,
        ExecutablePath = "",
        InstallDirectory = installDir,
        LauncherSource = launcher,
        LauncherId = id
    };

    private static string StoreFile(TempPath temp) => temp.GetFile("known_games.json");

    /// <summary>
    /// Creates a harmless placeholder file so <see cref="KnownGamesStore.AddManualGame"/> passes its
    /// existence check. It is never executed.
    /// </summary>
    private static string CreatePlaceholderExe(TempPath temp, string fileName)
    {
        var path = temp.GetFile(fileName);
        File.WriteAllText(path, "placeholder - not an executable");
        return path;
    }

    /// <summary>
    /// B1: Scanner results must outlive the process - a game merged in one session has to be there
    /// after a restart, otherwise every launch would re-scan from scratch.
    /// </summary>
    [Fact]
    public void MergeScannedGames_AddsNewEntriesAndPersistsAcrossReload()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var store = new KnownGamesStore();
            store.Load();
            store.MergeScannedGames(new[]
            {
                ScannerGame("steam_400", "Portal", @"C:\SteamLibrary\steamapps\common\Portal"),
                ScannerGame("epic_fn", "Fortnite", @"C:\Epic\Fortnite", "Epic")
            });

            var reloaded = new KnownGamesStore();
            reloaded.Load();

            var games = reloaded.GetAllGames();
            Assert.Equal(2, games.Count);
            Assert.Contains(games, g => g.Id == "steam_400" && g.GameName == "Portal");
            Assert.Contains(games, g => g.Id == "epic_fn" && g.LauncherSource == "Epic");
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    /// <summary>
    /// B2: A re-scan carries updated launcher data (moved library, renamed title). The existing
    /// scanner entry must be refreshed in place rather than duplicated.
    /// </summary>
    [Fact]
    public void MergeScannedGames_ReplacesExistingScannerEntryInPlace()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var store = new KnownGamesStore();
            store.Load();
            store.MergeScannedGames(new[] { ScannerGame("steam_500", "Old Name", @"C:\Old\Path") });
            store.MergeScannedGames(new[] { ScannerGame("steam_500", "New Name", @"D:\New\Path") });

            var stored = Assert.Single(store.GetAllGames());
            Assert.Equal("steam_500", stored.Id);
            Assert.Equal("New Name", stored.GameName);
            Assert.Equal(@"D:\New\Path", stored.InstallDirectory);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    /// <summary>
    /// B3: A manual entry is the user's own decision and holds the authoritative path. Scanner data
    /// that collides on ID must not overwrite it.
    /// </summary>
    [Fact]
    public void MergeScannedGames_PreservesManualEntryOnIdCollision()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var exePath = CreatePlaceholderExe(temp, "Collision.exe");

            var store = new KnownGamesStore();
            store.Load();
            var manual = store.AddManualGame(exePath);
            Assert.NotNull(manual);

            store.MergeScannedGames(new[]
            {
                ScannerGame(manual!.Id, "Scanner Version", @"C:\Scanner\Collision")
            });

            var stored = Assert.Single(store.GetAllGames());
            Assert.Equal("Manual", stored.LauncherSource);
            Assert.Equal("Collision", stored.GameName);
            Assert.Equal(exePath, stored.ExecutablePath);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    /// <summary>
    /// B4: A manual add pointing at a nonexistent executable is rejected, and the rejection is total -
    /// it must not create a store file as a side effect.
    /// </summary>
    [Fact]
    public void AddManualGame_NonexistentPath_ReturnsNullAndWritesNothing()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var store = new KnownGamesStore();
            store.Load();

            var result = store.AddManualGame(temp.GetFile("DoesNotExist.exe"));

            Assert.Null(result);
            Assert.Empty(store.GetAllGames());
            Assert.False(File.Exists(StoreFile(temp)));
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    /// <summary>
    /// B5: A manual add creates a deterministic identity derived from the executable name, so the
    /// same game keeps the same ID (and therefore the same profile) across sessions.
    /// </summary>
    [Fact]
    public void AddManualGame_ExistingFile_CreatesManualIdentityAndPersists()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var exePath = CreatePlaceholderExe(temp, "ManualGame.exe");

            var store = new KnownGamesStore();
            store.Load();
            var added = store.AddManualGame(exePath);

            Assert.NotNull(added);
            Assert.Equal(GameInfo.GenerateId("Manual", "ManualGame"), added!.Id);
            Assert.Equal("Manual", added.LauncherSource);
            Assert.Equal("ManualGame", added.GameName);
            Assert.Equal(exePath, added.ExecutablePath);
            Assert.Equal(temp.Path, added.InstallDirectory);

            var reloaded = new KnownGamesStore();
            reloaded.Load();
            var stored = Assert.Single(reloaded.GetAllGames());
            Assert.Equal(added.Id, stored.Id);
            Assert.Equal(exePath, stored.ExecutablePath);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    /// <summary>
    /// B6: Adding the same executable twice must reuse the existing identity instead of creating a
    /// second record that would compete for the same profile.
    /// </summary>
    [Fact]
    public void AddManualGame_Twice_ReturnsExistingWithoutDuplicating()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var exePath = CreatePlaceholderExe(temp, "TwiceAdded.exe");

            var store = new KnownGamesStore();
            store.Load();
            var first = store.AddManualGame(exePath);
            var second = store.AddManualGame(exePath);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.Id, second!.Id);
            Assert.Single(store.GetAllGames());
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    /// <summary>
    /// B7: Removal must persist (otherwise the entry returns on restart), and an unknown ID must be
    /// reported as a no-op rather than silently mutating the store.
    /// </summary>
    [Fact]
    public void RemoveGame_RemovesAndPersists_AndReturnsFalseForUnknownId()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var store = new KnownGamesStore();
            store.Load();
            store.MergeScannedGames(new[]
            {
                ScannerGame("steam_600", "Removed", @"C:\Games\Removed"),
                ScannerGame("steam_601", "Kept", @"C:\Games\Kept")
            });

            Assert.True(store.RemoveGame("steam_600"));

            Assert.False(store.RemoveGame("steam_does_not_exist"));
            Assert.Single(store.GetAllGames());

            var reloaded = new KnownGamesStore();
            reloaded.Load();
            var stored = Assert.Single(reloaded.GetAllGames());
            Assert.Equal("steam_601", stored.Id);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    /// <summary>
    /// B8: A truncated or corrupted store file (crash during write, disk error) must not take the
    /// application down at startup.
    /// </summary>
    [Fact]
    public void Load_MalformedJson_LeavesStoreEmptyAndDoesNotThrow()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            File.WriteAllText(StoreFile(temp), "{ this is not valid json");

            var store = new KnownGamesStore();
            store.Load();

            Assert.Empty(store.GetAllGames());
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    /// <summary>
    /// B9: A failed reload must not be mistaken for an empty library. Entries already in memory stay,
    /// so a corrupt file cannot silently wipe the user's known games (and get that emptiness saved
    /// back over the file by the next write).
    /// </summary>
    [Fact]
    public void Load_MalformedJson_DoesNotDiscardAlreadyLoadedEntries()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var seed = new KnownGamesStore();
            seed.Load();
            seed.MergeScannedGames(new[] { ScannerGame("steam_700", "Survivor", @"C:\Games\Survivor") });

            var store = new KnownGamesStore();
            store.Load();
            Assert.Single(store.GetAllGames());

            File.WriteAllText(StoreFile(temp), "{{{ corrupted");
            store.Load();

            var stored = Assert.Single(store.GetAllGames());
            Assert.Equal("steam_700", stored.Id);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    /// <summary>
    /// B10: Removing a scanner-detected game records it in the ignore list, so the next library scan
    /// must not merge the same launcher result back in while the launcher still reports it.
    /// </summary>
    [Fact]
    public void MergeScannedGames_DoesNotReAddPreviouslyRemovedScannerGame()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var scanned = ScannerGame("steam_800", "Unwanted", @"C:\Games\Unwanted");

            var store = new KnownGamesStore();
            store.Load();
            store.MergeScannedGames(new[] { scanned });
            Assert.True(store.RemoveGame("steam_800"));
            Assert.Empty(store.GetAllGames());

            store.MergeScannedGames(new[] { scanned });

            Assert.Empty(store.GetAllGames());
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }
}
