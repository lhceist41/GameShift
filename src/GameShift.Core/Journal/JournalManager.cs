using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Journal;

// ── Journal schema models ─────────────────────────────────────────────────────

/// <summary>Active game info recorded at session start.</summary>
public class ActiveGameInfo
{
    public string Name { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;
    public int Pid { get; set; }
    public DateTimeOffset StartTime { get; set; }
}

/// <summary>
/// A single optimization entry in the journal.
/// Mutable so RecordReverted can update State in place.
/// </summary>
public class JournalEntry
{
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = nameof(OptimizationState.Pending);
    public string OriginalValue { get; set; } = string.Empty;
    public string AppliedValue { get; set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>
/// A self-describing, reversible record of a per-game GameAction, so the watchdog can undo it
/// after a crash without reconstructing the original action instance. <see cref="Kind"/> selects
/// the undo path (registry / firewall / defender); <see cref="Payload"/> is kind-specific JSON.
/// </summary>
public class GameActionJournalEntry
{
    public string Kind { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>Root journal document written to state.json.</summary>
public class SessionJournalData
{
    public int Version { get; set; } = 2;
    public string WindowsBuild { get; set; } = string.Empty;
    public string GameShiftVersion { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public bool SessionActive { get; set; }
    public ActiveGameInfo? ActiveGame { get; set; }
    public List<JournalEntry> Optimizations { get; set; } = new();
    public List<GameActionJournalEntry> GameActions { get; set; } = new();

    // ── Boot recovery metadata ────────────────────────────────────────────────

    /// <summary>
    /// Set by boot recovery when the Windows build number differs from the value
    /// recorded at the start of the last session. Signals that a Windows Update
    /// occurred between the last session and this boot, and that all persistent
    /// registry-backed optimizations should be re-verified. Read and cleared by
    /// the main app on startup via <see cref="JournalManager.GetBuildChangedWarning"/> /
    /// <see cref="JournalManager.ClearBuildChangedWarning"/>.
    /// </summary>
    public bool BuildChangedWarning { get; set; }

    /// <summary>
    /// The Windows build string recorded in the journal when the session started.
    /// Populated only when <see cref="BuildChangedWarning"/> is true.
    /// </summary>
    public string? BuildAtLastSession { get; set; }

    /// <summary>
    /// The Windows build string read from the registry during boot recovery.
    /// Populated only when <see cref="BuildChangedWarning"/> is true.
    /// </summary>
    public string? BuildAtRecovery { get; set; }

    // ── Reboot-required fixes ─────────────────────────────────────────────────

    /// <summary>
    /// True when changes that require a reboot are pending (e.g., interrupt affinity,
    /// MSI mode, BCD edits). Signals the main app to show a reboot prompt on launch.
    /// Read and cleared by the main app on startup via
    /// <see cref="JournalManager.HasPendingRebootFixes"/> /
    /// <see cref="JournalManager.ClearPendingRebootFixes"/>.
    /// </summary>
    public bool HasPendingRebootFixes { get; set; }

    /// <summary>
    /// Descriptions of pending reboot-required fixes for display in the UI.
    /// </summary>
    public List<string> PendingRebootFixDescriptions { get; set; } = new();

    // ── Watchdog recovery coordination ────────────────────────────────────────

    /// <summary>
    /// UTC timestamp of the last watchdog-initiated recovery, if any.
    /// Set by WatchdogRevertEngine after a successful revert.
    /// OptimizationEngine checks this during DeactivateProfileAsync to avoid
    /// double-reverting optimizations the watchdog already rolled back.
    /// </summary>
    public DateTime? LastRecoveryTimestamp { get; set; }

    /// <summary>
    /// UTC timestamp when the current session was started via StartSession().
    /// Used by OptimizationEngine to detect whether a recovery happened during the session.
    /// </summary>
    public DateTime? SessionStartTime { get; set; }

    /// <summary>
    /// How often a start of the app began replaying this session after it did not end normally.
    /// Persisted before each replay, so a replay that takes the process down is not retried forever.
    /// </summary>
    public int RecoveryAttempts { get; set; }
}

internal enum JournalPeekStatus
{
    Missing,
    Parsed,

    /// <summary>The content is not a session record: invalid JSON, or JSON that deserializes to null.</summary>
    NotParsed,

    /// <summary>The file exists but could not be read, for example while another process holds it.</summary>
    NotRead
}

/// <param name="Data">Set only for <see cref="JournalPeekStatus.Parsed"/>.</param>
internal readonly record struct JournalPeek(JournalPeekStatus Status, SessionJournalData? Data);

// ── JournalManager ────────────────────────────────────────────────────────────

/// <summary>
/// Manages the session state journal at %ProgramData%\GameShift\state.json.
/// All writes are atomic: written to .tmp first, then renamed over the journal, both relative to a
/// handle on the folder (<see cref="JournalFolder"/>).
/// Used by OptimizationEngine to persist apply/revert state for crash recovery.
/// </summary>
public class JournalManager
{
    private readonly string _journalPath;
    private readonly ILogger _logger;
    // Static: multiple JournalManager instances exist (OptimizationEngine,
    // CoreIsolationManager, KernelTuningManager) but all write to the same
    // %ProgramData%\GameShift\state.json file. A per-instance lock would let
    // concurrent instances race on the shared .tmp file.
    private static readonly object _lock = new();
    // Set once the production journal directory's ACL has been hardened this process.
    private static bool _dirHardened;
    // Set when the production journal directory could not be made a plain folder; nothing is
    // written there for the rest of the process.
    private static string? _sharedFolderRefusal;
    private readonly string? _writeRefusal;
    private SessionJournalData _current = new();

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Production constructor - uses %ProgramData%\GameShift\state.json.
    /// </summary>
    public JournalManager() : this(GetDefaultJournalPath()) { }

    /// <summary>
    /// Test/explicit-path constructor. Allows overriding the journal file location.
    /// Internal to prevent production callers from depending on it.
    /// </summary>
    internal JournalManager(string journalPath) : this(journalPath, sharedFolderOwnerSid: null, afterReparseRemoved: null) { }

    /// <summary>
    /// With <paramref name="sharedFolderOwnerSid"/> set, the folder is prepared like the production
    /// one (<see cref="PrepareJournalDirectory"/>) with that owner, so a test can run it unelevated.
    /// </summary>
    internal JournalManager(string journalPath, string? sharedFolderOwnerSid, Action? afterReparseRemoved)
    {
        _logger = SettingsManager.Logger;
        _journalPath = journalPath;
        var dir = Path.GetDirectoryName(_journalPath);
        if (string.IsNullOrEmpty(dir))
            return;

        if (sharedFolderOwnerSid != null)
        {
            if (PrepareJournalDirectory(dir, sharedFolderOwnerSid, _logger, out var reason, afterReparseRemoved) == JournalFolderSetup.Unavailable)
                _writeRefusal = reason;
            return;
        }

        var defaultDir = Path.GetDirectoryName(GetDefaultJournalPath());
        if (string.IsNullOrEmpty(defaultDir) || !string.Equals(dir, defaultDir, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return;
        }

        // Harden the shared %ProgramData%\GameShift directory once per process. By default it
        // inherits ProgramData's ACL, which lets any standard user add files - so a low-priv
        // user could plant or tamper with state.json, which the elevated app and the SYSTEM
        // boot-recovery task consume (a privilege-escalation path). Lock it to SYSTEM/Admins
        // full control and Users read-only. Only the real production directory is touched.
        lock (_lock)
        {
            if (_sharedFolderRefusal == null && !_dirHardened)
            {
                switch (PrepareJournalDirectory(dir, JournalTrust.AdministratorsSid, _logger, out var reason))
                {
                    // Latch only on success so a transient failure (AV/sharing lock) doesn't
                    // permanently disable hardening for the process - a later construction can retry.
                    case JournalFolderSetup.Ready:
                        _dirHardened = true;
                        break;
                    case JournalFolderSetup.Unavailable:
                        _sharedFolderRefusal = reason;
                        break;
                }
            }

            _writeRefusal = _sharedFolderRefusal;
        }
    }

    /// <summary>
    /// Makes <paramref name="dir"/> a plain folder to journal in. A reparse point in its place
    /// (which a standard user can plant) is removed, never what it names, and a missing folder is
    /// created with <paramref name="ownerSid"/> as owner and the hardened DACL in the same call, so
    /// no user ever owns or can write it. An existing plain folder gets the hardened DACL by handle,
    /// its owner unchanged.
    /// </summary>
    internal static JournalFolderSetup PrepareJournalDirectory(
        string dir, string ownerSid, ILogger logger, out string? reason, Action? afterReparseRemoved = null)
    {
        reason = null;
        if (!OperatingSystem.IsWindows())
            return JournalFolderSetup.Ready;

        try
        {
            FileAttributes? attributes = null;
            try
            {
                attributes = File.GetAttributes(dir); // the entry itself, not what a reparse point names
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            if (attributes is { } existing)
            {
                if ((existing & FileAttributes.ReparsePoint) == 0)
                {
                    if ((existing & FileAttributes.Directory) != 0)
                        return TryHardenJournalDirectory(dir, logger) ? JournalFolderSetup.Ready : JournalFolderSetup.NotHardened;

                    return Unavailable(dir, "a file is in its place", logger, out reason);
                }

                if (!WindowsJournalSecurity.TryRemoveReparsePoint(dir, out var removeRefusal))
                    return Unavailable(dir, $"it is a reparse point that could not be removed: {removeRefusal}", logger, out reason);

                logger.Warning("[JournalManager] Removed a reparse point at {Dir}; its target was not touched", dir);
                afterReparseRemoved?.Invoke();
            }

            if (!WindowsJournalSecurity.TryCreateHardenedFolder(dir, ownerSid, out var createRefusal))
                return Unavailable(dir, $"the folder could not be created: {createRefusal}", logger, out reason);

            logger.Information("[JournalManager] Created {Dir} with the hardened owner and ACL", dir);
            return JournalFolderSetup.Ready;
        }
        catch (Exception ex)
        {
            return Unavailable(dir, $"preparing it failed: {ex.Message}", logger, out reason);
        }
    }

    private static JournalFolderSetup Unavailable(string dir, string why, ILogger logger, out string reason)
    {
        reason = why;
        logger.Error("[JournalManager] {Dir}: {Reason}; crash recovery is off for this run", dir, why);
        return JournalFolderSetup.Unavailable;
    }

    /// <summary>
    /// Replaces the journal directory's DACL with an inheritance-protected set: SYSTEM and
    /// Administrators get full control, BUILTIN\Users get read and execute only. This prevents a
    /// standard user from creating or modifying state.json (which is consumed elevated and by the
    /// SYSTEM boot-recovery task). Best-effort: logs and continues if the ACL cannot be changed
    /// (e.g. the app is running non-elevated in a dev build). Set through a handle on the folder
    /// itself, so a junction a user put in its place is refused rather than followed.
    /// </summary>
    internal static bool TryHardenJournalDirectory(string dir, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
            return true; // nothing to do off-Windows; treat as done so we don't retry forever

        try
        {
            if (!WindowsJournalSecurity.TryHardenFolderDacl(dir, out var refusal))
            {
                logger.Warning("[JournalManager] Did not harden ACL on {Dir}: {Reason} (continuing)", dir, refusal);
                return false;
            }

            logger.Information("[JournalManager] Hardened ACL on {Dir} (SYSTEM/Admins full, Users read-only)", dir);
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[JournalManager] Could not harden ACL on {Dir} (continuing)", dir);
            return false;
        }
    }

    /// <summary>The journal file this instance reads and writes.</summary>
    public string JournalPath => _journalPath;

    /// <summary>
    /// Why the journal folder could not be made a plain folder for this run, or null when it could.
    /// When set, nothing in the folder is written, and nothing in it should be read either: it may
    /// be a link to a folder a standard user controls.
    /// </summary>
    public string? FolderRefusal => _writeRefusal;

    private static string GetDefaultJournalPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GameShift");
        return Path.Combine(dir, "state.json");
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Opens a new session journal entry for the given profile.
    /// Overwrites any previous journal (new session always starts fresh).
    /// </summary>
    /// <param name="profile">The profile being applied.</param>
    /// <param name="gameName">
    /// The detected game's display name, recorded instead of the profile's name when given: a game
    /// on the default profile would otherwise be named "Default Profile" in the recovery notice.
    /// </param>
    public void StartSession(GameProfile profile, string? gameName = null)
    {
        lock (_lock)
        {
            _current = new SessionJournalData
            {
                Version = 2,
                WindowsBuild = ReadWindowsBuildString(),
                GameShiftVersion = ReadGameShiftVersion(global::System.Reflection.Assembly.GetEntryAssembly()),
                Timestamp = DateTimeOffset.UtcNow,
                SessionActive = true,
                SessionStartTime = DateTime.UtcNow,
                LastRecoveryTimestamp = null, // Fresh session - clear any stale recovery marker
                RecoveryAttempts = 0,
                ActiveGame = new ActiveGameInfo
                {
                    Name = string.IsNullOrWhiteSpace(gameName) ? profile.GameName : gameName,
                    Executable = profile.ExecutableName,
                    Pid = profile.ProcessId,
                    StartTime = DateTimeOffset.UtcNow
                },
                Optimizations = new List<JournalEntry>()
            };
            bool saved = Save(SaveScope.SessionState);

            // Crash recovery for journaled optimizations depends on this write landing. If it did
            // not persist (disk full, ProgramData not writable, AV lock), the watchdog/boot recovery
            // cannot revert after a crash - surface it loudly instead of failing silently. The
            // in-memory revert path still works for a normal game-close, so we do not abort the apply.
            if (!saved || !VerifySessionPersisted())
            {
                _logger.Error(
                    "[JournalManager] Session journal did not persist to {Path} - crash recovery is unavailable for this session",
                    _journalPath);
            }
        }
    }

    /// <summary>Reads the journal back from disk and confirms an active session was recorded.</summary>
    private bool VerifySessionPersisted()
    {
        try
        {
            if (!File.Exists(_journalPath)) return false;
            var data = JsonSerializer.Deserialize<SessionJournalData>(File.ReadAllText(_journalPath));
            return data?.SessionActive == true;
        }
        catch
        {
            // The file exists (the write landed) but we couldn't read it back right now - e.g. a
            // transient AV lock. Don't raise a false "recovery unavailable" alarm; treat only a
            // genuinely missing file as a persistence failure.
            return File.Exists(_journalPath);
        }
    }

    /// <summary>
    /// Appends a successful apply result to the journal.
    /// </summary>
    public void RecordApplied(OptimizationResult result)
    {
        lock (_lock)
        {
            _current.Optimizations.Add(new JournalEntry
            {
                Name = result.Name,
                State = result.State.ToString(),
                OriginalValue = result.OriginalValue,
                AppliedValue = result.AppliedValue,
                AppliedAt = DateTimeOffset.UtcNow
            });
            Save(SaveScope.SessionState);
        }
    }

    /// <summary>
    /// Appends a reversible per-game GameAction record to the journal so the watchdog can undo it
    /// after a crash. Uses the same in-memory session journal as the engine (share one instance).
    /// </summary>
    public void RecordGameAction(string kind, string payload)
    {
        lock (_lock)
        {
            _current.GameActions.Add(new GameActionJournalEntry
            {
                Kind = kind,
                Payload = payload,
                AppliedAt = DateTimeOffset.UtcNow
            });
            Save(SaveScope.SessionState);
        }
    }

    /// <summary>
    /// Updates the state of an existing journal entry after a revert.
    /// Matches the last entry with the given name (LIFO revert order).
    /// </summary>
    public void RecordReverted(string name, OptimizationState state)
    {
        lock (_lock)
        {
            // Walk in reverse to find the most-recently-applied entry
            for (int i = _current.Optimizations.Count - 1; i >= 0; i--)
            {
                if (_current.Optimizations[i].Name == name)
                {
                    _current.Optimizations[i].State = state.ToString();
                    break;
                }
            }
            Save(SaveScope.SessionState);
        }
    }

    /// <summary>
    /// Marks the session as inactive. Called after all optimizations are reverted.
    /// </summary>
    public void EndSession()
    {
        lock (_lock)
        {
            _current.SessionActive = false;
            Save(SaveScope.SessionState);
        }
    }

    /// <summary>
    /// Records a pending reboot-required fix in the journal.
    /// Called after interrupt affinity, MSI mode, or BCD changes that need a reboot.
    /// </summary>
    public void RecordPendingRebootFix(string description)
    {
        lock (_lock)
        {
            _current.HasPendingRebootFixes = true;
            _current.PendingRebootFixDescriptions.Add(description);
            Save(SaveScope.RecoveryMetadata);
        }
    }

    /// <summary>
    /// Returns true if there are pending DPC fixes that required a reboot but haven't been
    /// acknowledged by the user. Read by the main app on startup to surface a prompt.
    /// </summary>
    public bool HasPendingRebootFixes()
    {
        lock (_lock)
        {
            return _current.HasPendingRebootFixes;
        }
    }

    /// <summary>
    /// Returns a snapshot of the pending reboot-required fix descriptions.
    /// The returned list is a copy and is safe to enumerate without additional locking.
    /// </summary>
    public IReadOnlyList<string> GetPendingRebootFixDescriptions()
    {
        lock (_lock)
        {
            return _current.PendingRebootFixDescriptions.ToList();
        }
    }

    /// <summary>
    /// Clears all pending reboot-required fixes (called after the user acknowledges them).
    /// </summary>
    public void ClearPendingRebootFixes()
    {
        lock (_lock)
        {
            _current.HasPendingRebootFixes = false;
            _current.PendingRebootFixDescriptions.Clear();
            Save(SaveScope.RecoveryMetadata);
        }
    }

    /// <summary>
    /// Records a Windows Update warning in the journal.
    /// Called by boot recovery when the build stored in the journal differs from the
    /// current OS build, indicating that a Windows Update occurred since the last session.
    /// </summary>
    public void RecordBuildChanged(string buildAtLastSession, string buildAtRecovery)
    {
        lock (_lock)
        {
            _current.BuildChangedWarning = true;
            _current.BuildAtLastSession = buildAtLastSession;
            _current.BuildAtRecovery = buildAtRecovery;
            Save(SaveScope.RecoveryMetadata);
        }
    }

    /// <summary>
    /// Returns a formatted build-change warning string, or null if Windows has not changed
    /// since the last session. Read by the main app on startup to surface a prompt.
    /// </summary>
    public string? GetBuildChangedWarning()
    {
        lock (_lock)
        {
            if (!_current.BuildChangedWarning)
                return null;

            var before = _current.BuildAtLastSession ?? "unknown";
            var after = _current.BuildAtRecovery ?? "unknown";
            return $"Windows build changed from {before} to {after}.";
        }
    }

    /// <summary>
    /// Clears the build-changed warning (called after the user acknowledges it).
    /// </summary>
    public void ClearBuildChangedWarning()
    {
        lock (_lock)
        {
            _current.BuildChangedWarning = false;
            _current.BuildAtLastSession = null;
            _current.BuildAtRecovery = null;
            Save(SaveScope.RecoveryMetadata);
        }
    }

    /// <summary>
    /// Counts one more replay attempt of the loaded session and persists it. Returns false when the
    /// new count could not be read back from disk.
    /// </summary>
    public bool RecordRecoveryAttempt()
    {
        lock (_lock)
        {
            _current.RecoveryAttempts++;
            return Save(SaveScope.SessionState) && ReadFromDisk()?.RecoveryAttempts == _current.RecoveryAttempts;
        }
    }

    /// <summary>
    /// Copies the journal file to <c>state.{suffix}.json</c> in the same folder, so the records
    /// survive the next session start. Atomic like <see cref="Save"/> and, like it, read and written
    /// only through a handle on the folder. Returns the copy's path, or null when there was nothing
    /// to copy or the copy failed.
    /// </summary>
    public string? SaveEvidenceCopy(string suffix)
    {
        lock (_lock)
        {
            try
            {
                using var folder = OpenFolderForWrite();
                var content = folder?.ReadFile(Path.GetFileName(_journalPath));
                if (folder == null || content == null)
                    return null;

                var evidenceName = $"state.{suffix}.json";
                var evidencePath = Path.Combine(Path.GetDirectoryName(_journalPath) ?? string.Empty, evidenceName);
                if (!folder.WriteFileAtomically(evidenceName, content))
                    _logger.Debug("[JournalManager] Could not make Administrators the owner of {Path}", evidencePath);
                return evidencePath;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[JournalManager] Could not save a copy of {Path} as {Suffix}", _journalPath, suffix);
                return null;
            }
        }
    }

    /// <summary>
    /// Reads the journal file without making it this instance's state, so a caller can look at a
    /// file before deciding to trust it. Null when it is missing, cannot be read or cannot be parsed.
    /// </summary>
    public SessionJournalData? PeekJournal() => PeekJournalFile().Data;

    /// <summary>
    /// Like <see cref="PeekJournal"/>, but tells a missing file apart from one that could not be
    /// parsed and one that could not be read.
    /// </summary>
    internal JournalPeek PeekJournalFile()
    {
        lock (_lock)
        {
            string json;
            try
            {
                json = File.ReadAllText(_journalPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new JournalPeek(JournalPeekStatus.Missing, null);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[JournalManager] Could not read the journal at {Path}", _journalPath);
                return new JournalPeek(JournalPeekStatus.NotRead, null);
            }

            try
            {
                var data = JsonSerializer.Deserialize<SessionJournalData>(json, _writeOptions);
                if (data != null)
                    return new JournalPeek(JournalPeekStatus.Parsed, data);
                _logger.Warning("[JournalManager] The journal at {Path} holds no session record", _journalPath);
                return new JournalPeek(JournalPeekStatus.NotParsed, null);
            }
            catch (JsonException ex)
            {
                _logger.Warning(ex, "[JournalManager] Could not parse the journal at {Path}", _journalPath);
                return new JournalPeek(JournalPeekStatus.NotParsed, null);
            }
            catch (Exception ex)
            {
                // Not known to be damaged content, so it is treated like a failed read: nothing is moved.
                _logger.Warning(ex, "[JournalManager] Could not read the journal at {Path}", _journalPath);
                return new JournalPeek(JournalPeekStatus.NotRead, null);
            }
        }
    }

    /// <summary>
    /// Records the UTC timestamp of a watchdog-initiated recovery.
    /// Called by <see cref="WatchdogRevertEngine"/> after successfully reverting
    /// optimizations from the journal so the main app can skip its own redundant
    /// LIFO revert on next DeactivateProfileAsync.
    /// </summary>
    public void RecordRecoveryTimestamp(DateTime utcNow)
    {
        lock (_lock)
        {
            _current.LastRecoveryTimestamp = utcNow;
            Save(SaveScope.SessionState);
        }
    }

    /// <summary>
    /// Returns true if the watchdog has performed a recovery since the current session started.
    /// The OptimizationEngine uses this to skip revert of optimizations the watchdog already rolled back.
    /// </summary>
    public bool WasRecoveredDuringCurrentSession()
    {
        lock (_lock)
        {
            var start = _current.SessionStartTime;
            var recovery = _current.LastRecoveryTimestamp;
            return recovery.HasValue && start.HasValue && recovery.Value > start.Value;
        }
    }

    /// <summary>
    /// Reads and deserializes the journal from disk. Returns null if the file does not exist or
    /// cannot be parsed. Also updates internal state so that EndSession() can be called after
    /// a successful load (used by the watchdog recovery path).
    /// </summary>
    public SessionJournalData? LoadJournal()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_journalPath))
                    return null;

                var json = File.ReadAllText(_journalPath);
                var data = JsonSerializer.Deserialize<SessionJournalData>(json, _writeOptions);
                if (data != null && !_current.SessionActive)
                    _current = data;
                return data;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[JournalManager] Failed to load journal from {Path}", _journalPath);
                return null;
            }
        }
    }

    // ── Atomic write ──────────────────────────────────────────────────────────

    /// <summary>
    /// Identifies which half of the journal a write owns. Several JournalManager instances
    /// (OptimizationEngine, KernelTuningManager, CoreIsolationManager, the App-layer reader)
    /// write the same state.json, each from its own in-memory <see cref="_current"/>. A naive
    /// whole-file write would drop the half this instance did not touch, so the unowned half is
    /// re-read from disk and preserved on every save.
    /// </summary>
    private enum SaveScope
    {
        /// <summary>
        /// Active session state: ActiveGame, Optimizations, GameActions, session/recovery
        /// timestamps, SessionActive. Owned by the optimization engine / watchdog recovery.
        /// The boot-recovery metadata is preserved from disk.
        /// </summary>
        SessionState,

        /// <summary>
        /// Boot-recovery metadata: pending reboot-required fixes and the Windows-build-changed
        /// warning. Owned by KernelTuningManager / CoreIsolationManager / boot recovery / the App
        /// reader. The active session state is preserved from disk.
        /// </summary>
        RecoveryMetadata
    }

    /// <summary>
    /// The boot-recovery metadata fields, written independently of the active session. Kept in
    /// one place so <see cref="Save"/> can carry the half it does not own across a merge.
    /// </summary>
    private static void CopyRecoveryMetadata(SessionJournalData from, SessionJournalData to)
    {
        to.HasPendingRebootFixes = from.HasPendingRebootFixes;
        to.PendingRebootFixDescriptions = from.PendingRebootFixDescriptions;
        to.BuildChangedWarning = from.BuildChangedWarning;
        to.BuildAtLastSession = from.BuildAtLastSession;
        to.BuildAtRecovery = from.BuildAtRecovery;
    }

    /// <summary>Reads the on-disk journal without touching <see cref="_current"/>. Returns null
    /// if the file is missing or cannot be parsed (the caller then writes unmerged).</summary>
    private SessionJournalData? ReadFromDisk()
    {
        try
        {
            if (!File.Exists(_journalPath))
                return null;
            return JsonSerializer.Deserialize<SessionJournalData>(File.ReadAllText(_journalPath), _writeOptions);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[JournalManager] Could not re-read journal for merge at {Path}", _journalPath);
            return null;
        }
    }

    /// <summary>The same through the folder handle a save writes through.</summary>
    private SessionJournalData? ReadFromDisk(JournalFolder folder)
    {
        try
        {
            var content = folder.ReadFile(Path.GetFileName(_journalPath));
            return content == null
                ? null
                : JsonSerializer.Deserialize<SessionJournalData>(Encoding.UTF8.GetString(content).TrimStart('\uFEFF'), _writeOptions);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[JournalManager] Could not re-read journal for merge at {Path}", _journalPath);
            return null;
        }
    }

    /// <summary>
    /// Opens the journal folder for a write, or logs why nothing is written. A folder that is a
    /// reparse point is never written through, even when it points somewhere harmless.
    /// </summary>
    private JournalFolder? OpenFolderForWrite()
    {
        var dir = Path.GetDirectoryName(_journalPath);
        if (_writeRefusal != null || string.IsNullOrEmpty(dir))
        {
            _logger.Warning("[JournalManager] Not writing the state journal: {Reason}", _writeRefusal ?? "it has no folder");
            return null;
        }

        var folder = JournalFolder.Open(dir, out var refusal);
        if (folder == null)
            _logger.Warning("[JournalManager] Not writing the state journal to {Dir}: {Reason}", dir, refusal);
        return folder;
    }

    /// <summary>
    /// Persists the journal atomically (write to .tmp, then rename over the journal), merging back the
    /// half of the document this write does not own so a concurrent instance's write is not lost.
    /// The caller holds the static <see cref="_lock"/>, which makes this read-modify-write atomic
    /// against the other instances sharing the same file.
    /// </summary>
    /// <returns>True when the journal was written.</returns>
    private bool Save(SaveScope scope)
    {
        try
        {
            using var folder = OpenFolderForWrite();
            if (folder == null)
                return false;

            var toWrite = _current;
            var disk = ReadFromDisk(folder);
            if (disk != null)
            {
                if (scope == SaveScope.SessionState)
                {
                    // This write owns the session; keep any recovery metadata another instance set.
                    CopyRecoveryMetadata(from: disk, to: _current);
                }
                else
                {
                    // This write owns the recovery metadata; keep the session another instance owns
                    // by writing the on-disk document with only our metadata overlaid onto it.
                    CopyRecoveryMetadata(from: _current, to: disk);
                    toWrite = disk;
                }
            }

            var json = JsonSerializer.Serialize(toWrite, _writeOptions);
            if (!folder.WriteFileAtomically(Path.GetFileName(_journalPath), Encoding.UTF8.GetBytes(json)))
                _logger.Debug("[JournalManager] Could not make Administrators the owner of {Path}", _journalPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[JournalManager] Failed to save state journal to {Path}", _journalPath);
            return false;
        }
    }

    // ── Metadata helpers ──────────────────────────────────────────────────────

    private static string ReadWindowsBuildString()
    {
        try
        {
            var build = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "CurrentBuildNumber", "0")?.ToString() ?? "0";
            var ubr = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "UBR", "0")?.ToString() ?? "0";
            return $"{build}.{ubr}";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// The product version of the running app (GameShift.App.exe carries it; this assembly does not),
    /// in the same three-part form the updater compares, or "unknown" without an entry assembly.
    /// </summary>
    internal static string ReadGameShiftVersion(global::System.Reflection.Assembly? entryAssembly)
    {
        try
        {
            return entryAssembly?.GetName().Version?.ToString(3) ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
