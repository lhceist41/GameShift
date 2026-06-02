using System.Text.Json;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Increases NTFS memory allocation for filesystem metadata caching.
/// Registry: NtfsMemoryUsage = 2 at HKLM\...\Control\FileSystem.
///
/// Value 2 allows NTFS to use more paged pool memory for metadata caching,
/// reducing disk I/O for directory lookups and attribute reads.
/// Default is 1. Risk: Very low. Revert by restoring original value.
/// </summary>
public class OptimizeNtfsMemoryUsage : ISystemTweak
{
    public string Name => "Increase NTFS Memory Usage";
    public string Description => "Allows NTFS to use more memory for filesystem metadata caching, reducing disk I/O.";
    public string Category => "Filesystem";
    // NtfsMemoryUsage is consumed by ntfs.sys during driver init at boot - writing it at runtime
    // has no effect until the next reboot (same as the sibling DisableLastAccessTimestamp tweak).
    public bool RequiresReboot => true;

    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string ValueName = "NtfsMemoryUsage";

    public bool DetectIsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is int val && val == 2;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        if (key == null) return null;

        var original = key.GetValue(ValueName);
        key.SetValue(ValueName, 2, RegistryValueKind.DWord);

        Log.Information("[NtfsMemoryUsage] Set to 2 (was: {Original})", original ?? "<not set>");
        if (original is not null and not int)
            Log.Warning(
                "[NtfsMemoryUsage] Original value has unexpected type {Type} - it will be removed, not restored, on revert",
                original.GetType().Name);
        return JsonSerializer.Serialize(original is int o ? o : (int?)null);
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key == null) return false;

            var original = JsonSerializer.Deserialize<int?>(originalValuesJson);
            if (original.HasValue)
                key.SetValue(ValueName, original.Value, RegistryValueKind.DWord);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            Log.Information("[NtfsMemoryUsage] Reverted to {Value}", original?.ToString() ?? "<deleted>");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[NtfsMemoryUsage] Revert failed");
            return false;
        }
    }
}
