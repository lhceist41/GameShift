using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

public class DisableMemoryIntegrity : ISystemTweak
{
    public string Name => "Disable Memory Integrity (VBS/HVCI)";
    public string Description => "Removes VBS overhead (significant FPS impact on some systems). ⚠️ Reduces system security.";
    public string Category => "Security (Performance)";
    public bool RequiresReboot => true;

    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string ValueName = "Enabled";

    public bool DetectIsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            var val = key?.GetValue(ValueName);
            return val is int i && i == 0;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
        var original = key.GetValue(ValueName);
        key.SetValue(ValueName, 0, RegistryValueKind.DWord);
        return JsonSerializer.Serialize(new { Enabled = original });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            var val = doc.RootElement.GetProperty("Enabled");
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key == null) return false;
            if (val.ValueKind == JsonValueKind.Null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            else
                key.SetValue(ValueName, val.GetInt32(), RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }
}
