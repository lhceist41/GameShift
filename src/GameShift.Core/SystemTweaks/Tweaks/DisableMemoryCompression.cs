using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Management;
using System.Text.Json;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Disables Windows memory compression via PowerShell Disable-MMAgent.
/// Windows compresses memory pages in the background to defer paging - on systems
/// with 32GB+ RAM and fast NVMe, the CPU overhead of compress/decompress exceeds the
/// benefit. Only offered on 32GB+ systems.
/// NOT included in "Apply All Recommended" - opt-in only.
/// Requires reboot to take full effect.
/// </summary>
public class DisableMemoryCompression : ISystemTweak
{
    public string Name => "Disable Memory Compression";
    public string Description => "Reduces CPU overhead by disabling background memory compression. Only recommended for systems with 32GB+ RAM and fast NVMe storage.";
    public string Category => "Memory";
    public bool RequiresReboot => true;

    // Physical RAM doesn't change at runtime - detect once. WMI is expensive and these are read
    // from the Settings UI on every render pass.
    private static readonly double _totalRamGb = DetectTotalRamGb();

    private readonly double _ramGb;

    private const string MMAgentModule = "MMAgent";
    private const string QueryScript =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n(Get-MMAgent).MemoryCompression";
    private const string DisableScript = "Disable-MMAgent -MemoryCompression";
    private const string EnableScript = "Enable-MMAgent -MemoryCompression";

    /// <summary>Process-start seam; tests replace it so no PowerShell runs.</summary>
    internal Func<ProcessStartInfo, int, ProcessRunResult> RunProcess { get; set; } = ParameterizedPowerShell.Run;

    public DisableMemoryCompression() : this(_totalRamGb) { }

    /// <param name="totalRamGb">Installed RAM in GB; decides <see cref="IsApplicable"/>.</param>
    internal DisableMemoryCompression(double totalRamGb) => _ramGb = totalRamGb;

    /// <summary>
    /// Whether this tweak is applicable to the current system (32GB+ RAM).
    /// When false, the UI should gray out the toggle.
    /// </summary>
    public bool IsApplicable => _ramGb >= 32;

    /// <summary>Total RAM in GB for UI display.</summary>
    public double TotalRamGb => _ramGb;

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
        // Throw (rather than return null) on failure so SystemTweaksManager records a failure, not a
        // bogus "applied (no original)" state that can never be cleanly reverted.
        if (!IsApplicable)
        {
            Log.Warning("[MemoryCompression] Cannot disable - system has less than 32GB RAM");
            throw new InvalidOperationException("Memory compression tweak requires 32GB+ RAM");
        }

        bool wasEnabled = IsMemoryCompressionEnabled();

        var (exitCode, output) = RunPowerShell(DisableScript);

        if (exitCode != 0)
        {
            Log.Warning("[MemoryCompression] Failed to disable: {Output}", output);
            throw new InvalidOperationException($"Disable-MMAgent failed (exit {exitCode}): {output}");
        }

        Log.Information("[MemoryCompression] Disabled - reboot required");
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
                // Was already disabled before GameShift - nothing to restore
                return true;
            }

            var (exitCode, output) = RunPowerShell(EnableScript);

            if (exitCode != 0)
            {
                Log.Warning("[MemoryCompression] Failed to re-enable: {Output}", output);
                return false;
            }

            Log.Information("[MemoryCompression] Re-enabled - reboot required");
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
    private bool IsMemoryCompressionEnabled()
    {
        var (exitCode, output) = RunPowerShell(QueryScript);

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

    private (int exitCode, string output) RunPowerShell([ConstantExpected] string script)
    {
        try
        {
            var result = RunProcess(ParameterizedPowerShell.CreateStartInfo(MMAgentModule, script), 10000);
            if (result.TimedOut)
                return (-1, "PowerShell timed out");
            if (!result.Exited)
                return (-1, "Failed to start PowerShell");

            return (result.ExitCode, string.IsNullOrEmpty(result.StdOut) ? result.StdErr : result.StdOut);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
