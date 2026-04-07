using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Timer = System.Threading.Timer;

namespace GameShift.Core.Optimization;

/// <summary>
/// Applies Windows 11 Efficiency Mode (EcoQoS) to background processes during gaming.
/// Efficiency Mode constrains a process's CPU scheduling class and moves it to E-cores on hybrid CPUs.
/// On homogeneous CPUs it still lowers the scheduling QoS level, reducing priority quantum.
/// Uses SetProcessInformation with ProcessPowerThrottling to control Efficiency Mode per-process.
/// Requires Windows 11 21H2+ (build 22000+); gracefully skips on Windows 10.
/// Periodically rescans every 30 seconds to catch newly spawned background processes.
/// </summary>
public class EfficiencyModeController : IOptimization
{
    /// <summary>
    /// Records whether Efficiency Mode was already enabled before GameShift applied it.
    /// </summary>
    private readonly record struct EfficiencyOriginalState(int ProcessId, string ProcessName, bool WasEfficiencyEnabled);

    private readonly List<EfficiencyOriginalState> _modifiedProcesses = new();
    private readonly HashSet<int> _modifiedPids = new();
    private readonly object _lock = new();
    private Timer? _rescanTimer;
    private string[] _activeGameProcessNames = Array.Empty<string>();
    private bool _isWindows11;

    public const string OptimizationId = "Efficiency Mode Control";

    public string Name => OptimizationId;

    public string Description => "Applies Windows Efficiency Mode to background processes, constraining them to E-cores on hybrid CPUs";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // Gracefully skips on Win10

    /// <summary>
    /// Applies Efficiency Mode to targeted background processes.
    /// Gracefully skips on Windows 10 (build < 22000).
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            // Windows version gate: Efficiency Mode requires Win11 21H2+
            int build = Environment.OSVersion.Version.Build;
            _isWindows11 = build >= 22000;

            if (!_isWindows11)
            {
                SettingsManager.Logger.Information(
                    "[EfficiencyModeController] Efficiency Mode not available on Windows 10 (build {Build}) — skipping",
                    build);
                IsApplied = true;
                return Task.FromResult(true); // Not a failure, just not applicable
            }

            // Resolve game process names from profile
            _activeGameProcessNames = ResolveGameProcessNames(profile);

            SettingsManager.Logger.Information(
                "[EfficiencyModeController] Applying Efficiency Mode to background processes");

            // Initial scan and apply
            ScanAndApplyEfficiency();

            // Start periodic rescan to catch newly spawned background processes
            _rescanTimer = new Timer(
                _ => ScanAndApplyEfficiency(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));

            IsApplied = true;

            SettingsManager.Logger.Information(
                "[EfficiencyModeController] Efficiency Mode applied to {Count} background processes",
                _modifiedProcesses.Count);

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[EfficiencyModeController] Apply failed");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Removes Efficiency Mode from processes that GameShift applied it to.
    /// Processes that were already in Efficiency Mode before Apply() are left unchanged.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        try
        {
            SettingsManager.Logger.Information("[EfficiencyModeController] Reverting Efficiency Mode changes");

            // Stop periodic rescan
            _rescanTimer?.Dispose();
            _rescanTimer = null;

            if (!_isWindows11)
            {
                // Nothing to revert on Win10
                IsApplied = false;
                return Task.FromResult(true);
            }

            int restoredCount = 0;
            int skippedCount = 0;

            lock (_lock)
            {
                foreach (var state in _modifiedProcesses)
                {
                    // Don't revert processes that were already in Efficiency Mode before Apply()
                    if (state.WasEfficiencyEnabled)
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        using var process = Process.GetProcessById(state.ProcessId);

                        // PID reuse check — verify it's the same process
                        if (!process.ProcessName.Equals(state.ProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            skippedCount++;
                            continue;
                        }

                        if (DisableEfficiencyMode(process.Handle))
                        {
                            restoredCount++;
                            SettingsManager.Logger.Debug(
                                "[EfficiencyModeController] Removed Efficiency Mode: {Name} (PID {Pid})",
                                state.ProcessName, state.ProcessId);
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
                            "[EfficiencyModeController] Failed to remove Efficiency Mode for {Name}: {Error}",
                            state.ProcessName, ex.Message);
                    }
                }

                _modifiedProcesses.Clear();
                _modifiedPids.Clear();
            }

            SettingsManager.Logger.Information(
                "[EfficiencyModeController] Revert completed — {Restored} restored, {Skipped} skipped",
                restoredCount, skippedCount);

            IsApplied = false;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[EfficiencyModeController] Revert failed");
            IsApplied = false;
            return Task.FromResult(false);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Scans all running processes and applies Efficiency Mode to matching background processes.
    /// Called on initial Apply and every 30 seconds thereafter.
    /// Thread-safe via _lock.
    /// </summary>
    private void ScanAndApplyEfficiency()
    {
        int newlyApplied = 0;

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
                        // Skip if already modified in this session
                        if (_modifiedPids.Contains(process.Id))
                            continue;
                    }

                    // Query current Efficiency Mode state
                    bool wasAlreadyEfficient = IsEfficiencyModeEnabled(process.ProcessHandle);

                    // Apply Efficiency Mode
                    if (EnableEfficiencyMode(process.ProcessHandle))
                    {
                        lock (_lock)
                        {
                            _modifiedProcesses.Add(new EfficiencyOriginalState(
                                process.Id, name, wasAlreadyEfficient));
                            _modifiedPids.Add(process.Id);
                        }

                        newlyApplied++;
                        SettingsManager.Logger.Debug(
                            "[EfficiencyModeController] Efficiency Mode applied: {Name} (PID {Pid}){WasAlready}",
                            name, process.Id, wasAlreadyEfficient ? " (was already efficient)" : "");
                    }
                }
                catch (Exception ex)
                {
                    // Access denied on system processes or suspended processes is expected
                    if (ex is not global::System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
                    {
                        SettingsManager.Logger.Debug(
                            "[EfficiencyModeController] Could not apply Efficiency Mode to {Name}: {Error}",
                            process.ProcessName, ex.Message);
                    }
                }
                finally
                {
                    // Process objects are owned by ProcessSnapshotService cache — do not dispose
                }
            }

            if (newlyApplied > 0)
            {
                SettingsManager.Logger.Debug(
                    "[EfficiencyModeController] Rescan applied Efficiency Mode to {Count} new processes",
                    newlyApplied);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[EfficiencyModeController] ScanAndApplyEfficiency failed");
        }
    }

    /// <summary>
    /// Enables Efficiency Mode (EcoQoS) on a process.
    /// Sets PROCESS_POWER_THROTTLING_EXECUTION_SPEED in both ControlMask and StateMask.
    /// </summary>
    private static bool EnableEfficiencyMode(IntPtr processHandle)
    {
        var state = new NativeInterop.PROCESS_POWER_THROTTLING_STATE
        {
            Version = NativeInterop.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = NativeInterop.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = NativeInterop.PROCESS_POWER_THROTTLING_EXECUTION_SPEED
        };

        int size = Marshal.SizeOf<NativeInterop.PROCESS_POWER_THROTTLING_STATE>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(state, ptr, false);
            return NativeInterop.SetProcessInformation(
                processHandle,
                NativeInterop.ProcessPowerThrottling,
                ptr,
                size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Disables Efficiency Mode on a process (restores normal scheduling).
    /// Sets ControlMask with StateMask = 0 to indicate the feature should be disabled.
    /// </summary>
    private static bool DisableEfficiencyMode(IntPtr processHandle)
    {
        var state = new NativeInterop.PROCESS_POWER_THROTTLING_STATE
        {
            Version = NativeInterop.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = NativeInterop.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = 0 // 0 = disabled
        };

        int size = Marshal.SizeOf<NativeInterop.PROCESS_POWER_THROTTLING_STATE>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(state, ptr, false);
            return NativeInterop.SetProcessInformation(
                processHandle,
                NativeInterop.ProcessPowerThrottling,
                ptr,
                size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Queries whether Efficiency Mode is currently enabled on a process.
    /// </summary>
    private static bool IsEfficiencyModeEnabled(IntPtr processHandle)
    {
        var state = new NativeInterop.PROCESS_POWER_THROTTLING_STATE
        {
            Version = NativeInterop.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = 0,
            StateMask = 0
        };

        int size = Marshal.SizeOf<NativeInterop.PROCESS_POWER_THROTTLING_STATE>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(state, ptr, false);
            bool success = NativeInterop.GetProcessInformation(
                processHandle,
                NativeInterop.ProcessPowerThrottling,
                ptr,
                size);

            if (!success) return false;

            state = Marshal.PtrToStructure<NativeInterop.PROCESS_POWER_THROTTLING_STATE>(ptr);
            return (state.StateMask & NativeInterop.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Resolves the active game's process names from the profile.
    /// </summary>
    private static string[] ResolveGameProcessNames(GameProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.ExecutableName))
        {
            var name = Path.GetFileNameWithoutExtension(profile.ExecutableName);
            return new[] { name };
        }

        return Array.Empty<string>();
    }
}
