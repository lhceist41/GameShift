using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

public class DisableMemoryIntegrity : ISystemTweak
{
    public string Name => "Disable Memory Integrity (VBS/HVCI)";
    public string Description => "Disables VBS and HVCI to remove hypervisor overhead (significant FPS impact on some systems). ⚠️ Reduces system security. UEFI-locked VBS may require additional BIOS changes.";
    public string Category => "Security (Performance)";
    public bool RequiresReboot => true;

    private const string DeviceGuardPath = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    private const string HvciPath = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    public bool DetectIsApplied()
    {
        try
        {
            using var hvciKey = Registry.LocalMachine.OpenSubKey(HvciPath);
            var hvciVal = hvciKey?.GetValue("Enabled");
            bool hvciDisabled = hvciVal is int i && i == 0;

            using var dgKey = Registry.LocalMachine.OpenSubKey(DeviceGuardPath);
            var vbsVal = dgKey?.GetValue("EnableVirtualizationBasedSecurity");
            bool vbsDisabled = vbsVal is int v && v == 0;

            return hvciDisabled && vbsDisabled;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        // Back up all three values before modifying
        using var dgKey = Registry.LocalMachine.CreateSubKey(DeviceGuardPath);
        var originalVbs = dgKey.GetValue("EnableVirtualizationBasedSecurity");
        var originalReqFeatures = dgKey.GetValue("RequirePlatformSecurityFeatures");

        using var hvciKey = Registry.LocalMachine.CreateSubKey(HvciPath);
        var originalHvci = hvciKey.GetValue("Enabled");

        // 1. Disable HVCI
        hvciKey.SetValue("Enabled", 0, RegistryValueKind.DWord);

        // 2. Disable VBS
        dgKey.SetValue("EnableVirtualizationBasedSecurity", 0, RegistryValueKind.DWord);

        // 3. Clear RequirePlatformSecurityFeatures (prevents UEFI from re-enabling)
        dgKey.SetValue("RequirePlatformSecurityFeatures", 0, RegistryValueKind.DWord);

        return JsonSerializer.Serialize(new
        {
            Enabled = originalHvci,
            Vbs = originalVbs,
            ReqFeatures = originalReqFeatures
        });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);

            // Restore HVCI
            using var hvciKey = Registry.LocalMachine.OpenSubKey(HvciPath, writable: true);
            if (hvciKey != null)
                RestoreValue(hvciKey, "Enabled", doc, "Enabled");

            // Restore VBS and RequirePlatformSecurityFeatures
            using var dgKey = Registry.LocalMachine.OpenSubKey(DeviceGuardPath, writable: true);
            if (dgKey != null)
            {
                RestoreValue(dgKey, "EnableVirtualizationBasedSecurity", doc, "Vbs");
                RestoreValue(dgKey, "RequirePlatformSecurityFeatures", doc, "ReqFeatures");
            }

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
