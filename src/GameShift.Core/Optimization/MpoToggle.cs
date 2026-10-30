using System.Text.Json;
using Microsoft.Win32;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// GameShift no longer turns off multiplane overlay during a game. The class stays so a session
/// journal written by an earlier version still restores the DWM and graphics driver values.
/// </summary>
public class MpoToggle : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;
    private bool _isApplied;

    private const string DwmRegistryPath = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string OverlayTestModeValue = "OverlayTestMode";
    private const string EnableOverlayValue = "EnableOverlay";
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string DisableOverlaysValue = "DisableOverlays";

    public const string OptimizationId = "MPO Toggle";

    public string Name => OptimizationId;
    public string Description => "Revert-only: restores multiplane overlay values recorded by earlier versions";
    public bool IsApplied => _isApplied;
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
    /// Watchdog recovery path: parses the serialized original registry state from the journal
    /// and restores it without requiring any live instance fields.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            _logger.Information("[MpoToggle] Reverting from journal record (watchdog recovery)");

            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalValueJson);
            if (values == null)
                return RevertFail("Failed to parse originalValueJson");

            using var dwmKey = Registry.LocalMachine.OpenSubKey(DwmRegistryPath, writable: true);
            if (dwmKey == null)
                return RevertFail("Failed to open DWM registry key");

            if (values.TryGetValue(OverlayTestModeValue, out var otm))
                RestoreRegistryDword(dwmKey, OverlayTestModeValue, otm);

            if (values.TryGetValue(EnableOverlayValue, out var eo))
                RestoreRegistryDword(dwmKey, EnableOverlayValue, eo);

            if (values.TryGetValue(DisableOverlaysValue, out var dov))
            {
                using var gfxKey = Registry.LocalMachine.OpenSubKey(GraphicsDriversPath, writable: true);
                if (gfxKey != null)
                    RestoreRegistryDword(gfxKey, DisableOverlaysValue, dov);
                else
                    _logger.Warning("[MpoToggle] Could not open GraphicsDrivers key during watchdog revert");
            }

            _isApplied = false;
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MpoToggle] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Restores a DWORD registry value from a JsonElement: null deletes the value, a number sets it.
    /// </summary>
    private void RestoreRegistryDword(RegistryKey key, string valueName, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            _logger.Information("[MpoToggle] Deleted {Key}\\{Name} (was absent before session)", key.Name, valueName);
        }
        else if (element.ValueKind == JsonValueKind.Number)
        {
            int val = element.GetInt32();
            key.SetValue(valueName, val, RegistryValueKind.DWord);
            _logger.Information("[MpoToggle] Restored {Key}\\{Name} = {Value}", key.Name, valueName, val);
        }
    }

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);
}
