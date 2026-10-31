using System.Runtime.InteropServices;
using GameShift.Core.Journal;
using GameShift.Core.System;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Core.Config;

/// <summary>
/// The folder the elevated app writes its logs to by path (the Serilog file sink and its retention,
/// the DPC CSV, the diagnostic and crash logs). Path writes are safe only where no standard user can
/// create, rename or link anything, so the folder and its parent must both be admin-only: created
/// with the hardened owner and DACL, or already owned and writable only by SYSTEM and Administrators.
/// Both are held open without delete sharing for the process lifetime.
/// </summary>
internal sealed class LogFolder : IDisposable
{
    private readonly SafeFileHandle _parent;
    private readonly SafeFileHandle _folder;

    private LogFolder(SafeFileHandle parent, SafeFileHandle folder, string path)
    {
        _parent = parent;
        _folder = folder;
        Path = path;
    }

    public string Path { get; }

    /// <summary>
    /// Prepares <paramref name="path"/>: a missing folder or parent is created with
    /// <paramref name="ownerSid"/> as owner and the hardened DACL in one call. An existing one is
    /// never changed; it is refused when it is a junction or link, when a folder above it is, or
    /// when it is not owned and writable only by SYSTEM, Administrators or <paramref name="ownerSid"/>.
    /// </summary>
    internal static LogFolder? Prepare(string path, string ownerSid, out string? refusal)
    {
        SafeFileHandle? parent = null;
        try
        {
            var expected = HandleRelative.LongPath(path);
            var parentPath = global::System.IO.Path.GetDirectoryName(expected);
            if (parentPath == null)
            {
                refusal = "it has no parent folder";
                return null;
            }

            parent = OpenAdminOnly(parentPath, ownerSid, out refusal);
            if (parent == null)
            {
                refusal = $"{parentPath}: {refusal}";
                return null;
            }

            // The parent is admin-only and held open, so nothing can be swapped in below it now.
            var folder = OpenAdminOnly(expected, ownerSid, out refusal);
            if (folder == null)
            {
                parent.Dispose();
                return null;
            }

            return new LogFolder(parent, folder, expected);
        }
        catch (Exception ex)
        {
            parent?.Dispose();
            refusal = $"preparing it failed: {ex.Message}";
            return null;
        }
    }

    private static SafeFileHandle? OpenAdminOnly(string path, string ownerSid, out string? refusal)
    {
        refusal = null;
        bool exists;
        try
        {
            File.GetAttributes(path); // the entry itself, not what a reparse point names
            exists = true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            exists = false;
        }

        if (!exists && !WindowsJournalSecurity.TryCreateHardenedFolder(path, ownerSid, out refusal))
            return null;

        // No delete sharing: the folder cannot be renamed or replaced while the handle is open.
        var handle = NativeInterop.CreateFile(
            path,
            NativeInterop.FILE_TRAVERSE | NativeInterop.FILE_LIST_DIRECTORY | NativeInterop.FILE_READ_ATTRIBUTES |
            NativeInterop.READ_CONTROL | NativeInterop.SYNCHRONIZE,
            NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE, IntPtr.Zero, NativeInterop.OPEN_EXISTING,
            NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            refusal = $"it could not be opened (error {Marshal.GetLastWin32Error()})";
            handle.Dispose();
            return null;
        }

        var security = WindowsJournalSecurity.Describe(handle);
        var finalPath = HandleRelative.FinalPath(handle);
        if (security.IsReparsePoint)
            refusal = "it is a junction or link";
        else if (finalPath == null || !string.Equals(finalPath, path, StringComparison.OrdinalIgnoreCase))
            refusal = "a folder above it is a junction or link";
        else if (JournalTrust.EvaluateFolder(security, ownerSid) is { Count: > 0 } reasons)
            refusal = "it is not admin-only: " + string.Join("; ", reasons);
        else
            return handle;

        handle.Dispose();
        return null;
    }

    public void Dispose()
    {
        _folder.Dispose();
        _parent.Dispose();
    }
}
