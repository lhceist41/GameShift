using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Disables Multiplane Overlay (MPO) via DWM registry setting during gaming sessions.
/// MPO can cause frame pacing issues, especially on multi-monitor setups with mismatched refresh rates.
/// Handles apply, revert, multi-monitor suggestions, and profile toggle for MPO.
///
/// Implements IJournaledOptimization as the Sprint 3A proof-of-concept. Apply() and Revert()
/// contain the core logic; ApplyAsync/RevertAsync delegate to them for IOptimization compatibility.
/// </summary>
public class MpoToggle : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;
    private bool _isApplied;

    // State captured during Apply() for use in Revert()
    private bool _previousValueExisted;
    private int _previousValue;
    private bool _is24H2OrLater;
    private bool _enableOverlayPreviouslyExisted;
    private int _enableOverlayPreviousValue;
    private bool _disableOverlaysPreviouslyExisted;
    private int _disableOverlaysPreviousValue;

    // Context stored by CanApply() for use by Apply()
    private SystemContext? _context;

    private const string DwmRegistryPath = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string OverlayTestModeValue = "OverlayTestMode";
    private const string EnableOverlayValue = "EnableOverlay";
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string DisableOverlaysValue = "DisableOverlays";

    public const string OptimizationId = "MPO Toggle";

    /// <summary>
    /// Reads the OS build number from the registry. More reliable than Environment.OSVersion
    /// which can return wrong values without a compatibility manifest on older .NET hosts.
    /// </summary>
    private static int GetWindowsBuildNumber()
    {
        try
        {
            var val = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "CurrentBuildNumber", "0");
            return int.TryParse(val?.ToString(), out var build) ? build : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ── IOptimization ─────────────────────────────────────────────────────────

    public string Name => OptimizationId;
    public string Description => "Disables Multiplane Overlay to reduce frame pacing issues";
    public bool IsApplied => _isApplied;

    /// <summary>
    /// MPO toggle is always available on Windows 10/11.
    /// The DWM registry key always exists on supported Windows versions.
    /// </summary>
    public bool IsAvailable => true;

    /// <summary>
    /// Delegates to the journaled Apply() path. Stores context first via CanApply().
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        var context = new SystemContext { Profile = profile, Snapshot = snapshot };
        if (!CanApply(context))
        {
            _logger.Information("[MpoToggle] Skipping MPO disable for Casual intensity profile");
            return Task.FromResult(true);
        }

        var result = Apply();
        return Task.FromResult(result.State == OptimizationState.Applied);
    }

    /// <summary>
    /// Delegates to the journaled Revert() path.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        if (!_isApplied)
            return Task.FromResult(true);

        var result = Revert();
        return Task.FromResult(result.State == OptimizationState.Reverted);
    }

    // ── IJournaledOptimization ────────────────────────────────────────────────

    /// <summary>
    /// Pre-flight check. Stores context for use in Apply().
    /// Returns false for Casual intensity profiles — MPO disable is competitive-only.
    /// </summary>
    public bool CanApply(SystemContext context)
    {
        _context = context;
        return context.Profile.Intensity != OptimizationIntensity.Casual;
    }

    /// <summary>
    /// Disables MPO by writing registry values. Records original state for clean revert.
    /// Returns OptimizationResult with serialized original/applied registry values.
    /// </summary>
    public OptimizationResult Apply()
    {
        var snapshot = _context?.Snapshot;

        try
        {
            _logger.Information(
                "[MpoToggle] Applying MPO Toggle — disabling Multiplane Overlay via registry");

            using var key = Registry.LocalMachine.OpenSubKey(DwmRegistryPath, writable: true);
            if (key == null)
            {
                _logger.Error(
                    "[MpoToggle] Failed to open registry key {RegistryPath} for writing",
                    DwmRegistryPath);
                return Fail("Failed to open DWM registry key");
            }

            // Read and store existing value for clean revert
            var existingValue = key.GetValue(OverlayTestModeValue);
            if (existingValue != null)
            {
                _previousValueExisted = true;
                _previousValue = (int)existingValue;
                _logger.Debug(
                    "[MpoToggle] Existing {ValueName} = {OldValue}, will restore on revert",
                    OverlayTestModeValue, _previousValue);
            }
            else
            {
                _previousValueExisted = false;
                _logger.Debug(
                    "[MpoToggle] No existing {ValueName} value, will delete on revert",
                    OverlayTestModeValue);
            }

            // Build original state snapshot for journal
            var originalState = new Dictionary<string, object?>
            {
                [OverlayTestModeValue] = _previousValueExisted ? _previousValue : null
            };

            // Set OverlayTestMode = 5 to disable MPO
            key.SetValue(OverlayTestModeValue, 5, RegistryValueKind.DWord);
            VerifyRegistryValue(DwmRegistryPath, OverlayTestModeValue, 5);

            _logger.Information(
                "[MpoToggle] Set {RegistryPath}\\{ValueName} = 5 (was: {OldValue})",
                @"HKLM\" + DwmRegistryPath, OverlayTestModeValue,
                _previousValueExisted ? _previousValue.ToString() : "<not set>");

            snapshot?.RecordRegistryValue(@"HKLM\" + DwmRegistryPath, OverlayTestModeValue,
                _previousValueExisted ? _previousValue : (object)"<not set>");

            var appliedState = new Dictionary<string, object?>
            {
                [OverlayTestModeValue] = 5
            };

            // ── Windows 11 24H2+ fallback (build 26100+) ──
            var buildNumber = GetWindowsBuildNumber();
            _is24H2OrLater = buildNumber >= 26100;

            if (_is24H2OrLater)
            {
                _logger.Information(
                    "[MpoToggle] Windows 11 24H2+ detected (build {Build}), applying extended MPO disable",
                    buildNumber);

                // EnableOverlay = 0 in same DWM key
                var existingEnableOverlay = key.GetValue(EnableOverlayValue);
                if (existingEnableOverlay != null)
                {
                    _enableOverlayPreviouslyExisted = true;
                    _enableOverlayPreviousValue = (int)existingEnableOverlay;
                }
                else
                {
                    _enableOverlayPreviouslyExisted = false;
                }

                originalState[EnableOverlayValue] = _enableOverlayPreviouslyExisted ? _enableOverlayPreviousValue : null;

                key.SetValue(EnableOverlayValue, 0, RegistryValueKind.DWord);
                VerifyRegistryValue(DwmRegistryPath, EnableOverlayValue, 0);
                snapshot?.RecordRegistryValue(@"HKLM\" + DwmRegistryPath, EnableOverlayValue,
                    _enableOverlayPreviouslyExisted ? _enableOverlayPreviousValue : (object)"<not set>");

                appliedState[EnableOverlayValue] = 0;

                _logger.Information(
                    "[MpoToggle] Set {RegistryPath}\\{ValueName} = 0 (was: {OldValue})",
                    @"HKLM\" + DwmRegistryPath, EnableOverlayValue,
                    _enableOverlayPreviouslyExisted ? _enableOverlayPreviousValue.ToString() : "<not set>");

                // DisableOverlays = 1 in GraphicsDrivers key
                using var gfxKey = Registry.LocalMachine.OpenSubKey(GraphicsDriversPath, writable: true);
                if (gfxKey != null)
                {
                    var existingDisableOverlays = gfxKey.GetValue(DisableOverlaysValue);
                    if (existingDisableOverlays != null)
                    {
                        _disableOverlaysPreviouslyExisted = true;
                        _disableOverlaysPreviousValue = (int)existingDisableOverlays;
                    }
                    else
                    {
                        _disableOverlaysPreviouslyExisted = false;
                    }

                    originalState[DisableOverlaysValue] = _disableOverlaysPreviouslyExisted ? _disableOverlaysPreviousValue : null;

                    gfxKey.SetValue(DisableOverlaysValue, 1, RegistryValueKind.DWord);
                    VerifyRegistryValue(GraphicsDriversPath, DisableOverlaysValue, 1);
                    snapshot?.RecordRegistryValue(@"HKLM\" + GraphicsDriversPath, DisableOverlaysValue,
                        _disableOverlaysPreviouslyExisted ? _disableOverlaysPreviousValue : (object)"<not set>");

                    appliedState[DisableOverlaysValue] = 1;

                    _logger.Information(
                        "[MpoToggle] Set {RegistryPath}\\{ValueName} = 1 (was: {OldValue})",
                        @"HKLM\" + GraphicsDriversPath, DisableOverlaysValue,
                        _disableOverlaysPreviouslyExisted ? _disableOverlaysPreviousValue.ToString() : "<not set>");
                }
                else
                {
                    _logger.Warning(
                        "[MpoToggle] Failed to open {RegistryPath} for 24H2 DisableOverlays write",
                        GraphicsDriversPath);
                }
            }

            // Advisory: check for multi-monitor refresh rate mismatch
            CheckMultiMonitorSuggestion();

            _isApplied = true;
            return new OptimizationResult(
                Name: OptimizationId,
                OriginalValue: JsonSerializer.Serialize(originalState),
                AppliedValue: JsonSerializer.Serialize(appliedState),
                State: OptimizationState.Applied);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MpoToggle] Failed to apply MPO Toggle");
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Reverts MPO by restoring the previous registry values or deleting them.
    /// </summary>
    public OptimizationResult Revert()
    {
        try
        {
            _logger.Information(
                "[MpoToggle] Reverting MPO Toggle — restoring Multiplane Overlay");

            using var key = Registry.LocalMachine.OpenSubKey(DwmRegistryPath, writable: true);
            if (key == null)
            {
                _logger.Error(
                    "[MpoToggle] Failed to open registry key {RegistryPath} for writing during revert",
                    DwmRegistryPath);
                return RevertFail("Failed to open DWM registry key");
            }

            if (_previousValueExisted)
            {
                key.SetValue(OverlayTestModeValue, _previousValue, RegistryValueKind.DWord);
                _logger.Information(
                    "[MpoToggle] Restored {RegistryPath}\\{ValueName} = {RestoredValue}",
                    @"HKLM\" + DwmRegistryPath, OverlayTestModeValue, _previousValue);
            }
            else
            {
                key.DeleteValue(OverlayTestModeValue, throwOnMissingValue: false);
                _logger.Information(
                    "[MpoToggle] Deleted {RegistryPath}\\{ValueName} (was not present before apply)",
                    @"HKLM\" + DwmRegistryPath, OverlayTestModeValue);
            }

            // ── Revert 24H2 extended keys ──
            if (_is24H2OrLater)
            {
                if (_enableOverlayPreviouslyExisted)
                {
                    key.SetValue(EnableOverlayValue, _enableOverlayPreviousValue, RegistryValueKind.DWord);
                    _logger.Information(
                        "[MpoToggle] Restored {RegistryPath}\\{ValueName} = {RestoredValue}",
                        @"HKLM\" + DwmRegistryPath, EnableOverlayValue, _enableOverlayPreviousValue);
                }
                else
                {
                    key.DeleteValue(EnableOverlayValue, throwOnMissingValue: false);
                    _logger.Information(
                        "[MpoToggle] Deleted {RegistryPath}\\{ValueName} (was not present before apply)",
                        @"HKLM\" + DwmRegistryPath, EnableOverlayValue);
                }

                using var gfxKey = Registry.LocalMachine.OpenSubKey(GraphicsDriversPath, writable: true);
                if (gfxKey != null)
                {
                    if (_disableOverlaysPreviouslyExisted)
                    {
                        gfxKey.SetValue(DisableOverlaysValue, _disableOverlaysPreviousValue, RegistryValueKind.DWord);
                        _logger.Information(
                            "[MpoToggle] Restored {RegistryPath}\\{ValueName} = {RestoredValue}",
                            @"HKLM\" + GraphicsDriversPath, DisableOverlaysValue, _disableOverlaysPreviousValue);
                    }
                    else
                    {
                        gfxKey.DeleteValue(DisableOverlaysValue, throwOnMissingValue: false);
                        _logger.Information(
                            "[MpoToggle] Deleted {RegistryPath}\\{ValueName} (was not present before apply)",
                            @"HKLM\" + GraphicsDriversPath, DisableOverlaysValue);
                    }
                }
                else
                {
                    _logger.Warning(
                        "[MpoToggle] Failed to open {RegistryPath} for 24H2 DisableOverlays revert",
                        GraphicsDriversPath);
                }
            }

            _isApplied = false;
            return new OptimizationResult(
                Name: OptimizationId,
                OriginalValue: string.Empty,
                AppliedValue: string.Empty,
                State: OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MpoToggle] Failed to revert MPO Toggle");
            return RevertFail(ex.Message);
        }
    }

    /// <summary>
    /// Confirms the applied MPO registry changes are still in effect.
    /// </summary>
    public bool Verify()
    {
        if (!_isApplied)
            return false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DwmRegistryPath);
            if (key?.GetValue(OverlayTestModeValue) is not int otm || otm != 5)
                return false;

            if (_is24H2OrLater)
            {
                if (key.GetValue(EnableOverlayValue) is not int eo || eo != 0)
                    return false;

                using var gfxKey = Registry.LocalMachine.OpenSubKey(GraphicsDriversPath);
                if (gfxKey?.GetValue(DisableOverlaysValue) is not int dov || dov != 1)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OptimizationResult Fail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    private static OptimizationResult RevertFail(string error) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, error);

    /// <summary>
    /// Re-reads a DWORD registry value and confirms it matches the expected value.
    /// Logs a warning if mismatch detected (another process may be reverting).
    /// </summary>
    private bool VerifyRegistryValue(string keyPath, string valueName, int expected)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            var actual = key?.GetValue(valueName);
            if (actual is int intVal && intVal == expected)
            {
                _logger.Debug("[MpoToggle] Verified {Path}\\{Name} = {Value}",
                    keyPath, valueName, expected);
                return true;
            }

            _logger.Warning(
                "[MpoToggle] Verification FAILED: {Path}\\{Name} expected {Expected}, got {Actual}. " +
                "Another process may have reverted the value.",
                keyPath, valueName, expected, actual ?? "<not set>");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[MpoToggle] Verification read failed for {Path}\\{Name}",
                keyPath, valueName);
            return false;
        }
    }

    // ── Multi-monitor advisory ────────────────────────────────────────────────

    /// <summary>
    /// Detects multi-monitor setups with mismatched refresh rates and logs an advisory.
    /// Uses EnumDisplayDevices + EnumDisplaySettings P/Invoke to enumerate monitors.
    /// Advisory only — logged once during Apply, not a blocking operation.
    /// </summary>
    private void CheckMultiMonitorSuggestion()
    {
        try
        {
            var refreshRates = new List<(string deviceName, int refreshRate)>();

            var displayDevice = new DISPLAY_DEVICE();
            displayDevice.cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>();

            uint deviceIndex = 0;
            while (EnumDisplayDevicesW(null, deviceIndex, ref displayDevice, 0))
            {
                if ((displayDevice.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                {
                    var devMode = new DEVMODE();
                    devMode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();

                    if (EnumDisplaySettingsW(displayDevice.DeviceName, ENUM_CURRENT_SETTINGS, ref devMode))
                    {
                        if (devMode.dmDisplayFrequency > 0)
                            refreshRates.Add((displayDevice.DeviceName, (int)devMode.dmDisplayFrequency));
                    }
                }

                deviceIndex++;
                displayDevice.cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>();
            }

            if (refreshRates.Count > 1)
            {
                var distinctRates = refreshRates.Select(r => r.refreshRate).Distinct().ToList();

                if (distinctRates.Count > 1)
                {
                    var rateDescriptions = refreshRates
                        .Select(r => $"{r.deviceName}: {r.refreshRate}Hz")
                        .ToArray();

                    _logger.Information(
                        "[MpoToggle] Multi-monitor setup detected with mismatched refresh rates ({Rates}). " +
                        "MPO Toggle is recommended for reducing frame pacing issues",
                        string.Join(", ", rateDescriptions));
                }
                else
                {
                    _logger.Debug(
                        "[MpoToggle] Multi-monitor setup detected ({Count} monitors) with matching refresh rates ({Rate}Hz)",
                        refreshRates.Count, distinctRates[0]);
                }
            }
            else
            {
                _logger.Debug(
                    "[MpoToggle] Single monitor detected ({Count} active display{Plural})",
                    refreshRates.Count, refreshRates.Count == 1 ? "" : "s");
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(
                ex,
                "[MpoToggle] Could not enumerate monitors for refresh rate check (advisory only)");
        }
    }

    // ── P/Invoke for monitor enumeration ─────────────────────────────────────

    private const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(
        string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
