using System.Text.Json;
using Microsoft.Win32;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// GameShift no longer writes graphics driver registry values during a game. The class stays so a
/// session journal written by an earlier version still restores the values it recorded.
/// </summary>
public class GpuDriverOptimizer : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    public const string OptimizationId = "GPU Driver Optimizer";

    public string Name => OptimizationId;
    public string Description => "Revert-only: restores graphics driver values recorded by earlier versions";
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

    /// <summary>
    /// Watchdog recovery: restores the registry changes recorded in the journal OriginalValue JSON.
    /// SDK-level driver settings (NvAPI DRS / ADLX) need the live session and cannot be reverted
    /// statelessly, so only the persistent registry state (P-states, TDR, shader cache, etc.) is restored.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<GpuChangeEntry>>(originalValueJson);
            if (entries == null)
            {
                // Apply always journals a list (at minimum "[]"); a literal JSON null means the
                // record is corrupt. Report failure instead of claiming a successful revert.
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Failed,
                    "Corrupt journal record (null entry list)");
            }

            int successCount = 0;
            var problems = new List<string>();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e == null ||
                    !RecoveryInputRules.TryGpuDriverValue(e.KeyPath, e.ValueName, e.Kind, out var hive, out var subKeyPath, out var kind))
                {
                    _logger.Warning("[GpuDriverOptimizer] Refused a journal entry GameShift did not write: {KeyPath}\\{ValueName}", e?.KeyPath, e?.ValueName);
                    problems.Add("a refused entry");
                    continue;
                }

                try
                {
                    bool restored;
                    if (!e.Existed)
                    {
                        RecoveryRegistry.DeleteValue(hive, subKeyPath, e.ValueName);
                        restored = true;
                    }
                    else if (e.Value is JsonElement el)
                    {
                        object value = kind switch
                        {
                            RegistryValueKind.Binary => el.GetBytesFromBase64(),
                            RegistryValueKind.String => el.GetString() ?? throw new FormatException("String original is null"),
                            _ => el.GetInt32()
                        };
                        restored = RecoveryRegistry.SetValue(hive, subKeyPath, e.ValueName, value, kind);
                    }
                    else
                    {
                        restored = false;
                    }

                    if (restored)
                        successCount++;
                    else
                        problems.Add($@"{e.KeyPath}\{e.ValueName}");
                }
                catch (Exception ex)
                {
                    problems.Add($@"{e.KeyPath}\{e.ValueName}");
                    _logger.Warning(ex, "[GpuDriverOptimizer] RevertFromRecord failed for {KeyPath}\\{ValueName}", e.KeyPath, e.ValueName);
                }
            }

            _logger.Information("[GpuDriverOptimizer] RevertFromRecord - {Success} restored, {Fail} failed", successCount, problems.Count);
            return problems.Count == 0
                ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
                : new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed,
                    $"Not restored: {string.Join(", ", problems)}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[GpuDriverOptimizer] RevertFromRecord failed");
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, ex.Message);
        }
    }

    private sealed class GpuChangeEntry
    {
        public string KeyPath { get; set; } = "";
        public string ValueName { get; set; } = "";
        public bool Existed { get; set; }
        public string Kind { get; set; } = "";
        public object? Value { get; set; }
    }
}
