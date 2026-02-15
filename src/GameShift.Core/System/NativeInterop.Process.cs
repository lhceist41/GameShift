using System.Runtime.InteropServices;

namespace GameShift.Core.System;

/// <summary>
/// Process-related P/Invoke declarations for NativeInterop.
/// Partial class extension for process suspension and resumption.
/// </summary>
internal static partial class NativeInterop
{
    // ============================================================
    // ntdll.dll - Process Suspension/Resumption
    // ============================================================

    /// <summary>
    /// Suspends all threads in a process.
    /// Used by CompetitiveMode to suspend overlay processes during gaming.
    /// </summary>
    [DllImport("ntdll.dll", SetLastError = true)]
    internal static extern int NtSuspendProcess(IntPtr processHandle);

    /// <summary>
    /// Resumes all threads in a previously suspended process.
    /// Used by CompetitiveMode to restore overlay processes after gaming.
    /// </summary>
    [DllImport("ntdll.dll", SetLastError = true)]
    internal static extern int NtResumeProcess(IntPtr processHandle);

    // ============================================================
    // kernel32.dll - Process Handle Management
    // ============================================================

    /// <summary>
    /// Opens an existing local process object for suspension/resumption.
    /// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocess
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        int dwProcessId);

    /// <summary>
    /// Closes an open object handle.
    /// https://learn.microsoft.com/windows/win32/api/handleapi/nf-handleapi-closehandle
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Process access right for suspend/resume operations.
    /// PROCESS_SUSPEND_RESUME = 0x0800
    /// </summary>
    internal const uint PROCESS_SUSPEND_RESUME = 0x0800;

    /// <summary>
    /// Process access right for querying information.
    /// PROCESS_QUERY_INFORMATION = 0x0400
    /// </summary>
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
}
