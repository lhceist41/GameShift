using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// A session save never destroys a session record it cannot account for: an active record of a
/// session the saving instance did not start or adopt, or a file it cannot parse, is kept aside
/// first, and a file it cannot read is not written over. All journals live in a temp folder.
/// </summary>
public class JournalSaveGuardTests : IDisposable
{
    private readonly TempPath _temp = new();

    private string JournalPath => _temp.GetFile("state.json");

    public void Dispose() => _temp.Dispose();

    private static GameProfile Profile(string game) => new()
    {
        Id = "test",
        GameName = game,
        ExecutableName = "game.exe",
        ProcessId = 4242,
    };

    /// <summary>An active session as a crashed app left it, written through its own instance.</summary>
    private void CrashedSession(string game = "Crashed")
    {
        var writer = new JournalManager(JournalPath);
        writer.StartSession(Profile(game));
        writer.RecordApplied(new OptimizationResult("Opt1", "orig", "applied", OptimizationState.Applied));
    }

    private SessionJournalData OnDisk() => new JournalManager(JournalPath).PeekJournal()!;

    private string[] Kept(string kind) => Directory.GetFiles(_temp.Path, $"state.{kind}-*.json");

    private string[] AnyKept() => Directory.GetFiles(_temp.Path, "state.*-*.json");

    // ── Foreign active records are kept ───────────────────────────────────

    [Fact]
    public void NewSession_KeepsACrashedSessionRecordAside()
    {
        CrashedSession();
        var crashed = File.ReadAllBytes(JournalPath);

        new JournalManager(JournalPath).StartSession(Profile("Next"));

        var kept = Assert.Single(Kept("unrecovered"));
        Assert.Equal(crashed, File.ReadAllBytes(kept));
        Assert.Matches(@"^state\.unrecovered-\d{8}T\d{9}Z\.json$", Path.GetFileName(kept));
        Assert.True(OnDisk().SessionActive);
        Assert.Equal("Next", OnDisk().ActiveGame!.Name);
        Assert.Empty(OnDisk().Optimizations);
    }

    // The record a start of this version leaves alone because it does not replay that version.
    [Fact]
    public void NewSession_KeepsAnActiveRecordOfAnotherVersionAside()
    {
        CrashedSession();
        var text = File.ReadAllText(JournalPath).Replace("\"Version\": 2", "\"Version\": 3");
        File.WriteAllText(JournalPath, text);

        new JournalManager(JournalPath).StartSession(Profile("Next"));

        Assert.Equal(text, File.ReadAllText(Assert.Single(Kept("unrecovered"))));
        Assert.Equal(2, OnDisk().Version);
    }

    [Fact]
    public void EndSessionOnAnInstanceThatOwnsNoSession_KeepsTheActiveRecordAside()
    {
        CrashedSession();
        var crashed = File.ReadAllBytes(JournalPath);

        new JournalManager(JournalPath).EndSession();

        Assert.Equal(crashed, File.ReadAllBytes(Assert.Single(Kept("unrecovered"))));
        Assert.False(OnDisk().SessionActive);
    }

    // ── Records the instance may replace, as before ───────────────────────

    [Fact]
    public void OwnSession_IsWrittenOverAsBefore()
    {
        var journal = new JournalManager(JournalPath);
        journal.StartSession(Profile("Own"));
        journal.RecordApplied(new OptimizationResult("Opt1", "orig", "applied", OptimizationState.Applied));
        journal.RecordReverted("Opt1", OptimizationState.Reverted);
        journal.EndSession();
        journal.StartSession(Profile("Again"));

        Assert.Empty(AnyKept());
        Assert.Equal("Again", OnDisk().ActiveGame!.Name);
    }

    // Startup recovery adopts the crashed record before it counts the attempt and ends the session.
    [Theory]
    [InlineData(false)]
    [InlineData(true)] // a record from a version that did not write the start time
    public void AdoptedSession_IsWrittenOverAsBefore(bool withoutStartTime)
    {
        CrashedSession();
        if (withoutStartTime)
        {
            var lines = File.ReadAllLines(JournalPath).Where(l => !l.Contains("\"SessionStartTime\"", StringComparison.Ordinal));
            File.WriteAllLines(JournalPath, lines);
            Assert.Null(OnDisk().SessionStartTime);
        }

        var journal = new JournalManager(JournalPath);
        journal.LoadJournal();
        Assert.True(journal.RecordRecoveryAttempt());
        journal.EndSession();

        Assert.Empty(AnyKept());
        Assert.False(OnDisk().SessionActive);
        Assert.Equal(1, OnDisk().RecoveryAttempts);
    }

    [Fact]
    public void EndedSession_IsWrittenOverAsBefore()
    {
        var writer = new JournalManager(JournalPath);
        writer.StartSession(Profile("Ended"));
        writer.EndSession();

        new JournalManager(JournalPath).StartSession(Profile("Next"));

        Assert.Empty(AnyKept());
        Assert.Equal("Next", OnDisk().ActiveGame!.Name);
    }

    // A metadata save writes the disk record back with only the metadata changed.
    [Fact]
    public void MetadataSave_LeavesAForeignActiveRecordInPlace()
    {
        CrashedSession();

        new JournalManager(JournalPath).RecordPendingRebootFix("MSI mode");

        Assert.Empty(AnyKept());
        Assert.True(OnDisk().SessionActive);
        Assert.Equal("Crashed", OnDisk().ActiveGame!.Name);
        Assert.Single(OnDisk().Optimizations);
        Assert.Equal(new[] { "MSI mode" }, OnDisk().PendingRebootFixDescriptions);
    }

    // ── Files that cannot be read or parsed ───────────────────────────────

    // The handle shares delete, so without the guard the atomic replace would write over the file.
    [Fact]
    public void FileThatCannotBeRead_IsNotWrittenOver_AndIsKeptOnceItCanBe()
    {
        CrashedSession();
        var crashed = File.ReadAllBytes(JournalPath);
        var next = new JournalManager(JournalPath);

        using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            next.StartSession(Profile("Next"));
            next.RecordPendingRebootFix("MSI mode");
        }

        Assert.Equal(crashed, File.ReadAllBytes(JournalPath));
        Assert.Empty(AnyKept());

        next.RecordApplied(new OptimizationResult("Opt2", "orig", "applied", OptimizationState.Applied));

        Assert.Equal(crashed, File.ReadAllBytes(Assert.Single(Kept("unrecovered"))));
        Assert.Equal("Next", OnDisk().ActiveGame!.Name);
        Assert.Equal("Opt2", Assert.Single(OnDisk().Optimizations).Name);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void FileThatCannotBeParsed_IsKeptBeforeTheSave(string content)
    {
        File.WriteAllText(JournalPath, content);

        new JournalManager(JournalPath).StartSession(Profile("Next"));

        Assert.Equal(content, File.ReadAllText(Assert.Single(Kept("unreadable"))));
        Assert.Equal("Next", OnDisk().ActiveGame!.Name);
    }

    // ── Keeping fails safe ────────────────────────────────────────────────

    [Fact]
    public void KeptNameThatIsTaken_WritesNothing()
    {
        CrashedSession();
        var crashed = File.ReadAllBytes(JournalPath);
        var taken = _temp.GetFile("state.unrecovered-20261107T120000123Z.json");
        File.WriteAllText(taken, "older");
        var next = new JournalManager(JournalPath) { EvidenceClock = () => new DateTime(2026, 11, 7, 12, 0, 0, 123, DateTimeKind.Utc) };

        next.StartSession(Profile("Next"));

        Assert.Equal(crashed, File.ReadAllBytes(JournalPath));
        Assert.Equal("older", File.ReadAllText(taken));
        Assert.Single(AnyKept());
    }
}
