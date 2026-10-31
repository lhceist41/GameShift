using System.Runtime.InteropServices;
using GameShift.Core.System;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Core.Journal;

/// <summary>What preparing the shared journal folder left.</summary>
internal enum JournalFolderSetup
{
    /// <summary>A plain folder with the hardened DACL.</summary>
    Ready,

    /// <summary>A plain folder whose DACL could not be hardened this time; written to, retried later.</summary>
    NotHardened,

    /// <summary>Not a plain folder GameShift made or could harden; nothing is written there this run.</summary>
    Unavailable,
}

/// <summary>
/// The journal folder, opened as a handle on the folder itself and verified to be a plain folder,
/// never through a reparse point. Files are read, created and renamed relative to this handle, and
/// the folder cannot be renamed or replaced while it is open, so a junction put in its place cannot
/// redirect a write.
/// </summary>
internal sealed class JournalFolder : IDisposable
{
    private const uint ShareAll = NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE | NativeInterop.FILE_SHARE_DELETE;
    private const uint FileOptions =
        NativeInterop.FILE_NON_DIRECTORY_FILE | NativeInterop.FILE_SYNCHRONOUS_IO_NONALERT | NativeInterop.FILE_OPEN_REPARSE_POINT;

    private readonly SafeFileHandle _handle;

    /// <summary>Waits before replace attempt <c>n + 1</c>; 20, 40, 80 and 160 ms, 300 ms in all.</summary>
    internal Action<int> WaitBeforeReplaceRetry { get; set; } = attempt => Thread.Sleep(20 << (attempt - 1));

    private JournalFolder(SafeFileHandle handle) => _handle = handle;

    /// <summary>Opens <paramref name="folder"/>; null with the reason when it is not a plain folder or cannot be opened.</summary>
    public static JournalFolder? Open(string folder, out string? refusal)
    {
        refusal = null;
        // No delete sharing: the folder cannot be renamed or replaced while the handle is open.
        var handle = NativeInterop.CreateFile(
            folder,
            NativeInterop.FILE_TRAVERSE | NativeInterop.FILE_LIST_DIRECTORY | NativeInterop.FILE_READ_ATTRIBUTES | NativeInterop.SYNCHRONIZE,
            NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING,
            NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
            refusal = $"could not open it (error {Marshal.GetLastWin32Error()})";
        else if (!NativeInterop.GetFileInformationByHandle(handle, out var info))
            refusal = $"could not read its attributes (error {Marshal.GetLastWin32Error()})";
        else if ((info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            refusal = "it is a reparse point";
        else if ((info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_DIRECTORY) == 0)
            refusal = "it is not a folder";
        else
            return new JournalFolder(handle);

        handle.Dispose();
        return null;
    }

    /// <summary>Reads a file in the folder; null when there is none by that name.</summary>
    public byte[]? ReadFile(string name)
    {
        using var file = OpenRelative(name, NativeInterop.GENERIC_READ | NativeInterop.SYNCHRONIZE, ShareAll,
            NativeInterop.FILE_OPEN, extraOptions: 0, out var status);
        if (file == null)
        {
            return status == NativeInterop.STATUS_OBJECT_NAME_NOT_FOUND
                ? null
                : throw new IOException($"Could not open {name} (status 0x{status:X8})");
        }

        var content = new byte[RandomAccess.GetLength(file)];
        int read = 0;
        while (read < content.Length)
        {
            int n = RandomAccess.Read(file, content.AsSpan(read), read);
            if (n == 0)
                break;
            read += n;
        }

        return read == content.Length ? content : content[..read];
    }

    /// <summary>
    /// Writes <paramref name="name"/> atomically: <c>name.tmp</c> is created as a new file, filled,
    /// given Administrators as owner, then renamed over <paramref name="name"/>, even while another
    /// process reads it. Throws, leaving no temp file, when the file cannot be written or renamed.
    /// </summary>
    /// <returns>Whether Administrators could be made the owner.</returns>
    public bool WriteFileAtomically(string name, ReadOnlySpan<byte> content)
    {
        var tempName = name + ".tmp";

        // A leftover temp file may be a hard link or a link someone planted; removing that name
        // leaves whatever it pointed at alone, and the file below is then created fresh.
        using (OpenRelative(tempName, NativeInterop.DELETE | NativeInterop.SYNCHRONIZE, ShareAll, NativeInterop.FILE_OPEN,
                   NativeInterop.FILE_DELETE_ON_CLOSE, out _))
        {
        }

        using var file = OpenRelative(tempName,
            NativeInterop.GENERIC_WRITE | NativeInterop.DELETE | NativeInterop.WRITE_OWNER | NativeInterop.SYNCHRONIZE,
            shareAccess: 0, NativeInterop.FILE_CREATE, extraOptions: 0, out var status)
            ?? throw new IOException($"Could not create {tempName} (status 0x{status:X8})");

        RandomAccess.Write(file, content, 0);
        // A new file gets the token's default owner, which policy can make the admin user; the trust
        // check accepts only Administrators. On failure the write still completes and the next start
        // reports the journal as not trusted.
        bool ownerSet = WindowsJournalSecurity.TrySetAdministratorsOwner(file);

        status = Replace(file, name);
        if (status != 0)
        {
            NativeInterop.NtSetInformationFile(file, out _, new byte[] { 1 }, 1, NativeInterop.FileDispositionInformation);
            throw new IOException($"Could not rename {tempName} to {name} (status 0x{status:X8})");
        }

        return ownerSet;
    }

    private int Replace(SafeFileHandle file, string name) =>
        HandleRelative.Replace(file, _handle, name, WaitBeforeReplaceRetry);

    private SafeFileHandle? OpenRelative(
        string name, uint access, uint shareAccess, uint disposition, uint extraOptions, out int status) =>
        HandleRelative.Open(_handle, name, access, shareAccess, disposition, FileOptions | extraOptions, out status);

    public void Dispose() => _handle.Dispose();
}
