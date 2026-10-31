using System.Runtime.InteropServices;
using System.Text;
using GameShift.Core.Journal;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Core.System;

/// <summary>
/// File operations relative to an open folder handle, so a junction or link put in place of the
/// folder's path after it was opened cannot redirect them.
/// </summary>
internal static class HandleRelative
{
    private const int ReplaceAttempts = 5;

    /// <summary>Opens or creates <paramref name="name"/> in <paramref name="folder"/>; null with the NTSTATUS when it fails.</summary>
    public static SafeFileHandle? Open(
        SafeFileHandle folder, string name, uint access, uint shareAccess, uint disposition, uint options, out int status)
    {
        if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || name is "." or "..")
            throw new ArgumentException("A plain file name is required", nameof(name));

        var buffer = Marshal.StringToHGlobalUni(name);
        var objectName = Marshal.AllocHGlobal(Marshal.SizeOf<NativeInterop.UNICODE_STRING>());
        bool addedRef = false;
        try
        {
            folder.DangerousAddRef(ref addedRef);
            Marshal.StructureToPtr(new NativeInterop.UNICODE_STRING
            {
                Length = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)((name.Length + 1) * sizeof(char)),
                Buffer = buffer,
            }, objectName, fDeleteOld: false);

            var attributes = new NativeInterop.OBJECT_ATTRIBUTES
            {
                Length = (uint)Marshal.SizeOf<NativeInterop.OBJECT_ATTRIBUTES>(),
                RootDirectory = folder.DangerousGetHandle(),
                ObjectName = objectName,
                Attributes = NativeInterop.OBJ_CASE_INSENSITIVE,
            };

            status = NativeInterop.NtCreateFile(
                out var file, access, ref attributes, out _, IntPtr.Zero, NativeInterop.FILE_ATTRIBUTE_NORMAL,
                shareAccess, disposition, options, IntPtr.Zero, 0);
            if (status == 0)
                return file;

            file.Dispose();
            return null;
        }
        finally
        {
            if (addedRef)
                folder.DangerousRelease();
            Marshal.FreeHGlobal(objectName);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Renames <paramref name="file"/> (opened with DELETE) over <paramref name="name"/> in
    /// <paramref name="folder"/>. Returns the NTSTATUS, 0 on success.
    /// </summary>
    public static int Replace(SafeFileHandle file, SafeFileHandle folder, string name, Action<int> waitBeforeRetry)
    {
        for (int attempt = 1; ; attempt++)
        {
            var status = WindowsJournalSecurity.ReplaceRelative(file, folder, name);
            // A handle without delete sharing, typically a scanner or backup tool, blocks even the
            // POSIX replace but is usually closed again within milliseconds.
            if (attempt == ReplaceAttempts ||
                status is not (NativeInterop.STATUS_SHARING_VIOLATION or NativeInterop.STATUS_ACCESS_DENIED))
                return status;
            waitBeforeRetry(attempt);
        }
    }

    /// <summary>Marks an open file (opened with DELETE) for deletion; it goes when the last handle closes.</summary>
    public static int MarkForDeletion(SafeFileHandle file) =>
        NativeInterop.NtSetInformationFile(file, out _, new byte[] { 1 }, 1, NativeInterop.FileDispositionInformation);

    /// <summary>
    /// The path the handle actually names, with every junction and link resolved, as a plain drive
    /// path (<c>C:\...</c>) or UNC path (<c>\\server\share\...</c>); null when it cannot be read.
    /// </summary>
    public static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        uint length = NativeInterop.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = NativeInterop.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }

        if (length == 0 || length >= buffer.Capacity)
            return null;

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>
    /// <paramref name="path"/> made absolute with each existing component in its long form, so it
    /// compares equal to <see cref="FinalPath"/> for the same folder when no link is involved.
    /// Components that do not exist yet are kept as given.
    /// </summary>
    public static string LongPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var buffer = new StringBuilder(512);
        uint length = NativeInterop.GetLongPathName(full, buffer, (uint)buffer.Capacity);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = NativeInterop.GetLongPathName(full, buffer, (uint)buffer.Capacity);
        }

        if (length != 0 && length < buffer.Capacity)
            return Path.TrimEndingDirectorySeparator(buffer.ToString());

        var parent = Path.GetDirectoryName(full);
        return parent == null ? full : Path.Combine(LongPath(parent), Path.GetFileName(full));
    }

    /// <summary>Whether <paramref name="path"/> equals <paramref name="root"/> or lies below it.</summary>
    public static bool IsAtOrBelow(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
