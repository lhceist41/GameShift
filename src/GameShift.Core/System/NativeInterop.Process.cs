using System.Runtime.InteropServices;

namespace GameShift.Core.System;

/// <summary>
/// Process-related P/Invoke declarations for NativeInterop.
/// Partial class extension for process suspension and resumption.
/// </summary>
public static partial class NativeInterop
{
    // ============================================================
    // ntdll.dll - Process Suspension/Resumption
    // ============================================================

    /// <summary>
    /// Suspends all threads in a process.
    /// Used by CompetitiveMode to suspend overlay processes during gaming.
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtSuspendProcess(IntPtr processHandle);

    /// <summary>
    /// Resumes all threads in a previously suspended process.
    /// Used by CompetitiveMode to restore overlay processes after gaming.
    /// </summary>
    [DllImport("ntdll.dll")]
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

    /// <summary>
    /// Retrieves the full image path of a running process. Requires only
    /// PROCESS_QUERY_LIMITED_INFORMATION, unlike Process.MainModule which needs
    /// PROCESS_QUERY_INFORMATION|PROCESS_VM_READ and is refused on protected processes.
    /// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-queryfullprocessimagenamew
    /// </summary>
    /// <param name="hProcess">Process handle opened with PROCESS_QUERY_LIMITED_INFORMATION.</param>
    /// <param name="dwFlags">0 = Win32 path format.</param>
    /// <param name="lpExeName">Receives the path; must hold at least <paramref name="lpdwSize"/> characters.</param>
    /// <param name="lpdwSize">In: buffer size in characters. Out: characters written (excluding the null).</param>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);

    /// <summary>
    /// Waits until the specified object is signaled or the time-out elapses.
    /// A process handle becomes signaled when the process terminates, so a zero-timeout wait is a
    /// liveness check for the exact process object the handle refers to (immune to PID reuse).
    /// https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    /// <summary>
    /// Access right required to wait on a process handle.
    /// SYNCHRONIZE = 0x00100000
    /// </summary>
    internal const uint SYNCHRONIZE = 0x00100000;

    /// <summary>
    /// Wait result: the object is signaled. For a process handle this means the process has
    /// terminated. (WAIT_ABANDONED is documented for abandoned mutexes and is not a process-handle
    /// result, so it is deliberately not special-cased here.)
    /// </summary>
    internal const uint WAIT_OBJECT_0 = 0x00000000;

    /// <summary>
    /// Wait result: the time-out elapsed while the object was still nonsignaled. For a process
    /// handle this means the process was still running at that observation point.
    /// </summary>
    internal const uint WAIT_TIMEOUT = 0x00000102;

    /// <summary>
    /// Wait result: the wait failed; call GetLastError for the reason. Treated as "unknown".
    /// </summary>
    internal const uint WAIT_FAILED = 0xFFFFFFFF;

    /// <summary>
    /// Win32 error returned by OpenProcess when no process with the requested ID exists.
    /// ERROR_INVALID_PARAMETER = 87
    /// </summary>
    internal const int ERROR_INVALID_PARAMETER = 87;
}
