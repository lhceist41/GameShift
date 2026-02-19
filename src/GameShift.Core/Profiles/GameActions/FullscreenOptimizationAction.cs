using Microsoft.Win32;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Game-specific action that disables fullscreen optimizations for a game executable
/// by setting DISABLEDXMAXIMIZEDWINDOWEDMODE in the AppCompatFlags Layers registry key.
/// Apply sets the flag, Revert restores the previous value (or deletes if there was none).
/// </summary>
public class FullscreenOptimizationAction : GameAction
{
    private const string LayersKeyPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    private readonly string _name;
    private readonly string _executablePath;
    private readonly bool _includeDpiOverride;

    /// <param name="name">Display name, e.g. "Valorant Fullscreen Opt-Out".</param>
    /// <param name="executablePath">Full path to the game executable used as the registry value name.</param>
    /// <param name="includeDpiOverride">If true, also sets HIGHDPIAWARE for DPI scaling override.</param>
    public FullscreenOptimizationAction(string name, string executablePath,
        bool includeDpiOverride = false)
    {
        _name = name;
        _executablePath = executablePath;
        _includeDpiOverride = includeDpiOverride;
    }

    private string GetLayerValue() => _includeDpiOverride
        ? "~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE"
        : "~ DISABLEDXMAXIMIZEDWINDOWEDMODE";

    /// <inheritdoc/>
    public override string Name => _name;

    /// <inheritdoc/>
    public override void Apply(SystemStateSnapshot snapshot)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LayersKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(LayersKeyPath);

            if (key == null)
            {
                Log.Warning("FullscreenOptimizationAction: Could not open or create registry key {Key}", LayersKeyPath);
                return;
            }

            // Snapshot existing value before overwriting
            var existingValue = key.GetValue(_executablePath);
            if (existingValue != null)
            {
                snapshot.RecordRegistryValue(LayersKeyPath, _executablePath, existingValue);
            }

            key.SetValue(_executablePath, GetLayerValue());
            Log.Information(
                "FullscreenOptimizationAction: Set DISABLEDXMAXIMIZEDWINDOWEDMODE for {ExePath}",
                _executablePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "FullscreenOptimizationAction: Failed to set registry value for {ExePath}",
                _executablePath);
        }
    }

    /// <inheritdoc/>
    public override void Revert(SystemStateSnapshot snapshot)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LayersKeyPath, writable: true);

            if (key == null)
            {
                Log.Warning("FullscreenOptimizationAction: Registry key not found during revert: {Key}", LayersKeyPath);
                return;
            }

            var snapshotKey = $"{LayersKeyPath}\\{_executablePath}";
            if (snapshot.RegistryValues.TryGetValue(snapshotKey, out var previousValue))
            {
                // Restore previous value
                key.SetValue(_executablePath, previousValue);
                Log.Information(
                    "FullscreenOptimizationAction: Restored previous AppCompatFlags value for {ExePath}",
                    _executablePath);
            }
            else
            {
                // No previous value — delete our entry
                key.DeleteValue(_executablePath, throwOnMissingValue: false);
                Log.Information(
                    "FullscreenOptimizationAction: Removed DISABLEDXMAXIMIZEDWINDOWEDMODE for {ExePath}",
                    _executablePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "FullscreenOptimizationAction: Failed to revert registry value for {ExePath}",
                _executablePath);
        }
    }
}
