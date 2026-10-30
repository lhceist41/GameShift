using System.IO;
using System.Text.Json;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Profiles;

/// <summary>
/// Persistence and isolation tests for <see cref="ProfileManager"/>:
///  - SaveProfile is atomic and round-trips (guards the "tuned settings silently revert to default"
///    bug class: a truncated profile fails to deserialize and GetProfileForGame falls back to default).
///  - GetDefaultProfile hands out a clone, so per-session mutation never leaks across games.
/// </summary>
public class ProfileManagerPersistenceTests
{
    // ── Atomic save + round-trip (guards a non-atomic write truncating a profile) ──

    [Fact]
    public void SaveThenGet_RoundTripsAllFields()
    {
        using var temp = new TempPath();
        var pm = new ProfileManager(temp.Path);

        var profile = new GameProfile
        {
            Id = "steam_12345",
            GameName = "Test Game",
            // Competitive is the non-default tier (the default profile is Casual), so this proves
            // the value actually round-tripped rather than matching the default.
            Intensity = OptimizationIntensity.Competitive,
            MemoryThresholdMB = 4096,
            AntiCheat = AntiCheatType.BattlEye,
            ExecutableName = "test.exe"
        };

        pm.SaveProfile(profile);
        var loaded = pm.GetProfileForGame("steam_12345");

        Assert.Equal("steam_12345", loaded.Id);
        Assert.Equal("Test Game", loaded.GameName);
        Assert.Equal(OptimizationIntensity.Competitive, loaded.Intensity);
        Assert.Equal(4096, loaded.MemoryThresholdMB);
        Assert.Equal(AntiCheatType.BattlEye, loaded.AntiCheat);
        Assert.Equal("test.exe", loaded.ExecutableName);
    }

    [Fact]
    public void GetProfileForGame_LoadsAProfileWithTheRetiredToggles_AndKeepsThemOnSave()
    {
        using var temp = new TempPath();
        var pm = new ProfileManager(temp.Path);

        // Every property an earlier version wrote for a profile, with each toggle GameShift no longer
        // reads set away from its default.
        File.WriteAllText(Path.Combine(temp.Path, "steam_777.json"), """
            {
              "Id": "steam_777",
              "GameName": "Legacy Game",
              "ProcessId": 0,
              "Intensity": 0,
              "SuppressServices": false,
              "SwitchPowerPlan": true,
              "SetTimerResolution": true,
              "BoostProcessPriority": true,
              "OptimizeMemory": false,
              "ReduceVisualEffects": false,
              "OptimizeNetwork": false,
              "UsePerformanceCoresOnly": false,
              "ManageIoPriority": false,
              "EnableEfficiencyMode": true,
              "ManageMemoryPriority": false,
              "EmptyWorkingSets": false,
              "HardMinWorkingSetMB": 1024,
              "PinToVCacheCcd": true,
              "SuppressTier2Services": true,
              "SuppressScheduledTasks": false,
              "SuppressDefenderScheduledScan": true,
              "UnparkCpuCores": false,
              "DisableProcessorIdle": true,
              "DisableMpo": true,
              "EnableCompetitiveMode": true,
              "SuspendDiscordOverlay": false,
              "SuspendSteamOverlay": false,
              "SuspendNvidiaOverlay": false,
              "KillWidgets": false,
              "EnableGpuOptimization": false,
              "ForceMaxPerformancePowerMode": false,
              "OptimizeShaderCache": false,
              "EnableLowLatencyMode": false,
              "EnableDpcMonitoring": true,
              "DpcThresholdMicroseconds": 0,
              "AntiCheat": 0,
              "ExecutableName": "legacy.exe",
              "ExecutablePath": "",
              "LauncherSource": "Steam",
              "CustomServicesToPause": null,
              "MemoryThresholdMB": 3072
            }
            """);

        var loaded = pm.GetProfileForGame("steam_777");

        // A failed load would fall back to the default profile.
        Assert.Equal("steam_777", loaded.Id);
        Assert.False(loaded.SuppressServices);
        Assert.Equal("legacy.exe", loaded.ExecutableName);

        pm.SaveProfile(loaded);
        var resaved = pm.GetProfileForGame("steam_777");

        Assert.Equal(OptimizationIntensity.Competitive, resaved.Intensity);
        Assert.True(resaved.DisableMpo);
        Assert.True(resaved.EnableCompetitiveMode);
        Assert.False(resaved.EnableGpuOptimization);
        Assert.False(resaved.UnparkCpuCores);
        Assert.Equal(3072, resaved.MemoryThresholdMB);
    }

    [Fact]
    public void SaveProfile_LeavesNoTempFile()
    {
        using var temp = new TempPath();
        var pm = new ProfileManager(temp.Path);

        pm.SaveProfile(new GameProfile { Id = "steam_999", GameName = "X" });

        // The atomic write stages "<id>.json.tmp" then moves it into place; none should remain.
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        Assert.True(File.Exists(Path.Combine(temp.Path, "steam_999.json")));
    }

    [Fact]
    public void GetProfileForGame_CorruptFile_FallsBackToDefaultWithoutThrowing()
    {
        using var temp = new TempPath();
        var pm = new ProfileManager(temp.Path);

        // Simulate a truncated/garbled profile (e.g. a crash mid-write before the atomic-save fix).
        File.WriteAllText(Path.Combine(temp.Path, "corrupt_game.json"), "{ this is not valid json ");

        var loaded = pm.GetProfileForGame("corrupt_game");

        // Falls back to the default profile instead of throwing.
        Assert.Equal("default", loaded.Id);
    }

    [Fact]
    public void SaveDefaultProfile_InvalidatesCache()
    {
        using var temp = new TempPath();
        var pm = new ProfileManager(temp.Path);

        // Prime the cache with the hardcoded default (Casual tier).
        var first = pm.GetDefaultProfile();
        Assert.Equal(OptimizationIntensity.Casual, first.Intensity);

        // Persist a modified default; saving "default" must invalidate the cache.
        pm.SaveProfile(new GameProfile
        {
            Id = "default",
            GameName = "Default Profile",
            Intensity = OptimizationIntensity.Competitive
        });

        var second = pm.GetDefaultProfile();
        Assert.Equal(OptimizationIntensity.Competitive, second.Intensity);
    }

    // ── Default-profile isolation (no cross-game state leak) ──

    [Fact]
    public void GetDefaultProfile_ReturnsDistinctInstances()
    {
        using var temp = new TempPath();
        var pm = new ProfileManager(temp.Path);

        var a = pm.GetDefaultProfile();
        var b = pm.GetDefaultProfile();

        // A shared cached instance would let one game's mutation bleed into the next.
        Assert.NotSame(a, b);
    }

    [Fact]
    public void DefaultProfile_MutationDoesNotLeakAcrossGames()
    {
        using var temp = new TempPath();
        var pm = new ProfileManager(temp.Path);

        // Two different games with no custom profile both fall back to the default.
        var gameA = pm.GetProfileForGame("unknown_game_A");

        // Simulate the detection orchestrator mutating the active profile for an anti-cheat title.
        gameA.AntiCheat = AntiCheatType.EasyAntiCheat;
        gameA.ProcessId = 4321;

        var gameB = pm.GetProfileForGame("unknown_game_B");

        // gameB must NOT have inherited gameA's per-session mutations.
        Assert.Equal(AntiCheatType.None, gameB.AntiCheat);
        Assert.Equal(0, gameB.ProcessId);
    }

    // ── Migration of profiles saved before format versioning ──

    /// <summary>
    /// A profile as an earlier version saved it: every flag explicit, no SchemaVersion, timer
    /// resolution and both pinning flags on, and other fields away from their defaults.
    /// </summary>
    private const string Version0Profile = """
        {
          "Id": "steam_4242",
          "GameName": "Older Game",
          "SuppressServices": false,
          "SwitchPowerPlan": true,
          "SetTimerResolution": true,
          "BoostProcessPriority": false,
          "UsePerformanceCoresOnly": true,
          "EnableEfficiencyMode": false,
          "PinToVCacheCcd": true,
          "SuppressTier2Services": false,
          "EnableDpcMonitoring": false,
          "DpcThresholdMicroseconds": 1500,
          "AntiCheat": 2,
          "ExecutableName": "older.exe",
          "ExecutablePath": "D:/Games/Older/older.exe",
          "LauncherSource": "Steam",
          "CustomServicesToPause": ["Spooler"],
          "Intensity": 0,
          "MemoryThresholdMB": 2048
        }
        """;

    private static readonly string[] MigratedKeys =
        { "SetTimerResolution", "UsePerformanceCoresOnly", "PinToVCacheCcd", "SchemaVersion" };

    [Fact]
    public void Version0Profile_LoadsWithTimerAndPinningOff_AndIsSavedAtVersion1()
    {
        using var temp = new TempPath();
        var path = Path.Combine(temp.Path, "steam_4242.json");
        File.WriteAllText(path, Version0Profile);
        var pm = new ProfileManager(temp.Path);

        var loaded = pm.GetProfileForGame("steam_4242");

        Assert.False(loaded.SetTimerResolution);
        Assert.False(loaded.UsePerformanceCoresOnly);
        Assert.False(loaded.PinToVCacheCcd);

        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, saved.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.False(saved.RootElement.GetProperty("SetTimerResolution").GetBoolean());
        Assert.False(saved.RootElement.GetProperty("PinToVCacheCcd").GetBoolean());
    }

    [Fact]
    public void Version0Profile_KeepsEveryOtherFieldOnMigration()
    {
        using var temp = new TempPath();
        var path = Path.Combine(temp.Path, "steam_4242.json");
        File.WriteAllText(path, Version0Profile);

        new ProfileManager(temp.Path).GetProfileForGame("steam_4242");

        using var original = JsonDocument.Parse(Version0Profile);
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var property in original.RootElement.EnumerateObject())
        {
            if (MigratedKeys.Contains(property.Name))
                continue;
            Assert.True(saved.RootElement.TryGetProperty(property.Name, out var value), property.Name);
            Assert.True(JsonElement.DeepEquals(property.Value, value), property.Name);
        }
    }

    [Fact]
    public void Version0Profile_IsMigratedOnce()
    {
        using var temp = new TempPath();
        var path = Path.Combine(temp.Path, "steam_4242.json");
        File.WriteAllText(path, Version0Profile);
        var pm = new ProfileManager(temp.Path);

        var first = pm.GetProfileForGame("steam_4242");
        first.SetTimerResolution = true; // the user turns it on again after the migration
        pm.SaveProfile(first);

        Assert.True(new ProfileManager(temp.Path).GetProfileForGame("steam_4242").SetTimerResolution);
    }

    [Fact]
    public void Version1Profile_KeepsTimerAndPinningOn_AndIsNotRewritten()
    {
        using var temp = new TempPath();
        var path = Path.Combine(temp.Path, "steam_5151.json");
        var pm = new ProfileManager(temp.Path);
        pm.SaveProfile(new GameProfile { Id = "steam_5151", GameName = "Newer", SetTimerResolution = true, PinGameCores = true });
        var written = File.ReadAllText(path);

        var loaded = pm.GetProfileForGame("steam_5151");

        Assert.True(loaded.SetTimerResolution);
        Assert.True(loaded.PinGameCores);
        Assert.Equal(written, File.ReadAllText(path));
    }

    [Fact]
    public void Version0DefaultProfile_IsMigratedWhenLoaded()
    {
        using var temp = new TempPath();
        File.WriteAllText(Path.Combine(temp.Path, "default.json"),
            """{"Id":"default","GameName":"Default Profile","SetTimerResolution":true,"PinToVCacheCcd":true}""");

        var loaded = new ProfileManager(temp.Path).GetDefaultProfile();

        Assert.False(loaded.SetTimerResolution);
        Assert.False(loaded.PinGameCores);
        Assert.Equal(GameProfile.CurrentSchemaVersion, loaded.SchemaVersion);
    }
}
