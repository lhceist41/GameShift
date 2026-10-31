using System.Text.Json;
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

    internal RecoveryRegistry RecoveryRegistry { get; init; } = RecoveryRegistry.Windows;

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

            var notRestored = new List<string>();
            foreach (var (path, name) in new[]
            {
                (DwmRegistryPath, OverlayTestModeValue),
                (DwmRegistryPath, EnableOverlayValue),
                (GraphicsDriversPath, DisableOverlaysValue),
            })
            {
                if (values.TryGetValue(name, out var original) &&
                    !RecoveryRegistry.RestoreDword(RecoveryHive.LocalMachine, path, name, original))
                {
                    _logger.Warning("[MpoToggle] Could not restore HKLM\\{Path}\\{Name} from the journal record", path, name);
                    notRestored.Add(name);
                }
            }

            _isApplied = false;
            return notRestored.Count == 0
                ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
                : RevertFail($"Not restored: {string.Join(", ", notRestored)}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MpoToggle] RevertFromRecord failed");
            return RevertFail(ex.Message);
        }
    }

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);
}
