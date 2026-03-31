using System.Runtime.InteropServices;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Describes a single logical processor for the core map UI.
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
/// Snapshot of current reservation state returned by <see cref="CoreIsolationManager.GetStatus"/>.
/// </summary>
public class CoreIsolationStatus
{
    public bool IsHybridCpu { get; init; }
    public bool IsReservationActive { get; init; }
    public IReadOnlyList<CpuCoreInfo> AllCores { get; init; } = Array.Empty<CpuCoreInfo>();
    public IReadOnlyList<CpuCoreInfo> PCores { get; init; } = Array.Empty<CpuCoreInfo>();
    public IReadOnlyList<CpuCoreInfo> ECores { get; init; } = Array.Empty<CpuCoreInfo>();
    public HashSet<uint> ReservedCpuSetIds { get; init; } = new();
    public int PCoreTotalCount { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Manages OS-level CPU core reservation via
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\ReservedCpuSets</c>.
///
/// Reserved cores are invisible to all processes unless explicitly assigned via
/// <see cref="NativeInterop.SetProcessDefaultCpuSetMasks"/>. This is more powerful than
/// soft affinity because it's OS-enforced.
///
/// <para>Requires reboot to take effect. Revert = delete the registry value.</para>
///
/// <para>Safety rails:</para>
/// <list type="bullet">
///   <item>Non-hybrid CPUs: feature disabled entirely</item>
///   <item>Minimum 1 P-core must remain unreserved</item>
///   <item>E-cores cannot be reserved</item>
///   <item>Warning when fewer than 4 P-cores</item>
///   <item>Validated before write: reserved count &lt; total P-cores</item>
/// </list>
/// </summary>
public class CoreIsolationManager
{
    private readonly ILogger _logger = SettingsManager.Logger;

    private const string KernelKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string ValueName = "ReservedCpuSets";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads CPU topology and current reservation status.
    /// Returns a status object suitable for the UI core map.
    /// </summary>
    public CoreIsolationStatus GetStatus()
    {
        var (pCores, eCores, isHybrid) = DetectTopology();
        var reserved = ReadCurrentReservation();

        if (!isHybrid)
        {
            return new CoreIsolationStatus
            {
                IsHybridCpu = false,
                Message = "Core isolation is only available on hybrid CPUs (Intel 12th-15th gen, AMD Zen 5c)."
            };
        }

        return new CoreIsolationStatus
        {
            IsHybridCpu = true,
            IsReservationActive = reserved.Count > 0,
            AllCores = pCores.Concat(eCores).ToList(),
            PCores = pCores,
            ECores = eCores,
            ReservedCpuSetIds = reserved,
            PCoreTotalCount = pCores.Count,
            Message = reserved.Count > 0
                ? $"{reserved.Count} core(s) reserved for gaming. Reboot required for changes to take effect."
                : "No cores reserved. Select P-cores to isolate for gaming."
        };
    }

    /// <summary>
    /// Writes the ReservedCpuSets registry value for the given CPU Set IDs.
    /// Returns a validation error string, or null on success.
    /// </summary>
    public string? ApplyReservation(IReadOnlyList<uint> cpuSetIdsToReserve)
    {
        var (pCores, _, isHybrid) = DetectTopology();

        if (!isHybrid)
            return "Core isolation requires a hybrid CPU.";

        if (cpuSetIdsToReserve.Count == 0)
            return "No cores selected.";

        // Safety: validate all selected IDs are P-cores
        var pCoreIds = new HashSet<uint>(pCores.Select(c => c.CpuSetId));
        foreach (var id in cpuSetIdsToReserve)
        {
            if (!pCoreIds.Contains(id))
                return $"CPU Set ID {id} is not a P-core. Only P-cores can be reserved.";
        }

        // Safety: at least 1 P-core must remain unreserved
        if (cpuSetIdsToReserve.Count >= pCores.Count)
            return $"Cannot reserve all {pCores.Count} P-cores. At least 1 must remain for OS/background work.";

        // Build bitmask
        byte[] bitmask = BuildBitmask(cpuSetIdsToReserve);

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KernelKeyPath, writable: true);
            if (key == null)
                return "Cannot open kernel registry key for writing.";

            key.SetValue(ValueName, bitmask, RegistryValueKind.Binary);

            _logger.Information(
                "[CoreIsolation] Reserved {Count} P-cores (IDs: {Ids}) — reboot required",
                cpuSetIdsToReserve.Count,
                string.Join(", ", cpuSetIdsToReserve));

            // Record in journal
            try
            {
                var journal = new JournalManager();
                journal.LoadJournal();
                journal.RecordPendingRebootFix(
                    $"Core Isolation: {cpuSetIdsToReserve.Count} P-cores reserved for gaming");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[CoreIsolation] Failed to record pending reboot fix");
            }

            return null; // Success
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CoreIsolation] Failed to write ReservedCpuSets");
            return $"Registry write failed: {ex.Message}";
        }
    }

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
            _logger.Information("[CoreIsolation] Removed ReservedCpuSets — reboot required");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CoreIsolation] Failed to remove ReservedCpuSets");
            return $"Registry delete failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns the default suggested reservation: all P-cores except the last one
    /// (leave one P-core unreserved for OS work).
    /// </summary>
    public IReadOnlyList<uint> GetDefaultSuggestion()
    {
        var (pCores, _, isHybrid) = DetectTopology();
        if (!isHybrid || pCores.Count < 2) return Array.Empty<uint>();

        // Reserve all P-cores except the last one
        return pCores
            .Take(pCores.Count - 1)
            .Select(c => c.CpuSetId)
            .ToList();
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

            for (int byteIdx = 0; byteIdx < bitmask.Length; byteIdx++)
            {
                byte b = bitmask[byteIdx];
                if (b == 0) continue;

                for (int bit = 0; bit < 8; bit++)
                {
                    if ((b & (1 << bit)) != 0)
                        reserved.Add((uint)(byteIdx * 8 + bit));
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

    /// <summary>
    /// Builds a byte-array bitmask where bit N is set if CPU Set ID N is in the list.
    /// </summary>
    private static byte[] BuildBitmask(IReadOnlyList<uint> cpuSetIds)
    {
        if (cpuSetIds.Count == 0) return Array.Empty<byte>();

        uint maxId = cpuSetIds.Max();
        int byteCount = (int)(maxId / 8) + 1;
        var bitmask = new byte[byteCount];

        foreach (var id in cpuSetIds)
            bitmask[id / 8] |= (byte)(1 << (int)(id % 8));

        return bitmask;
    }
}
