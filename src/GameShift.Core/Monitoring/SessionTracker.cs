using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Optimization;
using Serilog;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Tracks gaming sessions by subscribing to GameDetector start/stop events.
/// Records session timing, DPC latency snapshots, and optimization counts.
/// Saves completed sessions to SessionHistoryStore.
/// </summary>
public class SessionTracker
{
    private readonly GameDetector _detector;
    private readonly DpcLatencyMonitor? _dpcMonitor;
    private readonly OptimizationEngine _engine;
    private readonly SessionHistoryStore _store;
    private GameSession? _currentSession;
    private readonly object _lock = new();

    /// <summary>Fired when a gaming session ends and has been saved.</summary>
    public event EventHandler<GameSession>? SessionEnded;

    public SessionTracker(
        GameDetector detector,
        DpcLatencyMonitor? dpcMonitor,
        OptimizationEngine engine,
        SessionHistoryStore store)
    {
        _detector = detector;
        _dpcMonitor = dpcMonitor;
        _engine = engine;
        _store = store;

        _detector.GameStarted += OnGameStarted;
        _detector.AllGamesStopped += OnAllGamesStopped;
    }

    private void OnGameStarted(object? sender, GameDetectedEventArgs e)
    {
        lock (_lock)
        {
            // Only track the first game (don't overwrite if multiple games detected)
            if (_currentSession != null) return;

            _currentSession = new GameSession
            {
                GameName = e.GameName,
                ExecutablePath = e.ExecutablePath,
                GameId = e.GameId,
                StartTime = DateTime.Now,
                AvgDpcBefore = _dpcMonitor?.AverageLatencyMicroseconds ?? 0
            };

            Log.Information("Session tracking started for {GameName} ({GameId})", e.GameName, e.GameId);
        }
    }

    private void OnAllGamesStopped(object? sender, EventArgs e)
    {
        GameSession? completed;

        lock (_lock)
        {
            if (_currentSession == null) return;

            _currentSession.EndTime = DateTime.Now;
            _currentSession.Duration = _currentSession.EndTime - _currentSession.StartTime;
            _currentSession.AvgDpcDuring = _dpcMonitor?.AverageLatencyMicroseconds ?? 0;
            _currentSession.PeakDpcDuring = _dpcMonitor?.PeakLatencyMicroseconds ?? 0;
            _currentSession.OptimizationsApplied = _engine.AppliedCount;

            completed = _currentSession;
            _currentSession = null;
        }

        _store.Add(completed);
        Log.Information(
            "Session tracking ended for {GameName}: {Duration}, DPC avg={AvgDpc:F0}us, optimizations={Count}",
            completed.GameName,
            completed.Duration,
            completed.AvgDpcDuring,
            completed.OptimizationsApplied);

        SessionEnded?.Invoke(this, completed);
    }
}
