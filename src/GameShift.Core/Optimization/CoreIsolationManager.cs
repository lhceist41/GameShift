using System.Runtime.InteropServices;
using GameShift.Core.Config;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Describes a single logical processor.
/// </summary>
public class CpuCoreInfo
{
    public uint CpuSetId { get; init; }
    public byte LogicalProcessorIndex { get; init; }
    public ushort Group { get; init; }
    public byte CoreIndex { get; init; }
    public bool IsPCore { get; init; }

    /// <summary>Display label: P0, P1, ..., E0, E1, ...</summary>
    public string Label { get; init; } = "";
}

/// <summary>
/// Reads and removes an OS-level CPU core reservation in
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\ReservedCpuSets</c>.
///
/// Earlier versions could set one from DPC Doctor; GameShift no longer does. The value does not
/// record who wrote it, so removing it is always the user's explicit choice. A change needs a
/// reboot.
/// </summary>
public class CoreIsolationManager
{
    private readonly ILogger _logger = SettingsManager.Logger;

    private const string KernelKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string ValueName = "ReservedCpuSets";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the ReservedCpuSets registry value, restoring default Windows scheduling
    /// on next reboot.
    /// </summary>
    public string? RemoveReservation()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KernelKeyPath, writable: true);
            if (key == null)
                return "Cannot open kernel registry key.";

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            _logger.Information("[CoreIsolation] Removed ReservedCpuSets - reboot required");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CoreIsolation] Failed to remove ReservedCpuSets");
            return $"Registry delete failed: {ex.Message}";
        }
    }

    /// <summary>
    /// True when a ReservedCpuSets value reserves at least one processor, whoever wrote it.
    /// Reads the raw mask, so it does not depend on the topology lookup.
    /// </summary>
    public static bool IsReservationPresent()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KernelKeyPath);
        return key?.GetValue(ValueName) is byte[] mask && mask.Any(b => b != 0);
    }

    /// <summary>
    /// Reads the current ReservedCpuSets bitmask from the registry.
    /// Returns the set of reserved CPU Set IDs. Empty if no reservation is active.
    /// Used by <see cref="CpuSchedulingOptimizer"/> to detect reserved cores.
    /// </summary>
    public static HashSet<uint> ReadCurrentReservation()
    {
        var reserved = new HashSet<uint>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KernelKeyPath);
            if (key?.GetValue(ValueName) is not byte[] bitmask)
                return reserved;

            // Bits are indexed by logical processor number; translate back to CPU Set IDs so
            // callers (CpuSchedulingOptimizer, GetStatus) keep working in CPU-Set-ID space.
            var (pCores, eCores, _) = DetectTopology();
            var logicalToId = new Dictionary<int, uint>();
            foreach (var c in pCores.Concat(eCores))
                logicalToId[c.LogicalProcessorIndex] = c.CpuSetId;

            for (int byteIdx = 0; byteIdx < bitmask.Length; byteIdx++)
            {
                byte b = bitmask[byteIdx];
                if (b == 0) continue;

                for (int bit = 0; bit < 8; bit++)
                {
                    if ((b & (1 << bit)) == 0) continue;
                    int logical = byteIdx * 8 + bit;
                    if (logicalToId.TryGetValue(logical, out var id))
                        reserved.Add(id);
                }
            }
        }
        catch { /* No reservation or can't read */ }

        return reserved;
    }

    /// <summary>
    /// Cleans up an orphaned ReservedCpuSets key. Called by boot recovery when
    /// GameShift is not installed but the reservation persists.
    /// </summary>
    public static void CleanupOrphanedReservation(ILogger logger)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KernelKeyPath);
            if (key?.GetValue(ValueName) == null) return;

            using var writeKey = Registry.LocalMachine.OpenSubKey(KernelKeyPath, writable: true);
            writeKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            logger.Information("[CoreIsolation] Cleaned up orphaned ReservedCpuSets");
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[CoreIsolation] Failed to clean up orphaned ReservedCpuSets");
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static (List<CpuCoreInfo> pCores, List<CpuCoreInfo> eCores, bool isHybrid) DetectTopology()
    {
        var pCores = new List<CpuCoreInfo>();
        var eCores = new List<CpuCoreInfo>();
        var allClasses = new HashSet<byte>();

        NativeInterop.GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero, 0);
        if (requiredSize == 0) return (pCores, eCores, false);

        var buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            if (!NativeInterop.GetSystemCpuSetInformation(buffer, requiredSize, out _, IntPtr.Zero, 0))
                return (pCores, eCores, false);

            int pIdx = 0, eIdx = 0, offset = 0;
            while (offset < (int)requiredSize)
            {
                var info = Marshal.PtrToStructure<NativeInterop.SYSTEM_CPU_SET_INFORMATION>(buffer + offset);

                if (info.Type == 0)
                {
                    allClasses.Add(info.EfficiencyClass);
                    bool isP = info.EfficiencyClass == 0;

                    var core = new CpuCoreInfo
                    {
                        CpuSetId = info.Id,
                        LogicalProcessorIndex = info.LogicalProcessorIndex,
                        Group = info.Group,
                        CoreIndex = info.CoreIndex,
                        IsPCore = isP,
                        Label = isP ? $"P{pIdx++}" : $"E{eIdx++}"
                    };

                    if (isP) pCores.Add(core);
                    else eCores.Add(core);
                }

                offset += (int)info.Size;
                if (info.Size == 0) break;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        return (pCores, eCores, allClasses.Count > 1);
    }
}
