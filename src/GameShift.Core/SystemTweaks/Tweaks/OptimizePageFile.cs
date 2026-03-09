using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Sets a fixed-size page file to eliminate dynamic resize stutter during gaming.
/// Fixed size = min equals max, preventing Windows from resizing on the fly.
/// Size is based on installed RAM. Requires reboot to take effect.
/// NOT included in "Apply All Recommended" — user must opt in.
/// </summary>
public class OptimizePageFile : ISystemTweak
{
    public string Name => "Optimize Page File";
    public string Description => "Sets a fixed-size page file to prevent dynamic resize stutter during gaming. Requires a reboot to take effect.";
    public string Category => "Memory";
    public bool RequiresReboot => true;

    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string ValueName = "PagingFiles";

    public bool DetectIsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            var val = key?.GetValue(ValueName) as string[];
            if (val == null || val.Length == 0) return false;

            // Check if any entry has fixed size (min == max and both > 0)
            foreach (var entry in val)
            {
                var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Format: "C:\pagefile.sys 4096 4096"
                if (parts.Length == 3 &&
                    int.TryParse(parts[1], out int min) &&
                    int.TryParse(parts[2], out int max) &&
                    min == max && min > 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch { return false; }
    }

    public string? Apply()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        if (key == null) return null;

        // Record original value
        var original = key.GetValue(ValueName) as string[];

        // Calculate optimal page file size based on installed RAM
        int pageFileSizeMB = GetOptimalPageFileSizeMB();
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        string driveLetterOnly = systemDrive.TrimEnd('\\');
        string fixedEntry = $"{driveLetterOnly}\\pagefile.sys {pageFileSizeMB} {pageFileSizeMB}";

        key.SetValue(ValueName, new[] { fixedEntry }, RegistryValueKind.MultiString);

        return JsonSerializer.Serialize(new { PagingFiles = original });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            var pagingFilesElement = doc.RootElement.GetProperty("PagingFiles");

            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key == null) return false;

            if (pagingFilesElement.ValueKind == JsonValueKind.Null)
            {
                // Original had no value — restore system-managed default
                key.SetValue(ValueName, new[] { @"?:\pagefile.sys" }, RegistryValueKind.MultiString);
            }
            else
            {
                var originalValues = new List<string>();
                foreach (var item in pagingFilesElement.EnumerateArray())
                {
                    var str = item.GetString();
                    if (str != null) originalValues.Add(str);
                }

                key.SetValue(ValueName, originalValues.ToArray(), RegistryValueKind.MultiString);
            }

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Determines optimal fixed page file size based on installed RAM.
    /// </summary>
    private static int GetOptimalPageFileSizeMB()
    {
        try
        {
            long totalRamBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            long totalRamMB = totalRamBytes / (1024 * 1024);

            return totalRamMB switch
            {
                <= 8192 => 8192,   // 8GB RAM → 8GB pagefile
                <= 16384 => 8192,  // 16GB RAM → 8GB pagefile
                <= 32768 => 4096,  // 32GB RAM → 4GB pagefile
                <= 65536 => 4096,  // 64GB RAM → 4GB pagefile
                _ => 2048          // 128GB+ → 2GB pagefile
            };
        }
        catch
        {
            return 4096; // Safe default
        }
    }
}
