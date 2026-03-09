using System.Diagnostics;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Core.Optimization;

/// <summary>
/// Disables resource-heavy Windows scheduled tasks during gaming sessions.
/// Tasks are organized into tiers: Tier 1 (always safe), Tier 2 (safe for gaming PCs),
/// and Tier 3 (Defender tasks — opt-in only via SuppressDefenderScheduledScan toggle).
/// Coordinates with Background Mode's TaskDeferralService to avoid double-handling.
/// Uses schtasks.exe for consistency with existing TaskDeferralService pattern.
/// </summary>
public class ScheduledTaskSuppressor : IOptimization
{
    /// <summary>
    /// Tier 1 — High impact, always safe to disable during gaming.
    /// </summary>
    private static readonly string[] Tier1Tasks = new[]
    {
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\Defrag\ScheduledDefrag",
        @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
        @"\Microsoft\Windows\Maintenance\WinSAT",
        @"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem",
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting"
    };

    /// <summary>
    /// Tier 2 — Medium impact, safe for gaming PCs.
    /// </summary>
    private static readonly string[] Tier2Tasks = new[]
    {
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Work",
        @"\Microsoft\Windows\WindowsUpdate\Scheduled Start",
        @"\Microsoft\Windows\.NET Framework\.NET Framework NGEN v4.0.30319",
        @"\Microsoft\Windows\.NET Framework\.NET Framework NGEN v4.0.30319 64",
        @"\Microsoft\Windows\Diagnosis\Scheduled",
        @"\Microsoft\Windows\Registry\RegIdleBackup"
    };

    /// <summary>
    /// Tier 3 — Conditional: only disable when user has opted in via SuppressDefenderScheduledScan.
    /// </summary>
    private static readonly string[] Tier3DefenderTasks = new[]
    {
        @"\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan",
        @"\Microsoft\Windows\Windows Defender\Windows Defender Cache Maintenance"
    };

    /// <summary>
    /// Tracks tasks we disabled and whether they were enabled before we touched them.
    /// </summary>
    private readonly record struct TaskOriginalState(string TaskPath, bool WasEnabled);

    private readonly List<TaskOriginalState> _disabledTasks = new();

    public const string OptimizationId = "Scheduled Task Suppression";

    public string Name => OptimizationId;

    public string Description => "Disables resource-heavy Windows scheduled tasks during gaming sessions";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // Tasks that don't exist are skipped individually

    /// <summary>
    /// Disables all targeted scheduled tasks based on profile settings.
    /// Tier 1 + 2 are always disabled when this module is active.
    /// Tier 3 (Defender) only disabled if SuppressDefenderScheduledScan is true.
    /// Records original state in snapshot for crash recovery.
    /// </summary>
    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        await Task.CompletedTask;

        int disabledCount = 0;
        int skippedCount = 0;
        int errorCount = 0;

        try
        {
            // Build target task list based on profile settings
            var targetTasks = new List<string>();
            targetTasks.AddRange(Tier1Tasks);
            targetTasks.AddRange(Tier2Tasks);

            if (profile.SuppressDefenderScheduledScan)
            {
                targetTasks.AddRange(Tier3DefenderTasks);
                SettingsManager.Logger.Information(
                    "ScheduledTaskSuppressor: Defender scan deferral enabled by profile");
            }

            foreach (var taskPath in targetTasks)
            {
                try
                {
                    // Query task state before disabling
                    bool? isEnabled = IsTaskEnabled(taskPath);

                    if (isEnabled == null)
                    {
                        // Task doesn't exist on this system — skip
                        SettingsManager.Logger.Debug(
                            "ScheduledTaskSuppressor: Task not found (skipping): {TaskPath}", taskPath);
                        skippedCount++;
                        continue;
                    }

                    if (!isEnabled.Value)
                    {
                        // Already disabled — skip but don't track for revert
                        SettingsManager.Logger.Debug(
                            "ScheduledTaskSuppressor: Task already disabled (skipping): {TaskPath}", taskPath);
                        skippedCount++;
                        continue;
                    }

                    // Disable the task
                    var result = RunSchtasks($"/change /tn \"{taskPath}\" /disable");
                    if (result != null && !result.Contains("ERROR"))
                    {
                        _disabledTasks.Add(new TaskOriginalState(taskPath, true));
                        disabledCount++;
                        SettingsManager.Logger.Debug(
                            "ScheduledTaskSuppressor: Disabled task: {TaskPath}", taskPath);
                    }
                    else
                    {
                        SettingsManager.Logger.Warning(
                            "ScheduledTaskSuppressor: Failed to disable task: {TaskPath}", taskPath);
                        errorCount++;
                    }
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(ex,
                        "ScheduledTaskSuppressor: Error processing task {TaskPath}", taskPath);
                    errorCount++;
                }
            }

            // Record disabled task paths in snapshot for crash recovery
            snapshot.RecordDisabledScheduledTasks(
                _disabledTasks.Select(t => t.TaskPath).ToList());

            SettingsManager.Logger.Information(
                "ScheduledTaskSuppressor: Completed — {Disabled} disabled, {Skipped} skipped, {Errors} errors",
                disabledCount, skippedCount, errorCount);

            IsApplied = true;
            return true; // Partial success is still success
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "ScheduledTaskSuppressor: Apply failed");
            return false;
        }
    }

    /// <summary>
    /// Re-enables all tasks that were disabled during Apply.
    /// Reverts in reverse order (LIFO pattern).
    /// </summary>
    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        await Task.CompletedTask;

        int restoredCount = 0;
        int errorCount = 0;

        try
        {
            // Revert in reverse order
            for (int i = _disabledTasks.Count - 1; i >= 0; i--)
            {
                var state = _disabledTasks[i];
                try
                {
                    if (state.WasEnabled)
                    {
                        var result = RunSchtasks($"/change /tn \"{state.TaskPath}\" /enable");
                        if (result != null && !result.Contains("ERROR"))
                        {
                            restoredCount++;
                            SettingsManager.Logger.Debug(
                                "ScheduledTaskSuppressor: Re-enabled task: {TaskPath}", state.TaskPath);
                        }
                        else
                        {
                            SettingsManager.Logger.Warning(
                                "ScheduledTaskSuppressor: Failed to re-enable task: {TaskPath}", state.TaskPath);
                            errorCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Warning(ex,
                        "ScheduledTaskSuppressor: Error re-enabling task {TaskPath}", state.TaskPath);
                    errorCount++;
                }
            }

            SettingsManager.Logger.Information(
                "ScheduledTaskSuppressor: Revert completed — {Restored} re-enabled, {Errors} errors",
                restoredCount, errorCount);

            _disabledTasks.Clear();
            IsApplied = false;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "ScheduledTaskSuppressor: Revert failed");
            IsApplied = false;
            return false;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Checks if a scheduled task exists and is enabled.
    /// Returns true if enabled, false if disabled, null if task doesn't exist.
    /// </summary>
    private static bool? IsTaskEnabled(string taskPath)
    {
        var output = RunSchtasks($"/query /tn \"{taskPath}\" /fo csv /nh");
        if (output == null || output.Contains("ERROR") || string.IsNullOrWhiteSpace(output))
            return null;

        // CSV output: "TaskPath","Next Run Time","Status"
        // Status can be "Ready", "Disabled", "Running", etc.
        if (output.Contains("Disabled", StringComparison.OrdinalIgnoreCase))
            return false;

        return true; // Ready, Running, etc. = enabled
    }

    /// <summary>
    /// Crash recovery: re-enables orphaned disabled tasks from a previous crashed session.
    /// Called during app startup when a stale lockfile is found.
    /// </summary>
    public static void CleanupStaleDisabledTasks(List<string> disabledTaskPaths)
    {
        if (disabledTaskPaths.Count == 0) return;

        foreach (var taskPath in disabledTaskPaths)
        {
            try
            {
                RunSchtasks($"/change /tn \"{taskPath}\" /enable");
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private static string? RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);

            return string.IsNullOrEmpty(error) ? output : $"ERROR: {error}";
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex,
                "ScheduledTaskSuppressor: schtasks failed: {Args}", arguments);
            return null;
        }
    }
}
