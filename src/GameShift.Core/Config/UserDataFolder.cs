using System.Runtime.InteropServices;
using System.Text;
using GameShift.Core.System;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Core.Config;

/// <summary>
/// A per-user data folder (%AppData%\GameShift or its profiles folder), which any process of the
/// user can change while the elevated app runs. It is opened once as a handle on the folder itself
/// and only accepted when it lies below the user's profile folder on a local path and no folder
/// below the profile folder on the way to it is a junction or link. Links at or above the profile
/// folder are accepted: a standard user can neither create nor replace them. The handle is held
/// without delete sharing for the process lifetime, and every read, write and delete goes relative
/// to it, so nothing planted in the folder's path can redirect an elevated write.
/// </summary>
internal sealed class UserDataFolder : IDisposable
{
    private const uint ShareAll = NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE | NativeInterop.FILE_SHARE_DELETE;
    private const uint EntryOptions = NativeInterop.FILE_SYNCHRONOUS_IO_NONALERT | NativeInterop.FILE_OPEN_REPARSE_POINT;

    private static readonly object RegistryLock = new();
    private static readonly Dictionary<string, (UserDataFolder? Folder, string? Refusal)> Registry =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SafeFileHandle _handle;

    private UserDataFolder(SafeFileHandle handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>The folder's path; for listing names only, never for reading or writing.</summary>
    public string Path { get; }

    /// <summary>Names the temp file a write of <c>name</c> starts with; always a new name.</summary>
    internal Func<string, string> TempNameFor { get; set; } = name => $"{name}.{Guid.NewGuid():N}.tmp";

    /// <summary>Waits before replace attempt <c>n + 1</c>; 20, 40, 80 and 160 ms, 300 ms in all.</summary>
    internal Action<int> WaitBeforeReplaceRetry { get; set; } = attempt => Thread.Sleep(20 << (attempt - 1));

    internal static class TestHooks
    {
        /// <summary>Null in production; the test assembly sets it so its temp folders count as the user's profile.</summary>
        internal static string? AllowedRootOverride { get; set; }

        /// <summary>Closes and forgets every folder at or below <paramref name="path"/>, so a test can delete it.</summary>
        internal static void ReleaseAtOrBelow(string path)
        {
            var root = HandleRelative.LongPath(path);
            lock (RegistryLock)
            {
                foreach (var key in Registry.Keys.Where(k => HandleRelative.IsAtOrBelow(k, root)).ToList())
                {
                    Registry[key].Folder?.Dispose();
                    Registry.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// The folder at <paramref name="path"/>, opened (and created when missing) on first use and kept
    /// for the process lifetime. A refusal is kept as well: nothing is read from or written to that
    /// path for the rest of the run.
    /// </summary>
    public static UserDataFolder? Get(string path, out string? refusal)
    {
        var full = global::System.IO.Path.GetFullPath(path);
        var key = IsNetworkOrDevicePath(full) ? full : HandleRelative.LongPath(full);
        lock (RegistryLock)
        {
            if (!Registry.TryGetValue(key, out var entry))
            {
                var allowedRoot = TestHooks.AllowedRootOverride ??
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var folder = Open(key, allowedRoot, out var reason);
                entry = (folder, reason);
                Registry[key] = entry;
            }

            refusal = entry.Refusal;
            return entry.Folder;
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/>, creating it inside its parent when missing. Null with the reason
    /// when it is a network path, when it does not lie below <paramref name="allowedRoot"/>, when it or
    /// a folder between it and <paramref name="allowedRoot"/> is a junction or link, or when it cannot
    /// be opened.
    /// </summary>
    internal static UserDataFolder? Open(string path, string allowedRoot, out string? refusal)
    {
        refusal = null;
        try
        {
            // Checked on the string alone: a loopback admin share such as \\127.0.0.1\C$ would
            // reach system folders with the elevated token, so not even a lookup goes there.
            if (IsNetworkOrDevicePath(global::System.IO.Path.GetFullPath(path)) ||
                IsNetworkOrDevicePath(global::System.IO.Path.GetFullPath(allowedRoot)))
            {
                refusal = "it is on a network path";
                return null;
            }

            var expected = HandleRelative.LongPath(path);
            var root = HandleRelative.LongPath(allowedRoot);
            if (!HandleRelative.IsAtOrBelow(expected, root) || SamePath(expected, root))
            {
                refusal = "it is not below the user's profile folder";
                return null;
            }

            var parentPath = global::System.IO.Path.GetDirectoryName(expected)!;
            var name = global::System.IO.Path.GetFileName(expected);

            // The profile folder may itself be reached through a link (a moved C:\Users, say); only
            // what lies below it must be plain folders, so paths are compared below its final path.
            var rootFinal = FinalPathOf(root);
            if (rootFinal == null)
            {
                refusal = $"the user's profile folder could not be opened (error {Marshal.GetLastWin32Error()})";
                return null;
            }

            bool parentIsRoot = SamePath(parentPath, root);

            // The parent is opened only to create the folder inside it: a junction above the folder
            // would otherwise let even that create land somewhere else.
            using var parent = NativeInterop.CreateFile(
                parentPath, NativeInterop.FILE_TRAVERSE | NativeInterop.FILE_READ_ATTRIBUTES | NativeInterop.SYNCHRONIZE,
                NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE, IntPtr.Zero, NativeInterop.OPEN_EXISTING,
                NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | (parentIsRoot ? 0 : NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT),
                IntPtr.Zero);
            if (parent.IsInvalid)
            {
                refusal = $"its parent folder could not be opened (error {Marshal.GetLastWin32Error()})";
                return null;
            }

            if (!NativeInterop.GetFileInformationByHandle(parent, out var parentInfo) ||
                (!parentIsRoot && (parentInfo.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0) ||
                !SamePath(HandleRelative.FinalPath(parent), BelowRoot(rootFinal, root, parentPath)))
            {
                refusal = "a folder above it is a junction or link";
                return null;
            }

            // No delete sharing: the folder cannot be renamed or replaced while the handle is open.
            var handle = HandleRelative.Open(parent, name,
                NativeInterop.FILE_TRAVERSE | NativeInterop.FILE_LIST_DIRECTORY | NativeInterop.FILE_READ_ATTRIBUTES |
                NativeInterop.SYNCHRONIZE,
                NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE, NativeInterop.FILE_OPEN_IF,
                NativeInterop.FILE_DIRECTORY_FILE | EntryOptions, out var status);
            if (handle == null)
            {
                refusal = $"it could not be opened (status 0x{status:X8})";
                return null;
            }

            if (!NativeInterop.GetFileInformationByHandle(handle, out var info))
                refusal = $"its attributes could not be read (error {Marshal.GetLastWin32Error()})";
            else if ((info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                refusal = "it is a junction or link";
            else if (!SamePath(HandleRelative.FinalPath(handle), BelowRoot(rootFinal, root, expected)))
                refusal = "a folder above it is a junction or link";
            else
                return new UserDataFolder(handle, expected);

            handle.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            refusal = $"it could not be opened: {ex.Message}";
            return null;
        }
    }

    /// <summary>The final path of a folder, following every link on the way; null when it cannot be opened.</summary>
    private static string? FinalPathOf(string folder)
    {
        using var handle = NativeInterop.CreateFile(
            folder, NativeInterop.FILE_READ_ATTRIBUTES,
            ShareAll, IntPtr.Zero, NativeInterop.OPEN_EXISTING, NativeInterop.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        return handle.IsInvalid ? null : HandleRelative.FinalPath(handle);
    }

    /// <summary><paramref name="path"/>, which lies at or below <paramref name="root"/>, with the root replaced by its final path.</summary>
    private static string BelowRoot(string rootFinal, string root, string path)
    {
        var relative = global::System.IO.Path.GetRelativePath(root, path);
        return relative == "." ? rootFinal : global::System.IO.Path.Join(rootFinal, relative);
    }

    /// <summary>A UNC path (\\server\share) or a device path (\\?\, \\.\), told apart from a drive path without any I/O.</summary>
    private static bool IsNetworkOrDevicePath(string fullPath) => fullPath.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool SamePath(string? actual, string expected) =>
        actual != null && string.Equals(
            global::System.IO.Path.TrimEndingDirectorySeparator(actual),
            global::System.IO.Path.TrimEndingDirectorySeparator(expected),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The subfolder <paramref name="name"/>, opened and verified the same way; see <see cref="Get"/>.</summary>
    public UserDataFolder? GetChild(string name, out string? refusal) =>
        Get(global::System.IO.Path.Combine(Path, name), out refusal);

    /// <summary>Whether a file by that name exists, without following a link.</summary>
    public bool FileExists(string name)
    {
        using var file = HandleRelative.Open(_handle, name, NativeInterop.FILE_READ_ATTRIBUTES | NativeInterop.SYNCHRONIZE,
            ShareAll, NativeInterop.FILE_OPEN, EntryOptions, out _);
        return file != null && NativeInterop.GetFileInformationByHandle(file, out var info) &&
               (info.FileAttributes & (NativeInterop.FILE_ATTRIBUTE_DIRECTORY | NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT)) == 0;
    }

    /// <summary>
    /// Reads a text file the way <see cref="File.ReadAllText(string)"/> does; null when there is
    /// none by that name. Throws for a link or a folder by that name.
    /// </summary>
    public string? ReadAllText(string name)
    {
        using var file = HandleRelative.Open(_handle, name, NativeInterop.GENERIC_READ | NativeInterop.SYNCHRONIZE,
            ShareAll, NativeInterop.FILE_OPEN, EntryOptions, out var status);
        if (file == null)
        {
            return status == NativeInterop.STATUS_OBJECT_NAME_NOT_FOUND
                ? null
                : throw new IOException($"Could not open {name} (status 0x{status:X8})");
        }

        if (!NativeInterop.GetFileInformationByHandle(file, out var info))
            throw new IOException($"Could not read the attributes of {name} (error {Marshal.GetLastWin32Error()})");
        if ((info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            throw new IOException($"{name} is a link; it was not read");
        if ((info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_DIRECTORY) != 0)
            throw new IOException($"{name} is a folder");

        using var stream = new FileStream(file, FileAccess.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes <paramref name="name"/> atomically as UTF-8: a temp file with a new name is created
    /// (never opened if something already has that name), filled, then renamed over
    /// <paramref name="name"/>. Throws, leaving no temp file, when the file cannot be written or
    /// renamed, or when <paramref name="name"/> is a link or a file with more than one link, since
    /// replacing it would remove a name for a file GameShift does not own.
    /// </summary>
    public void WriteAllText(string name, string contents)
    {
        RefuseUnlessPlainFile(name, "replaced");

        var tempName = TempNameFor(name);
        using var file = HandleRelative.Open(_handle, tempName,
            NativeInterop.GENERIC_WRITE | NativeInterop.DELETE | NativeInterop.SYNCHRONIZE, shareAccess: 0,
            NativeInterop.FILE_CREATE, NativeInterop.FILE_NON_DIRECTORY_FILE | EntryOptions, out var status)
            ?? throw new IOException($"Could not create {tempName} (status 0x{status:X8})");

        try
        {
            RandomAccess.Write(file, Utf8NoBom.GetBytes(contents), 0);
            status = HandleRelative.Replace(file, _handle, name, WaitBeforeReplaceRetry);
            if (status != 0)
                throw new IOException($"Could not rename {tempName} to {name} (status 0x{status:X8})");
        }
        catch
        {
            // Through the handle this write created, so only that new file can go.
            HandleRelative.MarkForDeletion(file);
            throw;
        }
    }

    /// <summary>
    /// Deletes the file <paramref name="name"/>. False when there is none; throws, deleting
    /// nothing, when it is a link or a file with more than one link.
    /// </summary>
    public bool DeleteFile(string name)
    {
        using var file = HandleRelative.Open(_handle, name,
            NativeInterop.DELETE | NativeInterop.FILE_READ_ATTRIBUTES | NativeInterop.SYNCHRONIZE, ShareAll,
            NativeInterop.FILE_OPEN, EntryOptions, out var status);
        if (file == null)
        {
            return status == NativeInterop.STATUS_OBJECT_NAME_NOT_FOUND
                ? false
                : throw new IOException($"Could not open {name} (status 0x{status:X8})");
        }

        RefuseUnlessPlainFile(file, name, "deleted");
        status = HandleRelative.MarkForDeletion(file);
        return status == 0 ? true : throw new IOException($"Could not delete {name} (status 0x{status:X8})");
    }

    private void RefuseUnlessPlainFile(string name, string action)
    {
        using var file = HandleRelative.Open(_handle, name, NativeInterop.FILE_READ_ATTRIBUTES | NativeInterop.SYNCHRONIZE,
            ShareAll, NativeInterop.FILE_OPEN, EntryOptions, out var status);
        if (file != null)
            RefuseUnlessPlainFile(file, name, action);
        else if (status != NativeInterop.STATUS_OBJECT_NAME_NOT_FOUND)
            throw new IOException($"Could not open {name} (status 0x{status:X8})");
    }

    private static void RefuseUnlessPlainFile(SafeFileHandle file, string name, string action)
    {
        if (!NativeInterop.GetFileInformationByHandle(file, out var info))
            throw new IOException($"Could not read the attributes of {name} (error {Marshal.GetLastWin32Error()})");
        if ((info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            throw new IOException($"{name} is a link; it was not {action}");
        if (info.NumberOfLinks != 1)
            throw new IOException($"{name} has {info.NumberOfLinks} links; it was not {action}");
    }

    public void Dispose() => _handle.Dispose();
}
