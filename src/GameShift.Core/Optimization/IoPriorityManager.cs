using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Timer = System.Threading.Timer;

namespace GameShift.Core.Optimization;

/// <summary>
/// Lowers disk I/O priority of background processes during gaming sessions.
/// Targets known resource-heavy processes (Windows Search, Defender, OneDrive, etc.)
/// and sets their I/O priority to Low (1) to reduce disk contention with the game.
/// Uses NtSetInformationProcess/NtQueryInformationProcess with ProcessIoPriority = 33.
/// Periodically rescans every 30 seconds to catch newly spawned background processes.
/// </summary>
public class IoPriorityManager : IOptimization
{
    /// <summary>
    /// Records original I/O priority state for a demoted process.
    /// </summary>
    private readonly record struct IoOriginalState(int ProcessId, string ProcessName, int OriginalIoPriority);

    private readonly List<IoOriginalState> _demotedProcesses = new();
    private readonly HashSet<int> _demotedPids = new();
    private readonly object _lock = new();
    private Timer? _rescanTimer;
    private string[] _activeGameProcessNames = Array.Empty<string>();

    public const string OptimizationId = "I/O Priority Management";

    public string Name => OptimizationId;

    public string Description => "Lowers disk I/O priority of background processes during gaming to prevent asset loading stutters";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // NtSetInformationProcess available on all supported Windows versions

    /// <summary>
    /// Lowers I/O priority of targeted background processes and starts periodic rescan.
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            // Resolve game process names from profile
            _activeGameProcessNames = ResolveGameProcessNames(profile);

            SettingsManager.Logger.Information(
                "[IoPriorityManager] Applying I/O priority demotion for background processes");

            // Initial scan and demote
            ScanAndDemote();

            // Start periodic rescan to catch newly spawned background processes
            _rescanTimer = new Timer(
                _ => ScanAndDemote(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));

            IsApplied = true;

            SettingsManager.Logger.Information(
                "[IoPriorityManager] I/O priority lowered on {Count} background processes",
                _demotedProcesses.Count);

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[IoPriorityManager] Apply failed");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Restores original I/O priorities and stops periodic rescan.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        try
        {
            SettingsManager.Logger.Information("[IoPriorityManager] Reverting I/O priority changes");

            // Stop periodic rescan
            _rescanTimer?.Dispose();
            _rescanTimer = null;

            int restoredCount = 0;
            int skippedCount = 0;

            lock (_lock)
            {
                foreach (var state in _demotedProcesses)
                {
                    try
                    {
                        using var process = Process.GetProcessById(state.ProcessId);

                        // PID reuse check — verify it's the same process
                        if (!process.ProcessName.Equals(state.ProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            skippedCount++;
                            continue;
                        }

                        int priority = state.OriginalIoPriority;
                        int status = NativeInterop.NtSetInformationProcess(
                            process.Handle,
                            NativeInterop.ProcessIoPriority,
                            ref priority,
                            sizeof(int));

                        if (status == 0)
                        {
                            restoredCount++;
                            SettingsManager.Logger.Debug(
                                "[IoPriorityManager] Restored I/O priority: {Name} (PID {Pid}) → {Priority}",
                                state.ProcessName, state.ProcessId, state.OriginalIoPriority);
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process no longer running — nothing to revert
                        skippedCount++;
                    }
                    catch (Exception ex)
                    {
                        SettingsManager.Logger.Debug(
                            "[IoPriorityManager] Failed to restore I/O priority for {Name}: {Error}",
                            state.ProcessName, ex.Message);
                    }
                }

                _demotedProcesses.Clear();
                _demotedPids.Clear();
            }

            SettingsManager.Logger.Information(
                "[IoPriorityManager] Revert completed — {Restored} restored, {Skipped} skipped",
                restoredCount, skippedCount);

            IsApplied = false;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[IoPriorityManager] Revert failed");
            IsApplied = false;
            return Task.FromResult(false);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Scans all running processes and demotes matching background processes.
    /// Called on initial Apply and every 30 seconds thereafter.
    /// Thread-safe via _lock.
    /// </summary>
    private void ScanAndDemote()
    {
        int newlyDemoted = 0;

        try
        {
            foreach (var process in ProcessSnapshotService.GetProcesses())
            {
                try
                {
                    string name = process.ProcessName;

                    // Use shared targeting logic
                    if (!BackgroundProcessTargets.ShouldDemote(name, _activeGameProcessNames))
                        continue;

                    lock (_lock)
                    {
                        // Skip if already demoted in this session
                        if (_demotedPids.Contains(process.Id))
                            continue;
                    }

                    // Open our own handle — ProcessSnapshot no longer carries OS handles
                    var hProcess = NativeInterop.OpenProcess(
                        NativeInterop.PROCESS_QUERY_INFORMATION | NativeInterop.PROCESS_SET_INFORMATION,
                        false, process.Id);
                    if (hProcess == IntPtr.Zero) continue;
                    try
                    {
                        // Query current I/O priority
                        int currentPriority = 0;
                        int status = NativeInterop.NtQueryInformationProcess(
                            hProcess,
                            NativeInterop.ProcessIoPriority,
                            ref currentPriority,
                            sizeof(int),
                            out _);

                        if (status != 0) continue; // Query failed, skip
                        if (currentPriority <= NativeInterop.IoPriorityLow) continue; // Already low, skip

                        // Demote to Low
                        int newPriority = NativeInterop.IoPriorityLow;
                        status = NativeInterop.NtSetInformationProcess(
                            hProcess,
                            NativeInterop.ProcessIoPriority,
                            ref newPriority,
                            sizeof(int));

                        if (status == 0)
                        {
                            lock (_lock)
                            {
                                _demotedProcesses.Add(new IoOriginalState(
                                    process.Id, name, currentPriority));
                                _demotedPids.Add(process.Id);
                            }

                            newlyDemoted++;
                            SettingsManager.Logger.Debug(
                                "[IoPriorityManager] I/O priority lowered: {Name} (PID {Pid}) {From} → {To}",
                                name, process.Id, currentPriority, NativeInterop.IoPriorityLow);
                        }
                    }
                    finally
                    {
                        NativeInterop.CloseHandle(hProcess);
                    }
                }
                catch (Exception ex)
                {
                    // Access denied on system processes is expected — skip silently
                    if (ex is not global::System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
                    {
                        SettingsManager.Logger.Debug(
                            "[IoPriorityManager] Could not adjust I/O priority for {Name}: {Error}",
                            process.ProcessName, ex.Message);
                    }
                }
                finally
                {
                    // Process objects are owned by ProcessSnapshotService cache — do not dispose
                }
            }

            if (newlyDemoted > 0)
            {
                SettingsManager.Logger.Debug(
                    "[IoPriorityManager] Rescan demoted {Count} new processes", newlyDemoted);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[IoPriorityManager] ScanAndDemote failed");
        }
    }

    /// <summary>
    /// Resolves the active game's process names from the profile.
    /// </summary>
    private static string[] ResolveGameProcessNames(GameProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.ExecutableName))
        {
            // Strip .exe extension for process name matching
            var name = Path.GetFileNameWithoutExtension(profile.ExecutableName);
            return new[] { name };
        }

        return Array.Empty<string>();
    }
}
