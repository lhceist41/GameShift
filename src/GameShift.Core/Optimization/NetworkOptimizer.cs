using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using System.ServiceProcess;

namespace GameShift.Core.Optimization;

/// <summary>
/// Reduces network latency by disabling Nagle's algorithm and stopping Delivery Optimization service.
/// Optimizes network settings for reduced latency during gameplay.
/// </summary>
public class NetworkOptimizer : IOptimization
{
    private const string TcpipInterfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string DoSvcServiceName = "DoSvc";

    /// <summary>
    /// Tracks which network interfaces were modified for targeted revert.
    /// </summary>
    private readonly List<string> _modifiedInterfaceIds = new();

    public string Name => "Network Optimizer";

    public string Description => "Disables Nagle's algorithm and Delivery Optimization for lower network latency";

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

            IsApplied = true;
            SettingsManager.Logger.Information("NetworkOptimizer: Applied successfully on {Count} interfaces", _modifiedInterfaceIds.Count);
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
            // Restore TCP settings
            await Task.Run(() => RestoreTcpSettings(snapshot));

            // Restart Delivery Optimization if it was running
            await Task.Run(() => RestartDeliveryOptimization(snapshot));

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
}
