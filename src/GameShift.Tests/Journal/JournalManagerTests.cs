using System.Text.Json;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Tests for <see cref="JournalManager"/> covering atomic writes, cross-instance
/// synchronization via the static lock, pending-reboot-fix and build-change metadata,
/// session lifecycle, apply/revert recording, and corrupt-journal fallback.
/// Separate file from JournalManagerRecoveryTests.cs which focuses on Phase 2
/// watchdog recovery coordination.
/// </summary>
public class JournalManagerTests
{
    private static GameProfile NewTestProfile(string id = "test", string name = "TestGame") => new()
    {
        Id = id,
        GameName = name,
        ExecutableName = "test.exe",
        ProcessId = 1234
    };

    private static OptimizationResult NewAppliedResult(string name, string original = "orig", string applied = "applied")
        => new(name, original, applied, OptimizationState.Applied);

    // ── Atomic write ──────────────────────────────────────────────────────────

    [Fact]
    public void Save_IsAtomic_NoPartialStateOnDisk()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());

        Assert.True(File.Exists(path));

        // Atomic tmp+rename pattern must not leave a .tmp file behind
        Assert.False(File.Exists(path + ".tmp"));

        // File should be valid JSON, no null bytes from partial writes
        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain((byte)0, bytes);
        var content = File.ReadAllText(path);
        using var parsed = JsonDocument.Parse(content);
        Assert.NotNull(parsed);
    }

    [Fact]
    public void Save_AfterMultipleOperations_LeavesNoTempFile()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());
        journal.RecordApplied(NewAppliedResult("Opt1"));
        journal.RecordApplied(NewAppliedResult("Opt2"));
        journal.RecordReverted("Opt1", OptimizationState.Reverted);
        journal.EndSession();

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    // ── Cross-instance static lock ────────────────────────────────────────────

    [Fact]
    public async Task MultipleInstances_WritesSerialized_NoCorruption()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        // 10 JournalManager instances pointing at the same file - the static lock
        // inside JournalManager must serialize them so neither the .tmp write nor
        // the rename collide.
        var instances = Enumerable.Range(0, 10)
            .Select(_ => new JournalManager(path))
            .ToList();

        var tasks = instances.Select((j, i) => Task.Run(() =>
        {
            j.StartSession(NewTestProfile($"test-{i}", $"Game{i}"));
            j.RecordApplied(NewAppliedResult("Optimization1"));
            j.EndSession();
        })).ToArray();

        await Task.WhenAll(tasks);

        // File must still parse as valid JSON
        var content = File.ReadAllText(path);
        using var parsed = JsonDocument.Parse(content);
        Assert.NotNull(parsed);

        // And still deserialize cleanly
        var data = JsonSerializer.Deserialize<SessionJournalData>(content);
        Assert.NotNull(data);
    }

    // ── Section-scoped merge (concurrent instances don't clobber each other) ──

    [Fact]
    public void SecondaryMetadataWrite_DoesNotClobberActiveSession()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        // The optimization engine records an active session with an applied optimization.
        var owner = new JournalManager(path);
        owner.StartSession(NewTestProfile("steam_1", "Game1"));
        owner.RecordApplied(NewAppliedResult("Opt1"));

        // A secondary instance (e.g. KernelTuningManager applying a BCD tweak) records a pending
        // reboot fix on the same file. A whole-file write of the secondary instance's own empty
        // _current would have wiped the owner's active session; the section-scoped merge must
        // preserve it.
        var secondary = new JournalManager(path);
        secondary.RecordPendingRebootFix("BCD: Disable dynamictick");

        var disk = new JournalManager(path).LoadJournal();
        Assert.NotNull(disk);
        Assert.True(disk.SessionActive);
        Assert.Equal("Game1", disk.ActiveGame?.Name);
        Assert.Single(disk.Optimizations);
        Assert.Equal("Opt1", disk.Optimizations[0].Name);

        // ...and the newly recorded pending reboot fix is also present.
        Assert.True(disk.HasPendingRebootFixes);
        Assert.Contains("BCD: Disable dynamictick", disk.PendingRebootFixDescriptions);
    }

    [Fact]
    public void SessionWrite_PreservesPendingRebootFixFromAnotherInstance()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        var owner = new JournalManager(path);
        owner.StartSession(NewTestProfile("steam_1", "Game1"));

        // Another instance records a pending reboot fix on the same file.
        var secondary = new JournalManager(path);
        secondary.RecordPendingRebootFix("BCD: Disable dynamictick");

        // The session owner keeps writing session state. Before the merge, this whole-file write
        // dropped the secondary instance's pending reboot fix; now it must survive.
        owner.RecordApplied(NewAppliedResult("Opt1"));

        var disk = new JournalManager(path).LoadJournal();
        Assert.NotNull(disk);
        Assert.True(disk.SessionActive);
        Assert.Single(disk.Optimizations);
        Assert.True(disk.HasPendingRebootFixes);
        Assert.Contains("BCD: Disable dynamictick", disk.PendingRebootFixDescriptions);
    }

    [Fact]
    public void StartSession_PreservesPendingRebootFixWrittenBeforeTheSession()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        // A reboot-required tweak is applied (and acknowledged later), then a game launches before
        // the user reboots. Starting the session must not silently drop the pending reboot warning.
        var tweaks = new JournalManager(path);
        tweaks.RecordPendingRebootFix("BCD: Disable dynamictick");

        var engine = new JournalManager(path);
        engine.StartSession(NewTestProfile("steam_1", "Game1"));

        var disk = new JournalManager(path).LoadJournal();
        Assert.NotNull(disk);
        Assert.True(disk.SessionActive);
        Assert.Equal("Game1", disk.ActiveGame?.Name);
        Assert.True(disk.HasPendingRebootFixes);
        Assert.Contains("BCD: Disable dynamictick", disk.PendingRebootFixDescriptions);
    }

    // ── LoadJournal SessionActive guard ───────────────────────────────────────

    [Fact]
    public void LoadJournal_WhenCurrentSessionInactive_OverwritesInMemoryState()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        // Instance 1 starts a session and persists it
        var j1 = new JournalManager(path);
        j1.StartSession(NewTestProfile("game1", "Game1"));
        j1.RecordApplied(NewAppliedResult("Opt1"));

        // Instance 2 has no local session active - LoadJournal should populate it from disk
        var j2 = new JournalManager(path);
        var loaded = j2.LoadJournal();

        Assert.NotNull(loaded);
        Assert.True(loaded.SessionActive);
        Assert.Equal("Game1", loaded.ActiveGame?.Name);
        Assert.Single(loaded.Optimizations);
    }

    [Fact]
    public void LoadJournal_WhenCurrentSessionActive_DoesNotOverwriteInMemoryState()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        // Instance 1 has an active session with its own pending reboot fix in memory
        var j1 = new JournalManager(path);
        j1.StartSession(NewTestProfile("game1", "Game1"));
        j1.RecordPendingRebootFix("In-memory fix");

        // Instance 2 writes completely different state to disk (a stale snapshot)
        var j2 = new JournalManager(path);
        j2.StartSession(NewTestProfile("game2", "Game2"));
        j2.EndSession();
        j2.ClearPendingRebootFixes();

        // j1 reloads - but its SessionActive flag is still true, so LoadJournal
        // should return the on-disk data but NOT clobber j1's in-memory _current.
        j1.LoadJournal();

        // j1's pending-reboot-fix state must be preserved (would be cleared if _current
        // were overwritten by the disk copy).
        Assert.True(j1.HasPendingRebootFixes());
        Assert.Contains("In-memory fix", j1.GetPendingRebootFixDescriptions());
    }

    [Fact]
    public void LoadJournal_MissingFile_ReturnsNull()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("does-not-exist.json");
        var journal = new JournalManager(path);

        Assert.Null(journal.LoadJournal());
    }

    // ── Pending reboot fixes ──────────────────────────────────────────────────

    [Fact]
    public void RecordPendingRebootFix_ThenReadersSeeIt()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        Assert.False(journal.HasPendingRebootFixes());
        Assert.Empty(journal.GetPendingRebootFixDescriptions());

        journal.RecordPendingRebootFix("Disable dynamictick");
        journal.RecordPendingRebootFix("Disable hypervisor");

        Assert.True(journal.HasPendingRebootFixes());
        var descriptions = journal.GetPendingRebootFixDescriptions();
        Assert.Equal(2, descriptions.Count);
        Assert.Contains("Disable dynamictick", descriptions);
        Assert.Contains("Disable hypervisor", descriptions);
    }

    [Fact]
    public void ClearPendingRebootFixes_ResetsState()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.RecordPendingRebootFix("Fix 1");
        journal.RecordPendingRebootFix("Fix 2");
        journal.ClearPendingRebootFixes();

        Assert.False(journal.HasPendingRebootFixes());
        Assert.Empty(journal.GetPendingRebootFixDescriptions());
    }

    [Fact]
    public void ClearPendingRebootFixes_PersistsAcrossInstances()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        var j1 = new JournalManager(path);
        j1.RecordPendingRebootFix("Fix 1");
        j1.ClearPendingRebootFixes();

        var j2 = new JournalManager(path);
        j2.LoadJournal();
        Assert.False(j2.HasPendingRebootFixes());
        Assert.Empty(j2.GetPendingRebootFixDescriptions());
    }

    [Fact]
    public void GetPendingRebootFixDescriptions_ReturnsSnapshotCopy()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.RecordPendingRebootFix("Fix A");
        var snapshot = journal.GetPendingRebootFixDescriptions();

        // Adding more fixes after the snapshot must not mutate it
        journal.RecordPendingRebootFix("Fix B");
        Assert.Single(snapshot);
        Assert.Equal("Fix A", snapshot[0]);
    }

    // ── Build changed warning ─────────────────────────────────────────────────

    [Fact]
    public void GetBuildChangedWarning_WhenNoneRecorded_ReturnsNull()
    {
        using var temp = new TempPath();
        var journal = new JournalManager(temp.GetFile("state.json"));

        Assert.Null(journal.GetBuildChangedWarning());
    }

    [Fact]
    public void RecordBuildChanged_ThenReaderReturnsWarningContainingBothBuilds()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.RecordBuildChanged("22631.3447", "22635.4510");

        var warning = journal.GetBuildChangedWarning();
        Assert.NotNull(warning);
        Assert.Contains("22631.3447", warning);
        Assert.Contains("22635.4510", warning);
    }

    [Fact]
    public void ClearBuildChangedWarning_NullsIt()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.RecordBuildChanged("22631", "22635");
        Assert.NotNull(journal.GetBuildChangedWarning());

        journal.ClearBuildChangedWarning();
        Assert.Null(journal.GetBuildChangedWarning());
    }

    [Fact]
    public void RecordBuildChanged_PersistsAcrossInstances()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        var j1 = new JournalManager(path);
        j1.RecordBuildChanged("22631", "22635");

        var j2 = new JournalManager(path);
        j2.LoadJournal();
        var warning = j2.GetBuildChangedWarning();
        Assert.NotNull(warning);
        Assert.Contains("22631", warning);
        Assert.Contains("22635", warning);
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────

    [Fact]
    public void StartSession_SetsSessionActiveAndPopulatesActiveGame()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile("steam_42", "Half-Life 3"));

        // Verify via LoadJournal on a fresh instance (reads disk)
        var fresh = new JournalManager(path);
        var data = fresh.LoadJournal();

        Assert.NotNull(data);
        Assert.True(data.SessionActive);
        Assert.NotNull(data.ActiveGame);
        Assert.Equal("Half-Life 3", data.ActiveGame.Name);
        Assert.Equal("test.exe", data.ActiveGame.Executable);
        Assert.Equal(1234, data.ActiveGame.Pid);
        Assert.NotNull(data.SessionStartTime);
    }

    [Fact]
    public void EndSession_ClearsSessionActive()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());
        journal.EndSession();

        var fresh = new JournalManager(path);
        var data = fresh.LoadJournal();
        Assert.NotNull(data);
        Assert.False(data.SessionActive);
    }

    [Fact]
    public void StartSession_OverwritesPreviousSession()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile("old", "Old Game"));
        journal.RecordApplied(NewAppliedResult("OldOpt"));

        // Starting a new session must reset Optimizations and replace ActiveGame
        journal.StartSession(NewTestProfile("new", "New Game"));

        var fresh = new JournalManager(path);
        var data = fresh.LoadJournal();
        Assert.NotNull(data);
        Assert.Equal("New Game", data.ActiveGame?.Name);
        Assert.Empty(data.Optimizations);
    }

    // ── RecordApplied / RecordReverted ────────────────────────────────────────

    [Fact]
    public void RecordApplied_AppendsToOptimizations()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());
        journal.RecordApplied(NewAppliedResult("Opt1", "orig1", "applied1"));
        journal.RecordApplied(NewAppliedResult("Opt2", "orig2", "applied2"));

        var fresh = new JournalManager(path);
        var data = fresh.LoadJournal();
        Assert.NotNull(data);
        Assert.Equal(2, data.Optimizations.Count);

        Assert.Equal("Opt1", data.Optimizations[0].Name);
        Assert.Equal("orig1", data.Optimizations[0].OriginalValue);
        Assert.Equal("applied1", data.Optimizations[0].AppliedValue);
        Assert.Equal(nameof(OptimizationState.Applied), data.Optimizations[0].State);

        Assert.Equal("Opt2", data.Optimizations[1].Name);
        Assert.Equal(nameof(OptimizationState.Applied), data.Optimizations[1].State);
    }

    [Fact]
    public void RecordReverted_UpdatesStateOfMatchingEntry()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());
        journal.RecordApplied(NewAppliedResult("Opt1"));
        journal.RecordApplied(NewAppliedResult("Opt2"));

        journal.RecordReverted("Opt1", OptimizationState.Reverted);

        var fresh = new JournalManager(path);
        var data = fresh.LoadJournal();
        Assert.NotNull(data);
        Assert.Equal(2, data.Optimizations.Count);

        var opt1 = data.Optimizations.Single(o => o.Name == "Opt1");
        Assert.Equal(nameof(OptimizationState.Reverted), opt1.State);

        // Opt2 must still be Applied
        var opt2 = data.Optimizations.Single(o => o.Name == "Opt2");
        Assert.Equal(nameof(OptimizationState.Applied), opt2.State);
    }

    [Fact]
    public void RecordReverted_WithDuplicateNames_UpdatesMostRecentEntry()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());
        // Same-named optimization recorded twice (LIFO revert semantics)
        journal.RecordApplied(NewAppliedResult("OptX", "orig-old", "applied-old"));
        journal.RecordApplied(NewAppliedResult("OptX", "orig-new", "applied-new"));

        journal.RecordReverted("OptX", OptimizationState.Reverted);

        var fresh = new JournalManager(path);
        var data = fresh.LoadJournal();
        Assert.NotNull(data);
        Assert.Equal(2, data.Optimizations.Count);

        // The older entry should still be Applied; only the most recent was reverted
        Assert.Equal(nameof(OptimizationState.Applied), data.Optimizations[0].State);
        Assert.Equal("orig-old", data.Optimizations[0].OriginalValue);
        Assert.Equal(nameof(OptimizationState.Reverted), data.Optimizations[1].State);
        Assert.Equal("orig-new", data.Optimizations[1].OriginalValue);
    }

    [Fact]
    public void RecordReverted_NonexistentName_IsNoop()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(NewTestProfile());
        journal.RecordApplied(NewAppliedResult("Opt1"));

        // Reverting a name that doesn't exist must not throw and must not corrupt state
        journal.RecordReverted("NotThere", OptimizationState.Reverted);

        var fresh = new JournalManager(path);
        var data = fresh.LoadJournal();
        Assert.NotNull(data);
        Assert.Single(data.Optimizations);
        Assert.Equal(nameof(OptimizationState.Applied), data.Optimizations[0].State);
    }

    // ── Corrupt JSON fallback ─────────────────────────────────────────────────

    [Fact]
    public void LoadJournal_CorruptJson_FallsBackGracefully()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        // Garbage on disk
        File.WriteAllText(path, "{ this is not valid JSON at all");

        var journal = new JournalManager(path);
        var loaded = journal.LoadJournal(); // Must not throw

        // The catch branch returns null and leaves _current as its default empty state
        Assert.Null(loaded);
        Assert.False(journal.HasPendingRebootFixes());
        Assert.Empty(journal.GetPendingRebootFixDescriptions());
        Assert.Null(journal.GetBuildChangedWarning());
    }

    [Fact]
    public void LoadJournal_EmptyFile_FallsBackGracefully()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        File.WriteAllText(path, string.Empty);

        var journal = new JournalManager(path);
        var loaded = journal.LoadJournal(); // Must not throw

        Assert.Null(loaded);
        Assert.False(journal.HasPendingRebootFixes());
    }
}
