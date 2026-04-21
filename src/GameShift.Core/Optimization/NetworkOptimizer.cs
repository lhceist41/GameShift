using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Reduces network latency by disabling Nagle's algorithm, stopping Delivery Optimization service,
/// disabling network throttling, optimizing NIC adapter settings (Interrupt Moderation, LSO, RSC),
/// and disabling Receive Segment Coalescing system-wide.
///
/// Implements IJournaledOptimization so the watchdog can restore the full TCP/NIC/service state
/// from the journal after a crash. The originalState dictionary uses typed key prefixes so a
/// single flat JSON carries registry values, service states, and netsh global state without
/// depending on any live instance fields at revert time.
/// </summary>
public class NetworkOptimizer : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    private const string TcpipInterfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string DoSvcServiceName = "DoSvc";

    // Task 1: Multimedia throttling registry path
    private const string MultimediaSystemProfilePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string NetworkThrottlingIndexName = "NetworkThrottlingIndex";
    private const string SystemResponsivenessName = "SystemResponsiveness";

    // Tasks 2-4: Network adapter class registry path
    private const string NetworkClassBasePath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    // ── originalState key prefixes (RevertFromRecord dispatches on these) ────
    private const string RegistryPrefix = "Registry:";
    private const string ServicePrefix = "Service:";
    private const string NetshPrefix = "Netsh:";
    private const string RscNetshKey = NetshPrefix + "rsc";

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

    /// <summary>
    /// Full original-state snapshot accumulated during Apply(). Populated by the per-action
    /// helpers and serialised into the OptimizationResult for journal-driven recovery.
    /// </summary>
    private Dictionary<string, object?> _originalState = new();

    /// <summary>
    /// Context stored by CanApply() for use by Apply().
    /// </summary>
    private SystemContext? _context;

    public const string OptimizationId = "Network Optimizer";

    public string Name => OptimizationId;

    public string Description => "Disables Nagle's algorithm, network throttling, interrupt moderation, LSO, and RSC for lower latency";

    public bool IsApplied { get; private set; }

    /// <summary>
    /// TCP/IP registry exists on all Windows systems.
    /// </summary>
    public bool IsAvailable => true;

    // ── IOptimization ─────────────────────────────────────────────────────────

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        var context = new SystemContext { Profile = profile, Snapshot = snapshot };
        if (!CanApply(context))
            return Task.FromResult(true);

        var result = Apply();
        return Task.FromResult(result.State == OptimizationState.Applied);
    }

    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        if (!IsApplied)
            return Task.FromResult(true);

        var result = Revert();
        return Task.FromResult(result.State == OptimizationState.Reverted);
    }

    // ── IJournaledOptimization ────────────────────────────────────────────────

    /// <summary>
    /// Pre-flight check. Stores context for use in Apply().
    /// </summary>
    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    /// <summary>
    /// Applies every tweak synchronously, capturing each original value into
    /// <see cref="_originalState"/> so the journaled revert path can restore the system
    /// without any live instance fields.
    /// </summary>
    public OptimizationResult Apply()
    {
        var snapshot = _context?.Snapshot;
        _originalState = new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            _logger.Information("[NetworkOptimizer] Applying network optimizations");

            // Disable Nagle's Algorithm on all network interfaces
            DisableNaglesAlgorithm(snapshot);

            // Stop Delivery Optimization service
            StopDeliveryOptimization(snapshot);

            // Task 1: Disable multimedia network throttling + set system responsiveness
            ApplyMultimediaThrottling(snapshot);

            // Tasks 2-4: Optimize NIC adapters (interrupt moderation, LSO, RSC registry)
            OptimizeNetworkAdapters(snapshot);

            // Task 4 (Approach A): Disable RSC globally via netsh
            DisableRscGlobal();

            IsApplied = true;
            _logger.Information(
                "[NetworkOptimizer] Applied successfully — {Interfaces} Nagle interfaces, {Nics} NIC adapters optimized",
                _modifiedInterfaceIds.Count, _nicOriginalStates.Count);

            return new OptimizationResult(
                Name: OptimizationId,
                OriginalValue: SerializeState(_originalState),
                AppliedValue: string.Empty,
                State: OptimizationState.Applied);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to apply");
            IsApplied = false;
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Reverts every tweak using live instance tracking (modified interface IDs,
    /// NIC states, RSC state).
    /// </summary>
    public OptimizationResult Revert()
    {
        try
        {
            _logger.Information("[NetworkOptimizer] Reverting network optimizations");

            // Restore TCP settings (Nagle)
            RestoreTcpSettings();

            // Restart Delivery Optimization if it was running
            RestartDeliveryOptimization();

            // Task 1: Restore multimedia throttling
            RevertMultimediaThrottling();

            // Tasks 2-4: Restore NIC adapter settings
            RevertNetworkAdapters();

            // Task 4: Restore RSC global state via netsh
            RestoreRscGlobal();

            IsApplied = false;
            _logger.Information("[NetworkOptimizer] Reverted successfully");

            return new OptimizationResult(
                Name: OptimizationId,
                OriginalValue: string.Empty,
                AppliedValue: string.Empty,
                State: OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to revert");
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Confirms the applied network changes are still in effect by spot-checking
    /// the multimedia throttling values (a fast, single-key sample).
    /// Returns true if applied values are still present on the system.
    /// </summary>
    public bool Verify()
    {
        if (!IsApplied)
            return false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MultimediaSystemProfilePath);
            if (key?.GetValue(NetworkThrottlingIndexName) is not int throttling)
                return false;
            if (throttling != unchecked((int)0xFFFFFFFF))
                return false;

            if (key.GetValue(SystemResponsivenessName) is not int responsiveness || responsiveness != 10)
                return false;

            // Spot-check the first modified Nagle interface (if any were recorded)
            if (_modifiedInterfaceIds.Count > 0)
            {
                using var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpipInterfacesPath);
                using var ifaceKey = interfacesKey?.OpenSubKey(_modifiedInterfaceIds[0]);
                if (ifaceKey?.GetValue("TcpAckFrequency") is not int ack || ack != 1)
                    return false;
                if (ifaceKey.GetValue("TCPNoDelay") is not int nodelay || nodelay != 1)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Watchdog recovery path: parses the serialized original state from the journal and
    /// restores every registry value, service state, and netsh setting without any live
    /// instance fields. Dispatches by key prefix.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            _logger.Information("[NetworkOptimizer] Reverting from journal record (watchdog recovery)");

            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalValueJson);
            if (values == null)
                return RevertFail("Failed to parse originalValueJson");

            foreach (var (fullKey, element) in values)
            {
                try
                {
                    if (fullKey.StartsWith(RegistryPrefix, StringComparison.Ordinal))
                    {
                        RestoreRegistryFromJson(fullKey[RegistryPrefix.Length..], element);
                    }
                    else if (fullKey.StartsWith(ServicePrefix, StringComparison.Ordinal))
                    {
                        RestoreServiceFromJson(fullKey[ServicePrefix.Length..], element);
                    }
                    else if (fullKey.StartsWith(NetshPrefix, StringComparison.Ordinal))
                    {
                        RestoreNetshFromJson(fullKey[NetshPrefix.Length..], element);
                    }
                    else
                    {
                        _logger.Warning(
                            "[NetworkOptimizer] Unknown key prefix in journal record: {Key}", fullKey);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "[NetworkOptimizer] Failed to restore entry {Key} during watchdog revert", fullKey);
                }
            }

            IsApplied = false;
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[NetworkOptimizer] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    // ── Nagle's Algorithm ─────────────────────────────────────────────────────

    /// <summary>
    /// Disables Nagle's algorithm by setting TcpAckFrequency=1 and TCPNoDelay=1 on all interfaces.
    /// </summary>
    private void DisableNaglesAlgorithm(SystemStateSnapshot? snapshot)
    {
        try
        {
            using var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpipInterfacesPath);
            if (interfacesKey == null)
            {
                _logger.Warning("[NetworkOptimizer] TCP/IP Interfaces registry key not found");
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

                    string registryPath = $@"HKLM\{TcpipInterfacesPath}\{interfaceId}";

                    // Record and set TcpAckFrequency
                    object? currentAckFreq = interfaceKey.GetValue("TcpAckFrequency");
                    RecordRegistry(registryPath, "TcpAckFrequency", currentAckFreq);
                    snapshot?.RecordRegistryValue(registryPath, "TcpAckFrequency", currentAckFreq ?? "__NOT_SET__");
                    interfaceKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);

                    // Record and set TCPNoDelay
                    object? currentNoDelay = interfaceKey.GetValue("TCPNoDelay");
                    RecordRegistry(registryPath, "TCPNoDelay", currentNoDelay);
                    snapshot?.RecordRegistryValue(registryPath, "TCPNoDelay", currentNoDelay ?? "__NOT_SET__");
                    interfaceKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);

                    _modifiedInterfaceIds.Add(interfaceId);
                    modifiedCount++;
                }
                catch (Exception ex)
                {
                    // Log per-interface errors but continue processing others
                    _logger.Debug(ex, "[NetworkOptimizer] Failed to modify interface {InterfaceId}", interfaceId);
                }
            }

            _logger.Information(
                "[NetworkOptimizer] Disabled Nagle's algorithm on {Count} network interfaces", modifiedCount);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to disable Nagle's algorithm");
        }
    }

    // ── Delivery Optimization service ─────────────────────────────────────────

    /// <summary>
    /// Stops the Delivery Optimization service to prevent background downloads.
    /// </summary>
    private void StopDeliveryOptimization(SystemStateSnapshot? snapshot)
    {
        try
        {
            using var service = new ServiceController(DoSvcServiceName);

            // Record original state if service is running
            if (service.Status == ServiceControllerStatus.Running)
            {
                _originalState[ServicePrefix + DoSvcServiceName] = nameof(ServiceControllerStatus.Running);
                snapshot?.RecordServiceState(DoSvcServiceName, service.Status);
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                _logger.Information("[NetworkOptimizer] Stopped Delivery Optimization service");
            }
            else
            {
                // Record non-running state as null so watchdog won't try to restart it
                _originalState[ServicePrefix + DoSvcServiceName] = null;
                _logger.Debug("[NetworkOptimizer] Delivery Optimization service not running, skipping");
            }
        }
        catch (Exception ex)
        {
            // Service may not exist or be accessible - not critical
            _logger.Debug(ex, "[NetworkOptimizer] Failed to stop Delivery Optimization service");
        }
    }

    // ── Task 1: Multimedia Throttling + SystemResponsiveness ──────────────────

    /// <summary>
    /// Disables multimedia network throttling by setting NetworkThrottlingIndex to 0xFFFFFFFF
    /// and reduces CPU reservation for background tasks via SystemResponsiveness = 10.
    /// </summary>
    private void ApplyMultimediaThrottling(SystemStateSnapshot? snapshot)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MultimediaSystemProfilePath, writable: true);
            if (key == null)
            {
                _logger.Warning("[NetworkOptimizer] Multimedia SystemProfile registry key not found");
                return;
            }

            string regPath = $@"HKLM\{MultimediaSystemProfilePath}";

            // NetworkThrottlingIndex: store original, set to 0xFFFFFFFF (disabled)
            object? currentThrottling = key.GetValue(NetworkThrottlingIndexName);
            RecordRegistry(regPath, NetworkThrottlingIndexName, currentThrottling);
            snapshot?.RecordRegistryValue(regPath, NetworkThrottlingIndexName,
                currentThrottling ?? "__NOT_SET__");
            key.SetValue(NetworkThrottlingIndexName, unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
            _logger.Information(
                "[NetworkOptimizer] Set NetworkThrottlingIndex to 0xFFFFFFFF (was {Original})",
                currentThrottling ?? "not set");

            // SystemResponsiveness: store original, set to 10 (minimum background reservation)
            object? currentResponsiveness = key.GetValue(SystemResponsivenessName);
            RecordRegistry(regPath, SystemResponsivenessName, currentResponsiveness);
            snapshot?.RecordRegistryValue(regPath, SystemResponsivenessName,
                currentResponsiveness ?? "__NOT_SET__");
            key.SetValue(SystemResponsivenessName, 10, RegistryValueKind.DWord);
            _logger.Information(
                "[NetworkOptimizer] Set SystemResponsiveness to 10 (was {Original})",
                currentResponsiveness ?? "not set");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to apply multimedia throttling settings");
        }
    }

    /// <summary>
    /// Restores original NetworkThrottlingIndex and SystemResponsiveness values from the in-memory
    /// original state.
    /// </summary>
    private void RevertMultimediaThrottling()
    {
        try
        {
            string regPath = $@"HKLM\{MultimediaSystemProfilePath}";

            using var key = Registry.LocalMachine.OpenSubKey(MultimediaSystemProfilePath, writable: true);
            if (key == null) return;

            RestoreRegistryDwordFromState(key, regPath, NetworkThrottlingIndexName);
            RestoreRegistryDwordFromState(key, regPath, SystemResponsivenessName);

            _logger.Information("[NetworkOptimizer] Restored multimedia throttling settings");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to revert multimedia throttling settings");
        }
    }

    // ── Tasks 2-4: Unified NIC Adapter Optimization Loop ──────────────────────

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
    private void OptimizeNetworkAdapters(SystemStateSnapshot? snapshot)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(NetworkClassBasePath);
            if (baseKey == null)
            {
                _logger.Warning("[NetworkOptimizer] Network adapter class registry key not found");
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

                    string adapterRegPath = $@"HKLM\{NetworkClassBasePath}\{subKeyName}";
                    bool modified = false;

                    // Task 2: Interrupt Moderation
                    if (original.InterruptModeration != null && original.InterruptModeration != "0")
                    {
                        RecordRegistry(adapterRegPath, "*InterruptModeration", original.InterruptModeration);
                        adapterKey.SetValue("*InterruptModeration", "0", RegistryValueKind.String);
                        _logger.Information(
                            "[NetworkOptimizer] Disabled Interrupt Moderation on {Adapter} (was \"{Original}\")",
                            driverDesc, original.InterruptModeration);
                        modified = true;
                    }

                    // Task 3: LSO v2 IPv4
                    if (original.LsoV2IPv4 != null && original.LsoV2IPv4 != "0")
                    {
                        RecordRegistry(adapterRegPath, "*LsoV2IPv4", original.LsoV2IPv4);
                        adapterKey.SetValue("*LsoV2IPv4", "0", RegistryValueKind.String);
                        _logger.Information(
                            "[NetworkOptimizer] Disabled LSO v2 IPv4 on {Adapter}", driverDesc);
                        modified = true;
                    }

                    // Task 3: LSO v2 IPv6
                    if (original.LsoV2IPv6 != null && original.LsoV2IPv6 != "0")
                    {
                        RecordRegistry(adapterRegPath, "*LsoV2IPv6", original.LsoV2IPv6);
                        adapterKey.SetValue("*LsoV2IPv6", "0", RegistryValueKind.String);
                        _logger.Information(
                            "[NetworkOptimizer] Disabled LSO v2 IPv6 on {Adapter}", driverDesc);
                        modified = true;
                    }

                    // Task 4 (Approach B): RSC IPv4
                    if (original.RscIPv4 != null && original.RscIPv4 != "0")
                    {
                        RecordRegistry(adapterRegPath, "*RscIPv4", original.RscIPv4);
                        adapterKey.SetValue("*RscIPv4", "0", RegistryValueKind.String);
                        _logger.Information(
                            "[NetworkOptimizer] Disabled RSC IPv4 on {Adapter}", driverDesc);
                        modified = true;
                    }

                    // Task 4 (Approach B): RSC IPv6
                    if (original.RscIPv6 != null && original.RscIPv6 != "0")
                    {
                        RecordRegistry(adapterRegPath, "*RscIPv6", original.RscIPv6);
                        adapterKey.SetValue("*RscIPv6", "0", RegistryValueKind.String);
                        _logger.Information(
                            "[NetworkOptimizer] Disabled RSC IPv6 on {Adapter}", driverDesc);
                        modified = true;
                    }

                    if (modified)
                    {
                        _nicOriginalStates.Add(original);
                        _logger.Information(
                            "[NetworkOptimizer] Optimized NIC: {Adapter} [{SubKey}]", driverDesc, subKeyName);
                    }

                    _ = snapshot; // snapshot already covered via shared registry records above
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex,
                        "[NetworkOptimizer] Failed to optimize NIC adapter [{SubKey}]", subKeyName);
                }
            }

            if (_nicOriginalStates.Count > 0)
            {
                _logger.Information(
                    "[NetworkOptimizer] NIC optimization complete — {Count} adapters modified. " +
                    "Some NICs may briefly drop connection when registry values change.",
                    _nicOriginalStates.Count);
            }
            else
            {
                _logger.Information(
                    "[NetworkOptimizer] No NIC adapters required modification (all already optimized or unsupported)");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to optimize NIC adapters");
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

                    _logger.Information(
                        "[NetworkOptimizer] Restored NIC settings on {Adapter} [{SubKey}]",
                        original.DriverDesc, original.SubKeyName);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex,
                        "[NetworkOptimizer] Failed to restore NIC [{SubKey}]", original.SubKeyName);
                }
            }

            _logger.Information(
                "[NetworkOptimizer] Restored NIC settings on {Count} adapters", _nicOriginalStates.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to revert NIC adapters");
        }

        _nicOriginalStates.Clear();
    }

    // ── Task 4 (Approach A): RSC Global via netsh ─────────────────────────────

    /// <summary>
    /// Disables Receive Segment Coalescing system-wide via netsh.
    /// Captures current RSC state for revert.
    /// </summary>
    private void DisableRscGlobal()
    {
        try
        {
            // Capture current RSC state
            var (queryExitCode, queryOutput) = RunProcess(NativeInterop.SystemExePath("netsh.exe"), "int tcp show global");
            if (queryExitCode == 0)
            {
                _originalRscState = ParseRscState(queryOutput);
                _originalState[RscNetshKey] = _originalRscState;
                _logger.Information(
                    "[NetworkOptimizer] Current RSC state: {State}", _originalRscState ?? "unknown");
            }
            else
            {
                _logger.Warning(
                    "[NetworkOptimizer] Failed to query RSC state (exit code {ExitCode})", queryExitCode);
            }

            // Disable RSC
            var (disableExitCode, _) = RunProcess(NativeInterop.SystemExePath("netsh.exe"), "int tcp set global rsc=disabled");
            if (disableExitCode == 0)
            {
                _logger.Information("[NetworkOptimizer] Disabled RSC globally via netsh");
            }
            else
            {
                _logger.Warning(
                    "[NetworkOptimizer] Failed to disable RSC via netsh (exit code {ExitCode})", disableExitCode);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to disable RSC globally");
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
            ApplyNetshRscSetting(_originalRscState);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to restore RSC globally");
        }

        _originalRscState = null;
    }

    /// <summary>
    /// Invokes netsh to set the global RSC flag to the given state ("enabled" or "disabled").
    /// Shared by live revert and journal-driven revert.
    /// </summary>
    private void ApplyNetshRscSetting(string state)
    {
        var (exitCode, _) = RunProcess(
            NativeInterop.SystemExePath("netsh.exe"), $"int tcp set global rsc={state}");
        if (exitCode == 0)
        {
            _logger.Information(
                "[NetworkOptimizer] Restored RSC to original state: {State}", state);
        }
        else
        {
            _logger.Warning(
                "[NetworkOptimizer] Failed to restore RSC via netsh (exit code {ExitCode})", exitCode);
        }
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

    // ── Revert helpers (live) ─────────────────────────────────────────────────

    /// <summary>
    /// Restores TCP settings for all interfaces that Apply() modified using the in-memory
    /// original state captured during Apply.
    /// </summary>
    private void RestoreTcpSettings()
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

                RestoreRegistryDwordFromState(interfaceKey, registryPath, "TcpAckFrequency");
                RestoreRegistryDwordFromState(interfaceKey, registryPath, "TCPNoDelay");
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[NetworkOptimizer] Failed to restore interface {InterfaceId}", interfaceId);
            }
        }

        _logger.Information(
            "[NetworkOptimizer] Restored TCP settings on {Count} interfaces", _modifiedInterfaceIds.Count);
    }

    /// <summary>
    /// Restarts Delivery Optimization service if it was running before optimization.
    /// </summary>
    private void RestartDeliveryOptimization()
    {
        try
        {
            if (_originalState.TryGetValue(ServicePrefix + DoSvcServiceName, out var statusObj)
                && statusObj is string statusName
                && statusName == nameof(ServiceControllerStatus.Running))
            {
                StartServiceIfStopped(DoSvcServiceName);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[NetworkOptimizer] Failed to restart Delivery Optimization service");
        }
    }

    /// <summary>
    /// Starts the named service if it's not already running. Waits up to 10s for the Running state.
    /// Shared between live revert and watchdog revert paths.
    /// </summary>
    private void StartServiceIfStopped(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                _logger.Information("[NetworkOptimizer] Started service {Service}", serviceName);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[NetworkOptimizer] Failed to start service {Service}", serviceName);
        }
    }

    // ── originalState helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Records a registry value (DWORD int or REG_SZ string) into <see cref="_originalState"/>
    /// keyed by the "Registry:" prefix. Null values indicate "value was absent — delete on revert".
    /// </summary>
    private void RecordRegistry(string regPath, string valueName, object? currentValue)
    {
        string key = $"{RegistryPrefix}{regPath}\\{valueName}";

        // Normalize stored value to JSON-friendly types.
        // null = didn't exist, int = DWORD, string = REG_SZ.
        _originalState[key] = currentValue switch
        {
            null => null,
            int i => i,
            string s => s,
            _ => currentValue.ToString() // fallback — any other numeric type becomes a string
        };
    }

    /// <summary>
    /// Restores a single registry value from the in-memory <see cref="_originalState"/>.
    /// </summary>
    private void RestoreRegistryDwordFromState(RegistryKey key, string regPath, string valueName)
    {
        string stateKey = $"{RegistryPrefix}{regPath}\\{valueName}";
        if (!_originalState.TryGetValue(stateKey, out var originalValue))
            return;

        if (originalValue == null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else if (originalValue is int intValue)
        {
            key.SetValue(valueName, intValue, RegistryValueKind.DWord);
        }
        else if (originalValue is string strValue)
        {
            key.SetValue(valueName, strValue, RegistryValueKind.String);
        }
    }

    /// <summary>
    /// Serialises <see cref="_originalState"/> into a JSON string suitable for persistence.
    /// </summary>
    private static string SerializeState(Dictionary<string, object?> state) =>
        JsonSerializer.Serialize(state);

    // ── Watchdog revert dispatchers ───────────────────────────────────────────

    /// <summary>
    /// Restores a registry value from the watchdog journal. <paramref name="fullRegistryPath"/>
    /// is the full "HKLM\...\ValueName" path (prefix already stripped).
    /// </summary>
    private void RestoreRegistryFromJson(string fullRegistryPath, JsonElement element)
    {
        // Split at the LAST backslash so the ValueName survives even when it contains slashes.
        int sepIdx = fullRegistryPath.LastIndexOf('\\');
        if (sepIdx <= 0 || sepIdx >= fullRegistryPath.Length - 1)
        {
            _logger.Warning(
                "[NetworkOptimizer] Invalid registry path in journal: {Path}", fullRegistryPath);
            return;
        }

        string fullKeyPath = fullRegistryPath[..sepIdx];
        string valueName = fullRegistryPath[(sepIdx + 1)..];

        // Strip "HKLM\" prefix to get the subkey path.
        const string hklmPrefix = @"HKLM\";
        if (!fullKeyPath.StartsWith(hklmPrefix, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning(
                "[NetworkOptimizer] Unsupported registry hive in journal path: {Path}", fullKeyPath);
            return;
        }
        string subKeyPath = fullKeyPath[hklmPrefix.Length..];

        using var key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true);
        if (key == null)
        {
            _logger.Warning(
                "[NetworkOptimizer] Could not open {Path} during watchdog revert", fullKeyPath);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                key.DeleteValue(valueName, throwOnMissingValue: false);
                _logger.Information(
                    "[NetworkOptimizer] Deleted {Path}\\{Name} (was absent before session)",
                    fullKeyPath, valueName);
                break;

            case JsonValueKind.Number:
                int intVal = element.GetInt32();
                key.SetValue(valueName, intVal, RegistryValueKind.DWord);
                _logger.Information(
                    "[NetworkOptimizer] Restored {Path}\\{Name} = {Value} (DWORD)",
                    fullKeyPath, valueName, intVal);
                break;

            case JsonValueKind.String:
                string strVal = element.GetString() ?? string.Empty;
                key.SetValue(valueName, strVal, RegistryValueKind.String);
                _logger.Information(
                    "[NetworkOptimizer] Restored {Path}\\{Name} = \"{Value}\" (REG_SZ)",
                    fullKeyPath, valueName, strVal);
                break;

            default:
                _logger.Warning(
                    "[NetworkOptimizer] Unsupported JSON kind {Kind} for registry {Path}\\{Name}",
                    element.ValueKind, fullKeyPath, valueName);
                break;
        }
    }

    /// <summary>
    /// Restores a service state from the watchdog journal. Only handles the "Running" case
    /// (start the service); if the original state was null or anything else, do nothing.
    /// </summary>
    private void RestoreServiceFromJson(string serviceName, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            _logger.Debug(
                "[NetworkOptimizer] Service {Service} was not running before session — nothing to restart",
                serviceName);
            return;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            _logger.Warning(
                "[NetworkOptimizer] Unexpected JSON kind {Kind} for service {Service}",
                element.ValueKind, serviceName);
            return;
        }

        string stateName = element.GetString() ?? string.Empty;
        if (stateName == nameof(ServiceControllerStatus.Running))
        {
            StartServiceIfStopped(serviceName);
        }
        else
        {
            _logger.Debug(
                "[NetworkOptimizer] Service {Service} original state was '{State}' — not restarting",
                serviceName, stateName);
        }
    }

    /// <summary>
    /// Restores a netsh-managed global setting from the watchdog journal.
    /// Currently supports the "rsc" key only.
    /// </summary>
    private void RestoreNetshFromJson(string setting, JsonElement element)
    {
        if (!string.Equals(setting, "rsc", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning(
                "[NetworkOptimizer] Unknown netsh setting in journal: {Setting}", setting);
            return;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            // Original state unknown — nothing to restore.
            _logger.Debug("[NetworkOptimizer] Netsh rsc original state was null — skipping restore");
            return;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            _logger.Warning(
                "[NetworkOptimizer] Unexpected JSON kind {Kind} for netsh rsc", element.ValueKind);
            return;
        }

        string state = element.GetString() ?? string.Empty;
        if (state != "enabled" && state != "disabled")
        {
            _logger.Warning(
                "[NetworkOptimizer] Invalid netsh rsc state in journal: {State}", state);
            return;
        }

        ApplyNetshRscSetting(state);
    }

    // ── Utility helpers ───────────────────────────────────────────────────────

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

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

        // Read stdout and stderr concurrently to avoid pipe buffer deadlock
        string stderr = "";
        var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
        string output = process.StandardOutput.ReadToEnd();
        stderrTask.Wait(5000);

        process.WaitForExit(5000);
        return (process.ExitCode, output);
    }
}
