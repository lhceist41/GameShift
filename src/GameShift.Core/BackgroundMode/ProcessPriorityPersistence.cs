using System.Diagnostics;
using GameShift.Core.Config;
using GameShift.Core.Detection;

namespace GameShift.Core.BackgroundMode;

/// <summary>
/// Persistent process priority rules. Subscribes to GameDetector.ProcessSpawned
/// and applies configured priority rules (e.g., chrome.exe -> BelowNormal).
/// Runs 24/7 when enabled.
/// </summary>
public class ProcessPriorityPersistence : IDisposable
{
    private GameDetector? _detector;
    private Dictionary<string, ProcessPriorityClass> _rules = new();
    private volatile bool _running;

    public bool IsRunning => _running;

    /// <summary>
    /// Set of process names currently managed by an active GameProfile session.
    /// Set by App layer. When a process name is in this set, persistent priority rules are skipped.
    /// </summary>
    public HashSet<string>? GameProfileActiveProcesses { get; set; }

    /// <summary>
    /// Starts monitoring for process starts and applying priority rules.
    /// Subscribes to GameDetector.ProcessSpawned instead of creating its own WMI watcher.
    /// </summary>
    public void Start(BackgroundModeSettings settings, GameDetector? detector)
    {
        if (_running) return;

        // Parse rules from settings
        _rules.Clear();
        foreach (var (exe, priorityStr) in settings.ProcessPriorityRules)
        {
            if (Enum.TryParse<ProcessPriorityClass>(priorityStr, true, out var priority))
            {
                _rules[exe.ToLowerInvariant()] = priority;
            }
            else
            {
                SettingsManager.Logger.Warning(
                    "[ProcessPriority] Invalid priority '{Priority}' for {Exe}", priorityStr, exe);
            }
        }

        if (_rules.Count == 0)
        {
            SettingsManager.Logger.Information("[ProcessPriority] No rules configured, not starting");
            return;
        }

        // Apply to already-running processes
        ApplyToRunningProcesses();

        // Subscribe to GameDetector's shared WMI process start events
        if (detector != null)
        {
            _detector = detector;
            _detector.ProcessSpawned += OnProcessSpawned;
            _running = true;

            SettingsManager.Logger.Information(
                "[ProcessPriority] Started with {Count} rules (using shared WMI watcher)", _rules.Count);
        }
        else
        {
            SettingsManager.Logger.Warning(
                "[ProcessPriority] No GameDetector available — process monitoring disabled");
        }
    }

    /// <summary>
    /// Stops monitoring. Does NOT revert priorities (they're meant to persist).
    /// </summary>
    public void Stop()
    {
        _running = false;

        if (_detector != null)
        {
            _detector.ProcessSpawned -= OnProcessSpawned;
            _detector = null;
        }

        SettingsManager.Logger.Information("[ProcessPriority] Stopped");
    }

    private void OnProcessSpawned(object? sender, ProcessSpawnedEventArgs e)
    {
        if (!_running) return;

        try
        {
            var processName = e.ProcessName;
            if (string.IsNullOrEmpty(processName)) return;

            var key = processName.ToLowerInvariant();
            if (!_rules.TryGetValue(key, out var targetPriority)) return;

            // GameProfile session takes priority over persistent rules
            if (GameProfileActiveProcesses?.Contains(key) == true)
            {
                SettingsManager.Logger.Debug(
                    "[ProcessPriority] Skipping {Process} — active GameProfile session takes priority", processName);
                return;
            }

            var pid = e.ProcessId;

            // Small delay to let the process initialize
            Task.Delay(500).ContinueWith(_ =>
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.PriorityClass = targetPriority;
                    SettingsManager.Logger.Debug(
                        "[ProcessPriority] Set {Process} (PID {Pid}) to {Priority}",
                        processName, pid, targetPriority);
                }
                catch { } // Process may have exited
            });
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[ProcessPriority] Error handling process start");
        }
    }

    private void ApplyToRunningProcesses()
    {
        foreach (var (exe, priority) in _rules)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(exe);
                if (GameProfileActiveProcesses?.Contains(exe) == true) continue;
                var processes = Process.GetProcessesByName(name);
                foreach (var proc in processes)
                {
                    try
                    {
                        proc.PriorityClass = priority;
                        SettingsManager.Logger.Debug(
                            "[ProcessPriority] Applied {Priority} to running {Exe} (PID {Pid})",
                            priority, exe, proc.Id);
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
