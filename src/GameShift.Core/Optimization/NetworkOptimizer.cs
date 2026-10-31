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

    internal RecoveryRegistry RecoveryRegistry { get; init; } = RecoveryRegistry.Windows;

    /// <summary>Starts a service unless it runs; true once it runs. A seam so the revert runs without the SCM.</summary>
    internal Func<string, bool> StartService { get; init; } = StartServiceIfStopped;

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

            var notRestored = new List<string>();
            int refused = 0;
            foreach (var (fullKey, element) in values)
            {
                bool? restored;
                try
                {
                    restored =
                        fullKey.StartsWith(RegistryPrefix, StringComparison.Ordinal)
                            ? RestoreRegistryFromJson(fullKey[RegistryPrefix.Length..], element)
                        : fullKey.StartsWith(ServicePrefix, StringComparison.Ordinal)
                            ? RestoreServiceFromJson(fullKey[ServicePrefix.Length..], element)
                        : fullKey.StartsWith(NetshPrefix, StringComparison.Ordinal)
                            ? RestoreNetshFromJson(fullKey[NetshPrefix.Length..], element)
                        : null;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "[NetworkOptimizer] Failed to restore entry {Key} during watchdog revert", fullKey);
                    restored = false;
                }

                if (restored == null)
                {
                    _logger.Warning("[NetworkOptimizer] Refused a journal entry GameShift did not write: {Key}", fullKey);
                    refused++;
                }
                else if (restored == false)
                {
                    notRestored.Add(fullKey);
                }
            }

            IsApplied = false;
            if (notRestored.Count == 0 && refused == 0)
                return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);

            var parts = new List<string>();
            if (notRestored.Count > 0)
                parts.Add($"Not restored: {string.Join(", ", notRestored)}");
            if (refused > 0)
                parts.Add($"Refused {refused} journal entr{(refused == 1 ? "y" : "ies")} that GameShift did not write");
            return RevertFail(string.Join(". ", parts));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[NetworkOptimizer] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    private static bool StartServiceIfStopped(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                SettingsManager.Logger.Information("[NetworkOptimizer] Started service {Service}", serviceName);
            }
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[NetworkOptimizer] Failed to start service {Service}", serviceName);
            return false;
        }
    }

    /// <summary>
    /// Restores a registry value from the watchdog journal. <paramref name="fullRegistryPath"/>
    /// is the full "HKLM\...\ValueName" path (prefix already stripped). Null when the record names
    /// a value the optimizer never wrote.
    /// </summary>
    private bool? RestoreRegistryFromJson(string fullRegistryPath, JsonElement element)
    {
        if (!RecoveryInputRules.TryNetworkRegistryValue(fullRegistryPath, out var subKeyPath, out var valueName))
            return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                RecoveryRegistry.DeleteValue(RecoveryHive.LocalMachine, subKeyPath, valueName);
                _logger.Information(
                    "[NetworkOptimizer] Deleted HKLM\\{Path}\\{Name} (was absent before session)", subKeyPath, valueName);
                return true;

            case JsonValueKind.Number when element.TryGetInt32(out int intVal):
                return Restored(RecoveryRegistry.SetValue(
                    RecoveryHive.LocalMachine, subKeyPath, valueName, intVal, RegistryValueKind.DWord), subKeyPath, valueName);

            case JsonValueKind.String:
                return Restored(RecoveryRegistry.SetValue(
                    RecoveryHive.LocalMachine, subKeyPath, valueName, element.GetString()!, RegistryValueKind.String), subKeyPath, valueName);

            default:
                return null;
        }
    }

    private bool Restored(bool written, string subKeyPath, string valueName)
    {
        if (written)
            _logger.Information("[NetworkOptimizer] Restored HKLM\\{Path}\\{Name}", subKeyPath, valueName);
        else
            _logger.Warning("[NetworkOptimizer] Could not open HKLM\\{Path} during watchdog revert", subKeyPath);
        return written;
    }

    /// <summary>
    /// Restores a service state from the watchdog journal. Only handles the "Running" case
    /// (start the service); if the original state was null or anything else, do nothing.
    /// </summary>
    private bool? RestoreServiceFromJson(string serviceName, JsonElement element)
    {
        if (!RecoveryInputRules.IsNetworkService(serviceName) ||
            element.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            return null;

        if (element.ValueKind == JsonValueKind.String &&
            element.GetString() == nameof(ServiceControllerStatus.Running))
            return StartService(serviceName);

        _logger.Debug(
            "[NetworkOptimizer] Service {Service} was not running before session - nothing to restart", serviceName);
        return true;
    }

    /// <summary>
    /// Restores a netsh-managed global setting from the watchdog journal.
    /// Currently supports the "rsc" key only.
    /// </summary>
    private bool? RestoreNetshFromJson(string setting, JsonElement element)
    {
        if (!string.Equals(setting, "rsc", StringComparison.OrdinalIgnoreCase))
            return null;

        if (element.ValueKind == JsonValueKind.Null)
        {
            // Earlier versions recorded null when they could not read RSC, and then left it alone.
            _logger.Debug("[NetworkOptimizer] Netsh rsc original state was null - skipping restore");
            return true;
        }

        string? state = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (state != "enabled" && state != "disabled")
            return null;

        var (exitCode, _) = Netsh($"int tcp set global rsc={state}");
        if (exitCode != 0)
        {
            _logger.Warning(
                "[NetworkOptimizer] Failed to restore RSC via netsh (exit code {ExitCode})", exitCode);
            return false;
        }

        _logger.Information("[NetworkOptimizer] Restored RSC to original state: {State}", state);
        return true;
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
