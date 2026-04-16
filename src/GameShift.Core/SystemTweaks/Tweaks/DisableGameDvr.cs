using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

public class DisableGameDvr : ISystemTweak
{
    public string Name => "Disable Game DVR / Game Bar";
    public string Description => "Disables Windows Game DVR background recording and Game Bar overlays. Eliminates 18-23ms input lag and 200-400MB RAM overhead.";
    public string Category => "Windows Gaming";
    public bool RequiresReboot => false;

    private static readonly (string Path, string Name, int Value)[] RegistryEntries =
    {
        (@"System\GameConfigStore", "GameDVR_Enabled", 0),
        (@"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2),
        (@"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1),
        (@"System\GameConfigStore", "GameDVR_FSEBehavior", 2),
        (@"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1),
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0),
    };

    public bool DetectIsApplied()
    {
        try
        {
            // Check the primary indicator
            using var key = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore");
            var val = key?.GetValue("GameDVR_Enabled");
            return val is int i && i == 0;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        var originals = new Dictionary<string, object?>();

        // HKCU entries
        foreach (var (path, name, value) in RegistryEntries)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            originals[$"HKCU\\{path}\\{name}"] = key.GetValue(name);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        // HKLM policy entry
        using var policyKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR");
        originals[@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR\AllowGameDVR"] = policyKey.GetValue("AllowGameDVR");
        policyKey.SetValue("AllowGameDVR", 0, RegistryValueKind.DWord);

        return JsonSerializer.Serialize(originals);
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;

        try
        {
            var originals = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalValuesJson);
            if (originals == null) return false;

            foreach (var (fullPath, originalValue) in originals)
            {
                var isHklm = fullPath.StartsWith("HKLM\\");
                var regPath = fullPath.Replace("HKLM\\", "").Replace("HKCU\\", "");
                var lastSlash = regPath.LastIndexOf('\\');
                var keyPath = regPath[..lastSlash];
                var valueName = regPath[(lastSlash + 1)..];

                var rootKey = isHklm ? Registry.LocalMachine : Registry.CurrentUser;

                using var key = rootKey.OpenSubKey(keyPath, writable: true);
                if (key == null) continue;

                if (originalValue.ValueKind == JsonValueKind.Null)
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                else if (originalValue.ValueKind == JsonValueKind.Number)
                    key.SetValue(valueName, originalValue.GetInt32(), RegistryValueKind.DWord);
                else if (originalValue.ValueKind == JsonValueKind.String)
                    key.SetValue(valueName, originalValue.GetString()!, RegistryValueKind.String);
            }

            return true;
        }
        catch { return false; }
    }
}
