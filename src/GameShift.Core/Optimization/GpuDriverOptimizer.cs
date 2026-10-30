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

            int successCount = 0, failCount = 0;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                try
                {
                    if (!e.Existed)
                    {
                        DeleteRegistryValue(e.KeyPath, e.ValueName);
                    }
                    else if (e.Value is JsonElement el)
                    {
                        var kind = Enum.TryParse<RegistryValueKind>(e.Kind, out var k) ? k : RegistryValueKind.DWord;
                        object restored = kind switch
                        {
                            RegistryValueKind.Binary => el.GetBytesFromBase64(),
                            RegistryValueKind.String or RegistryValueKind.ExpandString => el.GetString() ?? "",
                            RegistryValueKind.QWord => el.GetInt64(),
                            _ => el.GetInt32()
                        };
                        Registry.SetValue(e.KeyPath, e.ValueName, restored, kind);
                    }
                    successCount++;
                }
                catch (Exception ex)
                {
                    failCount++;
                    _logger.Warning(ex, "[GpuDriverOptimizer] RevertFromRecord failed for {KeyPath}\\{ValueName}", e.KeyPath, e.ValueName);
                }
            }

            _logger.Information("[GpuDriverOptimizer] RevertFromRecord - {Success} restored, {Fail} failed", successCount, failCount);
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty,
                failCount == 0 ? OptimizationState.Reverted : OptimizationState.Failed);
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

    /// <summary>
    /// Deletes a registry value that was created during Apply (did not previously exist).
    /// Handles both HKLM and HKCU paths.
    /// </summary>
    private void DeleteRegistryValue(string keyPath, string valueName)
    {
        try
        {
            // Parse the root key and subpath from the full path
            RegistryKey? rootKey = null;
            string subPath;

            if (keyPath.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase))
            {
                rootKey = Registry.LocalMachine;
                subPath = keyPath.Substring(@"HKEY_LOCAL_MACHINE\".Length);
            }
            else if (keyPath.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
            {
                rootKey = Registry.CurrentUser;
                subPath = keyPath.Substring(@"HKEY_CURRENT_USER\".Length);
            }
            else
            {
                _logger.Warning(
                    "[GpuDriverOptimizer] Cannot determine registry root for path: {KeyPath}",
                    keyPath);
                return;
            }

            using var key = rootKey.OpenSubKey(subPath, writable: true);
            if (key != null)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[GpuDriverOptimizer] Failed to delete registry value {KeyPath}\\{ValueName}",
                keyPath,
                valueName);
        }
    }
}
