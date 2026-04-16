using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;

namespace GameShift.Core.System;

/// <summary>
/// Represents a snapshot of system state before optimization.
/// Used for crash recovery to restore system to original state.
/// Full implementation of state capture/restore will be added in Phase 2.
/// </summary>
public class SystemStateSnapshot
{
    // P/Invoke for power plan capture
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
    /// <summary>
    /// The original Windows power plan GUID before any changes.
    /// </summary>
    public Guid OriginalPowerPlan { get; set; }

    /// <summary>
    /// Original state of Windows services that may be modified.
    /// Key: Service name, Value: Original status (Running/Stopped/etc.)
    /// </summary>
    public Dictionary<string, ServiceControllerStatus> ServiceStates { get; set; } = new();

    /// <summary>
    /// Original process priority class before optimization.
    /// </summary>
    public ProcessPriorityClass ProcessPriority { get; set; }

    /// <summary>
    /// Original Windows timer resolution in 100-nanosecond units.
    /// </summary>
    public int TimerResolution { get; set; }

    /// <summary>
    /// Original process priorities before optimization.
    /// Key: Process ID, Value: Original priority class
    /// </summary>
    public Dictionary<int, ProcessPriorityClass> ProcessPriorities { get; set; } = new();

    /// <summary>
    /// Original process affinities before optimization.
    /// Key: Process ID, Value: Original affinity mask
    /// </summary>
    public Dictionary<int, long> ProcessAffinities { get; set; } = new();

    /// <summary>
    /// Original registry values before modification.
    /// Key: "{RegistryKeyPath}\{ValueName}", Value: Original value
    /// </summary>
    public Dictionary<string, object> RegistryValues { get; set; } = new();

    /// <summary>
    /// Scheduled task paths disabled by ScheduledTaskSuppressor.
    /// Used for crash recovery to re-enable orphaned disabled tasks.
    /// </summary>
    public List<string> DisabledScheduledTasks { get; set; } = new();

    /// <summary>
    /// CPU parking: active scheme GUID when CpuParkingManager applied.
    /// Used for crash recovery to restore orphaned parking settings.
    /// </summary>
    public string? CpuParkingSchemeGuid { get; set; }

    /// <summary>
    /// CPU parking: original AC/DC values for each setting.
    /// Used for crash recovery to restore orphaned parking settings.
    /// </summary>
    public List<Optimization.CpuParkingSnapshotEntry> CpuParkingEntries { get; set; } = new();

    /// <summary>
    /// Original Win32PrioritySeparation registry value before session-scoped override.
    /// Null if not modified. Used for crash recovery to restore original scheduling quantum.
    /// </summary>
    public int? OriginalPrioritySeparation { get; set; }

    /// <summary>
    /// Power scheme GUID where processor idle was disabled during a gaming session.
    /// Null if idle was not disabled. Used for crash recovery to re-enable processor idle
    /// and restore time check interval if GameShift crashes mid-game with IDLEDISABLE=1.
    /// </summary>
    public string? IdleDisableSchemeGuid { get; set; }

    /// <summary>
    /// IFEO (Image File Execution Options) PerfOptions entries created by GameShift.
    /// Key: IFEO subkey path (e.g., "SOFTWARE\Microsoft\Windows NT\...\game.exe\PerfOptions")
    /// Value: JSON of original values if the key existed, or null if GameShift created it.
    /// Used for crash recovery to clean up orphaned IFEO entries.
    /// </summary>
    public Dictionary<string, string?> IfeoEntries { get; set; } = new();

    /// <summary>
    /// Timestamp when this snapshot was captured.
    /// </summary>
    public DateTime CaptureTime { get; set; }

    /// <summary>
    /// Creates a new snapshot of the current system state.
    /// Captures baseline state like power plan and timer resolution.
    /// Individual optimizations record specific changes via RecordX methods.
    /// </summary>
    /// <returns>A SystemStateSnapshot with current system state.</returns>
    public static SystemStateSnapshot Capture()
    {
        var snapshot = new SystemStateSnapshot
        {
            CaptureTime = DateTime.UtcNow,
            ProcessPriority = ProcessPriorityClass.Normal, // Placeholder for now
            TimerResolution = 156250 // Default Windows timer resolution (15.625ms)
        };

        // Capture current power plan via P/Invoke
        try
        {
            uint result = PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptrGuid);
            if (result == 0 && ptrGuid != IntPtr.Zero)
            {
                Guid activeGuid = Marshal.PtrToStructure<Guid>(ptrGuid);
                LocalFree(ptrGuid);
                snapshot.OriginalPowerPlan = activeGuid;
            }
            else
            {
                snapshot.OriginalPowerPlan = Guid.Empty;
            }
        }
        catch
        {
            // Fall back to empty GUID if P/Invoke fails
            snapshot.OriginalPowerPlan = Guid.Empty;
        }

        return snapshot;
    }

    /// <summary>
    /// Records a service's original state before modification.
    /// Only records if not already present (preserves first/original state).
    /// Use case: ServiceSuppressor calls this before stopping each service.
    /// </summary>
    /// <param name="serviceName">Name of the Windows service</param>
    /// <param name="status">Original status (Running, Stopped, etc.)</param>
    public void RecordServiceState(string serviceName, ServiceControllerStatus status)
    {
        if (!ServiceStates.ContainsKey(serviceName))
        {
            ServiceStates[serviceName] = status;
        }
    }

    /// <summary>
    /// Records a process's original priority before modification.
    /// Only records if not already present (preserves first/original state).
    /// Use case: ProcessPriorityBooster records original priority before boosting.
    /// </summary>
    /// <param name="processId">Process ID</param>
    /// <param name="priority">Original priority class</param>
    public void RecordProcessPriority(int processId, ProcessPriorityClass priority)
    {
        if (!ProcessPriorities.ContainsKey(processId))
        {
            ProcessPriorities[processId] = priority;
        }
    }

    /// <summary>
    /// Records a process's original CPU affinity before modification.
    /// Only records if not already present (preserves first/original state).
    /// Use case: Hybrid CPU affinity optimization for P-core/E-core assignment.
    /// </summary>
    /// <param name="processId">Process ID</param>
    /// <param name="affinity">Original affinity mask</param>
    public void RecordProcessAffinity(int processId, IntPtr affinity)
    {
        if (!ProcessAffinities.ContainsKey(processId))
        {
            ProcessAffinities[processId] = affinity.ToInt64();
        }
    }

    /// <summary>
    /// Records the original power plan GUID before switching.
    /// Only records if not already set (preserves first/original state).
    /// Use case: PowerPlanSwitcher records current plan before switching to Ultimate Performance.
    /// </summary>
    /// <param name="planGuid">Original power plan GUID</param>
    public void RecordPowerPlan(Guid planGuid)
    {
        if (OriginalPowerPlan == Guid.Empty)
        {
            OriginalPowerPlan = planGuid;
        }
    }

    /// <summary>
    /// Records the original timer resolution before modification.
    /// Only records if not already set (preserves first/original state).
    /// Use case: TimerResolutionManager records default 15.625ms before changing to 1ms.
    /// </summary>
    /// <param name="resolution100ns">Original resolution in 100-nanosecond units</param>
    public void RecordTimerResolution(int resolution100ns)
    {
        if (TimerResolution == 156250 || TimerResolution == 0)
        {
            TimerResolution = resolution100ns;
        }
    }

    /// <summary>
    /// Records disabled scheduled task paths for crash recovery.
    /// Use case: ScheduledTaskSuppressor records tasks it disabled.
    /// </summary>
    /// <param name="taskPaths">List of task paths that were disabled</param>
    public void RecordDisabledScheduledTasks(List<string> taskPaths)
    {
        DisabledScheduledTasks = taskPaths;
    }

    /// <summary>
    /// Records CPU parking original state for crash recovery.
    /// Use case: CpuParkingManager records the active scheme GUID and original values.
    /// </summary>
    /// <param name="schemeGuid">Active power scheme GUID when parking was modified</param>
    /// <param name="entries">Original AC/DC values for each parking setting</param>
    public void RecordCpuParkingState(string schemeGuid, List<Optimization.CpuParkingSnapshotEntry> entries)
    {
        CpuParkingSchemeGuid = schemeGuid;
        CpuParkingEntries = entries;
    }

    /// <summary>
    /// Records the original Win32PrioritySeparation value before session-scoped override.
    /// Only records if not already set (preserves first/original state).
    /// Use case: ProcessPriorityBooster records original value before setting gaming-optimal 0x2A.
    /// </summary>
    /// <param name="originalValue">Original Win32PrioritySeparation DWORD value</param>
    public void RecordPrioritySeparation(int originalValue)
    {
        OriginalPrioritySeparation ??= originalValue;
    }

    /// <summary>
    /// Records an IFEO PerfOptions entry for crash recovery.
    /// Only records if not already present (preserves first/original state).
    /// Use case: ProcessPriorityBooster and HybridCpuDetector IFEO fallback path.
    /// </summary>
    /// <param name="subKeyPath">Full IFEO subkey path (e.g., "SOFTWARE\Microsoft\...\game.exe\PerfOptions")</param>
    /// <param name="originalValueJson">JSON of original values if key existed, or null if created by GameShift</param>
    public void RecordIfeoEntry(string subKeyPath, string? originalValueJson)
    {
        if (!IfeoEntries.ContainsKey(subKeyPath))
        {
            IfeoEntries[subKeyPath] = originalValueJson;
        }
    }

    /// <summary>
    /// Records that processor idle was disabled on a specific power scheme.
    /// Used for crash recovery to re-enable idle if GameShift crashes during gaming.
    /// </summary>
    /// <param name="schemeGuid">Active power scheme GUID where idle was disabled</param>
    public void RecordIdleDisableState(string schemeGuid)
    {
        IdleDisableSchemeGuid ??= schemeGuid;
    }

    /// <summary>
    /// Records an original registry value before modification.
    /// Only records if not already present (preserves first/original state).
    /// Use case: VisualEffectReducer and NetworkOptimizer record registry changes.
    /// </summary>
    /// <param name="keyPath">Full registry key path (e.g., "HKEY_LOCAL_MACHINE\SOFTWARE\...")</param>
    /// <param name="valueName">Registry value name</param>
    /// <param name="originalValue">Original value (string, int, byte[], etc.)</param>
    public void RecordRegistryValue(string keyPath, string valueName, object originalValue)
    {
        string compositeKey = $"{keyPath}\\{valueName}";
        if (!RegistryValues.ContainsKey(compositeKey))
        {
            RegistryValues[compositeKey] = originalValue;
        }
    }

    /// <summary>
    /// Cleans up stale IFEO PerfOptions entries from a previous crashed session.
    /// Called during startup when a stale lockfile is found.
    /// </summary>
    /// <param name="snapshot">The snapshot loaded from the stale lockfile</param>
    public static void CleanupStaleIfeoEntries(SystemStateSnapshot snapshot)
    {
        if (snapshot.IfeoEntries.Count == 0)
            return;

        foreach (var (subKeyPath, originalValueJson) in snapshot.IfeoEntries)
        {
            try
            {
                if (originalValueJson == null)
                {
                    // GameShift created this key — delete it entirely
                    using var parentKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        subKeyPath.Contains("\\PerfOptions")
                            ? subKeyPath[..subKeyPath.LastIndexOf("\\PerfOptions", StringComparison.Ordinal)]
                            : subKeyPath,
                        writable: true);

                    parentKey?.DeleteSubKey("PerfOptions", throwOnMissingSubKey: false);
                }
                else
                {
                    // Original values existed — restore them
                    var originalValues = JsonSerializer
                        .Deserialize<Dictionary<string, int>>(originalValueJson);

                    if (originalValues != null)
                    {
                        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true);
                        if (key != null)
                        {
                            foreach (var (valueName, value) in originalValues)
                            {
                                key.SetValue(valueName, value, Microsoft.Win32.RegistryValueKind.DWord);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best-effort cleanup — log failure but continue
            }
        }
    }

    /// <summary>
    /// Saves this snapshot to a lockfile for crash recovery.
    /// The lockfile indicates an active optimization session.
    /// </summary>
    /// <param name="path">Full path to the lockfile (typically %AppData%/GameShift/active_session.json)</param>
    public void SaveToLockfile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Loads a snapshot from a lockfile.
    /// Used during startup to detect and recover from crashes.
    /// </summary>
    /// <param name="path">Full path to the lockfile</param>
    /// <returns>The loaded snapshot, or null if file doesn't exist or is invalid</returns>
    public static SystemStateSnapshot? LoadFromLockfile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SystemStateSnapshot>(json);
        }
        catch
        {
            // Corrupted lockfile - treat as no lockfile
            return null;
        }
    }

    /// <summary>
    /// Deletes the lockfile, indicating clean shutdown.
    /// </summary>
    /// <param name="path">Full path to the lockfile</param>
    public static void DeleteLockfile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
