using System.Runtime.InteropServices;
using GameShift.Core.Config;
using GameShift.Core.System;
using Timer = System.Threading.Timer;

namespace GameShift.Core.BackgroundMode;

/// <summary>
/// Always-on ISLC replacement. Periodically checks available RAM and purges the
/// Windows standby list when free memory drops below the configured threshold.
/// Runs 24/7 independently of gaming sessions.
/// </summary>
public class StandbyListCleaner : IDisposable
{
    private Timer? _timer;
    private volatile bool _running;
    private int _thresholdMB;
    private int _pollSeconds;

    public bool IsRunning => _running;

    /// <summary>
    /// Starts periodic standby list monitoring.
    /// </summary>
    public void Start(BackgroundModeSettings settings)
    {
        if (_running) return;

        _thresholdMB = settings.StandbyListThresholdMB;
        _pollSeconds = settings.StandbyListPollSeconds;

        _running = true;
        _timer = new Timer(_ => CheckAndPurge(), null,
            TimeSpan.FromSeconds(_pollSeconds),
            TimeSpan.FromSeconds(_pollSeconds));

        SettingsManager.Logger.Information(
            "[StandbyListCleaner] Started with {ThresholdMB}MB threshold, {PollSeconds}s interval",
            _thresholdMB, _pollSeconds);
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

    private void CheckAndPurge()
    {
        if (!_running) return;

        try
        {
            ulong availableMB = GetAvailableMemoryMB();
            if (availableMB < (ulong)_thresholdMB)
            {
                SettingsManager.Logger.Information(
                    "[StandbyListCleaner] Available {AvailableMB}MB < threshold {ThresholdMB}MB, purging",
                    availableMB, _thresholdMB);
                PurgeStandbyList();
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[StandbyListCleaner] Error during memory check");
        }
    }

    private void PurgeStandbyList()
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
