using System.Diagnostics;
using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// ProBalance-style dynamic background CPU restraint during gaming sessions.
///
/// Every 2 seconds, samples CPU usage of all non-game processes using
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
///   <item><see cref="Start"/> — called after all optimizations are applied</item>
///   <item><see cref="Stop"/> — called before LIFO revert, restores all restrained processes</item>
/// </list>
/// </summary>
public sealed class ProBalanceService : IDisposable
{
    private readonly ILogger _logger = SettingsManager.Logger;

    // ── Thresholds ────────────────────────────────────────────────────────────

    private const double CpuThresholdPercent = 15.0;
    private const int SustainedSamplesToRestrain = 3;
    private const int SustainedSamplesToRestore  = 5;
    private const int SampleIntervalMs           = 5000; // 5s — balances responsiveness vs CPU overhead

    // ── Safety list — never restrain ──────────────────────────────────────────

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
            // GameShift
            "GameShift", "GameShift.App", "GameShift.Watchdog",
        };

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<int, ProcessCpuTracker> _trackers = new();
    private readonly object _lock = new();
    private Timer? _timer;
    private string[] _gameProcessNames = Array.Empty<string>();
    private int _gamePid;
    private bool _running;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the ProBalance sampling loop. Call after all optimizations are applied.
    /// </summary>
    public void Start(int gamePid, string executableName)
    {
        _gamePid = gamePid;
        _gameProcessNames = !string.IsNullOrEmpty(executableName)
            ? new[] { Path.GetFileNameWithoutExtension(executableName) }
            : Array.Empty<string>();

        lock (_lock)
        {
            _trackers.Clear();
            _running = true;
        }

        _timer = new Timer(_ => OnSample(), null, SampleIntervalMs, SampleIntervalMs);
        _logger.Information(
            "[ProBalance] Started — monitoring background CPU usage (threshold: {Pct}%, restrain after {R} samples)",
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
                    using var proc = Process.GetProcessById(pid);
                    proc.PriorityClass = tracker.OriginalPriority;
                    restoredCount++;
                    _logger.Information(
                        "[ProBalance] Restored {Name} (PID {Pid}) to {Priority} on session end",
                        tracker.ProcessName, pid, tracker.OriginalPriority);
                }
                catch (ArgumentException)
                {
                    // Process already exited — nothing to restore
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

    private void OnSample()
    {
        if (!_running) return;

        try
        {
            var liveProcesses = new HashSet<int>();
            int processorCount = Environment.ProcessorCount;

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    int pid = proc.Id;
                    string name = proc.ProcessName;

                    liveProcesses.Add(pid);

                    // Skip: game process, protected processes, GameShift, idle
                    if (pid == _gamePid) continue;
                    if (pid <= 4) continue; // System/Idle
                    if (IsProtected(name)) continue;

                    lock (_lock)
                    {
                        if (!_trackers.TryGetValue(pid, out var tracker))
                        {
                            tracker = new ProcessCpuTracker(name, proc.TotalProcessorTime);
                            _trackers[pid] = tracker;
                            continue; // First sample — need a baseline, skip comparison
                        }

                        double cpu = tracker.Sample(proc.TotalProcessorTime, processorCount);

                        if (cpu > CpuThresholdPercent)
                        {
                            tracker.OverCount++;
                            tracker.UnderCount = 0;

                            if (tracker.OverCount >= SustainedSamplesToRestrain && !tracker.IsRestrained)
                            {
                                try
                                {
                                    tracker.OriginalPriority = proc.PriorityClass;
                                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                                    tracker.IsRestrained = true;

                                    _logger.Information(
                                        "[ProBalance] Restrained {Name} (PID {Pid}) — CPU: {Cpu:F1}% for {N} consecutive samples, demoted to BelowNormal",
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
                                    proc.PriorityClass = tracker.OriginalPriority;
                                    tracker.IsRestrained = false;

                                    _logger.Information(
                                        "[ProBalance] Restored {Name} (PID {Pid}) — CPU: {Cpu:F1}%, below threshold for {N} samples, priority → {Priority}",
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
                catch
                {
                    // Access denied or process exited during enumeration — expected
                }
                finally
                {
                    proc.Dispose();
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

        public ProcessCpuTracker(string name, TimeSpan initialCpuTime)
        {
            ProcessName = name;
            _lastCpuTime = initialCpuTime;
            _lastSampleTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Computes per-process CPU percentage since the last sample.
        /// Formula: (cpuDelta / (timeDelta * processorCount)) * 100
        /// </summary>
        public double Sample(TimeSpan currentCpuTime, int processorCount)
        {
            var now = DateTime.UtcNow;
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
