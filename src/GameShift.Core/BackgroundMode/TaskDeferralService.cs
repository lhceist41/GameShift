using System.Diagnostics;
using GameShift.Core.Config;

namespace GameShift.Core.BackgroundMode;

/// <summary>
/// Defers known Windows scheduled tasks during gaming sessions using schtasks.exe.
/// Disables tasks on game start, re-enables on game stop.
/// Tasks are well-known maintenance tasks that cause stutter during gaming.
/// </summary>
public class TaskDeferralService
{
    /// <summary>
    /// Well-known Windows maintenance tasks that can cause stutter during gaming.
    /// These are safe to temporarily disable and re-enable.
    /// </summary>
    private static readonly string[] DeferrableTasks = new[]
    {
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan",
        @"\Microsoft\Windows\UpdateOrchestrator\USO_UxBroker",
        @"\Microsoft\Windows\WindowsUpdate\Scheduled Start",
        @"\Microsoft\Windows\Defrag\ScheduledDefrag",
        @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
        @"\Microsoft\Windows\Maintenance\WinSAT",
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting"
    };

    private bool _isDeferred;
    private readonly List<string> _deferredTasks = new();

    public bool IsDeferred => _isDeferred;

    /// <summary>Number of tasks currently deferred.</summary>
    public int DeferredCount => _deferredTasks.Count;

    /// <summary>
    /// Disables all deferrable scheduled tasks. Called when a game starts.
    /// Only disables tasks that are currently enabled (records which ones for restore).
    /// </summary>
    public void DeferTasks()
    {
        if (_isDeferred) return;

        _deferredTasks.Clear();

        foreach (var task in DeferrableTasks)
        {
            try
            {
                // Check if task exists and is enabled first
                var queryOutput = RunSchtasks($"/query /tn \"{task}\" /fo csv /nh");
                if (queryOutput == null || queryOutput.Contains("ERROR")) continue;

                // Skip tasks that are already disabled — don't re-enable them on restore
                if (queryOutput.Contains("Disabled", StringComparison.OrdinalIgnoreCase)) continue;

                // Disable the task
                var result = RunSchtasks($"/change /tn \"{task}\" /disable");
                if (result != null && !result.Contains("ERROR"))
                {
                    _deferredTasks.Add(task);
                    SettingsManager.Logger.Debug("[TaskDeferral] Disabled: {Task}", task);
                }
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Warning(ex, "[TaskDeferral] Failed to disable {Task}", task);
            }
        }

        _isDeferred = _deferredTasks.Count > 0;
        SettingsManager.Logger.Information("[TaskDeferral] Deferred {Count} tasks", _deferredTasks.Count);
    }

    /// <summary>
    /// Re-enables all previously deferred tasks. Called when a game stops.
    /// </summary>
    public void RestoreTasks()
    {
        if (!_isDeferred) return;

        foreach (var task in _deferredTasks)
        {
            try
            {
                RunSchtasks($"/change /tn \"{task}\" /enable");
                SettingsManager.Logger.Debug("[TaskDeferral] Re-enabled: {Task}", task);
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Warning(ex, "[TaskDeferral] Failed to re-enable {Task}", task);
            }
        }

        SettingsManager.Logger.Information("[TaskDeferral] Restored {Count} tasks", _deferredTasks.Count);
        _deferredTasks.Clear();
        _isDeferred = false;
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
            SettingsManager.Logger.Warning(ex, "[TaskDeferral] schtasks failed: {Args}", arguments);
            return null;
        }
    }
}
