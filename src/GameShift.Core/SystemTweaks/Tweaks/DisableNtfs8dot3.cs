using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Disables NTFS 8.3 short name (DOS-compatible) creation for all volumes.
/// Short names like PROGRA~1 add overhead to every file creation operation.
/// No modern game or application needs 8.3 names.
/// Registry values: 0 = enabled on all volumes; 1 = disabled on all volumes;
///                  2 = per-volume; 3 = disabled except system volume.
/// Included in "Apply All Recommended" — universally safe.
/// </summary>
public class DisableNtfs8dot3 : ISystemTweak
{
    public string Name => "Disable NTFS 8.3 Name Creation";
    public string Description => "Eliminates legacy DOS filename overhead on file creation. Only affects new files — existing short names remain.";
    public string Category => "File System";
    public bool RequiresReboot => false;

    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string ValueName = "NtfsDisable8dot3NameCreation";

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
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        if (key == null) return null;

        var original = key.GetValue(ValueName);
        key.SetValue(ValueName, 1, RegistryValueKind.DWord);

        return JsonSerializer.Serialize(new { NtfsDisable8dot3NameCreation = original });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            var val = doc.RootElement.GetProperty("NtfsDisable8dot3NameCreation");

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
