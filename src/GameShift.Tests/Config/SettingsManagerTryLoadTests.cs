using System.IO;
using GameShift.Core.Config;
using GameShift.Core.SystemTweaks;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// A settings file that exists but cannot be read is never replaced by a save: one that opened but
/// is not settings is kept aside under a new name, and one that could not be opened stays in place
/// and makes every save throw until it can be read.
/// </summary>
[Collection("ConfigState")]
public class SettingsManagerTryLoadTests
{
    private const string KeptName = "settings.unreadable-20260102-030405.json";

    private static readonly DateTime Clock = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private sealed class Isolated : IDisposable
    {
        private readonly TempPath _temp = new();

        public Isolated(string folderName = "GameShift")
        {
            Folder = Path.Combine(_temp.Path, folderName);
            Directory.CreateDirectory(Folder);
            SettingsManager.SettingsFilePathOverride = SettingsFile;
            SettingsManager.UtcNow = () => Clock;
        }

        public string Root => _temp.Path;
        public string Folder { get; }
        public string SettingsFile => Path.Combine(Folder, "settings.json");
        public string Kept(string name = KeptName) => Path.Combine(Folder, name);

        public string[] Names() =>
            Directory.GetFileSystemEntries(Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

        public void Dispose()
        {
            SettingsManager.SettingsFilePathOverride = null;
            SettingsManager.UtcNow = () => DateTime.UtcNow;
            _temp.Dispose();
        }
    }

    [Theory]
    [InlineData("{ \"SkippedUpdateVersion\": ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("")]
    public void FileThatIsNotSettings_IsKeptAside_AndTheNextUpdateLeavesItAsItWas(string contents)
    {
        using var data = new Isolated();
        File.WriteAllText(data.SettingsFile, contents);
        var bytes = File.ReadAllBytes(data.SettingsFile);

        Assert.True(SettingsManager.TryLoad(out var settings));
        Assert.Empty(settings.SkippedUpdateVersion);
        Assert.Equal(new[] { KeptName }, data.Names());
        Assert.Equal(new[] { data.Kept() }, SettingsManager.GetUnreadableSettingsFiles());

        SettingsManager.Update(s => s.SkippedUpdateVersion = "3.8.8");

        Assert.Equal("3.8.8", SettingsManager.Load().SkippedUpdateVersion);
        Assert.Equal(bytes, File.ReadAllBytes(data.Kept()));
        Assert.Equal(new[] { "settings.json", KeptName }, data.Names());
    }

    [Fact]
    public void SaveOverAFileThatIsNotSettings_KeepsItAsideFirst()
    {
        using var data = new Isolated();
        File.WriteAllText(data.SettingsFile, "{ broken");

        SettingsManager.Save(new AppSettings { SkippedUpdateVersion = "3.8.8" });

        Assert.Equal("{ broken", File.ReadAllText(data.Kept()));
        Assert.Equal("3.8.8", SettingsManager.Load().SkippedUpdateVersion);
    }

    [Fact]
    public void KeptName_AlreadyTaken_GetsACounter_AndTheEarlierFilesStay()
    {
        using var data = new Isolated();
        File.WriteAllText(data.Kept(), "first");
        File.WriteAllText(data.Kept("settings.unreadable-20260102-030405-1.json"), "second");
        File.WriteAllText(data.SettingsFile, "null");

        Assert.True(SettingsManager.TryLoad(out _));

        Assert.Equal("first", File.ReadAllText(data.Kept()));
        Assert.Equal("second", File.ReadAllText(data.Kept("settings.unreadable-20260102-030405-1.json")));
        Assert.Equal("null", File.ReadAllText(data.Kept("settings.unreadable-20260102-030405-2.json")));
        Assert.False(File.Exists(data.SettingsFile));
    }

    [Fact]
    public void NoFreeKeptName_LeavesTheFileInPlace_AndNoSaveReplacesIt()
    {
        using var data = new Isolated();
        File.WriteAllText(data.Kept(), "taken");
        for (int n = 1; n < 100; n++)
            File.WriteAllText(data.Kept($"settings.unreadable-20260102-030405-{n}.json"), "taken");
        File.WriteAllText(data.SettingsFile, "{ broken");
        var mutated = false;

        Assert.False(SettingsManager.TryLoad(out _));
        Assert.Throws<IOException>(() => SettingsManager.Update(s => { mutated = true; s.SkippedUpdateVersion = "9.9.9"; }));
        Assert.Throws<IOException>(() => SettingsManager.Save(new AppSettings { SkippedUpdateVersion = "9.9.9" }));

        Assert.False(mutated);
        Assert.Equal("{ broken", File.ReadAllText(data.SettingsFile));
    }

    [Fact]
    public void FileOpenedWithoutSharing_StaysInPlace_AndNoSaveReplacesIt()
    {
        using var data = new Isolated();
        SettingsManager.Save(new AppSettings { SkippedUpdateVersion = "3.8.8" });
        var bytes = File.ReadAllBytes(data.SettingsFile);
        var mutated = false;

        using (new FileStream(data.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(SettingsManager.TryLoad(out _));
            Assert.Throws<IOException>(() => SettingsManager.Update(s => { mutated = true; s.SkippedUpdateVersion = "9.9.9"; }));
            Assert.Throws<IOException>(() => SettingsManager.Save(new AppSettings { SkippedUpdateVersion = "9.9.9" }));
        }

        Assert.False(mutated);
        Assert.Equal(bytes, File.ReadAllBytes(data.SettingsFile));
        Assert.Equal(new[] { "settings.json" }, data.Names());
        Assert.Empty(SettingsManager.GetUnreadableSettingsFiles());
    }

    [Fact]
    public void HardLinkedFileThatIsNotSettings_StaysInPlace_AndTheLinkedFileIsUnchanged()
    {
        using var data = new Isolated();
        var outside = Path.Combine(data.Root, "outside.json");
        File.WriteAllText(outside, "{ broken");
        UserDataFolderTests.MkLink("/H", data.SettingsFile, outside);

        var mutated = false;

        Assert.False(SettingsManager.TryLoad(out _));
        Assert.Throws<IOException>(() => SettingsManager.Update(s => { mutated = true; s.SkippedUpdateVersion = "9.9.9"; }));
        Assert.Throws<IOException>(() => SettingsManager.Save(new AppSettings { SkippedUpdateVersion = "9.9.9" }));

        Assert.False(mutated);
        Assert.Equal("{ broken", File.ReadAllText(outside));
        Assert.Equal(new[] { "settings.json" }, data.Names());
    }

    [Fact]
    public void TweakAppliedWhileTheFileIsInPlace_IsReportedAsFailed()
    {
        using var data = new Isolated();
        SettingsManager.Save(new AppSettings());
        var bytes = File.ReadAllBytes(data.SettingsFile);
        var tweak = new RecordingTweak();

        bool applied;
        using (new FileStream(data.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            applied = new SystemTweaksManager().ApplyTweak(tweak);

        Assert.False(applied);
        Assert.Equal(bytes, File.ReadAllBytes(data.SettingsFile));
    }

    /// <summary>A tweak that changes nothing on the machine.</summary>
    private sealed class RecordingTweak : ISystemTweak
    {
        public string Name => "Recording tweak";
        public string Description => "";
        public string Category => "";
        public bool RequiresReboot => false;
        public bool DetectIsApplied() => false;
        public string? Apply() => "{}";
        public bool Revert(string? originalValuesJson) => true;
    }

    [Fact]
    public void JunctionPlantedAsTheKeptName_IsSkipped_AndItsTargetStaysAsItWas()
    {
        using var data = new Isolated();
        var target = Path.Combine(data.Root, "victim");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "important.txt"), "original");
        var before = UserDataFolderTests.Snapshot(target);
        using var junction = UserDataFolderTests.Junction(data.Kept(), target);
        File.WriteAllText(data.SettingsFile, "null");

        Assert.True(SettingsManager.TryLoad(out _));

        Assert.Equal(before, UserDataFolderTests.Snapshot(target));
        Assert.Equal("null", File.ReadAllText(data.Kept("settings.unreadable-20260102-030405-1.json")));
    }

    [Fact]
    public void JunctionPlantedAsTheDataFolder_ChangesNothingInItsTarget()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "victim");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "settings.json"), "null");
        var before = UserDataFolderTests.Snapshot(target);
        var link = Path.Combine(temp.Path, "GameShift");
        using var junction = UserDataFolderTests.Junction(link, target);
        SettingsManager.SettingsFilePathOverride = Path.Combine(link, "settings.json");
        SettingsManager.UtcNow = () => Clock;
        try
        {
            SettingsManager.TryLoad(out _);
            SettingsManager.Update(s => s.SkippedUpdateVersion = "9.9.9");
            Assert.Empty(SettingsManager.GetUnreadableSettingsFiles());
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
            SettingsManager.UtcNow = () => DateTime.UtcNow;
        }

        Assert.Equal(before, UserDataFolderTests.Snapshot(target));
    }

    [Fact]
    public void MissingFile_IsTrue_WithDefaults()
    {
        using var data = new Isolated();

        Assert.True(SettingsManager.TryLoad(out var settings));
        Assert.Empty(settings.SkippedUpdateVersion);
        Assert.Empty(data.Names());
    }

    [Fact]
    public void SavedFile_IsTrue_WithItsValues()
    {
        using var data = new Isolated();
        SettingsManager.Save(new AppSettings { SkippedUpdateVersion = "3.8.8" });

        Assert.True(SettingsManager.TryLoad(out var settings));
        Assert.Equal("3.8.8", settings.SkippedUpdateVersion);
        Assert.Empty(SettingsManager.GetUnreadableSettingsFiles());
    }
}
