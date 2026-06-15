using System.Management;
using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Sets a fixed-size page file to eliminate dynamic resize stutter during gaming.
/// Fixed size = min equals max, preventing Windows from resizing on the fly.
/// Size is based on installed RAM. Requires reboot to take effect.
/// NOT included in "Apply All Recommended" - user must opt in.
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

        // Defensive: never set a fixed page file the system drive cannot hold. The UI also checks
        // this before calling, so this only fires for non-UI callers.
        var space = CheckSystemDriveSpace();
        if (!space.Ok)
            throw new InvalidOperationException(
                $"Insufficient free space for a fixed page file: need ~{space.NeededMB} MB, have {space.FreeMB} MB.");

        // Record original value (preserved verbatim for revert)
        var original = key.GetValue(ValueName) as string[];

        int pageFileSizeMB = GetOptimalPageFileSizeMB();
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        string driveLetterOnly = systemDrive.TrimEnd('\\'); // e.g. "C:"
        string fixedEntry = $"{driveLetterOnly}\\pagefile.sys {pageFileSizeMB} {pageFileSizeMB}";

        // Preserve any page file the user deliberately placed on OTHER drives; only replace the
        // system-drive entry. Flattening a multi-drive layout onto C: can fill the boot drive and
        // discard the user's larger data-drive page file.
        var entries = new List<string>();
        if (original != null)
        {
            foreach (var entry in original)
            {
                var firstToken = entry.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (!firstToken.StartsWith(driveLetterOnly, StringComparison.OrdinalIgnoreCase))
                    entries.Add(entry); // keep non-system-drive page file as-is
            }
        }
        entries.Add(fixedEntry);

        key.SetValue(ValueName, entries.ToArray(), RegistryValueKind.MultiString);

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
                // Original had no PagingFiles value - delete what we created rather than guess a
                // system-managed default. Revert must restore the EXACT prior state.
                key.DeleteValue(ValueName, throwOnMissingValue: false);
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

    /// <summary>The fixed page file size (MB) GameShift would set on this machine.</summary>
    public static int ComputeFixedSizeMB() => GetOptimalPageFileSizeMB();

    /// <summary>
    /// Whether the system drive has room for the fixed page file plus headroom. Returns Ok=true
    /// when free space cannot be determined (do not block on uncertainty).
    /// </summary>
    public static (bool Ok, long NeededMB, long FreeMB) CheckSystemDriveSpace()
    {
        long neededMB = GetOptimalPageFileSizeMB() + 4096; // page file + 4 GB headroom
        try
        {
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var di = new DriveInfo(systemDrive);
            long freeMB = di.AvailableFreeSpace / (1024 * 1024);
            return (freeMB >= neededMB, neededMB, freeMB);
        }
        catch
        {
            return (true, neededMB, -1);
        }
    }

    /// <summary>
    /// Determines the fixed page file size from installed physical RAM. The size is never reduced
    /// below 8 GB: a tiny fixed page file lowers the commit limit (out-of-memory risk) and can stop
    /// Windows from writing a kernel crash dump. Reads RAM via WMI (TotalPhysicalMemory) rather than
    /// GC.GetGCMemoryInfo, which can be skewed by job-object/container limits.
    /// </summary>
    private static int GetOptimalPageFileSizeMB() => SizeForRamMB(GetPhysicalRamMB());

    /// <summary>
    /// Pure sizing policy (separated for unit testing). Never returns less than 8 GB so the page
    /// file cannot be shrunk below a size that would lower the commit limit or break kernel dumps.
    /// </summary>
    internal static int SizeForRamMB(long ramMB) => ramMB switch
    {
        <= 16384 => 8192,   // up to 16GB RAM -> 8GB page file
        <= 65536 => 16384,  // 32-64GB RAM   -> 16GB page file
        _ => 24576          // 128GB+ RAM     -> 24GB page file
    };

    private static long GetPhysicalRamMB()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get())
            {
                if (obj["TotalPhysicalMemory"] is ulong bytes && bytes > 0)
                    return (long)(bytes / (1024UL * 1024UL));
            }
        }
        catch
        {
            // fall through to the GC-based estimate
        }
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
    }
}
