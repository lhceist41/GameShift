using System;
using System.IO;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using GameShift.Core.Optimization;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Detection;

/// <summary>
/// Scans hardware capabilities to recommend optimal GameShift settings.
/// Detects GPU (via GpuDetector), total RAM, VBS/HVCI state, and DPC baseline latency.
/// Also builds a consolidated HardwareScanResult for conditional game optimizations.
/// Used by the first-run wizard and Settings "Auto-Detect" button.
/// </summary>
public class HardwareScanner
{
    /// <summary>GPU name detected via WMI.</summary>
    public string GpuName { get; private set; } = "Unknown";

    /// <summary>Total physical RAM in GB.</summary>
    public double TotalRamGb { get; private set; }

    /// <summary>Whether VBS/HVCI is currently enabled.</summary>
    public bool VbsEnabled { get; private set; }

    /// <summary>Baseline DPC latency in microseconds (average over 5-second sample).</summary>
    public double DpcBaselineUs { get; private set; }

    /// <summary>Whether the scan has completed.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// Consolidated hardware scan result for conditional game optimizations.
    /// Null until ScanAsync() completes.
    /// </summary>
    public HardwareScanResult? Result { get; private set; }

    /// <summary>
    /// Runs the full hardware scan. Non-blocking — runs WMI queries on a background thread.
    /// </summary>
    public async Task ScanAsync(IProgress<string>? progress = null)
    {
        await Task.Run(() =>
        {
            // Step 1: GPU detection (reuses GpuDetector single source of truth)
            progress?.Report("Detecting GPU...");
            GpuName = GpuDetector.GetGpuName();
            Log.Information("HardwareScanner: GPU = {GpuName}", GpuName);

            // Step 2: RAM detection
            progress?.Report("Checking RAM...");
            TotalRamGb = DetectTotalRam();
            Log.Information("HardwareScanner: RAM = {RamGb:F1} GB", TotalRamGb);

            // Step 3: VBS/HVCI check
            progress?.Report("Checking VBS/HVCI...");
            var vbs = new VbsHvciToggle();
            vbs.CheckState();
            VbsEnabled = vbs.ShouldShowBanner;
            Log.Information("HardwareScanner: VBS enabled = {VbsEnabled}", VbsEnabled);

            // Step 4: DPC baseline (quick 3-second sample)
            progress?.Report("Measuring DPC baseline (3s)...");
            DpcBaselineUs = MeasureDpcBaseline();
            Log.Information("HardwareScanner: DPC baseline = {DpcUs:F0} µs", DpcBaselineUs);

            // Step 5: Build consolidated HardwareScanResult
            progress?.Report("Building hardware profile...");
            BuildHardwareScanResult(vbs);
            Log.Information("HardwareScanner: Result built — Vendor={Vendor}, Hybrid={Hybrid}, Laptop={Laptop}, HAGS={Hags}, RiotOnDisk={Riot}",
                Result!.GpuVendor, Result.IsHybridCpu, Result.IsLaptop, Result.IsHagsEnabled, Result.HasRiotGamesOnDisk);

            IsComplete = true;
            progress?.Report("Scan complete.");
        });
    }

    /// <summary>
    /// Quick hardware detection that populates the HardwareScanResult WITHOUT
    /// DPC baseline measurement (no 3-second delay). Used at startup for
    /// conditional game action filtering. Call ScanAsync() later for full results.
    /// </summary>
    /// <param name="vbs">VbsHvciToggle instance (already checked) for Vanguard detection.</param>
    public void DetectHardwareQuick(VbsHvciToggle vbs)
    {
        GpuName = GpuDetector.GetGpuName();
        TotalRamGb = DetectTotalRam();
        VbsEnabled = vbs.ShouldShowBanner;

        BuildHardwareScanResult(vbs);
        Log.Information(
            "HardwareScanner: Quick detect — Vendor={Vendor}, Hybrid={Hybrid}, Laptop={Laptop}, HAGS={Hags}, RiotOnDisk={Riot}",
            Result!.GpuVendor, Result.IsHybridCpu, Result.IsLaptop, Result.IsHagsEnabled, Result.HasRiotGamesOnDisk);
    }

    /// <summary>
    /// Builds the consolidated HardwareScanResult from existing scan data plus
    /// additional detection methods for conditional game optimizations.
    /// </summary>
    private void BuildHardwareScanResult(VbsHvciToggle vbs)
    {
        var gpuInfo = SystemInfoGatherer.GetGpuInfo();
        var cpuInfo = SystemInfoGatherer.GetCpuInfo();
        var displayInfo = SystemInfoGatherer.GetDisplayInfo();

        Result = new HardwareScanResult
        {
            GpuVendor = DetectGpuVendor(),
            GpuName = GpuName,
            GpuDriverVersion = gpuInfo.DriverVersion,
            GpuVramBytes = gpuInfo.AdapterRamBytes,
            CpuName = cpuInfo.Name,
            TotalCores = cpuInfo.Cores,
            TotalLogicalProcessors = cpuInfo.LogicalProcessors,
            IsHybridCpu = DetectIsHybridCpu(),
            TotalRamGb = TotalRamGb,
            IsLaptop = DetectIsLaptop(),
            VbsEnabled = VbsEnabled,
            IsVanguardDetected = vbs.IsVanguardInstalled(),
            HasRiotGamesOnDisk = DetectRiotGamesOnDisk(),
            PrimaryRefreshRate = displayInfo.RefreshRate,
            IsHagsEnabled = DetectHagsEnabled(),
            GpuGeneration = DetectGpuGeneration(GpuName),
            IsReBarEnabled = DetectReBarEnabled()
        };
    }

    // ── New detection methods for HardwareScanResult ─────────────────────

    /// <summary>
    /// Detects GPU vendor from WMI AdapterCompatibility field.
    /// Same pattern as GpuDriverOptimizer.DetectGpuVendor().
    /// </summary>
    private static GpuVendor DetectGpuVendor()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT AdapterCompatibility, Name FROM Win32_VideoController");

            foreach (ManagementObject obj in searcher.Get())
            {
                var compat = obj["AdapterCompatibility"]?.ToString() ?? "";
                var name = obj["Name"]?.ToString() ?? "";

                // Skip virtual adapters
                if (name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ContainsIgnoreCase(compat, "NVIDIA") || ContainsIgnoreCase(name, "NVIDIA"))
                    return GpuVendor.Nvidia;

                if (ContainsIgnoreCase(compat, "AMD") ||
                    ContainsIgnoreCase(compat, "Advanced Micro Devices") ||
                    ContainsIgnoreCase(name, "Radeon"))
                    return GpuVendor.Amd;

                if (ContainsIgnoreCase(compat, "Intel") || ContainsIgnoreCase(name, "Intel"))
                    return GpuVendor.Intel;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareScanner: Failed to detect GPU vendor");
        }

        return GpuVendor.Unknown;
    }

    /// <summary>
    /// Detects hybrid CPU architecture by checking registry EfficiencyClass values.
    /// Same approach as HybridCpuDetector — EfficiencyClass 0 = P-core, >0 = E-core.
    /// </summary>
    private static bool DetectIsHybridCpu()
    {
        try
        {
            const string cpuRegPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor";
            using var cpuKey = Registry.LocalMachine.OpenSubKey(cpuRegPath);
            if (cpuKey == null) return false;

            bool hasP = false, hasE = false;

            foreach (var subName in cpuKey.GetSubKeyNames())
            {
                using var core = cpuKey.OpenSubKey(subName);
                if (core == null) continue;

                var effClass = core.GetValue("EfficiencyClass");
                if (effClass is int eff)
                {
                    if (eff == 0) hasP = true;
                    else hasE = true;
                }
            }

            return hasP && hasE;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareScanner: Failed to detect hybrid CPU");
            return false;
        }
    }

    /// <summary>
    /// Detects if the system is a laptop via WMI Win32_SystemEnclosure ChassisTypes.
    /// Laptop chassis type values: 8,9,10,11,12,14,18,21,31,32.
    /// </summary>
    private static bool DetectIsLaptop()
    {
        try
        {
            // Laptop chassis type IDs per SMBIOS specification
            var laptopTypes = new HashSet<int> { 8, 9, 10, 11, 12, 14, 18, 21, 31, 32 };

            using var searcher = new ManagementObjectSearcher(
                "SELECT ChassisTypes FROM Win32_SystemEnclosure");

            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["ChassisTypes"] is ushort[] types)
                {
                    foreach (var t in types)
                    {
                        if (laptopTypes.Contains(t))
                            return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareScanner: Failed to detect chassis type");
        }

        return false;
    }

    /// <summary>
    /// Checks if Hardware Accelerated GPU Scheduling (HAGS) is enabled.
    /// Registry: HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode
    /// Value 2 = enabled.
    /// </summary>
    private static bool DetectHagsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (key == null) return false;

            var val = key.GetValue("HwSchMode");
            return val is int mode && mode == 2;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareScanner: Failed to detect HAGS state");
            return false;
        }
    }

    /// <summary>
    /// Detects whether Resizable BAR (NVIDIA) or Smart Access Memory (AMD) is enabled.
    ///
    /// Checks the primary GPU's driver class key for vendor-specific large-BAR indicators:
    ///   - NVIDIA: <c>RMApertureSizeInMB</c> under <c>Services\nvlddmkm</c> (large value = ReBAR)
    ///   - AMD: <c>EnableLargeBar</c> or presence of large BAR config in driver class key
    ///   - Fallback: compare <c>Win32_VideoController.AdapterRAM</c> mapped size hint
    ///
    /// Does NOT attempt to enable ReBAR — this is a BIOS-level setting.
    /// </summary>
    private static bool DetectReBarEnabled()
    {
        try
        {
            const string driverClassBase = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            using var classKey = Registry.LocalMachine.OpenSubKey(driverClassBase);
            if (classKey == null) return false;

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                // Only check numbered subkeys (0000, 0001, etc.)
                if (!int.TryParse(subKeyName, out _)) continue;

                using var driverKey = classKey.OpenSubKey(subKeyName);
                if (driverKey == null) continue;

                string provider = driverKey.GetValue("ProviderName")?.ToString() ?? "";

                // NVIDIA: check for large aperture size (> 256 MB indicates ReBAR)
                if (provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    // nvlddmkm reports aperture size when ReBAR is active
                    var aperture = driverKey.GetValue("RMApertureSizeInMB");
                    if (aperture is int apMb && apMb > 256)
                    {
                        Log.Debug("ReBAR detected: NVIDIA aperture {Size} MB", apMb);
                        return true;
                    }
                }

                // AMD: check for Large BAR enablement
                if (provider.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                {
                    // AMD drivers expose EnableLargeBar when SAM is active
                    var largeBar = driverKey.GetValue("EnableLargeBar");
                    if (largeBar is int lb && lb == 1)
                    {
                        Log.Debug("ReBAR/SAM detected: AMD EnableLargeBar = 1");
                        return true;
                    }

                    // Alternative: check KMD_EnableInternalLargePage
                    var internalLarge = driverKey.GetValue("KMD_EnableInternalLargePage");
                    if (internalLarge is int ilp && ilp == 1)
                    {
                        Log.Debug("ReBAR/SAM detected: AMD KMD_EnableInternalLargePage = 1");
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareScanner: Failed to detect ReBAR state");
        }

        return false;
    }

    /// <summary>
    /// Checks if Riot Games executables exist on disk at common install paths.
    /// Stronger check than just detecting running Vanguard service.
    /// </summary>
    private static bool DetectRiotGamesOnDisk()
    {
        try
        {
            var riotPaths = new[]
            {
                @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
                @"C:\Riot Games\League of Legends\Game\League of Legends.exe",
                @"C:\Program Files\Riot Vanguard\vgc.exe"
            };

            foreach (var path in riotPaths)
            {
                if (File.Exists(path))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareScanner: Failed to check Riot game paths");
        }

        return false;
    }

    /// <summary>
    /// Classifies the GPU generation from its name string.
    /// Used by the HAGS recommendation engine to determine Frame Generation support.
    /// </summary>
    public static GpuGeneration DetectGpuGeneration(string gpuName)
    {
        if (string.IsNullOrEmpty(gpuName)) return GpuGeneration.Unknown;

        string upper = gpuName.ToUpperInvariant();

        // NVIDIA — detect by model number patterns
        if (upper.Contains("RTX 50") || upper.Contains("RTX50")) return GpuGeneration.NvidiaRtx50;
        if (upper.Contains("RTX 40") || upper.Contains("RTX40")) return GpuGeneration.NvidiaRtx40;
        if (upper.Contains("RTX 30") || upper.Contains("RTX30")) return GpuGeneration.NvidiaRtx30;
        if (upper.Contains("RTX 20") || upper.Contains("RTX20")) return GpuGeneration.NvidiaRtx20;
        if (upper.Contains("GTX 16")) return GpuGeneration.NvidiaGtx16;
        if (upper.Contains("GTX 10")) return GpuGeneration.NvidiaGtx10;

        // AMD — detect by model number patterns (RX 9xxx = RDNA4, RX 7xxx = RDNA3, etc.)
        if (upper.Contains("RX 9") || upper.Contains("RX9")) return GpuGeneration.AmdRdna4;
        if (upper.Contains("RX 7") || upper.Contains("RX7")) return GpuGeneration.AmdRdna3;
        if (upper.Contains("RX 6") || upper.Contains("RX6")) return GpuGeneration.AmdRdna2;
        if (upper.Contains("RX 5") || upper.Contains("RX5")) return GpuGeneration.AmdRdna1;

        // Intel Arc — Battlemage (B-series), then Alchemist (A-series)
        if (upper.Contains("ARC B") || upper.Contains("B580") || upper.Contains("B570"))
            return GpuGeneration.IntelArcBattlemage;
        if (upper.Contains("ARC A") || upper.Contains("A770") || upper.Contains("A750") || upper.Contains("A580"))
            return GpuGeneration.IntelArcAlchemist;

        // Generic vendor detection as fallback
        if (upper.Contains("NVIDIA") || upper.Contains("GEFORCE")) return GpuGeneration.Other;
        if (upper.Contains("RADEON") || upper.Contains("AMD")) return GpuGeneration.Other;
        if (upper.Contains("INTEL")) return GpuGeneration.Other;

        return GpuGeneration.Unknown;
    }

    // ── Existing detection methods ───────────────────────────────────────

    /// <summary>
    /// Detects total physical RAM via WMI Win32_ComputerSystem.TotalPhysicalMemory.
    /// Returns 0 on failure.
    /// </summary>
    private static double DetectTotalRam()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");

            foreach (ManagementObject obj in searcher.Get())
            {
                var bytes = Convert.ToDouble(obj["TotalPhysicalMemory"]);
                return Math.Round(bytes / (1024.0 * 1024 * 1024), 1);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareScanner: Failed to detect RAM");
        }

        return 0;
    }

    /// <summary>
    /// Takes a quick 3-second DPC latency baseline measurement.
    /// Returns average latency in microseconds, or 0 if measurement fails.
    /// </summary>
    private static double MeasureDpcBaseline()
    {
        try
        {
            using var monitor = new Monitoring.DpcLatencyMonitor();
            double sum = 0;
            int count = 0;

            monitor.LatencySampled += (_, latency) =>
            {
                sum += latency;
                count++;
            };

            monitor.Start(1000); // 1000µs threshold (default)
            Thread.Sleep(3000); // 3-second sample
            monitor.Stop();

            return count > 0 ? Math.Round(sum / count, 0) : 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareScanner: DPC baseline measurement failed");
            return 0;
        }
    }

    /// <summary>
    /// Returns a human-readable summary of the scan results.
    /// </summary>
    public string GetSummary()
    {
        if (!IsComplete) return "Scan not yet completed.";

        return $"GPU: {GpuName}\n" +
               $"RAM: {TotalRamGb:F0} GB\n" +
               $"VBS/HVCI: {(VbsEnabled ? "Enabled (impacts performance)" : "Disabled")}\n" +
               $"DPC Baseline: {DpcBaselineUs:F0} µs {(DpcBaselineUs > 1000 ? "(High)" : "(Normal)")}";
    }

    private static bool ContainsIgnoreCase(string? source, string value)
    {
        return source != null && source.Contains(value, StringComparison.OrdinalIgnoreCase);
    }
}
