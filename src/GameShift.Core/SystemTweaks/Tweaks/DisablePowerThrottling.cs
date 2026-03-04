using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

public class DisablePowerThrottling : ISystemTweak
{
    public string Name => "Disable Power Throttling";
    public string Description => "Prevents Windows from throttling foreground game processes for power savings.";
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
}
