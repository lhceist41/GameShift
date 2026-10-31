using System.Runtime.InteropServices;

namespace GameShift.Core.System;

public static partial class NativeInterop
{
    // ── LSA account rights ───────────────────────────────────────────────────
    // https://learn.microsoft.com/windows/win32/secmgmt/managing-account-permissions

    internal const uint POLICY_CREATE_ACCOUNT = 0x00000010;
    internal const uint POLICY_LOOKUP_NAMES = 0x00000800;

    // NTSTATUS; STATUS_ACCESS_DENIED and STATUS_OBJECT_NAME_NOT_FOUND are shared with the file APIs.
    internal const int STATUS_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LSA_OBJECT_ATTRIBUTES
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>
    /// Opens the local LSA policy.
    /// https://learn.microsoft.com/windows/win32/api/ntsecapi/nf-ntsecapi-lsaopenpolicy
    /// </summary>
    [DllImport("advapi32.dll")]
    internal static extern int LsaOpenPolicy(
        IntPtr systemName,
        ref LSA_OBJECT_ATTRIBUTES objectAttributes,
        uint desiredAccess,
        out IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    internal static extern int LsaClose(IntPtr objectHandle);

    [DllImport("advapi32.dll")]
    internal static extern int LsaFreeMemory(IntPtr buffer);

    [DllImport("advapi32.dll")]
    internal static extern uint LsaNtStatusToWinError(int status);

    /// <summary>
    /// Lists the rights assigned directly to an account (not through its groups).
    /// https://learn.microsoft.com/windows/win32/api/ntsecapi/nf-ntsecapi-lsaenumerateaccountrights
    /// </summary>
    [DllImport("advapi32.dll")]
    internal static extern int LsaEnumerateAccountRights(
        IntPtr policyHandle,
        byte[] accountSid,
        out IntPtr userRights,
        out uint countOfRights);

    /// <summary>
    /// https://learn.microsoft.com/windows/win32/api/ntsecapi/nf-ntsecapi-lsaaddaccountrights
    /// </summary>
    [DllImport("advapi32.dll")]
    internal static extern int LsaAddAccountRights(
        IntPtr policyHandle,
        byte[] accountSid,
        LSA_UNICODE_STRING[] userRights,
        uint countOfRights);

    /// <summary>
    /// https://learn.microsoft.com/windows/win32/api/ntsecapi/nf-ntsecapi-lsaremoveaccountrights
    /// </summary>
    [DllImport("advapi32.dll")]
    internal static extern int LsaRemoveAccountRights(
        IntPtr policyHandle,
        byte[] accountSid,
        [MarshalAs(UnmanagedType.U1)] bool allRights,
        LSA_UNICODE_STRING[] userRights,
        uint countOfRights);
}
