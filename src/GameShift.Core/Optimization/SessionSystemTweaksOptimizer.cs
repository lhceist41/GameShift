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

    internal RecoveryRegistry RecoveryRegistry { get; init; } = RecoveryRegistry.Windows;

    /// <summary>Runs powercfg; true on exit code 0. A seam so the revert runs without powercfg.</summary>
    internal Func<string, bool> Powercfg { get; init; } = RunPowercfg;

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

            var problems = new List<string>();
            int refused = 0;

            // Restore registry values in reverse (LIFO) order.
            for (int i = payload.Backups.Count - 1; i >= 0; i--)
            {
                var b = payload.Backups[i];
                if (!RecoveryInputRules.IsSessionTweak(b.Hklm, b.Path, b.Name, b.Kind))
                {
                    _logger.Warning("[SessionSystemTweaks] Refused a journal entry GameShift did not write: {Path}\\{Name}", b.Path, b.Name);
                    refused++;
                    continue;
                }

                try
                {
                    bool restored;
                    if (!b.Existed)
                    {
                        RecoveryRegistry.DeleteValue(RecoveryHive.LocalMachine, b.Path, b.Name);
                        restored = true;
                    }
                    else if (b.Value is JsonElement { ValueKind: JsonValueKind.String } text && b.Kind == nameof(RegistryValueKind.String))
                    {
                        restored = RecoveryRegistry.SetValue(RecoveryHive.LocalMachine, b.Path, b.Name, text.GetString()!, RegistryValueKind.String);
                    }
                    else if (b.Value is JsonElement { ValueKind: JsonValueKind.Number } number && b.Kind == nameof(RegistryValueKind.DWord) &&
                             number.TryGetInt32(out int dword))
                    {
                        restored = RecoveryRegistry.SetValue(RecoveryHive.LocalMachine, b.Path, b.Name, dword, RegistryValueKind.DWord);
                    }
                    else
                    {
                        _logger.Warning("[SessionSystemTweaks] Recorded original of {Path}\\{Name} is not a {Kind}", b.Path, b.Name, b.Kind);
                        restored = false;
                    }

                    if (!restored)
                        problems.Add($@"HKLM\{b.Path}\{b.Name}");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SessionSystemTweaks] RevertFromRecord failed for {Path}\\{Name}", b.Path, b.Name);
                    problems.Add($@"HKLM\{b.Path}\{b.Name}");
                }
            }

            if (payload.Aspm.HasValue)
            {
                bool written = Powercfg($"/setacvalueindex SCHEME_CURRENT {PcieSubgroupGuid} {AspmSettingGuid} {payload.Aspm.Value}");
                bool activated = Powercfg("/setactive SCHEME_CURRENT");
                if (!written || !activated)
                    problems.Add("PCIe link state power management");
            }

            if (refused > 0)
                problems.Add($"{refused} refused registry entr{(refused == 1 ? "y" : "ies")}");

            return problems.Count == 0
                ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
                : new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed,
                    $"Not restored: {string.Join(", ", problems)}");
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

    private static bool RunPowercfg(string arguments)
    {
        var result = ProcessRunner.Run(NativeInterop.SystemExePath("powercfg.exe"), arguments, 10_000);
        return result.Exited && result.ExitCode == 0;
    }
}
