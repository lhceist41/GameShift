using GameShift.Core.Detection;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Profiles.GameActions;

/// <summary>
/// Game-specific action that overrides a GPU driver registry value during gameplay.
/// Hardware-conditional — only applies to the matching GPU vendor.
/// Example: disable AMD Anti-Lag during LoL (known crash issue with Anti-Lag enabled).
/// Reverts to the previous value on game exit.
/// </summary>
public class GpuRegistryOverrideAction : GameAction
{
    private const string DisplayAdapterClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DriverClassBasePath =
        @"SYSTEM\CurrentControlSet\Control\Class\" + DisplayAdapterClassGuid;

    private readonly string _name;
    private readonly GpuVendor _requiredVendor;
    private readonly string _umdValueName;
    private readonly string _gameActiveValue;
    private readonly string _conditionText;
    private string? _previousValue;
    private bool _previouslyExisted;
    private bool _applied;
    private string? _appliedSubkeyPath;

    /// <param name="name">Display name, e.g. "LoL AMD Anti-Lag Disable".</param>
    /// <param name="requiredVendor">GPU vendor this applies to (e.g. GpuVendor.Amd).</param>
    /// <param name="umdValueName">Registry value name under the UMD subkey (e.g. "AntiLag_DevMode").</param>
    /// <param name="gameActiveValue">Value to set during gameplay (e.g. "0" to disable).</param>
    /// <param name="conditionText">Human-readable condition description for UI display.</param>
    public GpuRegistryOverrideAction(string name, GpuVendor requiredVendor,
        string umdValueName, string gameActiveValue, string conditionText)
    {
        _name = name;
        _requiredVendor = requiredVendor;
        _umdValueName = umdValueName;
        _gameActiveValue = gameActiveValue;
        _conditionText = conditionText;
    }

    /// <inheritdoc/>
    public override string Name => _name;

    /// <inheritdoc/>
    public override string Impact => $"GPU registry override: {_umdValueName}";

    /// <inheritdoc/>
    public override bool IsConditional => true;

    /// <inheritdoc/>
    public override string Condition => _conditionText;

    /// <inheritdoc/>
    public override bool IsHardwareMatch(HardwareScanResult hw) =>
        hw.GpuVendor == _requiredVendor;

    /// <inheritdoc/>
    public override void Apply(SystemStateSnapshot snapshot)
    {
        try
        {
            var vendorNames = _requiredVendor switch
            {
                GpuVendor.Nvidia => new[] { "NVIDIA" },
                GpuVendor.Amd => new[] { "AMD", "Advanced Micro Devices", "Radeon" },
                GpuVendor.Intel => new[] { "Intel" },
                _ => Array.Empty<string>()
            };

            if (vendorNames.Length == 0)
            {
                Log.Warning(
                    "GpuRegistryOverrideAction: No vendor names for {Vendor}, skipping",
                    _requiredVendor);
                return;
            }

            var driverSubkey = FindDriverSubkey(vendorNames);
            if (driverSubkey == null)
            {
                Log.Warning(
                    "GpuRegistryOverrideAction: Could not find {Vendor} driver subkey",
                    _requiredVendor);
                return;
            }

            // Open UMD subkey
            var umdPath = $@"{driverSubkey}\UMD";
            using var umdKey = Registry.LocalMachine.OpenSubKey(umdPath, writable: true);

            if (umdKey == null)
            {
                // UMD subkey doesn't exist — try writing directly to driver subkey
                using var driverKey = Registry.LocalMachine.OpenSubKey(driverSubkey, writable: true);
                if (driverKey == null)
                {
                    Log.Warning(
                        "GpuRegistryOverrideAction: Cannot open driver subkey {Path}",
                        driverSubkey);
                    return;
                }

                ApplyToKey(driverKey, driverSubkey, snapshot);
                return;
            }

            ApplyToKey(umdKey, umdPath, snapshot);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "GpuRegistryOverrideAction: Failed to apply override for {ValueName}",
                _umdValueName);
        }
    }

    private void ApplyToKey(RegistryKey key, string keyPath, SystemStateSnapshot snapshot)
    {
        var currentValue = key.GetValue(_umdValueName);
        _previouslyExisted = currentValue != null;
        _previousValue = currentValue?.ToString();
        _appliedSubkeyPath = keyPath;

        key.SetValue(_umdValueName, _gameActiveValue, RegistryValueKind.String);
        _applied = true;

        // Record for crash recovery
        snapshot.RecordRegistryValue(keyPath, _umdValueName, currentValue);

        Log.Information(
            "GpuRegistryOverrideAction: Set {Path}\\{ValueName} = {NewValue} (was: {OldValue})",
            keyPath, _umdValueName, _gameActiveValue,
            _previouslyExisted ? _previousValue : "<not set>");
    }

    /// <inheritdoc/>
    public override void Revert(SystemStateSnapshot snapshot)
    {
        if (!_applied || _appliedSubkeyPath == null) return;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(_appliedSubkeyPath, writable: true);
            if (key == null)
            {
                Log.Warning(
                    "GpuRegistryOverrideAction: Cannot open key for revert: {Path}",
                    _appliedSubkeyPath);
                return;
            }

            if (_previouslyExisted && _previousValue != null)
            {
                key.SetValue(_umdValueName, _previousValue, RegistryValueKind.String);
                Log.Information(
                    "GpuRegistryOverrideAction: Restored {Path}\\{ValueName} = {Value}",
                    _appliedSubkeyPath, _umdValueName, _previousValue);
            }
            else
            {
                key.DeleteValue(_umdValueName, throwOnMissingValue: false);
                Log.Information(
                    "GpuRegistryOverrideAction: Deleted {Path}\\{ValueName}",
                    _appliedSubkeyPath, _umdValueName);
            }

            _applied = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "GpuRegistryOverrideAction: Failed to revert override for {ValueName}",
                _umdValueName);
        }
    }

    /// <summary>
    /// Finds the driver subkey under the display adapter class GUID that matches
    /// the given vendor name(s). Checks DriverDesc for a match.
    /// Same pattern as GpuDriverOptimizer.FindDriverSubkey().
    /// </summary>
    private static string? FindDriverSubkey(string[] vendorNames)
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DriverClassBasePath);
            if (classKey == null) return null;

            foreach (string subkeyName in classKey.GetSubKeyNames())
            {
                if (!int.TryParse(subkeyName, out _))
                    continue;

                try
                {
                    using var subkey = classKey.OpenSubKey(subkeyName);
                    if (subkey == null) continue;

                    string? driverDesc = subkey.GetValue("DriverDesc")?.ToString();
                    if (string.IsNullOrEmpty(driverDesc)) continue;

                    foreach (string vendor in vendorNames)
                    {
                        if (driverDesc.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                        {
                            return $@"{DriverClassBasePath}\{subkeyName}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex,
                        "GpuRegistryOverrideAction: Error reading driver subkey {Subkey}",
                        subkeyName);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "GpuRegistryOverrideAction: Failed to enumerate display adapter subkeys");
        }

        return null;
    }
}
