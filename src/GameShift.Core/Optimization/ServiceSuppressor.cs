using System.ServiceProcess;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Temporarily stops non-essential Windows services to free up system resources.
/// Suppresses non-essential Windows services during gaming sessions.
/// </summary>
public class ServiceSuppressor : IOptimization
{
    /// <summary>
    /// List of Windows services to suppress during gaming sessions.
    /// Based on PRD specification - includes telemetry, search indexing, updates, etc.
    /// </summary>
    private static readonly string[] ServicesToSuppress = new[]
    {
        "DiagTrack",                           // Connected User Experiences and Telemetry
        "diagnosticshub.standardcollector.service", // Microsoft Diagnostics Hub
        "dmwappushservice",                    // WAP Push Message Routing Service
        "WSearch",                             // Windows Search (indexing)
        "wuauserv",                            // Windows Update
        "DoSvc",                               // Delivery Optimization
        "UsoSvc",                              // Update Orchestrator Service
        "SysMain",                             // Superfetch/Prefetch
        "defragsvc",                           // Optimize Drives
        "WpnService",                          // Windows Push Notifications
        "TabletInputService",                  // Touch Keyboard and Handwriting Panel
        "MapsBroker",                          // Downloaded Maps Manager
        "lfsvc",                               // Geolocation Service
        "wisvc",                               // Windows Insider Service
        "XblAuthManager",                      // Xbox Live Auth Manager
        "XblGameSave",                         // Xbox Live Game Save
        "XboxGipSvc",                          // Xbox Accessory Management
        "XboxNetApiSvc"                        // Xbox Live Networking Service
    };

    public string Name => "Windows Service Suppressor";

    public string Description => "Temporarily stops non-essential Windows services to free up system resources";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // Services that don't exist are skipped individually

    /// <summary>
    /// Stops all running services from the suppression list.
    /// Records original state before stopping each service.
    /// Skips services that don't exist or aren't running.
    /// </summary>
    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        await Task.CompletedTask; // Make async to satisfy interface

        int stoppedCount = 0;
        int skippedCount = 0;
        int errorCount = 0;

        foreach (var serviceName in ServicesToSuppress)
        {
            try
            {
                using var sc = new ServiceController(serviceName);

                // Only stop if currently running
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    // Record original state before making changes
                    snapshot.RecordServiceState(serviceName, sc.Status);

                    SettingsManager.Logger.Debug(
                        "ServiceSuppressor: Stopping service {ServiceName} (current status: {Status})",
                        serviceName,
                        sc.Status);

                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));

                    SettingsManager.Logger.Information(
                        "ServiceSuppressor: Successfully stopped service {ServiceName}",
                        serviceName);

                    stoppedCount++;
                }
                else
                {
                    SettingsManager.Logger.Debug(
                        "ServiceSuppressor: Skipping service {ServiceName} (status: {Status})",
                        serviceName,
                        sc.Status);
                    skippedCount++;
                }
            }
            catch (InvalidOperationException ex)
            {
                // Service doesn't exist on this system - skip silently
                SettingsManager.Logger.Debug(
                    "ServiceSuppressor: Service {ServiceName} not found on system: {Message}",
                    serviceName,
                    ex.Message);
                skippedCount++;
            }
            catch (global::System.ServiceProcess.TimeoutException ex)
            {
                // Service didn't stop within timeout - log warning and continue
                SettingsManager.Logger.Warning(
                    "ServiceSuppressor: Service {ServiceName} failed to stop within timeout: {Message}",
                    serviceName,
                    ex.Message);
                errorCount++;
            }
            catch (Exception ex)
            {
                // Other errors - log and continue to next service
                SettingsManager.Logger.Warning(
                    ex,
                    "ServiceSuppressor: Failed to stop service {ServiceName}",
                    serviceName);
                errorCount++;
            }
        }

        SettingsManager.Logger.Information(
            "ServiceSuppressor: Completed - {StoppedCount} stopped, {SkippedCount} skipped, {ErrorCount} errors",
            stoppedCount,
            skippedCount,
            errorCount);

        IsApplied = true;
        return true; // Partial success is still success
    }

    /// <summary>
    /// Restarts all services that were stopped during ApplyAsync.
    /// Uses snapshot to determine which services to restart.
    /// </summary>
    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        await Task.CompletedTask; // Make async to satisfy interface

        int restartedCount = 0;
        int errorCount = 0;

        foreach (var entry in snapshot.ServiceStates)
        {
            // Only restart services that were running before
            if (entry.Value != ServiceControllerStatus.Running)
            {
                continue;
            }

            try
            {
                using var sc = new ServiceController(entry.Key);

                // Only restart if currently stopped
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    SettingsManager.Logger.Debug(
                        "ServiceSuppressor: Restarting service {ServiceName}",
                        entry.Key);

                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));

                    SettingsManager.Logger.Information(
                        "ServiceSuppressor: Successfully restarted service {ServiceName}",
                        entry.Key);

                    restartedCount++;
                }
                else
                {
                    SettingsManager.Logger.Debug(
                        "ServiceSuppressor: Service {ServiceName} already running (status: {Status})",
                        entry.Key,
                        sc.Status);
                }
            }
            catch (InvalidOperationException ex)
            {
                // Service no longer exists - not a fatal error
                SettingsManager.Logger.Warning(
                    "ServiceSuppressor: Service {ServiceName} not found during revert: {Message}",
                    entry.Key,
                    ex.Message);
                errorCount++;
            }
            catch (global::System.ServiceProcess.TimeoutException ex)
            {
                // Service didn't start within timeout
                SettingsManager.Logger.Warning(
                    "ServiceSuppressor: Service {ServiceName} failed to start within timeout: {Message}",
                    entry.Key,
                    ex.Message);
                errorCount++;
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Warning(
                    ex,
                    "ServiceSuppressor: Failed to restart service {ServiceName}",
                    entry.Key);
                errorCount++;
            }
        }

        SettingsManager.Logger.Information(
            "ServiceSuppressor: Revert completed - {RestartedCount} restarted, {ErrorCount} errors",
            restartedCount,
            errorCount);

        IsApplied = false;
        return true; // Partial success is still success
    }
}
