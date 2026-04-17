using System.Runtime.InteropServices;

namespace GameShift.Core.System;

/// <summary>
/// Centralized P/Invoke declarations for Windows API calls.
/// All platform invoke declarations should be placed here to maintain a single source of truth.
/// </summary>
internal static partial class NativeInterop
{
    // ============================================================
    // advapi32.dll - Security and Token Management
    // ============================================================

    /// <summary>
    /// Opens the access token associated with a process.
    /// https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocesstoken
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(
        IntPtr ProcessHandle,
        uint DesiredAccess,
        out IntPtr TokenHandle);

    /// <summary>
    /// Retrieves information about a specified access token.
    /// https://learn.microsoft.com/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        TOKEN_INFORMATION_CLASS TokenInformationClass,
        IntPtr TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);

    /// <summary>
    /// Token information classes for GetTokenInformation.
    /// https://learn.microsoft.com/windows/win32/api/winnt/ne-winnt-token_information_class
    /// </summary>
    internal enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20
    }

    /// <summary>
    /// Access rights for OpenProcessToken.
    /// https://learn.microsoft.com/windows/win32/secauthz/access-rights-for-access-token-objects
    /// </summary>
    internal const uint TOKEN_QUERY = 0x0008;

    /// <summary>
    /// Structure for TokenElevation information.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    // ============================================================
    // powrprof.dll - Power Management
    // ============================================================
    // Note: PowerGetActiveScheme is declared in SystemStateSnapshot.cs
    // to avoid duplicate P/Invoke declarations. PowerPlanSwitcher reads
    // the current plan from SystemStateSnapshot.OriginalPowerPlan.

    /// <summary>
    /// Sets the active power scheme for the current user.
    /// https://learn.microsoft.com/windows/win32/api/powrprof/nf-powrprof-powersetactivescheme
    /// </summary>
    /// <param name="UserRootPowerKey">Reserved, must be IntPtr.Zero</param>
    /// <param name="SchemeGuid">GUID of the power scheme to activate</param>
    /// <returns>ERROR_SUCCESS (0) on success, error code otherwise</returns>
    [DllImport("powrprof.dll")]
    internal static extern uint PowerSetActiveScheme(
        IntPtr UserRootPowerKey,
        ref Guid SchemeGuid);

    // ============================================================
    // ntdll.dll - NT Native API
    // ============================================================

    /// <summary>
    /// Sets the system timer resolution.
    /// https://learn.microsoft.com/windows/win32/api/ntdef/nf-ntdef-ntsettimerresolution
    /// </summary>
    /// <param name="DesiredResolution">Desired timer resolution in 100-nanosecond units</param>
    /// <param name="SetResolution">True to request resolution, false to release</param>
    /// <param name="CurrentResolution">Receives the actual resolution set</param>
    /// <returns>NTSTATUS code (0 = STATUS_SUCCESS)</returns>
    [DllImport("ntdll.dll")]
    internal static extern int NtSetTimerResolution(
        int DesiredResolution,
        bool SetResolution,
        out int CurrentResolution);

    /// <summary>
    /// Queries the system timer resolution capabilities.
    /// https://learn.microsoft.com/windows/win32/api/ntdef/nf-ntdef-ntquerytimerresolution
    /// </summary>
    /// <param name="MinimumResolution">Receives the minimum (coarsest) resolution</param>
    /// <param name="MaximumResolution">Receives the maximum (finest) resolution</param>
    /// <param name="CurrentResolution">Receives the current resolution</param>
    /// <returns>NTSTATUS code (0 = STATUS_SUCCESS)</returns>
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryTimerResolution(
        out int MinimumResolution,
        out int MaximumResolution,
        out int CurrentResolution);

    // ============================================================
    // kernel32.dll - CPU Set Information (added by Plan 03-02 if needed)
    // ============================================================

    // ============================================================
    // Safe executable path resolution
    // ============================================================

    /// <summary>
    /// Returns the absolute path to a Windows system executable (e.g., powercfg.exe).
    /// Prevents PATH hijacking when running as administrator.
    /// Accepts subdirectories (e.g., "WindowsPowerShell\v1.0\powershell.exe").
    /// </summary>
    internal static string SystemExePath(string exeName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName);
}
