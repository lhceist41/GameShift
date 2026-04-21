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
}

// ── JournalManager ────────────────────────────────────────────────────────────

/// <summary>
/// Manages the session state journal at %ProgramData%\GameShift\state.json.
/// All writes are atomic: written to .tmp first, then moved via File.Move(overwrite:true).
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
    internal JournalManager(string journalPath)
    {
        _logger = SettingsManager.Logger;
        _journalPath = journalPath;
        var dir = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

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
    public void StartSession(GameProfile profile)
    {
        lock (_lock)
        {
            _current = new SessionJournalData
            {
                Version = 2,
                WindowsBuild = ReadWindowsBuildString(),
                GameShiftVersion = ReadGameShiftVersion(),
                Timestamp = DateTimeOffset.UtcNow,
                SessionActive = true,
                SessionStartTime = DateTime.UtcNow,
                LastRecoveryTimestamp = null, // Fresh session — clear any stale recovery marker
                ActiveGame = new ActiveGameInfo
                {
                    Name = profile.GameName,
                    Executable = profile.ExecutableName,
                    Pid = profile.ProcessId,
                    StartTime = DateTimeOffset.UtcNow
                },
                Optimizations = new List<JournalEntry>()
            };
            Save();
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
            Save();
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
            Save();
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
            Save();
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
            Save();
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
            Save();
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
            Save();
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
            Save();
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
            Save();
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

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_current, _writeOptions);
            var tempPath = _journalPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _journalPath, overwrite: true); // Atomic on NTFS
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[JournalManager] Failed to save state journal to {Path}", _journalPath);
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

    private static string ReadGameShiftVersion()
    {
        try
        {
            return global::System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
