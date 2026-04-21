using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Tests for the watchdog-to-main-app recovery coordination in <see cref="JournalManager"/>.
/// Ensures <see cref="JournalManager.WasRecoveredDuringCurrentSession"/> correctly
/// distinguishes between (a) no recovery at all, (b) a recovery that happened after the
/// current session started, and (c) a stale recovery marker left over from a previous session.
/// </summary>
public class JournalManagerRecoveryTests
{
    private static GameProfile NewTestProfile() => new()
    {
        Id = "test",
        GameName = "TestGame",
        ExecutableName = "test.exe",
        ProcessId = 1234
    };

    [Fact]
    public void WasRecoveredDuringCurrentSession_NoSessionOrRecovery_ReturnsFalse()
    {
        using var temp = new TempPath();
        var journal = new JournalManager(temp.GetFile("state.json"));

        // Brand-new journal with no session started and no recovery recorded
        Assert.False(journal.WasRecoveredDuringCurrentSession());
    }

    [Fact]
    public void WasRecoveredDuringCurrentSession_SessionStartedButNoRecovery_ReturnsFalse()
    {
        using var temp = new TempPath();
        var journal = new JournalManager(temp.GetFile("state.json"));

        journal.StartSession(NewTestProfile());

        // Session is active but no recovery has occurred
        Assert.False(journal.WasRecoveredDuringCurrentSession());
    }

    [Fact]
    public void WasRecoveredDuringCurrentSession_RecoveryAfterSessionStart_ReturnsTrue()
    {
        using var temp = new TempPath();
        var journal = new JournalManager(temp.GetFile("state.json"));

        journal.StartSession(NewTestProfile());

        // Simulate watchdog stamping the journal AFTER session start
        journal.RecordRecoveryTimestamp(DateTime.UtcNow.AddSeconds(5));

        Assert.True(journal.WasRecoveredDuringCurrentSession());
    }

    [Fact]
    public void WasRecoveredDuringCurrentSession_StaleRecoveryBeforeSessionStart_ReturnsFalse()
    {
        using var temp = new TempPath();
        var journal = new JournalManager(temp.GetFile("state.json"));

        // Stale recovery marker from a previous session
        journal.RecordRecoveryTimestamp(DateTime.UtcNow.AddMinutes(-10));

        // New session starts now — StartSession clears the stale marker
        journal.StartSession(NewTestProfile());

        Assert.False(journal.WasRecoveredDuringCurrentSession());
    }

    [Fact]
    public void StartSession_ClearsStaleLastRecoveryTimestamp()
    {
        using var temp = new TempPath();
        var journal = new JournalManager(temp.GetFile("state.json"));

        // Pre-populate journal with a recovery marker
        journal.RecordRecoveryTimestamp(DateTime.UtcNow.AddMinutes(-10));

        // Even a recovery timestamp set BEFORE the new session is cleared and re-checked
        journal.StartSession(NewTestProfile());

        // Now simulate a recovery BEFORE session start (shouldn't happen in practice but
        // guards against timestamp ordering issues): must return false since StartSession
        // cleared the field and nothing has set it since.
        Assert.False(journal.WasRecoveredDuringCurrentSession());
    }

    [Fact]
    public void RecordRecoveryTimestamp_PersistsAcrossJournalInstances()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");

        // Session started by main app, then watchdog writes recovery timestamp
        var mainApp = new JournalManager(path);
        mainApp.StartSession(NewTestProfile());

        var watchdog = new JournalManager(path);
        // Watchdog loads the journal from disk (simulating its own process lifecycle)
        watchdog.LoadJournal();
        watchdog.RecordRecoveryTimestamp(DateTime.UtcNow.AddSeconds(5));

        // Main app reloads journal after watchdog has written
        var mainAppReload = new JournalManager(path);
        mainAppReload.LoadJournal();

        Assert.True(mainAppReload.WasRecoveredDuringCurrentSession());
    }
}
