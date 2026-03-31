namespace GameShift.Core.Detection;

/// <summary>
/// Consolidated hardware scan results used for conditional game optimizations.
/// Populated by HardwareScanner.ScanAsync(), exposed via HardwareScanner.Result.
/// </summary>
public class HardwareScanResult
{
    // GPU
    public GpuVendor GpuVendor { get; set; } = GpuVendor.Unknown;
    public string GpuName { get; set; } = "Unknown";
    public string GpuDriverVersion { get; set; } = "";
    public long GpuVramBytes { get; set; }
    public double GpuVramGb => GpuVramBytes / (1024.0 * 1024 * 1024);

    // CPU
    public string CpuName { get; set; } = "Unknown";
    public int TotalCores { get; set; }
    public int TotalLogicalProcessors { get; set; }
    public bool IsHybridCpu { get; set; }

    // System
    public double TotalRamGb { get; set; }
    public bool IsLaptop { get; set; }
    public bool VbsEnabled { get; set; }

    // Anti-Cheat / Safety
    public bool IsVanguardDetected { get; set; }
    public bool HasRiotGamesOnDisk { get; set; }

    // Display
    public int PrimaryRefreshRate { get; set; }

    // HAGS (Hardware Accelerated GPU Scheduling)
    public bool IsHagsEnabled { get; set; }

    // GPU Generation (Sprint 7 — HAGS recommendation engine)
    public GpuGeneration GpuGeneration { get; set; } = GpuGeneration.Unknown;

    // Resizable BAR / Smart Access Memory (Sprint 8E)
    public bool IsReBarEnabled { get; set; }

    // Convenience
    public bool HasSufficientVram(double requiredGb) => GpuVramGb >= requiredGb;
}
