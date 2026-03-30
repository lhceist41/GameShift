using System.Runtime.InteropServices;
using GameShift.Core.Config;
using GameShift.Core.System;
using Timer = System.Threading.Timer;

namespace GameShift.Core.BackgroundMode;

/// <summary>
/// Threshold-based standby list manager. Replaces the old timed purge with
/// condition-based purging: purge ONLY when BOTH conditions are true simultaneously:
///   1. Standby list size > StandbyListStandbyThresholdMB
///   2. Free physical memory &lt; StandbyListFreeMemoryMinMB
///
/// Thresholds auto-scale to total RAM if set to 0 in settings.
/// Optionally restricted to active gaming sessions only.
/// </summary>
public class StandbyListCleaner : IDisposable
{
    private Timer? _timer;
    private volatile bool _running;
    private volatile bool _gamingActive;

    // Active thresholds (set on Start/Reconfigure, in bytes)
    private long _standbyThresholdBytes;
    private long _freeMemoryMinBytes;
    private int _pollIntervalMs;
    private bool _onlyDuringGaming;

    public bool IsRunning => _running;

    /// <summary>
    /// Starts threshold-based standby list monitoring.
    /// Auto-scales thresholds from total RAM when settings values are 0.
    /// </summary>
    public void Start(BackgroundModeSettings settings)
    {
        if (_running) return;

        ApplySettings(settings);
        _running = true;
        _timer = new Timer(_ => CheckAndPurge(), null,
            TimeSpan.FromMilliseconds(_pollIntervalMs),
            TimeSpan.FromMilliseconds(_pollIntervalMs));

        var standbyMB = _standbyThresholdBytes / (1024 * 1024);
        var freeMB = _freeMemoryMinBytes / (1024 * 1024);
        SettingsManager.Logger.Information(
            "[StandbyListCleaner] Started — standby threshold: {StandbyMB}MB, free minimum: {FreeMB}MB, " +
            "poll: {PollMs}ms, gaming-only: {GamingOnly}",
            standbyMB, freeMB, _pollIntervalMs, _onlyDuringGaming);
    }

    /// <summary>
    /// Updates thresholds and poll interval without restarting (takes effect on next poll).
    /// If not running, this is a no-op.
    /// </summary>
    public void Reconfigure(BackgroundModeSettings settings)
    {
        if (!_running) return;

        Stop();
        Start(settings);
    }

    /// <summary>
    /// Stops periodic monitoring and disposes the timer.
    /// </summary>
    public void Stop()
    {
        _running = false;
        if (_timer != null)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _timer.Dispose();
            _timer = null;
        }
        SettingsManager.Logger.Information("[StandbyListCleaner] Stopped");
    }

    /// <summary>
    /// Notifies the cleaner that a gaming session has started or stopped.
    /// When OnlyDuringGaming is enabled, purging is suppressed outside active sessions.
    /// </summary>
    public void SetGamingActive(bool active)
    {
        _gamingActive = active;
    }

    /// <summary>
    /// Gets current available physical memory in MB.
    /// Exposed for dashboard display.
    /// </summary>
    public ulong GetAvailableMemoryMB()
    {
        var memInfo = new NativeInterop.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeInterop.MEMORYSTATUSEX>()
        };
        return NativeInterop.GlobalMemoryStatusEx(ref memInfo)
            ? memInfo.ullAvailPhys / (1024 * 1024)
            : 0;
    }

    /// <summary>
    /// Gets the current standby list size in MB via NtQuerySystemInformation.
    /// Returns 0 if the query fails (e.g. insufficient privileges).
    /// </summary>
    public ulong GetStandbyListMB()
    {
        var bytes = QueryStandbyListBytes();
        return bytes > 0 ? (ulong)(bytes / (1024 * 1024)) : 0;
    }

    /// <summary>
    /// Computes the auto-scaled defaults for this system's total RAM.
    /// Returns (standbyThresholdMB, freeMemoryMinMB).
    /// </summary>
    public static (int StandbyThresholdMB, int FreeMemoryMinMB) ComputeDefaults()
    {
        var memInfo = new NativeInterop.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeInterop.MEMORYSTATUSEX>()
        };

        if (!NativeInterop.GlobalMemoryStatusEx(ref memInfo))
            return (1024, 1024); // Fallback

        ulong totalGB = memInfo.ullTotalPhys / (1024UL * 1024 * 1024);

        if (totalGB <= 8)
            return (1024, 1024);
        if (totalGB <= 16)
            return (4096, 4096);
        if (totalGB <= 32)
            return (8192, 8192);

        return (16384, 16384); // 64GB+
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void ApplySettings(BackgroundModeSettings settings)
    {
        _onlyDuringGaming = settings.StandbyListOnlyDuringGaming;
        _pollIntervalMs = Math.Clamp(settings.StandbyListPollIntervalMs > 0
            ? settings.StandbyListPollIntervalMs : 1000, 500, 5000);

        var (autoStandby, autoFree) = ComputeDefaults();

        int standbyMB = settings.StandbyListStandbyThresholdMB > 0
            ? settings.StandbyListStandbyThresholdMB : autoStandby;
        int freeMB = settings.StandbyListFreeMemoryMinMB > 0
            ? settings.StandbyListFreeMemoryMinMB : autoFree;

        _standbyThresholdBytes = (long)standbyMB * 1024 * 1024;
        _freeMemoryMinBytes = (long)freeMB * 1024 * 1024;
    }

    private void CheckAndPurge()
    {
        if (!_running) return;

        // Skip when restricted to gaming sessions and none is active
        if (_onlyDuringGaming && !_gamingActive) return;

        try
        {
            long standbyBytes = QueryStandbyListBytes();
            long freeBytes = QueryFreeMemoryBytes();

            long standbyMB = standbyBytes / (1024 * 1024);
            long freeMB = freeBytes / (1024 * 1024);
            long standbyThreshMB = _standbyThresholdBytes / (1024 * 1024);
            long freeMinMB = _freeMemoryMinBytes / (1024 * 1024);

            bool standbyOverThreshold = standbyBytes > _standbyThresholdBytes;
            bool freeUnderMinimum = freeBytes < _freeMemoryMinBytes;

            if (!standbyOverThreshold)
            {
                SettingsManager.Logger.Debug(
                    "[StandbyListCleaner] Standby purge skipped (standby: {StandbyMB:F1}GB < threshold: {ThreshMB:F1}GB)",
                    standbyMB / 1024.0, standbyThreshMB / 1024.0);
                return;
            }

            if (!freeUnderMinimum)
            {
                SettingsManager.Logger.Debug(
                    "[StandbyListCleaner] Standby purge skipped (standby: {StandbyMB:F1}GB > threshold, " +
                    "but free: {FreeMB:F1}GB >= minimum: {FreeMinMB:F1}GB — not critical yet)",
                    standbyMB / 1024.0, freeMB / 1024.0, freeMinMB / 1024.0);
                return;
            }

            // Both conditions met — purge
            SettingsManager.Logger.Information(
                "[StandbyListCleaner] Standby purge triggered (standby: {StandbyMB:F1}GB > threshold: {ThreshMB:F1}GB, " +
                "free: {FreeMB:F1}GB < minimum: {FreeMinMB:F1}GB)",
                standbyMB / 1024.0, standbyThreshMB / 1024.0,
                freeMB / 1024.0, freeMinMB / 1024.0);

            PurgeStandbyList();
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[StandbyListCleaner] Error during memory check");
        }
    }

    /// <summary>
    /// Returns the total standby list size in bytes using NtQuerySystemInformation.
    /// Falls back to 0 on failure.
    /// </summary>
    private static long QueryStandbyListBytes()
    {
        int structSize = Marshal.SizeOf<NativeInterop.SYSTEM_MEMORY_LIST_INFORMATION>();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(structSize);
            int status = NativeInterop.NtQuerySystemInformation(
                NativeInterop.SystemMemoryListInformation,
                buffer,
                structSize,
                out _);

            if (status != 0)
            {
                SettingsManager.Logger.Debug(
                    "[StandbyListCleaner] NtQuerySystemInformation returned NTSTATUS 0x{Status:X8}", status);
                return 0;
            }

            var info = Marshal.PtrToStructure<NativeInterop.SYSTEM_MEMORY_LIST_INFORMATION>(buffer);

            ulong standbyPages = 0;
            if (info.PageCountByPriority != null)
            {
                foreach (var pages in info.PageCountByPriority)
                    standbyPages += pages;
            }

            return (long)(standbyPages * 4096UL);
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    private static long QueryFreeMemoryBytes()
    {
        var memInfo = new NativeInterop.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeInterop.MEMORYSTATUSEX>()
        };
        return NativeInterop.GlobalMemoryStatusEx(ref memInfo)
            ? (long)memInfo.ullAvailPhys
            : 0;
    }

    private static void PurgeStandbyList()
    {
        IntPtr bufferPtr = IntPtr.Zero;
        try
        {
            bufferPtr = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(bufferPtr, NativeInterop.MemoryPurgeStandbyList);
            int status = NativeInterop.NtSetSystemInformation(
                NativeInterop.SystemMemoryListInformation, bufferPtr, sizeof(int));

            if (status == 0)
                SettingsManager.Logger.Debug("[StandbyListCleaner] Purge succeeded");
            else
                SettingsManager.Logger.Warning("[StandbyListCleaner] Purge failed NTSTATUS: 0x{Status:X8}", status);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[StandbyListCleaner] Exception during purge");
        }
        finally
        {
            if (bufferPtr != IntPtr.Zero) Marshal.FreeHGlobal(bufferPtr);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
