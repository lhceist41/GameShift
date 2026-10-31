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
    internal const int FileRenameInformationEx = 65;
    internal const uint FILE_RENAME_REPLACE_IF_EXISTS = 0x1;
    internal const uint FILE_RENAME_POSIX_SEMANTICS = 0x2;
    internal const int FileDispositionInformation = 13;
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;

    // NtCreateFile dispositions, options and object attributes.
    internal const uint FILE_OPEN = 1;
    internal const uint FILE_CREATE = 2;
    internal const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x20;
    internal const uint FILE_NON_DIRECTORY_FILE = 0x40;
    internal const uint FILE_DELETE_ON_CLOSE = 0x1000;
    internal const uint FILE_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint OBJ_CASE_INSENSITIVE = 0x40;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    internal const int STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034);
    internal const int STATUS_INVALID_INFO_CLASS = unchecked((int)0xC0000003);
    internal const int STATUS_INVALID_PARAMETER = unchecked((int)0xC000000D);
    internal const int STATUS_NOT_SUPPORTED = unchecked((int)0xC00000BB);
    internal const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    internal const int STATUS_SHARING_VIOLATION = unchecked((int)0xC0000043);

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OBJECT_ATTRIBUTES
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    /// <summary>Opens or creates a name relative to a directory handle, which the Win32 calls cannot.</summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtCreateFile(
        out SafeFileHandle file,
        uint desiredAccess,
        ref OBJECT_ATTRIBUTES objectAttributes,
        out IO_STATUS_BLOCK ioStatus,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateDirectoryW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateDirectory(string pathName, ref SECURITY_ATTRIBUTES securityAttributes);

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
