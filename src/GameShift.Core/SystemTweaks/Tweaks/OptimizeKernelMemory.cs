using System.Text.Json;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Optimizes kernel memory management for gaming:
///   - DisablePagingExecutive = 1: keeps kernel pages in physical RAM (never paged to disk)
///   - LargeSystemCache = 0: optimize for applications, not file server caching
///
/// Registry: HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management
/// Risk: Very low. Revert restores original values.
/// </summary>
public class OptimizeKernelMemory : ISystemTweak
{
    public string Name => "Optimize Kernel Memory";
    public string Description => "Keeps kernel pages in physical RAM and optimizes cache for applications (not file server).";
    public string Category => "Memory";
    public bool RequiresReboot => true;

    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    public bool DetectIsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            if (key == null) return false;

            return key.GetValue("DisablePagingExecutive") is int dpe && dpe == 1 &&
                   key.GetValue("LargeSystemCache") is int lsc && lsc == 0;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        if (key == null) return null;

        var origDpe = key.GetValue("DisablePagingExecutive");
        var origLsc = key.GetValue("LargeSystemCache");

        key.SetValue("DisablePagingExecutive", 1, RegistryValueKind.DWord);
        key.SetValue("LargeSystemCache", 0, RegistryValueKind.DWord);

        Log.Information(
            "[KernelMemory] DisablePagingExecutive=1 (was: {Dpe}), LargeSystemCache=0 (was: {Lsc})",
            origDpe ?? "<not set>", origLsc ?? "<not set>");

        return JsonSerializer.Serialize(new KernelMemoryBackup
        {
            OriginalDisablePagingExecutive = origDpe as int?,
            OriginalLargeSystemCache = origLsc as int?
        });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;

        try
        {
            var backup = JsonSerializer.Deserialize<KernelMemoryBackup>(originalValuesJson);
            if (backup == null) return false;

            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key == null) return false;

            if (backup.OriginalDisablePagingExecutive.HasValue)
                key.SetValue("DisablePagingExecutive", backup.OriginalDisablePagingExecutive.Value, RegistryValueKind.DWord);
            else
                key.DeleteValue("DisablePagingExecutive", throwOnMissingValue: false);

            if (backup.OriginalLargeSystemCache.HasValue)
                key.SetValue("LargeSystemCache", backup.OriginalLargeSystemCache.Value, RegistryValueKind.DWord);
            else
                key.DeleteValue("LargeSystemCache", throwOnMissingValue: false);

            Log.Information("[KernelMemory] Reverted kernel memory settings");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KernelMemory] Revert failed");
            return false;
        }
    }

    private class KernelMemoryBackup
    {
        public int? OriginalDisablePagingExecutive { get; set; }
        public int? OriginalLargeSystemCache { get; set; }
    }
}
