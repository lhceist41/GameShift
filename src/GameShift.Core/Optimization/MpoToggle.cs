using System.Runtime.InteropServices;
using Microsoft.Win32;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Disables Multiplane Overlay (MPO) via DWM registry setting during gaming sessions.
/// MPO can cause frame pacing issues, especially on multi-monitor setups with mismatched refresh rates.
/// Handles apply, revert, multi-monitor suggestions, and profile toggle for MPO.
/// </summary>
public class MpoToggle : IOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;
    private bool _isApplied;
    private bool _previousValueExisted;
    private int _previousValue;

    private const string DwmRegistryPath = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string OverlayTestModeValue = "OverlayTestMode";

    public string Name => "MPO Toggle";

    public string Description => "Disables Multiplane Overlay to reduce frame pacing issues";

    public bool IsApplied => _isApplied;

    /// <summary>
    /// MPO toggle is always available on Windows 10/11.
    /// The DWM registry key always exists on supported Windows versions.
    /// </summary>
    public bool IsAvailable => true;

    /// <summary>
    /// Disables MPO by setting OverlayTestMode=5 in the DWM registry key.
    /// Records existing value for clean revert. Logs multi-monitor refresh rate advisory.
    /// </summary>
    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        await Task.CompletedTask;

        try
        {
            _logger.Information(
                "MpoToggle: Applying MPO Toggle — disabling Multiplane Overlay via registry");

            using var key = Registry.LocalMachine.OpenSubKey(DwmRegistryPath, writable: true);
            if (key == null)
            {
                _logger.Error(
                    "MpoToggle: Failed to open registry key {RegistryPath} for writing",
                    DwmRegistryPath);
                return false;
            }

            // Read and store existing value for clean revert
            var existingValue = key.GetValue(OverlayTestModeValue);
            if (existingValue != null)
            {
                _previousValueExisted = true;
                _previousValue = (int)existingValue;
                _logger.Debug(
                    "MpoToggle: Existing {ValueName} = {OldValue}, will restore on revert",
                    OverlayTestModeValue,
                    _previousValue);
            }
            else
            {
                _previousValueExisted = false;
                _logger.Debug(
                    "MpoToggle: No existing {ValueName} value, will delete on revert",
                    OverlayTestModeValue);
            }

            // Set OverlayTestMode = 5 to disable MPO
            key.SetValue(OverlayTestModeValue, 5, RegistryValueKind.DWord);

            _logger.Information(
                "MpoToggle: Set {RegistryPath}\\{ValueName} = 5 (was: {OldValue})",
                @"HKLM\" + DwmRegistryPath,
                OverlayTestModeValue,
                _previousValueExisted ? _previousValue.ToString() : "<not set>");

            // Advisory: check for multi-monitor refresh rate mismatch
            CheckMultiMonitorSuggestion();

            _isApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "MpoToggle: Failed to apply MPO Toggle");
            return false;
        }
    }

    /// <summary>
    /// Reverts MPO by restoring the previous registry value or deleting it.
    /// </summary>
    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        await Task.CompletedTask;

        if (!_isApplied)
        {
            return true; // No-op if not applied
        }

        try
        {
            _logger.Information(
                "MpoToggle: Reverting MPO Toggle — restoring Multiplane Overlay");

            using var key = Registry.LocalMachine.OpenSubKey(DwmRegistryPath, writable: true);
            if (key == null)
            {
                _logger.Error(
                    "MpoToggle: Failed to open registry key {RegistryPath} for writing during revert",
                    DwmRegistryPath);
                return false;
            }

            if (_previousValueExisted)
            {
                // Restore previous value
                key.SetValue(OverlayTestModeValue, _previousValue, RegistryValueKind.DWord);
                _logger.Information(
                    "MpoToggle: Restored {RegistryPath}\\{ValueName} = {RestoredValue}",
                    @"HKLM\" + DwmRegistryPath,
                    OverlayTestModeValue,
                    _previousValue);
            }
            else
            {
                // Delete the value (it didn't exist before)
                key.DeleteValue(OverlayTestModeValue, throwOnMissingValue: false);
                _logger.Information(
                    "MpoToggle: Deleted {RegistryPath}\\{ValueName} (was not present before apply)",
                    @"HKLM\" + DwmRegistryPath,
                    OverlayTestModeValue);
            }

            _isApplied = false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "MpoToggle: Failed to revert MPO Toggle");
            return false;
        }
    }

    // ── Multi-monitor advisory ──────────────────────────────

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
                // Only check active (attached) monitors
                if ((displayDevice.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                {
                    var devMode = new DEVMODE();
                    devMode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();

                    if (EnumDisplaySettingsW(displayDevice.DeviceName, ENUM_CURRENT_SETTINGS, ref devMode))
                    {
                        if (devMode.dmDisplayFrequency > 0)
                        {
                            refreshRates.Add((displayDevice.DeviceName, (int)devMode.dmDisplayFrequency));
                        }
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
                        "MpoToggle: Multi-monitor setup detected with mismatched refresh rates ({Rates}). " +
                        "MPO Toggle is recommended for reducing frame pacing issues",
                        string.Join(", ", rateDescriptions));
                }
                else
                {
                    _logger.Debug(
                        "MpoToggle: Multi-monitor setup detected ({Count} monitors) with matching refresh rates ({Rate}Hz)",
                        refreshRates.Count,
                        distinctRates[0]);
                }
            }
            else
            {
                _logger.Debug(
                    "MpoToggle: Single monitor detected ({Count} active display{Plural})",
                    refreshRates.Count,
                    refreshRates.Count == 1 ? "" : "s");
            }
        }
        catch (Exception ex)
        {
            // Advisory only — failure to detect monitors should not block the optimization
            _logger.Debug(
                ex,
                "MpoToggle: Could not enumerate monitors for refresh rate check (advisory only)");
        }
    }

    // ── Private P/Invoke declarations for monitor enumeration ────────
    // These are specific to MpoToggle's multi-monitor detection and not
    // general-purpose, so they are declared here rather than in NativeInterop.

    private const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(
        string lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        // Position union (POINTL or display orientation fields)
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;

        // ICM fields (not used but needed for correct struct size)
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
