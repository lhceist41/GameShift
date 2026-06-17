using System.IO;
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
}
