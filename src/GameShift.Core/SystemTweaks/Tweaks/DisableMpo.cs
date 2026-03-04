using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

public class DisableMpo : ISystemTweak
{
    public string Name => "Disable Multiplane Overlay (MPO)";
    public string Description => "Fixes multi-monitor flickering, alt-tab stutter, and display driver timeouts.";
    public string Category => "GPU";
    public bool RequiresReboot => true;

    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string ValueName = "OverlayTestMode";

    public bool DetectIsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            var val = key?.GetValue(ValueName);
            return val is int i && i == 5;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
        var original = key.GetValue(ValueName);
        key.SetValue(ValueName, 5, RegistryValueKind.DWord);
        // Store whether the value existed before (null = didn't exist, revert = delete)
        return JsonSerializer.Serialize(new { OverlayTestMode = original });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            var val = doc.RootElement.GetProperty("OverlayTestMode");
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
