using System.Runtime.InteropServices;

namespace GameShift.Core.System;

/// <summary>
/// Memory-related P/Invoke declarations for NativeInterop.
/// Partial class extension for standby list management and memory queries.
/// </summary>
internal static partial class NativeInterop
{
    // ============================================================
    // ntdll.dll - Memory Management
    // ============================================================

    /// <summary>
    /// Sets system information including memory list commands.
    /// Used to purge standby list for freeing cached memory.
    /// https://learn.microsoft.com/windows-hardware/drivers/ddi/ntddk/nf-ntddk-zwsetsysteminformation
    /// </summary>
    /// <param name="SystemInformationClass">Type of system information to set (e.g., SystemMemoryListInformation)</param>
    /// <param name="SystemInformation">Pointer to buffer containing command (e.g., MemoryPurgeStandbyList)</param>
    /// <param name="SystemInformationLength">Size of the buffer in bytes</param>
    /// <returns>NTSTATUS code (0 = STATUS_SUCCESS)</returns>
    [DllImport("ntdll.dll", SetLastError = true)]
    internal static extern int NtSetSystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformation,
        int SystemInformationLength);

    /// <summary>
    /// System information class for memory list operations.
    /// Used with NtSetSystemInformation to perform memory management commands.
    /// </summary>
    internal const int SystemMemoryListInformation = 0x0050; // 80 decimal

    /// <summary>
    /// Command to purge the standby list, freeing cached memory back to available pool.
    /// Passed as buffer content to NtSetSystemInformation with SystemMemoryListInformation.
    /// </summary>
    internal const int MemoryPurgeStandbyList = 4;

    // ============================================================
    // ntdll.dll - Memory List Query
    // ============================================================

    /// <summary>
    /// Queries system information. Used here with SystemMemoryListInformation (0x50)
    /// to retrieve exact standby list page counts by priority.
    /// https://learn.microsoft.com/windows-hardware/drivers/ddi/ntddk/nf-ntddk-zwquerysysteminformation
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformation,
        int SystemInformationLength,
        out int ReturnLength);

    /// <summary>
    /// Memory list information returned by NtQuerySystemInformation(SystemMemoryListInformation).
    /// Contains page counts for each memory list category.
    /// Total standby = sum of PageCountByPriority[0..7] * 4096 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_MEMORY_LIST_INFORMATION
    {
        public ulong ZeroPageCount;
        public ulong FreePageCount;
        public ulong ModifiedPageCount;
        public ulong ModifiedNoWritePageCount;
        public ulong BadPageCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public ulong[] PageCountByPriority; // Standby pages by priority 0-7
        public ulong RepurposedPageCount;
        public ulong ModifiedPageCountPageFile;
    }

    // ============================================================
    // kernel32.dll - Memory Status Query
    // ============================================================

    /// <summary>
    /// Retrieves information about the system's current usage of both physical and virtual memory.
    /// https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex
    /// </summary>
    /// <param name="lpBuffer">Pointer to MEMORYSTATUSEX structure to receive memory information</param>
    /// <returns>True if successful, false otherwise</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Contains information about the current state of both physical and virtual memory.
    /// Used with GlobalMemoryStatusEx to query available memory.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        /// <summary>Size of the structure in bytes. Must be set before calling GlobalMemoryStatusEx.</summary>
        public uint dwLength;

        /// <summary>Number between 0 and 100 specifying approximate percentage of physical memory in use.</summary>
        public uint dwMemoryLoad;

        /// <summary>Total size of physical memory in bytes.</summary>
        public ulong ullTotalPhys;

        /// <summary>Size of physical memory available in bytes.</summary>
        public ulong ullAvailPhys;

        /// <summary>Total size of the page file in bytes.</summary>
        public ulong ullTotalPageFile;

        /// <summary>Size of available space in the page file in bytes.</summary>
        public ulong ullAvailPageFile;

        /// <summary>Total size of the user mode portion of the virtual address space in bytes.</summary>
        public ulong ullTotalVirtual;

        /// <summary>Size of unreserved and uncommitted memory in the user mode portion of the virtual address space in bytes.</summary>
        public ulong ullAvailVirtual;

        /// <summary>Reserved, always zero.</summary>
        public ulong ullAvailExtendedVirtual;
    }
}
