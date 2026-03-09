using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using System.Diagnostics;
using System.ServiceProcess;

namespace GameShift.Core.Optimization;

/// <summary>
/// Reduces network latency by disabling Nagle's algorithm, stopping Delivery Optimization service,
/// disabling network throttling, optimizing NIC adapter settings (Interrupt Moderation, LSO, RSC),
/// and disabling Receive Segment Coalescing system-wide.
/// </summary>
public class NetworkOptimizer : IOptimization
{
    private const string TcpipInterfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string DoSvcServiceName = "DoSvc";

    // Task 1: Multimedia throttling registry path
    private const string MultimediaSystemProfilePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string NetworkThrottlingIndexName = "NetworkThrottlingIndex";
    private const string SystemResponsivenessName = "SystemResponsiveness";

    // Tasks 2-4: Network adapter class registry path
    private const string NetworkClassBasePath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Tracks which network interfaces were modified for Nagle's algorithm revert.
    /// </summary>
    private readonly List<string> _modifiedInterfaceIds = new();

    /// <summary>
    /// Tracks per-adapter NIC original states for Interrupt Moderation, LSO, RSC revert.
    /// </summary>
    private readonly List<NicOriginalState> _nicOriginalStates = new();

    /// <summary>
    /// Tracks original RSC state from netsh for revert.
    /// Null if RSC state was not captured.
    /// </summary>
    private string? _originalRscState;

    public const string OptimizationId = "Network Optimizer";

    public string Name => OptimizationId;

    public string Description => "Disables Nagle's algorithm, network throttling, interrupt moderation, LSO, and RSC for lower latency";

    public bool IsApplied { get; private set; }

    /// <summary>
    /// TCP/IP registry exists on all Windows systems.
    /// </summary>
    public bool IsAvailable => true;

    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            // Disable Nagle's Algorithm on all network interfaces
            await Task.Run(() => DisableNaglesAlgorithm(snapshot));

            // Stop Delivery Optimization service
            await Task.Run(() => StopDeliveryOptimization(snapshot));

            // Task 1: Disable multimedia network throttling + set system responsiveness
            await Task.Run(() => ApplyMultimediaThrottling(snapshot));

            // Tasks 2-4: Optimize NIC adapters (interrupt moderation, LSO, RSC registry)
            await Task.Run(() => OptimizeNetworkAdapters(snapshot));

            // Task 4 (Approach A): Disable RSC globally via netsh
            await Task.Run(() => DisableRscGlobal());

            IsApplied = true;
            SettingsManager.Logger.Information(
                "NetworkOptimizer: Applied successfully — {Interfaces} Nagle interfaces, {Nics} NIC adapters optimized",
                _modifiedInterfaceIds.Count, _nicOriginalStates.Count);
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "NetworkOptimizer: Failed to apply");
            IsApplied = false;
            return false;
        }
    }

    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        try
        {
            // Restore TCP settings (Nagle)
            await Task.Run(() => RestoreTcpSettings(snapshot));

            // Restart Delivery Optimization if it was running
            await Task.Run(() => RestartDeliveryOptimization(snapshot));

            // Task 1: Restore multimedia throttling
            await Task.Run(() => RevertMultimediaThrottling(snapshot));

            // Tasks 2-4: Restore NIC adapter settings
            await Task.Run(() => RevertNetworkAdapters());

            // Task 4: Restore RSC global state via netsh
            await Task.Run(() => RestoreRscGlobal());

            IsApplied = false;
            SettingsManager.Logger.Information("NetworkOptimizer: Reverted successfully");
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "NetworkOptimizer: Failed to revert");
            return false;
        }
    }

    // ── Existing: Nagle's Algorithm ─────────────────────────────────────────

    /// <summary>
    /// Disables Nagle's algorithm by setting TcpAckFrequency=1 and TCPNoDelay=1 on all interfaces.
    /// </summary>
    private void DisableNaglesAlgorithm(SystemStateSnapshot snapshot)
    {
        try
        {
            using var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpipInterfacesPath);
            if (interfacesKey == null)
            {
                SettingsManager.Logger.Warning("NetworkOptimizer: TCP/IP Interfaces registry key not found");
                return;
            }

            string[] interfaceIds = interfacesKey.GetSubKeyNames();
            int modifiedCount = 0;

            foreach (string interfaceId in interfaceIds)
            {
                try
                {
                    using var interfaceKey = interfacesKey.OpenSubKey(interfaceId, writable: true);
                    if (interfaceKey == null)
                    {
                        // Skip non-writable or inaccessible interfaces
                        continue;
                    }

                    // Record and set TcpAckFrequency
                    object? currentAckFreq = interfaceKey.GetValue("TcpAckFrequency");
                    string registryPath = $@"HKLM\{TcpipInterfacesPath}\{interfaceId}";

                    if (currentAckFreq == null)
                    {
                        snapshot.RecordRegistryValue(registryPath, "TcpAckFrequency", "__NOT_SET__");
                    }
                    else
                    {
                        snapshot.RecordRegistryValue(registryPath, "TcpAckFrequency", currentAckFreq);
                    }
                    interfaceKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);

                    // Record and set TCPNoDelay
                    object? currentNoDelay = interfaceKey.GetValue("TCPNoDelay");
                    if (currentNoDelay == null)
                    {
                        snapshot.RecordRegistryValue(registryPath, "TCPNoDelay", "__NOT_SET__");
                    }
                    else
                    {
                        snapshot.RecordRegistryValue(registryPath, "TCPNoDelay", currentNoDelay);
                    }
                    interfaceKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);

                    _modifiedInterfaceIds.Add(interfaceId);
                    modifiedCount++;
                }
                catch (Exception ex)
                {
                    // Log per-interface errors but continue processing others
                    SettingsManager.Logger.Debug(ex, "NetworkOptimizer: Failed to modify interface {InterfaceId}", interfaceId);
                }
            }

            SettingsManager.Logger.Information("NetworkOptimizer: Disabled Nagle's algorithm on {Count} network interfaces", modifiedCount);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "NetworkOptimizer: Failed to disable Nagle's algorithm");
        }
    }

    // ── Existing: Delivery Optimization service ─────────────────────────────

    /// <summary>
    /// Stops the Delivery Optimization service to prevent background downloads.
    /// </summary>
    private void StopDeliveryOptimization(SystemStateSnapshot snapshot)
    {
        try
        {
            var service = new ServiceController(DoSvcServiceName);

            // Record original state if service is running
            if (service.Status == ServiceControllerStatus.Running)
            {
                snapshot.RecordServiceState(DoSvcServiceName, service.Status);
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                SettingsManager.Logger.Information("NetworkOptimizer: Stopped Delivery Optimization service");
            }
            else
            {
                SettingsManager.Logger.Debug("NetworkOptimizer: Delivery Optimization service not running, skipping");
            }
        }
        catch (Exception ex)
        {
            // Service may not exist or be accessible - not critical
            SettingsManager.Logger.Debug(ex, "NetworkOptimizer: Failed to stop Delivery Optimization service");
        }
    }

    // ── Task 1: Multimedia Throttling + SystemResponsiveness ────────────────

    /// <summary>
    /// Disables multimedia network throttling by setting NetworkThrottlingIndex to 0xFFFFFFFF
    /// and reduces CPU reservation for background tasks via SystemResponsiveness = 10.
    /// </summary>
    private void ApplyMultimediaThrottling(SystemStateSnapshot snapshot)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MultimediaSystemProfilePath, writable: true);
            if (key == null)
            {
                SettingsManager.Logger.Warning("NetworkOptimizer: Multimedia SystemProfile registry key not found");
                return;
            }

            string regPath = $@"HKLM\{MultimediaSystemProfilePath}";

            // NetworkThrottlingIndex: store original, set to 0xFFFFFFFF (disabled)
            object? currentThrottling = key.GetValue(NetworkThrottlingIndexName);
            snapshot.RecordRegistryValue(regPath, NetworkThrottlingIndexName,
                currentThrottling ?? "__NOT_SET__");
            key.SetValue(NetworkThrottlingIndexName, unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
            SettingsManager.Logger.Information(
                "NetworkOptimizer: Set NetworkThrottlingIndex to 0xFFFFFFFF (was {Original})",
                currentThrottling ?? "not set");

            // SystemResponsiveness: store original, set to 10 (minimum background reservation)
            object? currentResponsiveness = key.GetValue(SystemResponsivenessName);
            snapshot.RecordRegistryValue(regPath, SystemResponsivenessName,
                currentResponsiveness ?? "__NOT_SET__");
            key.SetValue(SystemResponsivenessName, 10, RegistryValueKind.DWord);
            SettingsManager.Logger.Information(
                "NetworkOptimizer: Set SystemResponsiveness to 10 (was {Original})",
                currentResponsiveness ?? "not set");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "NetworkOptimizer: Failed to apply multimedia throttling settings");
        }
    }

    /// <summary>
    /// Restores original NetworkThrottlingIndex and SystemResponsiveness values.
    /// </summary>
    private void RevertMultimediaThrottling(SystemStateSnapshot snapshot)
    {
        try
        {
            string regPath = $@"HKLM\{MultimediaSystemProfilePath}";

            using var key = Registry.LocalMachine.OpenSubKey(MultimediaSystemProfilePath, writable: true);
            if (key == null) return;

            // Restore NetworkThrottlingIndex
            RestoreRegistryDword(key, regPath, NetworkThrottlingIndexName, snapshot);

            // Restore SystemResponsiveness
            RestoreRegistryDword(key, regPath, SystemResponsivenessName, snapshot);

            SettingsManager.Logger.Information("NetworkOptimizer: Restored multimedia throttling settings");
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "NetworkOptimizer: Failed to revert multimedia throttling settings");
        }
    }

    // ── Tasks 2-4: Unified NIC Adapter Optimization Loop ────────────────────

    /// <summary>
    /// Captures original NIC adapter state per adapter for revert.
    /// </summary>
    private readonly record struct NicOriginalState(
        string SubKeyName,
        string DriverDesc,
        string? InterruptModeration,
        string? LsoV2IPv4,
        string? LsoV2IPv6,
        string? RscIPv4,
        string? RscIPv6
    );

    /// <summary>
    /// Single-pass optimization of all network adapters:
    /// - Task 2: Disable Interrupt Moderation (*InterruptModeration = "0")
    /// - Task 3: Disable LSO v2 (*LsoV2IPv4 = "0", *LsoV2IPv6 = "0")
    /// - Task 4 (Approach B): Disable RSC (*RscIPv4 = "0", *RscIPv6 = "0")
    /// Only modifies values that already exist on the adapter (feature-supported).
    /// </summary>
    private void OptimizeNetworkAdapters(SystemStateSnapshot snapshot)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(NetworkClassBasePath);
            if (baseKey == null)
            {
                SettingsManager.Logger.Warning("NetworkOptimizer: Network adapter class registry key not found");
                return;
            }

            foreach (string subKeyName in baseKey.GetSubKeyNames())
            {
                // Skip non-numeric subkeys (like "Properties")
                if (!int.TryParse(subKeyName, out _)) continue;

                try
                {
                    using var adapterKey = baseKey.OpenSubKey(subKeyName, writable: true);
                    if (adapterKey == null) continue;

                    // Check if this is an actual network adapter (has DriverDesc)
                    var driverDesc = adapterKey.GetValue("DriverDesc") as string;
                    if (string.IsNullOrEmpty(driverDesc)) continue;

                    // Capture all original values (null if not supported by this adapter)
                    var original = new NicOriginalState(
                        SubKeyName: subKeyName,
                        DriverDesc: driverDesc,
                        InterruptModeration: adapterKey.GetValue("*InterruptModeration") as string,
                        LsoV2IPv4: adapterKey.GetValue("*LsoV2IPv4") as string,
                        LsoV2IPv6: adapterKey.GetValue("*LsoV2IPv6") as string,
                        RscIPv4: adapterKey.GetValue("*RscIPv4") as string,
                        RscIPv6: adapterKey.GetValue("*RscIPv6") as string
                    );

                    bool modified = false;

                    // Task 2: Interrupt Moderation
                    if (original.InterruptModeration != null && original.InterruptModeration != "0")
                    {
                        adapterKey.SetValue("*InterruptModeration", "0", RegistryValueKind.String);
                        SettingsManager.Logger.Information(
                            "NetworkOptimizer: Disabled Interrupt Moderation on {Adapter} (was \"{Original}\")",
                            driverDesc, original.InterruptModeration);
                        modified = true;
                    }

                    // Task 3: LSO v2 IPv4
                    if (original.LsoV2IPv4 != null && original.LsoV2IPv4 != "0")
                    {
                        adapterKey.SetValue("*LsoV2IPv4", "0", RegistryValueKind.String);
                        SettingsManager.Logger.Information(
                            "NetworkOptimizer: Disabled LSO v2 IPv4 on {Adapter}", driverDesc);
                        modified = true;
                    }

                    // Task 3: LSO v2 IPv6
                    if (original.LsoV2IPv6 != null && original.LsoV2IPv6 != "0")
                    {
                        adapterKey.SetValue("*LsoV2IPv6", "0", RegistryValueKind.String);
                        SettingsManager.Logger.Information(
                            "NetworkOptimizer: Disabled LSO v2 IPv6 on {Adapter}", driverDesc);
                        modified = true;
                    }

                    // Task 4 (Approach B): RSC IPv4
                    if (original.RscIPv4 != null && original.RscIPv4 != "0")
                    {
                        adapterKey.SetValue("*RscIPv4", "0", RegistryValueKind.String);
                        SettingsManager.Logger.Information(
                            "NetworkOptimizer: Disabled RSC IPv4 on {Adapter}", driverDesc);
                        modified = true;
                    }

                    // Task 4 (Approach B): RSC IPv6
                    if (original.RscIPv6 != null && original.RscIPv6 != "0")
                    {
                        adapterKey.SetValue("*RscIPv6", "0", RegistryValueKind.String);
                        SettingsManager.Logger.Information(
                            "NetworkOptimizer: Disabled RSC IPv6 on {Adapter}", driverDesc);
                        modified = true;
                    }

                    if (modified)
                    {
                        _nicOriginalStates.Add(original);
                        SettingsManager.Logger.Information(
                            "NetworkOptimizer: Optimized NIC: {Adapter} [{SubKey}]", driverDesc, subKeyName);
                    }
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Debug(ex,
                        "NetworkOptimizer: Failed to optimize NIC adapter [{SubKey}]", subKeyName);
                }
            }

            if (_nicOriginalStates.Count > 0)
            {
                SettingsManager.Logger.Information(
                    "NetworkOptimizer: NIC optimization complete — {Count} adapters modified. " +
                    "Some NICs may briefly drop connection when registry values change.",
                    _nicOriginalStates.Count);
            }
            else
            {
                SettingsManager.Logger.Information(
                    "NetworkOptimizer: No NIC adapters required modification (all already optimized or unsupported)");
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "NetworkOptimizer: Failed to optimize NIC adapters");
        }
    }

    /// <summary>
    /// Restores original NIC adapter settings for all modified adapters.
    /// Only restores values that were actually changed.
    /// </summary>
    private void RevertNetworkAdapters()
    {
        if (_nicOriginalStates.Count == 0) return;

        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(NetworkClassBasePath);
            if (baseKey == null) return;

            foreach (var original in _nicOriginalStates)
            {
                try
                {
                    using var adapterKey = baseKey.OpenSubKey(original.SubKeyName, writable: true);
                    if (adapterKey == null) continue;

                    // Restore each value to its original state
                    if (original.InterruptModeration != null)
                        adapterKey.SetValue("*InterruptModeration", original.InterruptModeration, RegistryValueKind.String);

                    if (original.LsoV2IPv4 != null)
                        adapterKey.SetValue("*LsoV2IPv4", original.LsoV2IPv4, RegistryValueKind.String);

                    if (original.LsoV2IPv6 != null)
                        adapterKey.SetValue("*LsoV2IPv6", original.LsoV2IPv6, RegistryValueKind.String);

                    if (original.RscIPv4 != null)
                        adapterKey.SetValue("*RscIPv4", original.RscIPv4, RegistryValueKind.String);

                    if (original.RscIPv6 != null)
                        adapterKey.SetValue("*RscIPv6", original.RscIPv6, RegistryValueKind.String);

                    SettingsManager.Logger.Information(
                        "NetworkOptimizer: Restored NIC settings on {Adapter} [{SubKey}]",
                        original.DriverDesc, original.SubKeyName);
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Debug(ex,
                        "NetworkOptimizer: Failed to restore NIC [{SubKey}]", original.SubKeyName);
                }
            }

            SettingsManager.Logger.Information(
                "NetworkOptimizer: Restored NIC settings on {Count} adapters", _nicOriginalStates.Count);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "NetworkOptimizer: Failed to revert NIC adapters");
        }

        _nicOriginalStates.Clear();
    }

    // ── Task 4 (Approach A): RSC Global via netsh ───────────────────────────

    /// <summary>
    /// Disables Receive Segment Coalescing system-wide via netsh.
    /// Captures current RSC state for revert.
    /// </summary>
    private void DisableRscGlobal()
    {
        try
        {
            // Capture current RSC state
            var (queryExitCode, queryOutput) = RunProcess("netsh", "int tcp show global");
            if (queryExitCode == 0)
            {
                _originalRscState = ParseRscState(queryOutput);
                SettingsManager.Logger.Information(
                    "NetworkOptimizer: Current RSC state: {State}", _originalRscState ?? "unknown");
            }
            else
            {
                SettingsManager.Logger.Warning(
                    "NetworkOptimizer: Failed to query RSC state (exit code {ExitCode})", queryExitCode);
            }

            // Disable RSC
            var (disableExitCode, _) = RunProcess("netsh", "int tcp set global rsc=disabled");
            if (disableExitCode == 0)
            {
                SettingsManager.Logger.Information("NetworkOptimizer: Disabled RSC globally via netsh");
            }
            else
            {
                SettingsManager.Logger.Warning(
                    "NetworkOptimizer: Failed to disable RSC via netsh (exit code {ExitCode})", disableExitCode);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "NetworkOptimizer: Failed to disable RSC globally");
        }
    }

    /// <summary>
    /// Restores RSC to its original state via netsh.
    /// </summary>
    private void RestoreRscGlobal()
    {
        if (_originalRscState == null) return;

        try
        {
            var (exitCode, _) = RunProcess("netsh", $"int tcp set global rsc={_originalRscState}");
            if (exitCode == 0)
            {
                SettingsManager.Logger.Information(
                    "NetworkOptimizer: Restored RSC to original state: {State}", _originalRscState);
            }
            else
            {
                SettingsManager.Logger.Warning(
                    "NetworkOptimizer: Failed to restore RSC via netsh (exit code {ExitCode})", exitCode);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "NetworkOptimizer: Failed to restore RSC globally");
        }

        _originalRscState = null;
    }

    /// <summary>
    /// Parses the "Receive Segment Coalescing State" from netsh output.
    /// Returns "enabled" or "disabled", or null if not found.
    /// </summary>
    private static string? ParseRscState(string netshOutput)
    {
        // Look for "Receive Segment Coalescing State" line
        foreach (var line in netshOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Receive Segment Coalescing State", StringComparison.OrdinalIgnoreCase))
            {
                // Format: "Receive Segment Coalescing State : enabled"
                int colonIdx = trimmed.IndexOf(':');
                if (colonIdx >= 0)
                {
                    var value = trimmed[(colonIdx + 1)..].Trim().ToLowerInvariant();
                    return value; // "enabled" or "disabled"
                }
            }
        }
        return null;
    }

    // ── Existing: Revert helpers ────────────────────────────────────────────

    /// <summary>
    /// Restores original TCP settings from snapshot.
    /// Deletes registry values that didn't exist before optimization.
    /// </summary>
    private void RestoreTcpSettings(SystemStateSnapshot snapshot)
    {
        foreach (string interfaceId in _modifiedInterfaceIds)
        {
            try
            {
                using var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpipInterfacesPath);
                if (interfacesKey == null) continue;

                using var interfaceKey = interfacesKey.OpenSubKey(interfaceId, writable: true);
                if (interfaceKey == null) continue;

                string registryPath = $@"HKLM\{TcpipInterfacesPath}\{interfaceId}";

                // Restore TcpAckFrequency
                string ackFreqKey = $"{registryPath}\\TcpAckFrequency";
                if (snapshot.RegistryValues.TryGetValue(ackFreqKey, out object? ackFreqValue))
                {
                    if (ackFreqValue is string strValue && strValue == "__NOT_SET__")
                    {
                        // Value didn't exist before - delete it
                        interfaceKey.DeleteValue("TcpAckFrequency", throwOnMissingValue: false);
                    }
                    else
                    {
                        // Restore original value
                        interfaceKey.SetValue("TcpAckFrequency", ackFreqValue, RegistryValueKind.DWord);
                    }
                }

                // Restore TCPNoDelay
                string noDelayKey = $"{registryPath}\\TCPNoDelay";
                if (snapshot.RegistryValues.TryGetValue(noDelayKey, out object? noDelayValue))
                {
                    if (noDelayValue is string strValue && strValue == "__NOT_SET__")
                    {
                        // Value didn't exist before - delete it
                        interfaceKey.DeleteValue("TCPNoDelay", throwOnMissingValue: false);
                    }
                    else
                    {
                        // Restore original value
                        interfaceKey.SetValue("TCPNoDelay", noDelayValue, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Debug(ex, "NetworkOptimizer: Failed to restore interface {InterfaceId}", interfaceId);
            }
        }

        SettingsManager.Logger.Information("NetworkOptimizer: Restored TCP settings on {Count} interfaces", _modifiedInterfaceIds.Count);
    }

    /// <summary>
    /// Restarts Delivery Optimization service if it was running before optimization.
    /// </summary>
    private void RestartDeliveryOptimization(SystemStateSnapshot snapshot)
    {
        try
        {
            if (snapshot.ServiceStates.TryGetValue(DoSvcServiceName, out var originalStatus))
            {
                if (originalStatus == ServiceControllerStatus.Running)
                {
                    var service = new ServiceController(DoSvcServiceName);
                    if (service.Status != ServiceControllerStatus.Running)
                    {
                        service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        SettingsManager.Logger.Information("NetworkOptimizer: Restarted Delivery Optimization service");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Debug(ex, "NetworkOptimizer: Failed to restart Delivery Optimization service");
        }
    }

    // ── Utility helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Restores a DWORD registry value from snapshot, or deletes it if it didn't exist before.
    /// </summary>
    private static void RestoreRegistryDword(RegistryKey key, string regPath, string valueName, SystemStateSnapshot snapshot)
    {
        string compositeKey = $"{regPath}\\{valueName}";
        if (snapshot.RegistryValues.TryGetValue(compositeKey, out object? originalValue))
        {
            if (originalValue is string strValue && strValue == "__NOT_SET__")
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            else if (originalValue is int intValue)
            {
                key.SetValue(valueName, intValue, RegistryValueKind.DWord);
            }
            else
            {
                key.SetValue(valueName, originalValue, RegistryValueKind.DWord);
            }
        }
    }

    /// <summary>
    /// Runs an external process and captures output. 5-second timeout.
    /// </summary>
    private static (int exitCode, string output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            return (-1, "");

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return (process.ExitCode, output);
    }
}
