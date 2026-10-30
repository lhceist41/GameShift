using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// GameShift no longer changes multimedia scheduler, USB suspend or PCIe link power settings during a
/// game. The class stays so a session journal written by an earlier version still restores them.
/// </summary>
public class SessionSystemTweaksOptimizer : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    private const string PcieSubgroupGuid = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string AspmSettingGuid = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    public const string OptimizationId = "Session System Tweaks";

    public string Name => OptimizationId;
    public string Description => "Revert-only: restores session system tweaks recorded by earlier versions";
    public bool IsApplied => false;
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
    /// Watchdog recovery: restores the registry values and PCIe ASPM from the journaled
    /// OriginalValue JSON without any live instance state.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<SessionRevertPayload>(originalValueJson);
            if (payload == null)
                return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, "Failed to parse originalValueJson");

            // Restore registry values in reverse (LIFO) order.
            for (int i = payload.Backups.Count - 1; i >= 0; i--)
            {
                var b = payload.Backups[i];
                try
                {
                    var root = b.Hklm ? Registry.LocalMachine : Registry.CurrentUser;
                    using var key = root.OpenSubKey(b.Path, writable: true);
                    if (key == null) continue;

                    if (!b.Existed)
                    {
                        key.DeleteValue(b.Name, throwOnMissingValue: false);
                    }
                    else if (b.Value is JsonElement el)
                    {
                        if (string.Equals(b.Kind, "String", StringComparison.Ordinal))
                            key.SetValue(b.Name, el.GetString() ?? "", RegistryValueKind.String);
                        else
                            key.SetValue(b.Name, el.GetInt32(), RegistryValueKind.DWord);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SessionSystemTweaks] RevertFromRecord failed for {Path}\\{Name}", b.Path, b.Name);
                }
            }

            if (payload.Aspm.HasValue)
            {
                RunPowercfg($"/setacvalueindex SCHEME_CURRENT {PcieSubgroupGuid} {AspmSettingGuid} {payload.Aspm.Value}");
                RunPowercfg("/setactive SCHEME_CURRENT");
            }

            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SessionSystemTweaks] RevertFromRecord failed");
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, ex.Message);
        }
    }

    private sealed class SessionRevertPayload
    {
        public List<SessionBackupEntry> Backups { get; set; } = new();
        public int? Aspm { get; set; }
    }

    private sealed class SessionBackupEntry
    {
        public bool Hklm { get; set; }
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Existed { get; set; }
        public string Kind { get; set; } = "";
        public object? Value { get; set; }
    }

    private static string RunPowercfg(string arguments)
    {
        // Returns stdout regardless of exit code (callers regex-parse it); empty string when the
        // process can't start. The shared runner additionally reaps a wedged child instead of
        // leaving it running, with no change to the returned output.
        return ProcessRunner.Run(NativeInterop.SystemExePath("powercfg.exe"), arguments, 10_000).StdOut;
    }
}
