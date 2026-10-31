using System.Diagnostics;
using System.Text;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// The elevated app never writes, replaces or deletes anything under %AppData%\GameShift through a
/// junction or hard link a process of the same user planted there. Run unelevated on temp folders
/// with real junctions and hard links, which need no privilege.
/// </summary>
[Collection("ConfigState")]
public sealed class UserDataFolderTests
{
    internal static void MkLink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/c", "mklink", kind, link, target })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit(10_000);
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>
    /// Makes a junction and removes it on dispose; a recursive delete of the temp folder stops at a
    /// junction when unelevated and would leave the folder behind.
    /// </summary>
    internal static IDisposable Junction(string link, string target)
    {
        MkLink("/J", link, target);
        return new LinkRemover(link);
    }

    private sealed class LinkRemover(string link) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link);
        }
    }

    internal static int LinkCount(string path)
    {
        using var handle = NativeInterop.CreateFile(
            path, NativeInterop.FILE_READ_ATTRIBUTES | NativeInterop.READ_CONTROL,
            NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE | NativeInterop.FILE_SHARE_DELETE,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING,
            NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        return WindowsJournalSecurity.Describe(handle).LinkCount;
    }

    /// <summary>Names and contents below a folder, to show it was left exactly as it was.</summary>
    internal static string Snapshot(string folder)
    {
        var text = new StringBuilder();
        foreach (var entry in Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).OrderBy(e => e, StringComparer.Ordinal))
        {
            text.Append(Path.GetRelativePath(folder, entry));
            if (File.Exists(entry))
                text.Append('=').Append(File.ReadAllText(entry));
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>A folder outside the data folder with a file in it, standing in for one the user must not reach elevated.</summary>
    private static string Victim(TempPath temp, out string victimFile)
    {
        var folder = Path.Combine(temp.Path, "victim");
        Directory.CreateDirectory(folder);
        victimFile = Path.Combine(folder, "important.txt");
        File.WriteAllText(victimFile, "original");
        return folder;
    }

    // ── Opening the folder ────────────────────────────────────────────────

    [Fact]
    public void Open_CreatesAMissingFolder_AndWritesAndReadsRelativeToIt()
    {
        using var temp = new TempPath();
        var path = Path.Combine(temp.Path, "GameShift");

        using var folder = UserDataFolder.Open(path, temp.Path, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(folder);
        Assert.True(Directory.Exists(path));
        folder!.WriteAllText("settings.json", "{\"a\":1}");
        folder.WriteAllText("settings.json", "{\"a\":2}");
        Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(path, "settings.json")));
        Assert.Equal("{\"a\":2}", folder.ReadAllText("settings.json"));
        Assert.Null(folder.ReadAllText("missing.json"));
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(path).Select(Path.GetFileName));
    }

    [Fact]
    public void Open_AJunctionInPlaceOfTheFolder_IsRefused_AndTheTargetStaysAsItWas()
    {
        using var temp = new TempPath();
        var target = Victim(temp, out _);
        var before = Snapshot(target);
        var junction = Path.Combine(temp.Path, "GameShift");
        using var junction2 = Junction(junction, target);

        var folder = UserDataFolder.Open(junction, temp.Path, out var refusal);

        Assert.Null(folder);
        Assert.Contains("junction", refusal);
        Assert.Equal(before, Snapshot(target));
    }

    [Fact]
    public void Open_AJunctionInPlaceOfTheParent_IsRefused_AndNothingIsCreatedInTheTarget()
    {
        using var temp = new TempPath();
        var target = Victim(temp, out _);
        var before = Snapshot(target);
        var junction = Path.Combine(temp.Path, "Roaming");
        using var junction3 = Junction(junction, target);

        var folder = UserDataFolder.Open(Path.Combine(junction, "GameShift"), temp.Path, out var refusal);

        Assert.Null(folder);
        Assert.Contains("above", refusal);
        Assert.Equal(before, Snapshot(target));
    }

    [Fact]
    public void Open_AJunctionHigherUp_IsRefused_AndNothingIsCreatedInTheTarget()
    {
        using var temp = new TempPath();
        var target = Victim(temp, out _);
        Directory.CreateDirectory(Path.Combine(target, "Roaming"));
        var before = Snapshot(target);
        var junction = Path.Combine(temp.Path, "AppData");
        using var junction4 = Junction(junction, target);

        var folder = UserDataFolder.Open(Path.Combine(junction, "Roaming", "GameShift"), temp.Path, out var refusal);

        Assert.Null(folder);
        Assert.Contains("above", refusal);
        Assert.Equal(before, Snapshot(target));
    }

    [Fact]
    public void Open_AFolderOutsideTheUsersProfile_IsRefused_AndNotCreated()
    {
        using var temp = new TempPath();
        var profile = Path.Combine(temp.Path, "profile");
        Directory.CreateDirectory(profile);
        var elsewhere = Path.Combine(temp.Path, "elsewhere", "GameShift");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);

        var folder = UserDataFolder.Open(elsewhere, profile, out var refusal);

        Assert.Null(folder);
        Assert.Contains("profile", refusal);
        Assert.False(Directory.Exists(elsewhere));
    }

    [Fact]
    public void Open_AFolderWhoseNameOnlyStartsLikeTheProfile_IsRefused()
    {
        using var temp = new TempPath();
        var profile = Path.Combine(temp.Path, "user");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.Combine(temp.Path, "user2"));

        var folder = UserDataFolder.Open(Path.Combine(temp.Path, "user2", "GameShift"), profile, out var refusal);

        Assert.Null(folder);
        Assert.Contains("profile", refusal);
    }

    [Theory]
    [InlineData(@"\\127.0.0.1\C$\Users\someone", @"\\127.0.0.1\C$\Users\someone\AppData\Roaming\GameShift")]
    [InlineData(@"\\127.0.0.1\gameshift-no-such-share\someone", @"\\127.0.0.1\gameshift-no-such-share\someone\GameShift")]
    [InlineData(@"//127.0.0.1/C$/Users/someone", @"//127.0.0.1/C$/Users/someone/AppData/Roaming/GameShift")]
    public void Open_ANetworkPath_IsRefusedBeforeAnyLookup(string profile, string path)
    {
        var folder = UserDataFolder.Open(path, profile, out var refusal);

        Assert.Null(folder);
        Assert.Equal("it is on a network path", refusal);
    }

    [Fact]
    public void Open_BelowAProfileReachedThroughAJunction_IsAccepted_AndWritesLandInItsTarget()
    {
        using var temp = new TempPath();
        var realProfile = Path.Combine(temp.Path, "D-Users-someone");
        Directory.CreateDirectory(Path.Combine(realProfile, "AppData", "Roaming"));
        var profile = Path.Combine(temp.Path, "someone");
        using var junction1 = Junction(profile, realProfile);

        using (var nested = UserDataFolder.Open(Path.Combine(profile, "AppData", "Roaming", "GameShift"), profile, out var refusal))
        {
            Assert.Null(refusal);
            nested!.WriteAllText("settings.json", "{}");
        }

        using (var direct = UserDataFolder.Open(Path.Combine(profile, "GameShift"), profile, out var refusal))
        {
            Assert.Null(refusal);
            direct!.WriteAllText("settings.json", "{}");
        }

        Assert.True(File.Exists(Path.Combine(realProfile, "AppData", "Roaming", "GameShift", "settings.json")));
        Assert.True(File.Exists(Path.Combine(realProfile, "GameShift", "settings.json")));
    }

    [Fact]
    public void Open_AJunctionBelowAProfileReachedThroughAJunction_IsRefused_AndTheTargetStaysAsItWas()
    {
        using var temp = new TempPath();
        var target = Victim(temp, out _);
        Directory.CreateDirectory(Path.Combine(target, "Roaming"));
        var before = Snapshot(target);
        var realProfile = Path.Combine(temp.Path, "D-Users-someone");
        Directory.CreateDirectory(realProfile);
        var profile = Path.Combine(temp.Path, "someone");
        using var junction1 = Junction(profile, realProfile);
        using var junction2 = Junction(Path.Combine(realProfile, "AppData"), target);

        var folder = UserDataFolder.Open(Path.Combine(profile, "AppData", "Roaming", "GameShift"), profile, out var refusal);

        Assert.Null(folder);
        Assert.Contains("above", refusal);
        Assert.Equal(before, Snapshot(target));
    }

    [Fact]
    public void AnOpenFolder_CannotBeRenamedAway()
    {
        using var temp = new TempPath();
        var path = Path.Combine(temp.Path, "GameShift");

        using var folder = UserDataFolder.Open(path, temp.Path, out _);

        Assert.NotNull(folder);
        Assert.ThrowsAny<IOException>(() => Directory.Move(path, Path.Combine(temp.Path, "moved")));
        Assert.True(Directory.Exists(path));
    }

    // ── Writes ────────────────────────────────────────────────────────────

    [Fact]
    public void Write_NeverOpensAPlantedTempName_AndTheLinkedFileStaysAsItWas()
    {
        using var temp = new TempPath();
        Victim(temp, out var victimFile);
        var path = Path.Combine(temp.Path, "GameShift");
        using var folder = UserDataFolder.Open(path, temp.Path, out _)!;
        folder.TempNameFor = name => name + ".tmp";
        MkLink("/H", Path.Combine(path, "settings.json.tmp"), victimFile);

        Assert.ThrowsAny<IOException>(() => folder.WriteAllText("settings.json", "attacker-chosen"));

        Assert.Equal("original", File.ReadAllText(victimFile));
        Assert.Equal(2, LinkCount(victimFile));
        Assert.False(File.Exists(Path.Combine(path, "settings.json")));
    }

    [Fact]
    public void Write_OverAHardLinkedTarget_IsRefused_AndTheLinkedFileStaysAsItWas()
    {
        using var temp = new TempPath();
        Victim(temp, out var victimFile);
        var path = Path.Combine(temp.Path, "GameShift");
        using var folder = UserDataFolder.Open(path, temp.Path, out _)!;
        MkLink("/H", Path.Combine(path, "settings.json"), victimFile);

        Assert.ThrowsAny<IOException>(() => folder.WriteAllText("settings.json", "attacker-chosen"));

        Assert.Equal("original", File.ReadAllText(victimFile));
        Assert.Equal(2, LinkCount(victimFile));
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(path).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_ThatCannotBeRenamed_LeavesNoTempFile()
    {
        using var temp = new TempPath();
        var path = Path.Combine(temp.Path, "GameShift");
        using var folder = UserDataFolder.Open(path, temp.Path, out _)!;
        folder.WaitBeforeReplaceRetry = _ => { };
        Directory.CreateDirectory(Path.Combine(path, "settings.json"));

        Assert.ThrowsAny<IOException>(() => folder.WriteAllText("settings.json", "x"));

        Assert.Empty(Directory.GetFiles(path));
    }

    // ── Deletes ───────────────────────────────────────────────────────────

    [Fact]
    public void Delete_OfAHardLinkedFile_IsRefused_AndBothNamesStay()
    {
        using var temp = new TempPath();
        Victim(temp, out var victimFile);
        var path = Path.Combine(temp.Path, "GameShift");
        using var folder = UserDataFolder.Open(path, temp.Path, out _)!;
        var link = Path.Combine(path, "steam_1.json");
        MkLink("/H", link, victimFile);

        Assert.ThrowsAny<IOException>(() => folder.DeleteFile("steam_1.json"));

        Assert.True(File.Exists(link));
        Assert.Equal("original", File.ReadAllText(victimFile));
        Assert.Equal(2, LinkCount(victimFile));
    }

    [Fact]
    public void Delete_OfAJunction_IsRefused_AndTheJunctionAndItsTargetStay()
    {
        using var temp = new TempPath();
        var target = Victim(temp, out _);
        var before = Snapshot(target);
        var path = Path.Combine(temp.Path, "GameShift");
        using var folder = UserDataFolder.Open(path, temp.Path, out _)!;
        var junction = Path.Combine(path, "steam_1.json");
        using var junction5 = Junction(junction, target);

        Assert.ThrowsAny<IOException>(() => folder.DeleteFile("steam_1.json"));

        Assert.True(Directory.Exists(junction));
        Assert.Equal(before, Snapshot(target));
    }

    [Fact]
    public void Delete_OfAPlainFile_RemovesIt_AndAMissingOneReportsFalse()
    {
        using var temp = new TempPath();
        var path = Path.Combine(temp.Path, "GameShift");
        using var folder = UserDataFolder.Open(path, temp.Path, out _)!;
        folder.WriteAllText("steam_1.json", "{}");

        Assert.True(folder.DeleteFile("steam_1.json"));
        Assert.False(File.Exists(Path.Combine(path, "steam_1.json")));
        Assert.False(folder.DeleteFile("steam_1.json"));
    }

    // ── The stores ────────────────────────────────────────────────────────

    [Fact]
    public void Settings_InAJunctionedDataFolder_AreNeitherReadNorWritten()
    {
        using var temp = new TempPath();
        var target = Victim(temp, out _);
        File.WriteAllText(Path.Combine(target, "settings.json"), "{\"SkippedUpdateVersion\":\"6.6.6\"}");
        var before = Snapshot(target);
        var junction = Path.Combine(temp.Path, "GameShift");
        using var junction6 = Junction(junction, target);
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = Path.Combine(junction, "settings.json");
        try
        {
            Assert.NotEqual("6.6.6", SettingsManager.Load().SkippedUpdateVersion);
            SettingsManager.Save(new AppSettings { SkippedUpdateVersion = "9.9.9" });
            SettingsManager.Update(s => s.SkippedUpdateVersion = "9.9.9");
            Assert.False(SettingsManager.IsFirstRun());
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = previous;
        }

        Assert.Equal(before, Snapshot(target));
    }

    [Fact]
    public void SessionHistory_InAJunctionedDataFolder_IsNotWritten()
    {
        using var temp = new TempPath();
        var target = Victim(temp, out _);
        var before = Snapshot(target);
        var junction = Path.Combine(temp.Path, "GameShift");
        using var junction7 = Junction(junction, target);

        var store = new SessionHistoryStore(Path.Combine(junction, "session-history.json"));
        store.Add(new GameSession { GameName = "Game", StartTime = DateTime.Now });

        Assert.Equal(before, Snapshot(target));
    }

    [Fact]
    public void KnownGames_WithAHardLinkedStoreFile_AreNotWrittenThroughTheLink()
    {
        using var temp = new TempPath();
        Victim(temp, out var victimFile);
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            MkLink("/H", temp.GetFile("known_games.json"), victimFile);
            var store = new KnownGamesStore();

            store.MergeScannedGames(new[] { new GameInfo { Id = "steam_1", GameName = "Game", LauncherSource = "Steam" } });

            Assert.Equal("original", File.ReadAllText(victimFile));
            Assert.Equal(2, LinkCount(victimFile));
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = previous;
        }
    }

    [Fact]
    public void Profiles_InAJunctionedProfilesFolder_AreNeitherReadNorWritten()
    {
        using var temp = new TempPath();
        var target = Victim(temp, out _);
        File.WriteAllText(Path.Combine(target, "steam_1.json"), "{\"Id\":\"steam_1\",\"GameName\":\"Planted\"}");
        var before = Snapshot(target);
        var junction = Path.Combine(temp.Path, "profiles");
        using var junction8 = Junction(junction, target);

        var manager = new ProfileManager(junction);
        manager.SaveProfile(new GameProfile { Id = "steam_2", GameName = "Mine" });

        Assert.DoesNotContain(manager.GetAllProfiles(), p => p.GameName == "Planted");
        Assert.NotEqual("Planted", manager.GetProfileForGame("steam_1").GameName);
        Assert.False(manager.DeleteProfile("steam_1"));
        Assert.Equal(before, Snapshot(target));
    }

    [Fact]
    public void ProfileDelete_OfAHardLinkedOrJunctionEntry_IsRefused()
    {
        using var temp = new TempPath();
        var target = Victim(temp, out var victimFile);
        var profiles = Path.Combine(temp.Path, "profiles");
        var manager = new ProfileManager(profiles);
        MkLink("/H", Path.Combine(profiles, "steam_1.json"), victimFile);
        using var junction9 = Junction(Path.Combine(profiles, "steam_2.json"), target);

        Assert.False(manager.DeleteProfile("steam_1"));
        Assert.False(manager.DeleteProfile("steam_2"));

        Assert.True(File.Exists(Path.Combine(profiles, "steam_1.json")));
        Assert.True(Directory.Exists(Path.Combine(profiles, "steam_2.json")));
        Assert.Equal("original", File.ReadAllText(victimFile));
        Assert.Equal(2, LinkCount(victimFile));
    }

    [Fact]
    public void Profiles_SaveReadListAndDelete_GoThroughTheVerifiedFolder()
    {
        using var temp = new TempPath();
        var profiles = Path.Combine(temp.Path, "profiles");
        var manager = new ProfileManager(profiles);

        manager.SaveProfile(new GameProfile { Id = "steam_1", GameName = "One" });

        Assert.Equal("One", manager.GetProfileForGame("steam_1").GameName);
        Assert.Contains(manager.GetAllProfiles(), p => p.GameName == "One");
        Assert.True(manager.DeleteProfile("steam_1"));
        Assert.False(File.Exists(Path.Combine(profiles, "steam_1.json")));
    }
}
