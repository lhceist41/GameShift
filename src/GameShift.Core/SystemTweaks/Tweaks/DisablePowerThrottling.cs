using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

public class DisablePowerThrottling : ISystemTweak
{
    public string Name => "Disable Power Throttling";
    public string Description => "Prevents Windows from throttling foreground game processes for power savings. System-wide - not recommended on laptops, as it increases battery drain. Skipped automatically while on battery.";
    public string Category => "Power";
    public bool RequiresReboot => false;

    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";
    private const string ValueName = "PowerThrottlingOff";

    public bool DetectIsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            var val = key?.GetValue(ValueName);
            return val is int i && i == 1;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        if (IsOnBattery())
        {
            // Battery-aware: don't disable system-wide power throttling while on battery - it would
            // hurt laptop battery life. Throw (not return null) so the manager records "not applied"
            // rather than a bogus applied-state. The user can apply it while plugged in.
            throw new InvalidOperationException(
                "Skipped Disable Power Throttling - running on battery would harm battery life");
        }

        using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
        var original = key.GetValue(ValueName); // null if doesn't exist
        key.SetValue(ValueName, 1, RegistryValueKind.DWord);
        return JsonSerializer.Serialize(new { PowerThrottlingOff = original });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            var val = doc.RootElement.GetProperty("PowerThrottlingOff");
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key == null) return false;
            if (val.ValueKind == JsonValueKind.Null)
                key.DeleteValue(ValueName, throwOnMissingValue: false); // Delete entirely on revert
            else
                key.SetValue(ValueName, val.GetInt32(), RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns true when the system is running on battery (AC offline). Desktops report "unknown"
    /// (255) and are treated as plugged-in so the tweak is never wrongly skipped on a desktop.
    /// </summary>
    private static bool IsOnBattery()
    {
        return GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;      // 0 = on battery, 1 = plugged in, 255 = unknown
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
