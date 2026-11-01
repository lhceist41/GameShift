using System.Globalization;
using GameShift.Core.System;

namespace GameShift.Core.Presentation;

/// <summary>
/// The System page's hardware lines. Numbers use the invariant culture, like the rest of the
/// English text, so a German PC shows "15.9 GB" and not "15,9 GB".
/// </summary>
public static class SystemText
{
    private const double BytesPerGb = 1024.0 * 1024 * 1024;

    public static string OsVersion(SystemInfoGatherer.OsInfo os) =>
        $"Version {os.Version} (Build {os.BuildNumber})";

    public static string CpuDetails(SystemInfoGatherer.CpuInfo cpu) =>
        Invariant($"{cpu.Cores} cores / {cpu.LogicalProcessors} threads @ {cpu.MaxClockSpeedMHz} MHz");

    public static string GpuDetails(SystemInfoGatherer.GpuInfo gpu) =>
        gpu.AdapterRamBytes > 0
            ? $"Driver {gpu.DriverVersion} | {Gb(gpu.AdapterRamBytes, "0.0")} VRAM"
            : $"Driver {gpu.DriverVersion}";

    public static string RamTotal(SystemInfoGatherer.RamInfo ram) => Gb(ram.TotalBytes, "0.0");

    public static string RamDetails(SystemInfoGatherer.RamInfo ram) =>
        ram.SpeedMHz > 0
            ? Invariant($"{ram.ModuleCount} module(s) @ {ram.SpeedMHz} MHz")
            : Invariant($"{ram.ModuleCount} module(s)");

    public static string StorageDetails(SystemInfoGatherer.DriveInfo2 drive) =>
        drive.FreeSpaceBytes > 0
            ? $"{Gb(drive.SizeBytes, "0")} total | {Gb(drive.FreeSpaceBytes, "0")} free"
            : $"{Gb(drive.SizeBytes, "0")} | {drive.MediaType}";

    public static string DisplayResolution(SystemInfoGatherer.DisplayInfo display) =>
        Invariant($"{display.Width} x {display.Height}");

    public static string DisplayDetails(SystemInfoGatherer.DisplayInfo display) =>
        Invariant($"{display.RefreshRate} Hz refresh rate");

    public static string ProcessDetails(SystemInfoGatherer.ProcessInfo process) =>
        Invariant($"PID {process.Pid} | CPU {process.CpuSeconds:0.0} s | {process.MemoryMB} MB");

    private static string Gb(long bytes, string format) =>
        (bytes / BytesPerGb).ToString(format, CultureInfo.InvariantCulture) + " GB";

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
