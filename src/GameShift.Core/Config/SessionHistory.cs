using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GameShift.Core.Config;

/// <summary>
/// Represents a single gaming session with timing and DPC performance data.
/// </summary>
public class GameSession
{
    public string GameName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string GameId { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public double AvgDpcBefore { get; set; }
    public double AvgDpcDuring { get; set; }
    public double PeakDpcDuring { get; set; }
    public int OptimizationsApplied { get; set; }

    // ── Benchmark data (Sprint 6) ──────────────────────────────────────

    /// <summary>
    /// Average FPS from a benchmark capture during this session. 0 if no benchmark was run.
    /// </summary>
    public double BenchmarkAvgFps { get; set; }

    /// <summary>
    /// 1% low FPS from a benchmark capture during this session. 0 if no benchmark was run.
    /// </summary>
    public double BenchmarkFps1PercentLow { get; set; }

    /// <summary>
    /// Whether a benchmark was captured during this session.
    /// </summary>
    [JsonIgnore]
    public bool HasBenchmark => BenchmarkAvgFps > 0;
}

/// <summary>
/// Aggregated per-game statistics computed from session history.
/// </summary>
public class PerGameStats
{
    public string GameId { get; init; } = "";
    public string GameName { get; init; } = "";
    public TimeSpan TotalPlayTime { get; init; }
    public int SessionCount { get; init; }
    public double AvgDpcLatency { get; init; }
    public double BestDpcLatency { get; init; }
    public double AvgOptimizationsApplied { get; init; }
}

/// <summary>
/// Persists gaming session history to %AppData%/GameShift/session-history.json.
/// Max 100 sessions stored (oldest trimmed on save). Thread-safe load/save.
/// </summary>
public class SessionHistoryStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<GameSession> _sessions = new();
    private const int MaxSessions = 100;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Production constructor - uses %AppData%\GameShift\session-history.json.
    /// </summary>
    public SessionHistoryStore() : this(GetDefaultFilePath()) { }

    /// <summary>
    /// Test/explicit-path constructor. Allows overriding the session history file location.
    /// Internal to prevent production callers from depending on it.
    /// </summary>
    internal SessionHistoryStore(string filePath)
    {
        _filePath = filePath;
    }

    private static string GetDefaultFilePath()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataPath, "GameShift", "session-history.json");
    }

    /// <summary>Load session history from disk.</summary>
    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _sessions = JsonSerializer.Deserialize<List<GameSession>>(json, _jsonOptions) ?? new();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load session history from {Path}", _filePath);
                _sessions = new();
            }
        }
    }

    /// <summary>Save session history to disk (trims to MaxSessions).</summary>
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                // Trim oldest sessions beyond limit
                while (_sessions.Count > MaxSessions)
                    _sessions.RemoveAt(0);

                var dir = Path.GetDirectoryName(_filePath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_sessions, _jsonOptions);
                var tmpPath = _filePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save session history to {Path}", _filePath);
            }
        }
    }

    /// <summary>Add a completed session and save to disk.</summary>
    public void Add(GameSession session)
    {
        lock (_lock)
        {
            _sessions.Add(session);
        }
        Save();
    }

    /// <summary>Get all sessions (newest first).</summary>
    public IReadOnlyList<GameSession> GetAll()
    {
        lock (_lock)
        {
            return _sessions.OrderByDescending(s => s.StartTime).ToList().AsReadOnly();
        }
    }

    /// <summary>Get sessions for a specific game (newest first).</summary>
    public IReadOnlyList<GameSession> GetForGame(string gameId)
    {
        lock (_lock)
        {
            return _sessions
                .Where(s => s.GameId == gameId)
                .OrderByDescending(s => s.StartTime)
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>Get aggregated stats for a specific game.</summary>
    public PerGameStats? GetStatsForGame(string gameId)
    {
        lock (_lock)
        {
            var gameSessions = _sessions.Where(s => s.GameId == gameId).ToList();
            if (gameSessions.Count == 0) return null;

            return new PerGameStats
            {
                GameId = gameId,
                GameName = gameSessions.Last().GameName,
                TotalPlayTime = TimeSpan.FromTicks(gameSessions.Sum(s => s.Duration.Ticks)),
                SessionCount = gameSessions.Count,
                AvgDpcLatency = gameSessions.Average(s => s.AvgDpcDuring),
                BestDpcLatency = gameSessions.Min(s => s.AvgDpcDuring),
                AvgOptimizationsApplied = gameSessions.Average(s => s.OptimizationsApplied)
            };
        }
    }

    /// <summary>Get the most recent N sessions across all games.</summary>
    public IReadOnlyList<GameSession> GetRecent(int count = 5)
    {
        lock (_lock)
        {
            return _sessions
                .OrderByDescending(s => s.StartTime)
                .Take(count)
                .ToList()
                .AsReadOnly();
        }
    }
}
