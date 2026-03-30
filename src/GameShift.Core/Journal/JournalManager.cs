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
    /// registry-backed optimizations should be re-verified.
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
    private SessionJournalData _current = new();

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public JournalManager()
    {
        _logger = SettingsManager.Logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GameShift");
        Directory.CreateDirectory(dir);
        _journalPath = Path.Combine(dir, "state.json");
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Opens a new session journal entry for the given profile.
    /// Overwrites any previous journal (new session always starts fresh).
    /// </summary>
    public void StartSession(GameProfile profile)
    {
        _current = new SessionJournalData
        {
            Version = 2,
            WindowsBuild = ReadWindowsBuildString(),
            GameShiftVersion = ReadGameShiftVersion(),
            Timestamp = DateTimeOffset.UtcNow,
            SessionActive = true,
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

    /// <summary>
    /// Appends a successful apply result to the journal.
    /// </summary>
    public void RecordApplied(OptimizationResult result)
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

    /// <summary>
    /// Updates the state of an existing journal entry after a revert.
    /// Matches the last entry with the given name (LIFO revert order).
    /// </summary>
    public void RecordReverted(string name, OptimizationState state)
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

    /// <summary>
    /// Marks the session as inactive. Called after all optimizations are reverted.
    /// </summary>
    public void EndSession()
    {
        _current.SessionActive = false;
        Save();
    }

    /// <summary>
    /// Records a Windows Update warning in the journal.
    /// Called by boot recovery when the build stored in the journal differs from the
    /// current OS build, indicating that a Windows Update occurred since the last session.
    /// </summary>
    public void RecordBuildChanged(string buildAtLastSession, string buildAtRecovery)
    {
        _current.BuildChangedWarning = true;
        _current.BuildAtLastSession = buildAtLastSession;
        _current.BuildAtRecovery = buildAtRecovery;
        Save();
    }

    /// <summary>
    /// Reads and deserializes the journal from disk. Returns null if the file does not exist or
    /// cannot be parsed. Also updates internal state so that EndSession() can be called after
    /// a successful load (used by the watchdog recovery path).
    /// </summary>
    public SessionJournalData? LoadJournal()
    {
        try
        {
            if (!File.Exists(_journalPath))
                return null;

            var json = File.ReadAllText(_journalPath);
            var data = JsonSerializer.Deserialize<SessionJournalData>(json, _writeOptions);
            if (data != null)
                _current = data;
            return data;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[JournalManager] Failed to load journal from {Path}", _journalPath);
            return null;
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
