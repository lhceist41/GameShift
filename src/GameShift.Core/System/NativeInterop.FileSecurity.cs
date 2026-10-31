using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Core.System;

/// <summary>
/// Partial class extension for reading and setting file-system security through handles, so a
/// reparse point is never followed.
/// </summary>
public static partial class NativeInterop
{
    internal const uint READ_CONTROL = 0x00020000;
    internal const uint WRITE_DAC = 0x00040000;
    internal const uint WRITE_OWNER = 0x00080000;
    internal const uint FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint DELETE = 0x00010000;
    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    internal const uint FILE_TRAVERSE = 0x0020;
    internal const int FileRenameInformation = 10;

    internal const uint FILE_SHARE_READ = 0x1;
    internal const uint FILE_SHARE_WRITE = 0x2;
    internal const uint FILE_SHARE_DELETE = 0x4;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    internal const int SE_FILE_OBJECT = 1;
    internal const uint OWNER_SECURITY_INFORMATION = 0x1;
    internal const uint DACL_SECURITY_INFORMATION = 0x4;

    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public global::System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public global::System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public global::System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>TOKEN_PRIVILEGES with room for exactly one privilege.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES_ONE
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    internal static extern IntPtr GetCurrentProcessHandle();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    internal static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION information);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_STATUS_BLOCK
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    /// <summary>
    /// Used for renames with a root directory handle; the Win32 rename call resolves a name without
    /// a root against the process's current directory.
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtSetInformationFile(
        SafeFileHandle file, out IO_STATUS_BLOCK ioStatus, byte[] information, uint length, int informationClass);

    [DllImport("advapi32.dll")]
    internal static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    internal static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>
    /// Sets security on an open handle without propagating inheritable entries to children, unlike
    /// SetSecurityInfo.
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint securityInfo, byte[] securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES_ONE newState,
        uint bufferLength,
        out TOKEN_PRIVILEGES_ONE previousState,
        out uint returnLength);
}
