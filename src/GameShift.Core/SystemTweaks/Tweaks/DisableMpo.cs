using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

public class DisableMpo : ISystemTweak
{
    public string Name => "Disable Multiplane Overlay (MPO)";
    public string Description => "Fixes multi-monitor flickering, alt-tab stutter, and display driver timeouts.";
    public string Category => "GPU";
    public bool RequiresReboot => true;

    private const string DwmKeyPath = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string GraphicsDriversKeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    public bool DetectIsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DwmKeyPath);
            var val = key?.GetValue("OverlayTestMode");
            return val is int i && i == 5;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        // Back up all values before modifying
        using var dwmKey = Registry.LocalMachine.CreateSubKey(DwmKeyPath);
        var originalOverlayTestMode = dwmKey.GetValue("OverlayTestMode");
        var originalOverlayMinFPS = dwmKey.GetValue("OverlayMinFPS");

        using var gfxKey = Registry.LocalMachine.CreateSubKey(GraphicsDriversKeyPath);
        var originalDisableOverlays = gfxKey.GetValue("DisableOverlays");

        // Core MPO disable (all Windows versions)
        dwmKey.SetValue("OverlayTestMode", 5, RegistryValueKind.DWord);

        // 24H2 Chromium freezing fix
        dwmKey.SetValue("OverlayMinFPS", 0, RegistryValueKind.DWord);

        // 25H2 forward-compatibility
        gfxKey.SetValue("DisableOverlays", 1, RegistryValueKind.DWord);

        return JsonSerializer.Serialize(new
        {
            OverlayTestMode = originalOverlayTestMode,
            OverlayMinFPS = originalOverlayMinFPS,
            DisableOverlays = originalDisableOverlays
        });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);

            using var dwmKey = Registry.LocalMachine.OpenSubKey(DwmKeyPath, writable: true);
            if (dwmKey != null)
            {
                RestoreValue(dwmKey, "OverlayTestMode", doc, "OverlayTestMode");
                RestoreValue(dwmKey, "OverlayMinFPS", doc, "OverlayMinFPS");
            }

            using var gfxKey = Registry.LocalMachine.OpenSubKey(GraphicsDriversKeyPath, writable: true);
            if (gfxKey != null)
                RestoreValue(gfxKey, "DisableOverlays", doc, "DisableOverlays");

            return true;
        }
        catch { return false; }
    }

    private static void RestoreValue(RegistryKey key, string valueName, JsonDocument doc, string jsonProp)
    {
        if (!doc.RootElement.TryGetProperty(jsonProp, out var val)) return;
        if (val.ValueKind == JsonValueKind.Null)
            key.DeleteValue(valueName, throwOnMissingValue: false);
        else
            key.SetValue(valueName, val.GetInt32(), RegistryValueKind.DWord);
    }
}
