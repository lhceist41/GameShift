using System.Diagnostics;
using GameShift.Core.Config;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// ProBalance-style dynamic background CPU restraint during gaming sessions.
///
/// Every 5 seconds, samples CPU usage of all non-game processes using
/// <see cref="Process.TotalProcessorTime"/> deltas. If a background process
/// exceeds 15% CPU for 3 consecutive samples, it is demoted to
/// <see cref="ProcessPriorityClass.BelowNormal"/>. When it drops below threshold
/// for 5 consecutive samples, its original priority is restored.
///
/// A safety list protects critical processes: the active game, anti-cheat,
/// audio services, system processes, and GameShift itself.
///
/// <para>Lifecycle (managed by <see cref="OptimizationEngine"/>):</para>
/// <list type="bullet">
///   <item><see cref="Start"/> - called after all optimizations are applied</item>
///   <item><see cref="Stop"/> - called before LIFO revert, restores all restrained processes</item>
/// </list>
/// </summary>
public sealed class ProBalanceService : IDisposable
{
    private readonly ILogger _logger = SettingsManager.Logger;

    // ── Thresholds ────────────────────────────────────────────────────────────

    private const double CpuThresholdPercent = 15.0;
    private const int SustainedSamplesToRestrain = 3;
    private const int SustainedSamplesToRestore = 5;
    private const int SampleIntervalMs = 5000; // 5s - balances responsiveness vs CPU overhead

    // ── Safety list - never restrain ──────────────────────────────────────────

    private static readonly HashSet<string> ProtectedProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // System critical
            "csrss", "smss", "services", "lsass", "svchost",
            "wininit", "winlogon", "dwm", "System",
            // Audio
            "AudioSrv", "Audiosrv", "audiodg",
            // Anti-cheat
            "vgc", "vgtray", "BEService", "BEDaisy",
            "EasyAntiCheat", "EasyAntiCheat_EOS",
            "EAAntiCheat.GameService",
            "FACEITService", "FACEITClient", "faceit",
            // GameShift
            "GameShift", "GameShift.App", "GameShift.Watchdog",
        };

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<int, ProcessCpuTracker> _trackers = new();
    private readonly object _lock = new();
    private IDisposable? _timer;
    private string[] _gameProcessNames = Array.Empty<string>();
    private int _gamePid;
    // volatile: read in the timer callback (OnSample) without the lock, written from Start/Stop.
    private volatile bool _running;

    private readonly Func<IReadOnlyList<ProcessCpuSample>> _sampleProcesses;
    private readonly IProcessPriorityAccess _priorities;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<Action, IDisposable> _startTimer;
    private volatile Func<IEnumerable<int>>? _sessionGamePids;

    /// <summary>One process in a sample: CPU time is null when it cannot be read.</summary>
    internal readonly record struct ProcessCpuSample(int Pid, string Name, TimeSpan? CpuTime);

    public ProBalanceService()
        : this(SampleSystemProcesses, SystemProcessPriorityAccess.Instance, () => DateTime.UtcNow,
            callback => new Timer(_ => callback(), null, SampleIntervalMs, SampleIntervalMs))
    {
    }

    internal ProBalanceService(
        Func<IReadOnlyList<ProcessCpuSample>> sampleProcesses,
        IProcessPriorityAccess priorities,
        Func<DateTime> utcNow,
        Func<Action, IDisposable> startTimer)
    {
        _sampleProcesses = sampleProcesses;
        _priorities = priorities;
        _utcNow = utcNow;
        _startTimer = startTimer;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Supplies the PIDs of every game the session tracks, read at each sample, so a second game
    /// running alongside the first is never restrained.
    /// </summary>
    internal void ProtectSessionGames(Func<IEnumerable<int>> sessionGamePids) =>
        _sessionGamePids = sessionGamePids;

    /// <summary>
    /// Starts the ProBalance sampling loop. Call after all optimizations are applied.
    /// </summary>
    public void Start(int gamePid, string executableName)
    {
        // A second activation (Quick Optimize, then a game) must not drop the trackers of
        // processes it already restrained, or they stay BelowNormal for good.
        Stop();

        _gamePid = gamePid;
        _gameProcessNames = !string.IsNullOrEmpty(executableName)
            ? new[] { Path.GetFileNameWithoutExtension(executableName) }
            : Array.Empty<string>();

        lock (_lock)
        {
            _trackers.Clear();
            _running = true;
        }

        _timer = _startTimer(OnSample);
        _logger.Information(
            "[ProBalance] Started - monitoring background CPU usage (threshold: {Pct}%, restrain after {R} samples)",
            CpuThresholdPercent, SustainedSamplesToRestrain);
    }

    /// <summary>
    /// Stops sampling and restores all currently restrained processes to their original priority.
    /// </summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;

        if (!_running) return;
        _running = false;

        lock (_lock)
        {
            int restoredCount = 0;

            foreach (var (pid, tracker) in _trackers)
            {
                if (!tracker.IsRestrained) continue;

                try
                {
                    if (!_priorities.RestoreIfSameProcess(pid, tracker.ProcessName, tracker.OriginalPriority))
                        continue; // PID reused by another process

                    restoredCount++;
                    _logger.Information(
                        "[ProBalance] Restored {Name} (PID {Pid}) to {Priority} on session end",
                        tracker.ProcessName, pid, tracker.OriginalPriority);
                }
                catch (ArgumentException)
                {
                    // Process already exited - nothing to restore
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "[ProBalance] Failed to restore priority for {Name} (PID {Pid})",
                        tracker.ProcessName, pid);
                }
            }

            _trackers.Clear();

            if (restoredCount > 0)
                _logger.Information("[ProBalance] Restored {Count} restrained process(es) on stop", restoredCount);
        }

        _logger.Information("[ProBalance] Stopped");
    }

    public void Dispose() => Stop();

    // ── Sampling loop ─────────────────────────────────────────────────────────

    private void OnSample() => SampleOnce();

    /// <summary>One sampling pass. Internal so tests can drive it without the timer.</summary>
    internal void SampleOnce()
    {
        if (!_running) return;

        try
        {
            if (!TryReadSessionGamePids(out var sessionGames))
                return;

            var liveProcesses = new HashSet<int>();
            int processorCount = Environment.ProcessorCount;
            var now = _utcNow();

            foreach (var sample in _sampleProcesses())
            {
                int pid = sample.Pid;
                string name = sample.Name;

                liveProcesses.Add(pid);

                // Skip: game processes, protected processes, GameShift, idle
                if (pid == _gamePid || sessionGames.Contains(pid))
                {
                    ReleaseGame(pid, name);
                    continue;
                }
                if (pid <= 4) continue; // System/Idle
                if (IsProtected(name)) continue;
                if (sample.CpuTime is not { } cpuTime) continue; // access denied

                lock (_lock)
                {
                    // A PID now held by a process of another name is a new process: its CPU baseline
                    // and recorded priority belong to the one that exited.
                    if (!_trackers.TryGetValue(pid, out var tracker) ||
                        !string.Equals(tracker.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        tracker = new ProcessCpuTracker(name, cpuTime, now);
                        _trackers[pid] = tracker;
                        continue; // First sample - need a baseline, skip comparison
                    }

                    double cpu = tracker.Sample(cpuTime, processorCount, now);

                    if (cpu > CpuThresholdPercent)
                    {
                        tracker.OverCount++;
                        tracker.UnderCount = 0;

                        if (tracker.OverCount >= SustainedSamplesToRestrain && !tracker.IsRestrained)
                        {
                            try
                            {
                                var current = _priorities.GetPriority(pid);
                                // Already lowered by another writer (a launcher demotion, the user):
                                // restraining it would make Stop() write BelowNormal over their restore.
                                if (ProcessPriorityOrder.NeedsNoChange(current, ProcessPriorityClass.BelowNormal))
                                    continue;

                                tracker.OriginalPriority = current;
                                _priorities.SetPriority(pid, ProcessPriorityClass.BelowNormal);
                                tracker.IsRestrained = true;

                                _logger.Information(
                                    "[ProBalance] Restrained {Name} (PID {Pid}) - CPU: {Cpu:F1}% for {N} consecutive samples, demoted to BelowNormal",
                                    name, pid, cpu, tracker.OverCount);
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug(
                                    "[ProBalance] Cannot restrain {Name} (PID {Pid}): {Error}",
                                    name, pid, ex.Message);
                            }
                        }
                    }
                    else
                    {
                        tracker.UnderCount++;
                        tracker.OverCount = 0;

                        if (tracker.IsRestrained && tracker.UnderCount >= SustainedSamplesToRestore)
                        {
                            try
                            {
                                _priorities.SetPriority(pid, tracker.OriginalPriority);
                                tracker.IsRestrained = false;

                                _logger.Information(
                                    "[ProBalance] Restored {Name} (PID {Pid}) - CPU: {Cpu:F1}%, below threshold for {N} samples, priority → {Priority}",
                                    name, pid, cpu, tracker.UnderCount, tracker.OriginalPriority);
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug(
                                    "[ProBalance] Cannot restore {Name} (PID {Pid}): {Error}",
                                    name, pid, ex.Message);
                            }
                        }
                    }
                }
            }

            // Prune trackers for exited processes
            lock (_lock)
            {
                var stale = _trackers.Keys.Where(pid => !liveProcesses.Contains(pid)).ToList();
                foreach (var pid in stale)
                    _trackers.Remove(pid);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[ProBalance] Sampling error");
        }
    }

    /// <summary>
    /// Reads the tracked game PIDs. When they cannot be read the sample is skipped: restraining
    /// without knowing which processes are games could demote one.
    /// </summary>
    private bool TryReadSessionGamePids(out HashSet<int> pids)
    {
        pids = new HashSet<int>();
        var provider = _sessionGamePids;
        if (provider == null)
            return true;

        try
        {
            pids.UnionWith(provider());
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug("[ProBalance] Cannot read the session's games, skipping this sample: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// A process that became a tracked game after it was restrained gets its priority back at once.
    /// </summary>
    private void ReleaseGame(int pid, string name)
    {
        lock (_lock)
        {
            if (!_trackers.Remove(pid, out var tracker) || !tracker.IsRestrained ||
                !string.Equals(tracker.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                _priorities.SetPriority(pid, tracker.OriginalPriority);
                _logger.Information(
                    "[ProBalance] Restored game {Name} (PID {Pid}) to {Priority}",
                    name, pid, tracker.OriginalPriority);
            }
            catch (Exception ex)
            {
                _logger.Debug(
                    "[ProBalance] Cannot restore game {Name} (PID {Pid}): {Error}",
                    name, pid, ex.Message);
            }
        }
    }

    private static IReadOnlyList<ProcessCpuSample> SampleSystemProcesses()
    {
        var samples = new List<ProcessCpuSample>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                TimeSpan? cpuTime;
                try { cpuTime = proc.TotalProcessorTime; }
                catch { cpuTime = null; }

                samples.Add(new ProcessCpuSample(proc.Id, proc.ProcessName, cpuTime));
            }
            catch
            {
                // Process exited during enumeration - expected
            }
            finally
            {
                proc.Dispose();
            }
        }
        return samples;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool IsProtected(string processName)
    {
        if (ProtectedProcesses.Contains(processName))
            return true;

        // Game process by name
        foreach (var gn in _gameProcessNames)
        {
            if (processName.Equals(gn, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ── Per-process CPU tracker ───────────────────────────────────────────────

    private sealed class ProcessCpuTracker
    {
        public string ProcessName { get; }
        public bool IsRestrained { get; set; }
        public ProcessPriorityClass OriginalPriority { get; set; }
        public int OverCount { get; set; }
        public int UnderCount { get; set; }

        private TimeSpan _lastCpuTime;
        private DateTime _lastSampleTime;

        public ProcessCpuTracker(string name, TimeSpan initialCpuTime, DateTime now)
        {
            ProcessName = name;
            _lastCpuTime = initialCpuTime;
            _lastSampleTime = now;
        }

        /// <summary>
        /// Computes per-process CPU percentage since the last sample.
        /// Formula: (cpuDelta / (timeDelta * processorCount)) * 100
        /// </summary>
        public double Sample(TimeSpan currentCpuTime, int processorCount, DateTime now)
        {
            var cpuDelta = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
            var timeDelta = (now - _lastSampleTime).TotalMilliseconds;

            _lastCpuTime = currentCpuTime;
            _lastSampleTime = now;

            if (timeDelta <= 0 || processorCount <= 0)
                return 0;

            return (cpuDelta / (timeDelta * processorCount)) * 100.0;
        }
    }
}
