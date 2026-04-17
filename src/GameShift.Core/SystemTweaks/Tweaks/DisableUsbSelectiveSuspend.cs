using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameShift.Core.System;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Disables USB Selective Suspend via power plan settings.
/// USB Selective Suspend powers down idle USB ports to save energy, but causes
/// latency spikes when the port wakes (e.g., after a brief mouse pause).
/// Critical for high-polling-rate gaming mice (1000Hz+).
/// Uses powercfg to set the current power scheme's USB suspend setting.
/// Included in "Apply All Recommended" — safe and universally beneficial for gaming.
/// May slightly reduce battery life on laptops (~0.1-0.5W).
/// </summary>
public class DisableUsbSelectiveSuspend : ISystemTweak
{
    public string Name => "Disable USB Selective Suspend";
    public string Description => "Prevents USB port sleep that causes input latency spikes with gaming mice. May reduce battery life on laptops.";
    public string Category => "USB & Input";
    public bool RequiresReboot => false;

    // Power plan GUIDs for USB selective suspend
    private const string UsbSubGroupGuid = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSuspendSettingGuid = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    public bool DetectIsApplied()
    {
        try
        {
            int acValue = QueryPowerSettingValue("AC");
            return acValue == 0; // 0 = disabled
        }
        catch { return false; }
    }

    public string? Apply()
    {
        // Capture original AC and DC values
        int originalAc = QueryPowerSettingValue("AC");
        int originalDc = QueryPowerSettingValue("DC");

        // Set to 0 (disabled) on both AC and DC
        RunPowercfg($"/setacvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} 0");
        RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} 0");
        RunPowercfg("/setactive SCHEME_CURRENT");

        return JsonSerializer.Serialize(new { OriginalAc = originalAc, OriginalDc = originalDc });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            int originalAc = doc.RootElement.GetProperty("OriginalAc").GetInt32();
            int originalDc = doc.RootElement.GetProperty("OriginalDc").GetInt32();

            RunPowercfg($"/setacvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} {originalAc}");
            RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} {originalDc}");
            RunPowercfg("/setactive SCHEME_CURRENT");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Queries the current USB selective suspend power setting value.
    /// </summary>
    /// <param name="powerType">"AC" or "DC"</param>
    /// <returns>Setting value (0 = disabled, 1 = enabled), or -1 on failure</returns>
    private static int QueryPowerSettingValue(string powerType)
    {
        var output = RunPowercfg($"/query SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid}");
        if (output == null) return -1;

        // Parse "Current AC Power Setting Index: 0x00000001" or "Current DC Power Setting Index: ..."
        var pattern = $@"Current {powerType} Power Setting Index:\s*0x([0-9a-fA-F]+)";
        var match = Regex.Match(output, pattern);
        return match.Success ? Convert.ToInt32(match.Groups[1].Value, 16) : -1;
    }

    private static string? RunPowercfg(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = NativeInterop.SystemExePath("powercfg.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stderr = "";
            var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
            var output = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(5000);
            process.WaitForExit(5000);
            return output;
        }
        catch
        {
            return null;
        }
    }
}
