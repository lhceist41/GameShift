using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using System.ServiceProcess;
using System.Text.Json;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// GameShift no longer changes TCP, network adapter or Delivery Optimization settings during a game.
/// The class stays so a session journal written by an earlier version still restores them.
/// </summary>
public class NetworkOptimizer : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    // Key prefixes in the recorded state; RevertFromRecord dispatches on these.
    private const string RegistryPrefix = "Registry:";
    private const string ServicePrefix = "Service:";
    private const string NetshPrefix = "Netsh:";

    /// <summary>Runs netsh with the given arguments. A seam so the RSC path can be tested without netsh.</summary>
    internal Func<string, (int exitCode, string output)> Netsh { get; init; } =
        arguments => RunProcess(NativeInterop.SystemExePath("netsh.exe"), arguments);

    public const string OptimizationId = "Network Optimizer";

    public string Name => OptimizationId;
    public string Description => "Revert-only: restores network settings recorded by earlier versions";
    public bool IsApplied { get; private set; }
    public bool IsAvailable => false;

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile) => Task.FromResult(false);
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot) => Task.FromResult(true);

    public bool CanApply(SystemContext context) => false;

    public OptimizationResult Apply() =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, "Revert-only: no longer applied");

    public OptimizationResult Revert() =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);

    public bool Verify() => false;

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

    /// <summary>
    /// Invokes netsh to set the global RSC flag to the given state ("enabled" or "disabled").
    /// Shared by live revert and journal-driven revert.
    /// </summary>
    private void ApplyNetshRscSetting(string state)
    {
        var (exitCode, _) = Netsh($"int tcp set global rsc={state}");
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
                "[NetworkOptimizer] Service {Service} was not running before session - nothing to restart",
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
                "[NetworkOptimizer] Service {Service} original state was '{State}' - not restarting",
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
            // Original state unknown - nothing to restore.
            _logger.Debug("[NetworkOptimizer] Netsh rsc original state was null - skipping restore");
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

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    /// <summary>
    /// Runs an external process and captures output. 5-second timeout.
    /// </summary>
    private static (int exitCode, string output) RunProcess(string fileName, string arguments)
    {
        var result = ProcessRunner.Run(fileName, arguments, 5000);
        // Preserve the prior contract: exit code on a clean exit, otherwise -1 (failed-to-start or
        // timed-out) with whatever partial stdout was captured.
        return result.Exited ? (result.ExitCode, result.StdOut) : (-1, result.StdOut);
    }
}
