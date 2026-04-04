using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.System;

/// <summary>
/// Static utility class for gathering system hardware and software information via WMI.
/// All methods use try-catch with graceful fallback (returning "Unavailable" on failure).
/// Used by the System Overview page to display hardware details.
/// </summary>
public static class SystemInfoGatherer
{
    // ── OS Information ──────────────────────────────────────────────────────

    public class OsInfo
    {
        public string Caption { get; init; } = "Unavailable";
        public string Version { get; init; } = "";
        public string BuildNumber { get; init; } = "";
    }

    public static OsInfo GetOsInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                return new OsInfo
                {
                    Caption = obj["Caption"]?.ToString()?.Trim() ?? "Unavailable",
                    Version = obj["Version"]?.ToString() ?? "",
                    BuildNumber = obj["BuildNumber"]?.ToString() ?? ""
                };
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemInfoGatherer: Failed to query OS info");
        }
        return new OsInfo();
    }

    // ── CPU Information ─────────────────────────────────────────────────────

    public class CpuInfo
    {
        public string Name { get; init; } = "Unavailable";
        public int Cores { get; init; }
        public int LogicalProcessors { get; init; }
        public int MaxClockSpeedMHz { get; init; }
    }

    public static CpuInfo GetCpuInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                return new CpuInfo
                {
                    Name = obj["Name"]?.ToString()?.Trim() ?? "Unavailable",
                    Cores = Convert.ToInt32(obj["NumberOfCores"]),
                    LogicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"]),
                    MaxClockSpeedMHz = Convert.ToInt32(obj["MaxClockSpeed"])
                };
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemInfoGatherer: Failed to query CPU info");
        }
        return new CpuInfo();
    }

    // ── GPU Information ─────────────────────────────────────────────────────

    public class GpuInfo
    {
        public string Name { get; init; } = "Unavailable";
        public string DriverVersion { get; init; } = "";
        public long AdapterRamBytes { get; init; }
        public int CurrentResolutionWidth { get; init; }
        public int CurrentResolutionHeight { get; init; }
        public int RefreshRate { get; init; }
    }

    private const string DisplayClassBasePath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static GpuInfo GetGpuInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                // Skip Microsoft Basic Display Adapter
                var name = obj["Name"]?.ToString() ?? "";
                if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;

                long vramBytes = GetGpuVramFromRegistry(name);
                if (vramBytes <= 0)
                    vramBytes = (long)(uint)Convert.ToInt64(obj["AdapterRAM"]);

                return new GpuInfo
                {
                    Name = name.Trim(),
                    DriverVersion = obj["DriverVersion"]?.ToString() ?? "",
                    AdapterRamBytes = vramBytes,
                    CurrentResolutionWidth = Convert.ToInt32(obj["CurrentHorizontalResolution"]),
                    CurrentResolutionHeight = Convert.ToInt32(obj["CurrentVerticalResolution"]),
                    RefreshRate = Convert.ToInt32(obj["CurrentRefreshRate"])
                };
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemInfoGatherer: Failed to query GPU info");
        }
        return new GpuInfo();
    }

    /// <summary>
    /// Reads accurate VRAM via the registry QWORD HardwareInformation.qwMemorySize,
    /// which is not subject to the uint32 overflow in Win32_VideoController.AdapterRAM.
    /// Falls back to 0 on integrated GPUs or if the key is absent.
    /// </summary>
    private static long GetGpuVramFromRegistry(string gpuName)
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassBasePath);
            if (classKey == null) return 0;

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                // Subkeys are "0000", "0001", etc.
                if (!int.TryParse(subKeyName, out _)) continue;

                using var adapterKey = classKey.OpenSubKey(subKeyName);
                if (adapterKey == null) continue;

                var desc = adapterKey.GetValue("DriverDesc")?.ToString() ?? "";
                if (!desc.Contains(gpuName, StringComparison.OrdinalIgnoreCase) &&
                    !gpuName.Contains(desc, StringComparison.OrdinalIgnoreCase))
                    continue;

                var qwMemory = adapterKey.GetValue("HardwareInformation.qwMemorySize");
                if (qwMemory is long l) return l;
                if (qwMemory is byte[] bytes && bytes.Length >= 8)
                    return BitConverter.ToInt64(bytes, 0);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemInfoGatherer: Failed to read GPU VRAM from registry");
        }
        return 0;
    }

    // ── RAM Information ─────────────────────────────────────────────────────

    public class RamInfo
    {
        public long TotalBytes { get; init; }
        public int SpeedMHz { get; init; }
        public int ModuleCount { get; init; }
    }

    public static RamInfo GetRamInfo()
    {
        try
        {
            long totalBytes = 0;
            int speedMHz = 0;
            int moduleCount = 0;

            // Total RAM from ComputerSystem
            using (var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject obj in results)
                    totalBytes = Convert.ToInt64(obj["TotalPhysicalMemory"]);
            }

            // Speed and module count from PhysicalMemory
            using (var searcher = new ManagementObjectSearcher("SELECT Speed FROM Win32_PhysicalMemory"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject obj in results)
                {
                    moduleCount++;
                    if (speedMHz == 0)
                        speedMHz = Convert.ToInt32(obj["Speed"]);
                }
            }

            return new RamInfo { TotalBytes = totalBytes, SpeedMHz = speedMHz, ModuleCount = moduleCount };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemInfoGatherer: Failed to query RAM info");
        }
        return new RamInfo();
    }

    // ── Storage Information ─────────────────────────────────────────────────

    public class DriveInfo2
    {
        public string Model { get; set; } = "";
        public string MediaType { get; set; } = "";
        public long SizeBytes { get; set; }
        public string DriveLetter { get; set; } = "";
        public long FreeSpaceBytes { get; set; }
    }

    public static List<DriveInfo2> GetStorageDrives()
    {
        var drives = new List<DriveInfo2>();
        try
        {
            // Get physical disk info
            using var searcher = new ManagementObjectSearcher("SELECT Model, MediaType, Size FROM Win32_DiskDrive");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                drives.Add(new DriveInfo2
                {
                    Model = obj["Model"]?.ToString()?.Trim() ?? "Unknown",
                    MediaType = obj["MediaType"]?.ToString() ?? "",
                    SizeBytes = Convert.ToInt64(obj["Size"])
                });
            }

            // Supplement with logical disk free space
            foreach (var di in global::System.IO.DriveInfo.GetDrives())
            {
                if (!di.IsReady || di.DriveType != DriveType.Fixed) continue;

                // Try to match or just add logical drive info
                var existing = drives.FirstOrDefault();
                if (existing != null && string.IsNullOrEmpty(existing.DriveLetter))
                {
                    existing.DriveLetter = di.Name;
                    existing.FreeSpaceBytes = di.TotalFreeSpace;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemInfoGatherer: Failed to query storage info");
        }
        return drives;
    }

    // ── Display Information ─────────────────────────────────────────────────

    public class DisplayInfo
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int RefreshRate { get; init; }
    }

    public static DisplayInfo GetDisplayInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                var w = Convert.ToInt32(obj["CurrentHorizontalResolution"]);
                if (w > 0) // Skip adapters without active display
                {
                    return new DisplayInfo
                    {
                        Width = w,
                        Height = Convert.ToInt32(obj["CurrentVerticalResolution"]),
                        RefreshRate = Convert.ToInt32(obj["CurrentRefreshRate"])
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemInfoGatherer: Failed to query display info");
        }
        return new DisplayInfo();
    }

    // ── Windows Gaming Features ─────────────────────────────────────────────

    public static bool IsGameModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            var val = key?.GetValue("AutoGameModeEnabled");
            return val is int i ? i == 1 : true; // Default is enabled
        }
        catch { return false; }
    }

    public static bool IsGameBarEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            var val = key?.GetValue("UseNexusForGameBarEnabled");
            return val is int i ? i == 1 : true; // Default is enabled
        }
        catch { return false; }
    }

    // ── Top Processes by CPU ────────────────────────────────────────────────

    public class ProcessInfo
    {
        public string Name { get; init; } = "";
        public int Pid { get; init; }
        public double CpuSeconds { get; init; }
        public long MemoryMB { get; init; }
    }

    public static List<ProcessInfo> GetTopProcessesByCpu(int count = 5)
    {
        var result = new List<ProcessInfo>();
        try
        {
            var processes = Process.GetProcesses()
                .Where(p =>
                {
                    try { return p.TotalProcessorTime.TotalSeconds > 0; }
                    catch { return false; }
                })
                .Select(p =>
                {
                    try
                    {
                        return new ProcessInfo
                        {
                            Name = p.ProcessName,
                            Pid = p.Id,
                            CpuSeconds = p.TotalProcessorTime.TotalSeconds,
                            MemoryMB = p.WorkingSet64 / (1024 * 1024)
                        };
                    }
                    catch { return null; }
                })
                .Where(p => p != null)
                .OrderByDescending(p => p!.CpuSeconds)
                .Take(count)
                .ToList();

            result = processes!;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemInfoGatherer: Failed to enumerate top processes");
        }
        return result!;
    }
}
