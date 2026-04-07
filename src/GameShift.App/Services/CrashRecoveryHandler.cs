using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using GameShift.Core.System;
using Serilog;

namespace GameShift.App.Services;

/// <summary>
/// Handles crash recovery on startup: detects orphaned session lockfiles,
/// restores system state from a saved snapshot, cleans up ETW sessions,
/// and removes leftover update artifacts.
/// </summary>
public static class CrashRecoveryHandler
{
    /// <summary>
    /// Checks for an orphaned active_session.json lockfile and, if found,
    /// restores the captured system state (power plan, CPU parking, scheduled tasks,
    /// IFEO entries, registry values, Win32PrioritySeparation). Also cleans up
    /// orphaned ETW sessions and previous-update artifacts.
    /// </summary>
    public static void RecoverIfNeeded(string gameshiftPath)
    {
        var lockfilePath = Path.Combine(gameshiftPath, "active_session.json");

        // Ensure GameShift AppData directory exists
        if (!Directory.Exists(gameshiftPath))
        {
            Directory.CreateDirectory(gameshiftPath);
        }

        if (File.Exists(lockfilePath))
        {
            try
            {
                var snapshot = SystemStateSnapshot.LoadFromLockfile(lockfilePath);

                Log.Warning("Detected orphaned session lockfile - performing crash recovery");

                if (snapshot != null)
                {
                    // Recover processor idle disable state (if GameShift crashed with IDLEDISABLE=1)
                    if (snapshot.IdleDisableSchemeGuid != null)
                    {
                        Log.Information("Crash recovery: restoring processor idle state for scheme {Guid}",
                            snapshot.IdleDisableSchemeGuid);
                        GameShift.Core.Optimization.CpuParkingManager.CleanupStaleIdleDisable(
                            snapshot.IdleDisableSchemeGuid);
                    }

                    // Restore active power plan
                    if (snapshot.OriginalPowerPlan != Guid.Empty)
                    {
                        Log.Information("Crash recovery: restoring original power plan {Plan}",
                            snapshot.OriginalPowerPlan);
                        GameShift.Core.Optimization.PowerPlanSwitcher.CleanupStalePowerPlan(
                            snapshot.OriginalPowerPlan);
                    }

                    // Restore CPU parking settings
                    if (snapshot.CpuParkingSchemeGuid != null && snapshot.CpuParkingEntries.Count > 0)
                    {
                        Log.Information("Crash recovery: restoring CPU parking settings for scheme {Guid}",
                            snapshot.CpuParkingSchemeGuid);
                        GameShift.Core.Optimization.CpuParkingManager.CleanupStaleParkingState(
                            snapshot.CpuParkingSchemeGuid, snapshot.CpuParkingEntries);
                    }

                    // Re-enable scheduled tasks that were disabled during gaming
                    if (snapshot.DisabledScheduledTasks.Count > 0)
                    {
                        Log.Information("Crash recovery: re-enabling {Count} disabled scheduled tasks",
                            snapshot.DisabledScheduledTasks.Count);
                        GameShift.Core.Optimization.ScheduledTaskSuppressor.CleanupStaleDisabledTasks(
                            snapshot.DisabledScheduledTasks);
                    }

                    // Clean up IFEO PerfOptions entries
                    if (snapshot.IfeoEntries.Count > 0)
                    {
                        Log.Information("Crash recovery: cleaning up {Count} IFEO PerfOptions entries",
                            snapshot.IfeoEntries.Count);
                        SystemStateSnapshot.CleanupStaleIfeoEntries(snapshot);
                    }

                    // Restore registry values (GPU, visual effects, network optimizations)
                    if (snapshot.RegistryValues.Count > 0)
                    {
                        Log.Information("Crash recovery: restoring {Count} registry values",
                            snapshot.RegistryValues.Count);
                        RestoreCrashRecoveryRegistryValues(snapshot.RegistryValues);
                    }

                    // Restore Win32PrioritySeparation
                    if (snapshot.OriginalPrioritySeparation.HasValue)
                    {
                        Log.Information("Crash recovery: restoring Win32PrioritySeparation to {Value}",
                            snapshot.OriginalPrioritySeparation.Value);
                        try
                        {
                            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                                @"SYSTEM\CurrentControlSet\Control\PriorityControl", writable: true);
                            key?.SetValue("Win32PrioritySeparation",
                                snapshot.OriginalPrioritySeparation.Value,
                                Microsoft.Win32.RegistryValueKind.DWord);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Crash recovery: failed to restore Win32PrioritySeparation");
                        }
                    }
                }

                MessageBox.Show(
                    "GameShift recovered from an unexpected shutdown. All settings have been restored.",
                    "Crash Recovery",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                SystemStateSnapshot.DeleteLockfile(lockfilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to perform crash recovery");
                // Still continue to main window even if recovery fails
            }
        }
        else
        {
            Log.Information("No orphaned session detected, proceeding normally");
        }

        // Clean up orphaned ETW DPC trace session from a previous crash
        try
        {
            var zombieSession = Microsoft.Diagnostics.Tracing.Session.TraceEventSession
                .GetActiveSession("GameShift-DPC-Trace");
            if (zombieSession != null)
            {
                zombieSession.Stop();
                zombieSession.Dispose();
                Log.Information("Cleaned up orphaned DPC monitoring ETW session");
            }
        }
        catch { /* Best-effort cleanup -- session may not exist */ }

        // Clean up leftover update artifacts from a previous auto-update
        GameShift.Core.Updates.UpdateApplier.CleanupPreviousUpdate();
    }

    /// <summary>
    /// Restores registry values from a crash recovery snapshot.
    /// Each key is "{RegistryKeyPath}\{ValueName}" and the value is the original data.
    /// </summary>
    private static void RestoreCrashRecoveryRegistryValues(Dictionary<string, object> registryValues)
    {
        foreach (var (compositeKey, originalValue) in registryValues)
        {
            try
            {
                // Split composite key into key path and value name
                var lastSlash = compositeKey.LastIndexOf('\\');
                if (lastSlash < 0) continue;

                var keyPath = compositeKey[..lastSlash];
                var valueName = compositeKey[(lastSlash + 1)..];

                // Determine the root key
                Microsoft.Win32.RegistryKey? rootKey = null;
                string subKeyPath;

                if (keyPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
                {
                    rootKey = Microsoft.Win32.Registry.LocalMachine;
                    subKeyPath = keyPath["HKEY_LOCAL_MACHINE\\".Length..];
                }
                else if (keyPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
                {
                    rootKey = Microsoft.Win32.Registry.CurrentUser;
                    subKeyPath = keyPath["HKEY_CURRENT_USER\\".Length..];
                }
                else
                {
                    Log.Warning("Crash recovery: unrecognized registry root in key '{Key}'", keyPath);
                    continue;
                }

                using var key = rootKey.OpenSubKey(subKeyPath, writable: true);
                if (key == null)
                {
                    Log.Debug("Crash recovery: registry key '{Key}' no longer exists, skipping", keyPath);
                    continue;
                }

                // Restore the original value
                // originalValue comes from JSON deserialization and may be JsonElement
                if (originalValue is System.Text.Json.JsonElement jsonElement)
                {
                    switch (jsonElement.ValueKind)
                    {
                        case System.Text.Json.JsonValueKind.Number:
                            key.SetValue(valueName, jsonElement.GetInt32(), Microsoft.Win32.RegistryValueKind.DWord);
                            break;
                        case System.Text.Json.JsonValueKind.String:
                            key.SetValue(valueName, jsonElement.GetString() ?? "", Microsoft.Win32.RegistryValueKind.String);
                            break;
                        default:
                            Log.Debug("Crash recovery: unsupported JSON type for registry value '{Key}\\{Name}'", keyPath, valueName);
                            break;
                    }
                }
                else if (originalValue is int intVal)
                {
                    key.SetValue(valueName, intVal, Microsoft.Win32.RegistryValueKind.DWord);
                }
                else if (originalValue is string strVal)
                {
                    key.SetValue(valueName, strVal, Microsoft.Win32.RegistryValueKind.String);
                }

                Log.Debug("Crash recovery: restored registry value '{Key}\\{Name}'", keyPath, valueName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Crash recovery: failed to restore registry value '{Key}'", compositeKey);
            }
        }
    }
}
