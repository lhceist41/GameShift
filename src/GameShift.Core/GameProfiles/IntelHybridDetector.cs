using System.Runtime.InteropServices;
using GameShift.Core.Config;

namespace GameShift.Core.GameProfiles;

/// <summary>
/// Detects Intel hybrid CPU architecture (P-cores vs E-cores) and calculates
/// P-core-only affinity masks. Fails gracefully on AMD/older Intel.
/// </summary>
public static class IntelHybridDetector
{
    private static bool? _isHybrid;
    private static int _pCoreCount;
    private static long _pCoreAffinityMask;

    /// <summary>Whether the CPU has a hybrid architecture (P-cores + E-cores).</summary>
    public static bool IsHybridCpu
    {
        get
        {
            if (_isHybrid == null) Detect();
            return _isHybrid ?? false;
        }
    }

    /// <summary>Number of P-cores detected.</summary>
    public static int PCoreCount
    {
        get
        {
            if (_isHybrid == null) Detect();
            return _pCoreCount;
        }
    }

    /// <summary>
    /// Affinity mask covering only P-core logical processors.
    /// Returns 0 if not a hybrid CPU (caller should use all cores).
    /// </summary>
    public static long PCoreAffinityMask
    {
        get
        {
            if (_isHybrid == null) Detect();
            return _pCoreAffinityMask;
        }
    }

    /// <summary>
    /// Gets the appropriate affinity mask for a game profile.
    /// If IntelHybridPCoreOnly is true and CPU is hybrid, returns P-core mask.
    /// Otherwise returns the profile's explicit AffinityMask, or 0 (all cores).
    /// </summary>
    public static long GetAffinityMask(GameSessionConfig profile)
    {
        if (profile.IntelHybridPCoreOnly && IsHybridCpu && _pCoreAffinityMask != 0)
            return _pCoreAffinityMask;

        return profile.AffinityMask ?? 0;
    }

    private static void Detect()
    {
        try
        {
            // Check for hybrid architecture via GetSystemCpuSetInformation
            // Intel 12th+ gen: P-cores have EfficiencyClass=0, E-cores have EfficiencyClass=1
            var processorInfo = GetLogicalProcessorInformation();
            if (processorInfo != null)
            {
                int pCoreLogical = 0;
                int eCoreLogical = 0;
                long mask = 0;

                foreach (var info in processorInfo)
                {
                    if (info.EfficiencyClass == 0)
                    {
                        pCoreLogical += info.LogicalProcessorCount;
                        mask |= info.AffinityMask;
                    }
                    else
                    {
                        eCoreLogical += info.LogicalProcessorCount;
                    }
                }

                if (eCoreLogical > 0 && pCoreLogical > 0)
                {
                    _isHybrid = true;
                    _pCoreCount = pCoreLogical / 2; // P-cores are hyperthreaded, so logical/2 = physical
                    _pCoreAffinityMask = mask;
                    SettingsManager.Logger.Information(
                        "[IntelHybrid] Hybrid CPU detected: {PCores} P-cores ({PLogical} logical), {ECores} E-core logical processors. P-core mask: 0x{Mask:X}",
                        _pCoreCount, pCoreLogical, eCoreLogical, _pCoreAffinityMask);
                    return;
                }
            }

            // Not hybrid
            _isHybrid = false;
            _pCoreCount = 0;
            _pCoreAffinityMask = 0;
            SettingsManager.Logger.Debug("[IntelHybrid] Not a hybrid CPU architecture");
        }
        catch (Exception ex)
        {
            _isHybrid = false;
            _pCoreCount = 0;
            _pCoreAffinityMask = 0;
            SettingsManager.Logger.Warning(ex, "[IntelHybrid] Detection failed, assuming non-hybrid");
        }
    }

    // -- P/Invoke for GetSystemCpuSetInformation --

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information, int bufferLength, out int returnedLength,
        IntPtr process, int flags);

    private record struct CpuSetInfo(int EfficiencyClass, int LogicalProcessorCount, long AffinityMask);

    private static List<CpuSetInfo>? GetLogicalProcessorInformation()
    {
        try
        {
            // First call to get buffer size
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out int bufferSize, IntPtr.Zero, 0);
            if (bufferSize == 0) return null;

            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, bufferSize, out _, IntPtr.Zero, 0))
                    return null;

                var results = new Dictionary<int, CpuSetInfo>(); // group by EfficiencyClass
                int offset = 0;
                while (offset < bufferSize)
                {
                    // SYSTEM_CPU_SET_INFORMATION structure:
                    // Size (DWORD) at offset 0
                    // Type (CPU_SET_INFORMATION_TYPE) at offset 4
                    // Id at offset 8
                    // Group at offset 12
                    // LogicalProcessorIndex at offset 14
                    // CoreIndex at offset 15
                    // LastLevelCacheIndex at offset 16
                    // NumaNodeIndex at offset 17
                    // EfficiencyClass at offset 18
                    var size = Marshal.ReadInt32(buffer, offset);
                    if (size == 0) break;

                    var efficiencyClass = Marshal.ReadByte(buffer, offset + 18);
                    var logicalIndex = Marshal.ReadByte(buffer, offset + 14);

                    if (!results.ContainsKey(efficiencyClass))
                        results[efficiencyClass] = new CpuSetInfo(efficiencyClass, 0, 0);

                    var existing = results[efficiencyClass];
                    results[efficiencyClass] = existing with
                    {
                        LogicalProcessorCount = existing.LogicalProcessorCount + 1,
                        AffinityMask = existing.AffinityMask | (1L << logicalIndex)
                    };

                    offset += size;
                }

                return results.Values.ToList();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }
}
