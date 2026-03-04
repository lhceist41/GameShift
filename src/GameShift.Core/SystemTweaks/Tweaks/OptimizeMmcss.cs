using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

public class OptimizeMmcss : ISystemTweak
{
    public string Name => "Optimize MMCSS (Multimedia Class Scheduler)";
    public string Description => "Better GPU/CPU scheduling for games. Sets moderate network throttle (NOT fully disabled — full disable can increase ping).";
    public string Category => "CPU Scheduling";
    public bool RequiresReboot => false;

    private const string ProfilePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string GamesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

    public bool DetectIsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProfilePath);
            var sr = key?.GetValue("SystemResponsiveness");
            return sr is int i && i == 0;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        var originals = new Dictionary<string, object?>();

        using (var profileKey = Registry.LocalMachine.CreateSubKey(ProfilePath))
        {
            originals["NetworkThrottlingIndex"] = profileKey.GetValue("NetworkThrottlingIndex");
            originals["SystemResponsiveness"] = profileKey.GetValue("SystemResponsiveness");

            profileKey.SetValue("NetworkThrottlingIndex", 20, RegistryValueKind.DWord);
            profileKey.SetValue("SystemResponsiveness", 0, RegistryValueKind.DWord);
        }

        using (var gamesKey = Registry.LocalMachine.CreateSubKey(GamesPath))
        {
            originals["GPU Priority"] = gamesKey.GetValue("GPU Priority");
            originals["Priority"] = gamesKey.GetValue("Priority");
            originals["Scheduling Category"] = gamesKey.GetValue("Scheduling Category");
            originals["SFIO Priority"] = gamesKey.GetValue("SFIO Priority");

            gamesKey.SetValue("GPU Priority", 8, RegistryValueKind.DWord);
            gamesKey.SetValue("Priority", 6, RegistryValueKind.DWord);
            gamesKey.SetValue("Scheduling Category", "High", RegistryValueKind.String);
            gamesKey.SetValue("SFIO Priority", "High", RegistryValueKind.String);
        }

        return JsonSerializer.Serialize(originals);
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var originals = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalValuesJson);
            if (originals == null) return false;

            using (var profileKey = Registry.LocalMachine.OpenSubKey(ProfilePath, writable: true))
            {
                if (profileKey != null)
                {
                    RestoreValue(profileKey, "NetworkThrottlingIndex", originals);
                    RestoreValue(profileKey, "SystemResponsiveness", originals);
                }
            }

            using (var gamesKey = Registry.LocalMachine.OpenSubKey(GamesPath, writable: true))
            {
                if (gamesKey != null)
                {
                    RestoreValue(gamesKey, "GPU Priority", originals);
                    RestoreValue(gamesKey, "Priority", originals);
                    RestoreStringValue(gamesKey, "Scheduling Category", originals);
                    RestoreStringValue(gamesKey, "SFIO Priority", originals);
                }
            }

            return true;
        }
        catch { return false; }
    }

    private static void RestoreValue(RegistryKey key, string name, Dictionary<string, JsonElement> originals)
    {
        if (originals.TryGetValue(name, out var val))
        {
            if (val.ValueKind == JsonValueKind.Null)
                key.DeleteValue(name, throwOnMissingValue: false);
            else
                key.SetValue(name, val.GetInt32(), RegistryValueKind.DWord);
        }
    }

    private static void RestoreStringValue(RegistryKey key, string name, Dictionary<string, JsonElement> originals)
    {
        if (originals.TryGetValue(name, out var val))
        {
            if (val.ValueKind == JsonValueKind.Null)
                key.DeleteValue(name, throwOnMissingValue: false);
            else
                key.SetValue(name, val.GetString() ?? "", RegistryValueKind.String);
        }
    }
}
