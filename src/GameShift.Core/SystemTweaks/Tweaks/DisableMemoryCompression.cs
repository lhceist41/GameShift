using System.Diagnostics;
using System.Management;
using System.Text.Json;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Disables Windows memory compression via PowerShell Disable-MMAgent.
/// Windows compresses memory pages in the background to defer paging — on systems
/// with 32GB+ RAM and fast NVMe, the CPU overhead of compress/decompress exceeds the
/// benefit. Only offered on 32GB+ systems.
/// NOT included in "Apply All Recommended" — opt-in only.
/// Requires reboot to take full effect.
/// </summary>
public class DisableMemoryCompression : ISystemTweak
{
    public string Name => "Disable Memory Compression";
    public string Description => "Reduces CPU overhead by disabling background memory compression. Only recommended for systems with 32GB+ RAM and fast NVMe storage.";
    public string Category => "Memory";
    public bool RequiresReboot => true;

    /// <summary>
    /// Whether this tweak is applicable to the current system (32GB+ RAM).
    /// When false, the UI should gray out the toggle.
    /// </summary>
    public bool IsApplicable => DetectTotalRamGb() >= 32;

    /// <summary>Total RAM in GB for UI display.</summary>
    public double TotalRamGb => DetectTotalRamGb();

    public bool DetectIsApplied()
    {
        try
        {
            return !IsMemoryCompressionEnabled();
        }
        catch
        {
            return false;
        }
    }

    public string? Apply()
    {
        if (!IsApplicable)
        {
            Log.Warning("[MemoryCompression] Cannot disable — system has less than 32GB RAM");
            return null;
        }

        bool wasEnabled = IsMemoryCompressionEnabled();

        var (exitCode, output) = RunPowerShell(
            "-NoProfile -Command \"Disable-MMAgent -MemoryCompression\"");

        if (exitCode != 0)
        {
            Log.Warning("[MemoryCompression] Failed to disable: {Output}", output);
            return null;
        }

        Log.Information("[MemoryCompression] Disabled — reboot required");
        return JsonSerializer.Serialize(new { WasEnabled = wasEnabled });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;

        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            bool wasEnabled = doc.RootElement.GetProperty("WasEnabled").GetBoolean();

            if (!wasEnabled)
            {
                // Was already disabled before GameShift — nothing to restore
                return true;
            }

            var (exitCode, output) = RunPowerShell(
                "-NoProfile -Command \"Enable-MMAgent -MemoryCompression\"");

            if (exitCode != 0)
            {
                Log.Warning("[MemoryCompression] Failed to re-enable: {Output}", output);
                return false;
            }

            Log.Information("[MemoryCompression] Re-enabled — reboot required");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MemoryCompression] Failed to revert");
            return false;
        }
    }

    /// <summary>
    /// Checks if memory compression is currently enabled via PowerShell Get-MMAgent.
    /// </summary>
    private static bool IsMemoryCompressionEnabled()
    {
        var (exitCode, output) = RunPowerShell(
            "-NoProfile -Command \"(Get-MMAgent).MemoryCompression\"");

        if (exitCode != 0)
            return true; // Assume enabled if query fails

        return output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects total physical RAM in GB via WMI.
    /// </summary>
    private static double DetectTotalRamGb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");

            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var bytes = Convert.ToDouble(obj["TotalPhysicalMemory"]);
                    return Math.Round(bytes / (1024.0 * 1024 * 1024), 1);
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MemoryCompression] Failed to detect RAM");
        }

        return 0;
    }

    private static (int exitCode, string output) RunPowerShell(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = NativeInterop.SystemExePath("WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return (-1, "Failed to start PowerShell");

            var error = "";
            var stderrTask = Task.Run(() => { error = process.StandardError.ReadToEnd(); });
            var output = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(10000);
            process.WaitForExit(10000);

            return (process.ExitCode, string.IsNullOrEmpty(output) ? error : output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
