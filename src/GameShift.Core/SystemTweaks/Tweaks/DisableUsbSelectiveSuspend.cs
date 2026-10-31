using System.Diagnostics;
using System.Text.Json;
using GameShift.Core.System;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Disables USB Selective Suspend via power plan settings.
/// USB Selective Suspend powers down idle USB ports to save energy, but causes
/// latency spikes when the port wakes (e.g., after a brief mouse pause).
/// Critical for high-polling-rate gaming mice (1000Hz+).
/// Uses powercfg to set the current power scheme's USB suspend setting.
/// Included in "Apply All Recommended" - safe and universally beneficial for gaming.
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
    private static readonly Guid UsbSubGroup = new(UsbSubGroupGuid);
    private static readonly Guid UsbSuspendSetting = new(UsbSuspendSettingGuid);

    /// <summary>Reads the active scheme's AC (true) or DC (false) index; tests replace it.</summary>
    internal Func<bool, uint?> ReadIndex { get; init; } =
        ac => ActiveSchemePowerSetting.ReadIndex(UsbSubGroup, UsbSuspendSetting, ac);

    /// <summary>Reads Windows' default AC (true) or DC (false) index for the active scheme; tests replace it.</summary>
    internal Func<bool, uint?> ReadDefaultIndex { get; init; } =
        ac => ActiveSchemePowerSetting.ReadDefaultIndex(UsbSubGroup, UsbSuspendSetting, ac);

    /// <summary>Runs powercfg and returns its exit code; tests replace it.</summary>
    internal Func<string, int> RunPowercfg { get; init; } = RunPowercfgProcess;

    public bool DetectIsApplied() => ReadIndex(true) == 0; // 0 = disabled

    public string? Apply()
    {
        // Without both originals the change could not be undone, so nothing is changed.
        var originalAc = ReadIndex(true);
        var originalDc = ReadIndex(false);
        if (originalAc == null || originalDc == null)
            throw new InvalidOperationException("Could not read the USB selective suspend values, so nothing was changed.");

        // Set to 0 (disabled) on both AC and DC, checking each powercfg exit code.
        int acExit = RunPowercfg($"/setacvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} 0");
        int dcExit = RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} 0");
        int activeExit = RunPowercfg("/setactive SCHEME_CURRENT");

        if (acExit != 0 || dcExit != 0 || activeExit != 0)
        {
            // Roll back any partial change, then throw so the manager records a failure rather than a
            // stale applied-state.
            RunPowercfg($"/setacvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} {originalAc}");
            RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} {originalDc}");
            RunPowercfg("/setactive SCHEME_CURRENT");
            throw new InvalidOperationException(
                $"powercfg failed disabling USB selective suspend (ac={acExit}, dc={dcExit}, active={activeExit})");
        }

        return JsonSerializer.Serialize(new { OriginalAc = originalAc.Value, OriginalDc = originalDc.Value });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(originalValuesJson);
            var ac = RevertValue(doc.RootElement.GetProperty("OriginalAc").GetInt64(), ac: true);
            var dc = RevertValue(doc.RootElement.GetProperty("OriginalDc").GetInt64(), ac: false);
            if (ac == null || dc == null) return false;

            int acExit = RunPowercfg($"/setacvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} {ac}");
            int dcExit = RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {UsbSubGroupGuid} {UsbSuspendSettingGuid} {dc}");
            int activeExit = RunPowercfg("/setactive SCHEME_CURRENT");
            return acExit == 0 && dcExit == 0 && activeExit == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// True when an earlier version recorded -1 for AC or DC because it could not read the value;
    /// a revert sets that one back to Windows' default.
    /// </summary>
    internal static bool HasUnrecordedOriginal(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(originalValuesJson);
            return doc.RootElement.GetProperty("OriginalAc").GetInt64() < 0
                || doc.RootElement.GetProperty("OriginalDc").GetInt64() < 0;
        }
        catch { return false; }
    }

    private uint? RevertValue(long original, bool ac) =>
        original < 0 ? ReadDefaultIndex(ac)
        : original <= uint.MaxValue ? (uint)original
        : null;

    private static int RunPowercfgProcess(string arguments)
    {
        try
        {
            var result = ProcessRunner.Run(NativeInterop.SystemExePath("powercfg.exe"), arguments, 5000);
            return result.Exited && !result.TimedOut ? result.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }
}
