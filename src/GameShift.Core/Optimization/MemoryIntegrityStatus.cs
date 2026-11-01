using System.Management;
using GameShift.Core.Config;

namespace GameShift.Core.Optimization;

/// <summary>
/// What Windows does with Memory Integrity (VBS/HVCI) in the current boot.
/// </summary>
public enum MemoryIntegrityState
{
    /// <summary>Neither turned on nor running.</summary>
    Off,

    /// <summary>Turned on, but not running in this boot (for example until the next restart).</summary>
    NotRunning,

    /// <summary>Running in this boot.</summary>
    Running,

    /// <summary>The registry turns it on, but Windows could not report whether it runs.</summary>
    TurnedOn,
}

/// <summary>
/// Whether a Memory Integrity disable done by GameShift has taken effect.
/// </summary>
public enum MemoryIntegrityDisableOutcome
{
    /// <summary>No GameShift disable to judge, or Memory Integrity is not running.</summary>
    None,

    /// <summary>Still running, and Windows has not restarted since the disable.</summary>
    PendingRestart,

    /// <summary>Still running after a restart that followed the disable.</summary>
    DidNotTakeEffect,
}

/// <summary>The Win32_DeviceGuard values the shown state is built from.</summary>
internal sealed record DeviceGuardReading(
    int VbsStatus,
    IReadOnlyCollection<int> ServicesConfigured,
    IReadOnlyCollection<int> ServicesRunning);

internal interface IDeviceGuardReader
{
    /// <summary>The current reading, or null when Windows could not report it.</summary>
    DeviceGuardReading? Read();
}

internal static class MemoryIntegrityStatus
{
    // Win32_DeviceGuard: VirtualizationBasedSecurityStatus 1 = enabled but not running, 2 = running;
    // in SecurityServicesConfigured and SecurityServicesRunning, 2 = memory integrity.
    private const int VbsEnabledNotRunning = 1;
    private const int VbsRunning = 2;
    private const int MemoryIntegrityService = 2;

    /// <summary>
    /// The state to show. Without a reading only the registry is known, and a registry that turns it
    /// on never becomes "off".
    /// </summary>
    internal static MemoryIntegrityState Evaluate(DeviceGuardReading? reading, bool registryTurnedOn)
    {
        if (reading == null)
            return registryTurnedOn ? MemoryIntegrityState.TurnedOn : MemoryIntegrityState.Off;

        if (reading.VbsStatus == VbsRunning || reading.ServicesRunning.Contains(MemoryIntegrityService))
            return MemoryIntegrityState.Running;

        if (registryTurnedOn
            || reading.VbsStatus == VbsEnabledNotRunning
            || reading.ServicesConfigured.Contains(MemoryIntegrityService))
            return MemoryIntegrityState.NotRunning;

        return MemoryIntegrityState.Off;
    }

    /// <summary>
    /// Judges GameShift's last disable. A boot after the recorded disable time means Windows has
    /// restarted since: the system tick count resets only on a full boot, which is also the only
    /// thing that applies a VBS change. Without a recorded time nothing is claimed, and neither when
    /// the registry turns VBS or HVCI on again: GameShift wrote both to 0, so something turned it on since.
    /// </summary>
    internal static MemoryIntegrityDisableOutcome JudgeDisable(
        MemoryIntegrityState state, bool registryTurnedOn, AppSettings settings, DateTime bootTimeUtc)
    {
        if (state != MemoryIntegrityState.Running
            || registryTurnedOn
            || !settings.VbsHvciDisabledByGameShift
            || settings.VbsHvciDisabledAtUtc is not { } disabledAt)
            return MemoryIntegrityDisableOutcome.None;

        return bootTimeUtc > disabledAt
            ? MemoryIntegrityDisableOutcome.DidNotTakeEffect
            : MemoryIntegrityDisableOutcome.PendingRestart;
    }

    /// <summary>When Windows started this boot, from the tick count (which counts sleep too).</summary>
    internal static DateTime BootTimeUtc() =>
        DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>Reads Win32_DeviceGuard; a missing class or a failed query reads as null.</summary>
internal sealed class WmiDeviceGuardReader : IDeviceGuardReader
{
    public DeviceGuardReading? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesConfigured, SecurityServicesRunning FROM Win32_DeviceGuard");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);

            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    return Parse(
                        item["VirtualizationBasedSecurityStatus"],
                        item["SecurityServicesConfigured"],
                        item["SecurityServicesRunning"]);
                }
            }

            SettingsManager.Logger.Warning("VbsHvciToggle: Win32_DeviceGuard returned no instance");
            return null;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "VbsHvciToggle: Could not read Win32_DeviceGuard");
            return null;
        }
    }

    internal static DeviceGuardReading? Parse(object? vbsStatus, object? configured, object? running)
    {
        if (vbsStatus is not uint status)
            return null;

        return new DeviceGuardReading((int)status, ToList(configured), ToList(running));
    }

    private static IReadOnlyCollection<int> ToList(object? values) =>
        values is uint[] array ? array.Select(v => (int)v).ToArray() : Array.Empty<int>();
}
