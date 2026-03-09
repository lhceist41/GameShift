using System.Runtime.InteropServices;
using System.ServiceProcess;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;

namespace GameShift.Core.Optimization;

/// <summary>
/// Temporarily stops non-essential Windows services to free up system resources.
/// Services are organized into three tiers:
///   Tier 1 — High impact, always safe to stop during gaming.
///   Tier 2 — Medium impact, safe for gaming PCs (toggleable via profile).
///   Tier 3 — Conditional, only stopped when hardware/software conditions are met.
/// A safety list prevents critical system services from ever being stopped.
/// </summary>
public class ServiceSuppressor : IOptimization
{
    /// <summary>
    /// Describes a service target with metadata for tiered suppression.
    /// </summary>
    /// <param name="ServiceName">Windows service name (e.g., "WSearch").</param>
    /// <param name="DisplayName">Human-readable name for logging/UI.</param>
    /// <param name="Description">Optional tooltip/reason text.</param>
    /// <param name="Condition">For Tier 3: condition that must return true to auto-disable. Null = unconditional.</param>
    public readonly record struct ServiceInfo(
        string ServiceName,
        string DisplayName,
        string? Description = null,
        Func<bool>? Condition = null);

    // ── Tier 1 — High impact, always safe ────────────────────────────

    private static readonly ServiceInfo[] Tier1Services =
    {
        // Existing services (carried forward)
        new("DiagTrack",                                "Connected User Experiences and Telemetry"),
        new("diagnosticshub.standardcollector.service", "Diagnostics Hub Standard Collector"),
        new("dmwappushservice",                         "WAP Push Message Routing"),
        new("WSearch",                                  "Windows Search"),
        new("wuauserv",                                 "Windows Update"),
        new("DoSvc",                                    "Delivery Optimization"),
        new("UsoSvc",                                   "Update Orchestrator Service"),
        new("SysMain",                                  "Superfetch/SysMain"),
        new("defragsvc",                                "Optimize Drives"),
        new("WpnService",                               "Windows Push Notifications"),
        new("MapsBroker",                               "Downloaded Maps Manager"),
        new("lfsvc",                                    "Geolocation Service"),
        new("wisvc",                                    "Windows Insider Service"),

        // New additions
        new("BITS",                                     "Background Intelligent Transfer Service"),
        new("WerSvc",                                   "Windows Error Reporting"),
        new("DPS",                                      "Diagnostic Policy Service"),
        new("diagsvc",                                  "Diagnostic Execution Service"),
        new("PcaSvc",                                   "Program Compatibility Assistant"),
        new("AppXSvc",                                  "AppX Deployment Service",             "High CPU/memory usage on 24H2"),
        new("wercplsupport",                            "Problem Reports Control Panel Support"),
        new("Wecsvc",                                   "Windows Event Collector"),
    };

    // ── Tier 2 — Medium impact, safe for gaming PCs ──────────────────

    private static readonly ServiceInfo[] Tier2Services =
    {
        new("Spooler",        "Print Spooler",                  "Disable if you don't print during gaming"),
        new("Fax",            "Fax",                            "Safe to disable — almost nobody uses fax"),
        new("RemoteRegistry", "Remote Registry",                "Safe to disable on home PCs"),
        new("TapiSrv",        "Telephony",                      "Safe unless using dial-up modem"),
        new("SEMgrSvc",       "Payments and NFC/SE Manager",    "Safe unless using NFC"),
        new("iphlpsvc",       "IP Helper",                      "IPv6 transition — safe on most networks"),
        new("PhoneSvc",       "Phone Service",                  "Safe unless using Your Phone app actively"),
        new("RetailDemo",     "Retail Demo Service",            "Only used on store display PCs"),
    };

    // ── Tier 3 — Conditional ─────────────────────────────────────────

    private static readonly ServiceInfo[] Tier3Services =
    {
        // Xbox services — only if not needed
        new("XblAuthManager", "Xbox Live Auth Manager",         "Disable if not using Xbox features",
            () => !IsXboxRequired()),
        new("XblGameSave",    "Xbox Live Game Save",            "Disable if not using Xbox cloud saves",
            () => !IsXboxRequired()),
        new("XboxGipSvc",     "Xbox Accessory Management",      "Disable if not using Xbox controllers",
            () => !IsXboxRequired()),
        new("XboxNetApiSvc",  "Xbox Live Networking Service",   "Disable if not using Xbox networking",
            () => !IsXboxRequired()),

        // Bluetooth — only if no paired devices
        new("BthAvctpSvc",    "Bluetooth Audio AVCTP",          "Disable if not using Bluetooth audio",
            () => !HasBluetoothDevices()),
        new("BTAGService",    "Bluetooth Audio Gateway",        "Disable if not using Bluetooth headset",
            () => !HasBluetoothDevices()),

        // Touch/pen — only if no touchscreen
        new("TabletInputService", "Touch Keyboard and Handwriting", "Disable if no touchscreen",
            () => !HasTouchScreen()),

        // Biometrics — only if no fingerprint/face reader
        new("WbioSrvc",       "Windows Biometric Service",      "Disable if no fingerprint/face reader",
            () => !HasBiometrics()),
    };

    // ── Never-stop safety list ───────────────────────────────────────

    /// <summary>
    /// Services that must NEVER be stopped, regardless of tier assignments.
    /// Stopping any of these causes system instability, networking failure, or audio loss.
    /// </summary>
    private static readonly HashSet<string> NeverStopServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs",                 // Remote Procedure Call — stopping = instant freeze
        "RpcEptMapper",          // RPC Endpoint Mapper
        "DcomLaunch",            // DCOM Server Process Launcher
        "LSM",                   // Local Session Manager
        "CoreMessagingRegistrar",
        "Winmgmt",               // WMI — GameShift itself depends on this
        "NlaSvc",                // Network Location Awareness — breaks networking
        "Wcmsvc",                // Windows Connection Manager
        "nsi",                   // Network Store Interface — breaks networking
        "Dhcp",                  // DHCP Client — breaks networking
        "Dnscache",              // DNS Client — breaks name resolution
        "BrokerInfrastructure",  // Background Tasks Infrastructure
        "Power",                 // Power service
        "AudioSrv",              // Windows Audio — breaks game audio
        "AudioEndpointBuilder",  // Audio Endpoint Builder
        "EventLog",              // Windows Event Log

        // Anti-cheat services
        "vgc",                   // Riot Vanguard
        "BEService",             // BattlEye
        "EasyAntiCheat",         // EAC
        "EasyAntiCheat_EOS",     // EAC (Epic Online Services variant)
    };

    // ── IOptimization implementation ─────────────────────────────────

    public const string OptimizationId = "Windows Service Suppressor";

    public string Name => OptimizationId;

    public string Description => "Temporarily stops non-essential Windows services to free up system resources";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // Services that don't exist are skipped individually

    /// <summary>
    /// Stops services across all applicable tiers.
    /// Tier 1: always stopped.
    /// Tier 2: stopped if profile.SuppressTier2Services is true.
    /// Tier 3: stopped if the per-service condition is met.
    /// Records original state before stopping each service.
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        int stoppedCount = 0;
        int skippedCount = 0;
        int errorCount = 0;

        // Build the combined list of services to suppress
        var servicesToSuppress = new List<ServiceInfo>();

        // Always add Tier 1
        servicesToSuppress.AddRange(Tier1Services);

        // Add Tier 2 if profile allows (default: yes)
        if (profile.SuppressTier2Services)
        {
            servicesToSuppress.AddRange(Tier2Services);
        }

        // Add Tier 3 where conditions are met
        foreach (var svc in Tier3Services)
        {
            try
            {
                if (svc.Condition == null || svc.Condition())
                {
                    servicesToSuppress.Add(svc);
                }
                else
                {
                    SettingsManager.Logger.Debug(
                        "ServiceSuppressor: Tier 3 service {ServiceName} skipped — condition not met",
                        svc.ServiceName);
                }
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Debug(
                    "ServiceSuppressor: Tier 3 condition check failed for {ServiceName}: {Message}",
                    svc.ServiceName, ex.Message);
            }
        }

        foreach (var svcInfo in servicesToSuppress)
        {
            // Safety check — never stop critical services
            if (NeverStopServices.Contains(svcInfo.ServiceName))
            {
                SettingsManager.Logger.Warning(
                    "ServiceSuppressor: Service {ServiceName} is in the never-stop safety list — skipping",
                    svcInfo.ServiceName);
                skippedCount++;
                continue;
            }

            try
            {
                using var sc = new ServiceController(svcInfo.ServiceName);

                // Only stop if currently running
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    // Record original state before making changes
                    snapshot.RecordServiceState(svcInfo.ServiceName, sc.Status);

                    SettingsManager.Logger.Debug(
                        "ServiceSuppressor: Stopping service {ServiceName} ({DisplayName})",
                        svcInfo.ServiceName,
                        svcInfo.DisplayName);

                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));

                    SettingsManager.Logger.Information(
                        "ServiceSuppressor: Successfully stopped service {ServiceName}",
                        svcInfo.ServiceName);

                    stoppedCount++;
                }
                else
                {
                    SettingsManager.Logger.Debug(
                        "ServiceSuppressor: Skipping service {ServiceName} (status: {Status})",
                        svcInfo.ServiceName,
                        sc.Status);
                    skippedCount++;
                }
            }
            catch (InvalidOperationException ex)
            {
                // Service doesn't exist on this system - skip silently
                SettingsManager.Logger.Debug(
                    "ServiceSuppressor: Service {ServiceName} not found on system: {Message}",
                    svcInfo.ServiceName,
                    ex.Message);
                skippedCount++;
            }
            catch (global::System.ServiceProcess.TimeoutException ex)
            {
                // Service didn't stop within timeout - log warning and continue
                SettingsManager.Logger.Warning(
                    "ServiceSuppressor: Service {ServiceName} failed to stop within timeout: {Message}",
                    svcInfo.ServiceName,
                    ex.Message);
                errorCount++;
            }
            catch (Exception ex)
            {
                // Other errors - log and continue to next service
                SettingsManager.Logger.Warning(
                    ex,
                    "ServiceSuppressor: Failed to stop service {ServiceName}",
                    svcInfo.ServiceName);
                errorCount++;
            }
        }

        SettingsManager.Logger.Information(
            "ServiceSuppressor: Completed — {StoppedCount} stopped, {SkippedCount} skipped, {ErrorCount} errors",
            stoppedCount,
            skippedCount,
            errorCount);

        IsApplied = true;
        return Task.FromResult(true); // Partial success is still success
    }

    /// <summary>
    /// Restarts all services that were stopped during ApplyAsync.
    /// Uses snapshot to determine which services to restart.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
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
            "ServiceSuppressor: Revert completed — {RestartedCount} restarted, {ErrorCount} errors",
            restartedCount,
            errorCount);

        IsApplied = false;
        return Task.FromResult(true); // Partial success is still success
    }

    // ── Tier 3 condition helpers ──────────────────────────────────────

    /// <summary>
    /// Checks whether Xbox services are likely needed.
    /// Returns true if XblAuthManager is currently running (indicates active Xbox usage).
    /// </summary>
    private static bool IsXboxRequired()
    {
        try
        {
            using var sc = new ServiceController("XblAuthManager");
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false; // Service not found — Xbox not installed
        }
    }

    /// <summary>
    /// Checks whether any Bluetooth devices are paired.
    /// Looks for device entries under the BTHPORT registry key.
    /// </summary>
    private static bool HasBluetoothDevices()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
            return key?.SubKeyCount > 0;
        }
        catch
        {
            return false; // No Bluetooth stack installed
        }
    }

    /// <summary>
    /// Checks whether a touchscreen is present via GetSystemMetrics.
    /// </summary>
    private static bool HasTouchScreen()
    {
        try
        {
            return GetSystemMetrics(SM_MAXIMUMTOUCHES) > 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_MAXIMUMTOUCHES = 95;

    /// <summary>
    /// Checks whether biometric hardware is likely present.
    /// Returns true if the WbioSrvc service is currently running.
    /// </summary>
    private static bool HasBiometrics()
    {
        try
        {
            using var sc = new ServiceController("WbioSrvc");
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false; // Service not installed
        }
    }
}
