using System.Runtime.InteropServices;

namespace GameShift.Core.System;

/// <summary>
/// Process information P/Invoke declarations for NativeInterop.
/// Partial class extension for I/O priority, Efficiency Mode, and memory priority management.
/// Used by IoPriorityManager, EfficiencyModeController, and MemoryOptimizer.
/// </summary>
internal static partial class NativeInterop
{
    // ============================================================
    // ntdll.dll - Process I/O Priority
    // ============================================================

    /// <summary>
    /// Sets information for a process.
    /// Used to set I/O priority via ProcessIoPriority information class.
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtSetInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref int processInformation,
        int processInformationLength);

    /// <summary>
    /// Queries information about a process.
    /// Used to query I/O priority via ProcessIoPriority information class.
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref int processInformation,
        int processInformationLength,
        out int returnLength);

    /// <summary>ProcessInformationClass for I/O priority queries and sets.</summary>
    internal const int ProcessIoPriority = 33;

    /// <summary>I/O priority: Very Low — idle-priority background work.</summary>
    internal const int IoPriorityVeryLow = 0;

    /// <summary>I/O priority: Low — background tasks.</summary>
    internal const int IoPriorityLow = 1;

    /// <summary>I/O priority: Normal — default for all processes.</summary>
    internal const int IoPriorityNormal = 2;

    // ============================================================
    // kernel32.dll - Process Power Throttling (Efficiency Mode)
    // ============================================================

    /// <summary>
    /// Sets information for a specified process.
    /// Used for Efficiency Mode (ProcessPowerThrottling) and Memory Priority.
    /// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        IntPtr ProcessInformation,
        int ProcessInformationSize);

    /// <summary>
    /// Retrieves information about a specified process.
    /// Used to query Efficiency Mode state and Memory Priority.
    /// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessinformation
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        IntPtr ProcessInformation,
        int ProcessInformationSize);

    /// <summary>ProcessInformationClass for memory priority.</summary>
    internal const int ProcessMemoryPriority = 1;

    /// <summary>ProcessInformationClass for power throttling (Efficiency Mode).</summary>
    internal const int ProcessPowerThrottling = 4;

    /// <summary>Current version of the PROCESS_POWER_THROTTLING_STATE structure.</summary>
    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;

    /// <summary>Flag for execution speed throttling (EcoQoS / Efficiency Mode).</summary>
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    /// <summary>
    /// Structure for power throttling (Efficiency Mode) state.
    /// Used with SetProcessInformation/GetProcessInformation and ProcessPowerThrottling class.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    /// <summary>
    /// Structure for memory priority information.
    /// Used with SetProcessInformation/GetProcessInformation and ProcessMemoryPriority class.
    /// Memory priority levels: 0 (Lowest) to 5 (Normal, default).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    // ============================================================
    // Process Access Rights (additional)
    // ============================================================

    /// <summary>
    /// Combined access rights for setting process information.
    /// PROCESS_SET_INFORMATION = 0x0200
    /// </summary>
    internal const uint PROCESS_SET_INFORMATION = 0x0200;

    /// <summary>
    /// Process access right for limited queries.
    /// PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    /// </summary>
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// Memory flush modified list command for NtSetSystemInformation.
    /// Flushes dirty pages from the modified page list to disk.
    /// </summary>
    internal const int MemoryFlushModifiedList = 3;

    // ============================================================
    // kernel32.dll - CPU Set APIs (Hybrid CPU support)
    // ============================================================

    /// <summary>
    /// Retrieves CPU Set information for the system or a specific process.
    /// Returns variable-length array of SYSTEM_CPU_SET_INFORMATION structures.
    /// First call with null buffer to get required size, then allocate and call again.
    /// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemcpusetinformation
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);

    /// <summary>
    /// Sets the default CPU Set IDs for threads in a process.
    /// Threads will only run on the specified CPU Sets unless overridden per-thread.
    /// Pass cpuSetIdCount=0 and cpuSetIds=null to clear all restrictions.
    /// Thread Director-aware — works correctly with Intel hybrid CPUs.
    /// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessdefaultcpusets
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDefaultCpuSets(
        IntPtr process,
        [In] uint[]? cpuSetIds,
        uint cpuSetIdCount);

    /// <summary>
    /// SYSTEM_CPU_SET_INFORMATION structure returned by GetSystemCpuSetInformation.
    /// Contains CPU Set ID, efficiency class, core index, and cache topology for each logical processor.
    /// Layout follows the Windows SDK definition with union/struct nesting flattened.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct SYSTEM_CPU_SET_INFORMATION
    {
        /// <summary>Size of this structure element (varies — use for offset walking).</summary>
        [FieldOffset(0)]
        public uint Size;

        /// <summary>Type of information (0 = CpuSetInformation).</summary>
        [FieldOffset(4)]
        public int Type;

        // ── CpuSet union fields (when Type == 0) ──

        /// <summary>Unique CPU Set ID — pass to SetProcessDefaultCpuSets.</summary>
        [FieldOffset(8)]
        public uint Id;

        /// <summary>Processor group.</summary>
        [FieldOffset(12)]
        public ushort Group;

        /// <summary>Logical processor index within the group.</summary>
        [FieldOffset(14)]
        public byte LogicalProcessorIndex;

        /// <summary>Physical core index.</summary>
        [FieldOffset(15)]
        public byte CoreIndex;

        /// <summary>Last-level cache index (used for CCD grouping on AMD).</summary>
        [FieldOffset(16)]
        public byte LastLevelCacheIndex;

        /// <summary>NUMA node index.</summary>
        [FieldOffset(17)]
        public byte NumaNodeIndex;

        /// <summary>
        /// Efficiency class: 0 = P-core, 1 = E-core, 2 = LP-E-core (Panther Lake+).
        /// On homogeneous CPUs all cores report 0.
        /// </summary>
        [FieldOffset(18)]
        public byte EfficiencyClass;

        // Remaining bytes (AllFlags, SchedulingClass, etc.) up to Size are unused by GameShift.
    }
}
