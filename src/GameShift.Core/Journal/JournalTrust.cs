using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using GameShift.Core.System;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace GameShift.Core.Journal;

public enum JournalAceType
{
    Allow,
    Deny,

    /// <summary>Any other entry type; judged as if it allowed its rights.</summary>
    Other
}

/// <param name="Sid">The entry's SID in string form, or null when the entry has none GameShift can read.</param>
public sealed record JournalAce(string? Sid, JournalAceType Type, int AccessMask, bool InheritOnly);

/// <param name="Dacl">Null when the object has no DACL, which grants everyone full access.</param>
public sealed record JournalObjectSecurity(
    string? OwnerSid,
    IReadOnlyList<JournalAce>? Dacl,
    bool DaclProtected,
    bool IsDirectory,
    bool IsReparsePoint,
    int LinkCount);

public sealed record JournalTrustDescriptor(JournalObjectSecurity Folder, JournalObjectSecurity File);

/// <param name="Reasons">Why the journal is not trusted, for the log; empty when trusted.</param>
public sealed record JournalTrustResult(bool Trusted, IReadOnlyList<string> Reasons)
{
    public static JournalTrustResult Untrusted(string reason) => new(false, new[] { reason });
}

/// <summary>
/// Decides whether the session journal may be replayed elevated. The journal is trusted only when
/// no standard user could have written it: the folder and the file are owned by SYSTEM or
/// Administrators, no entry in either DACL grants a write, append, delete, WRITE_DAC or WRITE_OWNER
/// right to anyone else, neither is a reparse point, and the file has exactly one link. A hash or
/// key stored beside the file would prove nothing, so this check is the trust.
/// </summary>
public static class JournalTrust
{
    internal const string SystemSid = "S-1-5-18";
    internal const string AdministratorsSid = "S-1-5-32-544";
    internal const string UsersSid = "S-1-5-32-545";
    private const string CreatorOwnerSid = "S-1-3-0";
    private const string OwnerRightsSid = "S-1-3-4";

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint GenericExecute = 0x20000000;
    private const uint GenericAll = 0x10000000;
    private const uint MaximumAllowed = 0x02000000;
    private const uint FileGenericRead = 0x120089;
    private const uint FileGenericWrite = 0x120116;
    private const uint FileGenericExecute = 0x1200A0;
    internal const uint FileAllAccess = 0x1F01FF;
    internal const uint FileReadAndExecute = 0x1200A9;

    // FILE_WRITE_DATA (add file), FILE_APPEND_DATA (add subfolder), FILE_WRITE_EA, FILE_DELETE_CHILD,
    // FILE_WRITE_ATTRIBUTES, DELETE, WRITE_DAC, WRITE_OWNER.
    private const uint WriteRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000;

    /// <summary>Reads the journal's folder and file security and evaluates it. Never throws; anything unreadable is untrusted.</summary>
    public static JournalTrustResult Check(string journalPath)
    {
        try
        {
            return Evaluate(WindowsJournalSecurity.Read(journalPath));
        }
        catch (Exception ex)
        {
            return JournalTrustResult.Untrusted($"could not read the journal's security: {ex.Message}");
        }
    }

    public static JournalTrustResult Evaluate(JournalTrustDescriptor descriptor)
    {
        var reasons = new List<string>();
        // An inheritable CREATOR OWNER or OWNER RIGHTS entry on the folder applies to whoever owns
        // a child, so it resolves to the file's owner as well as the folder's.
        EvaluateObject("folder", descriptor.Folder, expectDirectory: true,
            new[] { descriptor.Folder.OwnerSid, descriptor.File.OwnerSid }, alsoTrustedSid: null, reasons);
        EvaluateObject("file", descriptor.File, expectDirectory: false,
            new[] { descriptor.File.OwnerSid }, alsoTrustedSid: null, reasons);
        return new JournalTrustResult(reasons.Count == 0, reasons);
    }

    /// <summary>
    /// Why a folder is not admin-only by the same rules, with <paramref name="ownerSid"/> (the owner
    /// GameShift creates such folders with) trusted besides SYSTEM and Administrators. Empty when it is.
    /// </summary>
    internal static IReadOnlyList<string> EvaluateFolder(JournalObjectSecurity folder, string ownerSid)
    {
        var reasons = new List<string>();
        EvaluateObject("folder", folder, expectDirectory: true, new[] { folder.OwnerSid }, ownerSid, reasons);
        return reasons;
    }

    private static void EvaluateObject(
        string what, JournalObjectSecurity security, bool expectDirectory, string?[] owners, string? alsoTrustedSid,
        List<string> reasons)
    {
        if (security.IsReparsePoint)
            reasons.Add($"the {what} is a reparse point");
        if (security.IsDirectory != expectDirectory)
            reasons.Add($"the {what} is {(security.IsDirectory ? "a directory" : "not a directory")}");
        if (!expectDirectory && security.LinkCount != 1)
            reasons.Add($"the {what} has {security.LinkCount} links");
        if (!IsTrustedSid(security.OwnerSid, alsoTrustedSid))
            reasons.Add($"the {what} is owned by {security.OwnerSid ?? "nobody"}");

        if (security.Dacl == null)
        {
            reasons.Add($"the {what} has no DACL");
            return;
        }

        foreach (var ace in security.Dacl)
        {
            if (ace.Type == JournalAceType.Deny)
                continue;

            var rights = MapGenericRights(unchecked((uint)ace.AccessMask)) & WriteRights;
            if (rights == 0)
                continue;

            foreach (var principal in ResolvePrincipals(ace.Sid, owners))
            {
                if (!IsTrustedSid(principal, alsoTrustedSid))
                {
                    var resolved = principal == ace.Sid ? string.Empty : $" (resolved to {principal ?? "nobody"})";
                    reasons.Add($"the {what} grants write rights 0x{rights:X} to {ace.Sid ?? "an unreadable entry"}{resolved}");
                }
            }
        }
    }

    private static IEnumerable<string?> ResolvePrincipals(string? sid, string?[] owners) =>
        sid is CreatorOwnerSid or OwnerRightsSid ? owners : new[] { sid };

    private static bool IsTrustedSid(string? sid, string? alsoTrustedSid) =>
        sid is SystemSid or AdministratorsSid || (sid != null && sid == alsoTrustedSid);

    private static uint MapGenericRights(uint mask)
    {
        var mapped = mask & ~(GenericRead | GenericWrite | GenericExecute | GenericAll | MaximumAllowed);
        if ((mask & GenericRead) != 0) mapped |= FileGenericRead;
        if ((mask & GenericWrite) != 0) mapped |= FileGenericWrite;
        if ((mask & GenericExecute) != 0) mapped |= FileGenericExecute;
        if ((mask & (GenericAll | MaximumAllowed)) != 0) mapped |= FileAllAccess;
        return mapped;
    }

    /// <summary>
    /// Makes the journal folder owned by Administrators, with the hardened DACL (SYSTEM and
    /// Administrators full control, Users read and execute). Only for a folder the trust check
    /// refused. Entries in the folder keep their owner: a file a standard user planted, even one
    /// created while the repair runs, stays theirs and keeps failing the owner rule.
    /// </summary>
    /// <returns>True when the folder was repaired.</returns>
    public static bool Repair(string folder, ILogger logger) => Repair(folder, WindowsJournalSecurity.Instance, logger);

    internal static bool Repair(string folder, IJournalSecurityStore store, ILogger logger)
    {
        try
        {
            using var privileges = store.EnablePrivileges();
            using var root = store.Open(folder);
            if (root == null)
            {
                logger.Warning("[JournalTrust] Could not open {Folder} to repair its permissions", folder);
                return false;
            }

            if (root.IsReparsePoint || !root.IsDirectory)
            {
                logger.Warning("[JournalTrust] {Folder} is not a plain folder; its permissions were not repaired", folder);
                return false;
            }

            if (!root.ApplyHardened())
            {
                logger.Warning("[JournalTrust] Could not repair the permissions of {Folder}", folder);
                return false;
            }

            logger.Information("[JournalTrust] Repaired the owner and permissions of {Folder}", folder);
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[JournalTrust] Repairing the permissions of {Folder} failed", folder);
            return false;
        }
    }
}

internal interface IJournalSecurityStore
{
    /// <summary>Enables the privileges a repair needs until disposed.</summary>
    IDisposable EnablePrivileges();

    /// <summary>Opens an entry itself, never what a reparse point names; null when it cannot be opened.</summary>
    IJournalSecurityEntry? Open(string path);
}

internal interface IJournalSecurityEntry : IDisposable
{
    bool IsDirectory { get; }
    bool IsReparsePoint { get; }
    int LinkCount { get; }

    /// <summary>Owner Administrators and the hardened, protected DACL; inheritable on a folder.</summary>
    bool ApplyHardened();
}

/// <summary>Reads and sets journal security through handles opened on the entry itself.</summary>
internal sealed class WindowsJournalSecurity : IJournalSecurityStore
{
    public static readonly WindowsJournalSecurity Instance = new();

    private static readonly string[] RepairPrivileges =
        { "SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege" };

    // With backup semantics on the opens, these grant list, traverse and delete whatever the DACLs say.
    private static readonly string[] RenamePrivileges = { "SeBackupPrivilege", "SeRestorePrivilege" };

    private const uint ShareAll = NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE | NativeInterop.FILE_SHARE_DELETE;
    private const uint OpenFlags = NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT;

    /// <summary>Builds the trust descriptor read-only. Throws when anything cannot be read.</summary>
    public static JournalTrustDescriptor Read(string journalPath)
    {
        var fullPath = Path.GetFullPath(journalPath);
        var folder = Path.GetDirectoryName(fullPath) ?? throw new IOException("The journal path has no folder");

        using var folderHandle = OpenForRead(folder);
        using var fileHandle = OpenForRead(fullPath);
        return new JournalTrustDescriptor(Describe(folderHandle), Describe(fileHandle));
    }

    private static SafeFileHandle OpenForRead(string path) =>
        OpenOrThrow(path, NativeInterop.READ_CONTROL | NativeInterop.FILE_READ_ATTRIBUTES, ShareAll);

    private static SafeFileHandle OpenOrThrow(string path, uint access, uint share)
    {
        var handle = NativeInterop.CreateFile(path, access, share, IntPtr.Zero, NativeInterop.OPEN_EXISTING, OpenFlags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open {path} (error {error})");
        }

        return handle;
    }

    internal static JournalObjectSecurity Describe(SafeFileHandle handle)
    {
        if (!NativeInterop.GetFileInformationByHandle(handle, out var info))
            throw new IOException($"Could not read file information (error {Marshal.GetLastWin32Error()})");

        var descriptor = ReadSecurityDescriptor(handle);
        IReadOnlyList<JournalAce>? dacl = null;
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0 && descriptor.DiscretionaryAcl != null)
            dacl = descriptor.DiscretionaryAcl.Cast<GenericAce>().Select(ToJournalAce).ToList();

        return new JournalObjectSecurity(
            descriptor.Owner?.Value,
            dacl,
            DaclProtected: (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
            IsDirectory: (info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_DIRECTORY) != 0,
            IsReparsePoint: (info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0,
            LinkCount: (int)info.NumberOfLinks);
    }

    private static RawSecurityDescriptor ReadSecurityDescriptor(SafeFileHandle handle)
    {
        var status = NativeInterop.GetSecurityInfo(
            handle, NativeInterop.SE_FILE_OBJECT,
            NativeInterop.OWNER_SECURITY_INFORMATION | NativeInterop.DACL_SECURITY_INFORMATION,
            out _, out _, out _, out _, out var pointer);
        if (status != 0)
            throw new IOException($"Could not read the security descriptor (error {status})");

        try
        {
            var bytes = new byte[NativeInterop.GetSecurityDescriptorLength(pointer)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(bytes, 0);
        }
        finally
        {
            NativeInterop.LocalFree(pointer);
        }
    }

    private static JournalAce ToJournalAce(GenericAce ace)
    {
        bool inheritOnly = (ace.AceFlags & AceFlags.InheritOnly) != 0;
        if (ace is not KnownAce known)
            return new JournalAce(null, JournalAceType.Other, -1, inheritOnly);

        var type = known is QualifiedAce qualified
            ? qualified.AceQualifier switch
            {
                AceQualifier.AccessAllowed => JournalAceType.Allow,
                AceQualifier.AccessDenied => JournalAceType.Deny,
                _ => JournalAceType.Other,
            }
            : JournalAceType.Other;
        return new JournalAce(known.SecurityIdentifier.Value, type, known.AccessMask, inheritOnly);
    }

    public IDisposable EnablePrivileges() => PrivilegeScope.Enable(RepairPrivileges);

    public IJournalSecurityEntry? Open(string path)
    {
        var handle = NativeInterop.CreateFile(
            path,
            NativeInterop.WRITE_DAC | NativeInterop.WRITE_OWNER | NativeInterop.READ_CONTROL | NativeInterop.FILE_READ_ATTRIBUTES,
            // No delete sharing: the entry cannot be renamed or replaced while it is repaired.
            NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING, OpenFlags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        if (!NativeInterop.GetFileInformationByHandle(handle, out var info))
        {
            handle.Dispose();
            return null;
        }

        return new Entry(handle, info);
    }

    /// <summary>
    /// Renames an entry to <paramref name="newName"/> in the folder its path names. Both are opened
    /// as handles, never through a reparse point, and the new name is placed relative to the folder
    /// handle, so nothing swapped in after the open can redirect where the entry lands. Fails when
    /// the folder is a reparse point or <paramref name="newName"/> already exists. Runs with the
    /// backup and restore privileges, so a folder whose DACL shuts administrators out can still be
    /// set aside.
    /// </summary>
    public static bool RenameInPlace(string path, string newName)
    {
        if (newName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            throw new ArgumentException("A plain file name is required", nameof(newName));

        using var privileges = PrivilegeScope.Enable(RenamePrivileges);

        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath);
        if (folder == null)
            return false;

        // No delete sharing: the folder cannot be renamed or replaced while the handle is open.
        using var folderHandle = NativeInterop.CreateFile(
            folder,
            NativeInterop.FILE_TRAVERSE | NativeInterop.FILE_LIST_DIRECTORY | NativeInterop.FILE_READ_ATTRIBUTES | NativeInterop.SYNCHRONIZE,
            NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING, OpenFlags, IntPtr.Zero);
        if (folderHandle.IsInvalid ||
            !NativeInterop.GetFileInformationByHandle(folderHandle, out var folderInfo) ||
            (folderInfo.FileAttributes & NativeInterop.FILE_ATTRIBUTE_DIRECTORY) == 0 ||
            (folderInfo.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            return false;

        using var handle = NativeInterop.CreateFile(
            fullPath, NativeInterop.DELETE | NativeInterop.SYNCHRONIZE, ShareAll,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING, OpenFlags, IntPtr.Zero);
        if (handle.IsInvalid)
            return false;

        return RenameRelative(handle, folderHandle, newName, replaceIfExists: false) == 0;
    }

    /// <summary>
    /// Renames the entry <paramref name="file"/> was opened on (with DELETE access) to
    /// <paramref name="newName"/> inside the folder <paramref name="folder"/> was opened on.
    /// Returns the NTSTATUS, 0 on success.
    /// </summary>
    internal static int RenameRelative(SafeFileHandle file, SafeFileHandle folder, string newName, bool replaceIfExists)
        => SetRenameInformation(file, folder, newName, replaceIfExists ? 1u : 0u, NativeInterop.FileRenameInformation);

    /// <summary>
    /// Like <see cref="RenameRelative"/> with replace, but with POSIX semantics: a target another
    /// process holds open with delete sharing leaves the name and stays readable through that
    /// handle, where the legacy replace refuses any open target. Falls back to the legacy replace
    /// on a file system without the extended rename.
    /// </summary>
    internal static int ReplaceRelative(SafeFileHandle file, SafeFileHandle folder, string newName)
    {
        var status = SetRenameInformation(file, folder, newName,
            NativeInterop.FILE_RENAME_REPLACE_IF_EXISTS | NativeInterop.FILE_RENAME_POSIX_SEMANTICS,
            NativeInterop.FileRenameInformationEx);
        return status is NativeInterop.STATUS_INVALID_INFO_CLASS or NativeInterop.STATUS_INVALID_PARAMETER
            or NativeInterop.STATUS_NOT_SUPPORTED
            ? RenameRelative(file, folder, newName, replaceIfExists: true)
            : status;
    }

    private static int SetRenameInformation(
        SafeFileHandle file, SafeFileHandle folder, string newName, uint flags, int informationClass)
    {
        bool addedRef = false;
        try
        {
            folder.DangerousAddRef(ref addedRef);

            // FILE_RENAME_INFORMATION(_EX): a BOOLEAN ReplaceIfExists or, for the extended class, a
            // ULONG Flags sharing its offset, then root directory, name length, name. Written as a
            // little-endian ULONG, flag 1 is the same bytes as ReplaceIfExists = TRUE.
            var name = global::System.Text.Encoding.Unicode.GetBytes(newName);
            int rootOffset = IntPtr.Size;
            int lengthOffset = rootOffset + IntPtr.Size;
            int nameOffset = lengthOffset + sizeof(uint);
            var info = new byte[nameOffset + name.Length + sizeof(char)];
            BitConverter.GetBytes(flags).CopyTo(info, 0);
            var root = folder.DangerousGetHandle();
            (IntPtr.Size == 8 ? BitConverter.GetBytes(root.ToInt64()) : BitConverter.GetBytes(root.ToInt32())).CopyTo(info, rootOffset);
            BitConverter.GetBytes((uint)name.Length).CopyTo(info, lengthOffset);
            name.CopyTo(info, nameOffset);

            return NativeInterop.NtSetInformationFile(file, out _, info, (uint)info.Length, informationClass);
        }
        finally
        {
            if (addedRef)
                folder.DangerousRelease();
        }
    }

    /// <summary>
    /// Deletes a reparse point itself (a junction, mount point or symbolic link), never what it
    /// names. False, with nothing deleted, when the entry is not a reparse point or cannot be
    /// opened. Runs with the backup and restore privileges, so a DACL on the link cannot keep it.
    /// </summary>
    internal static bool TryRemoveReparsePoint(string path, out string? refusal)
    {
        refusal = null;
        using var privileges = PrivilegeScope.Enable(RenamePrivileges);
        using var handle = NativeInterop.CreateFile(
            path, NativeInterop.DELETE | NativeInterop.FILE_READ_ATTRIBUTES, ShareAll,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING, OpenFlags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            refusal = $"could not open it (error {Marshal.GetLastWin32Error()})";
            return false;
        }

        if (!NativeInterop.GetFileInformationByHandle(handle, out var info) ||
            (info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) == 0)
        {
            refusal = "it is not a reparse point";
            return false;
        }

        // FILE_DISPOSITION_INFORMATION { DeleteFile = TRUE }; the entry goes when the handle closes.
        var status = NativeInterop.NtSetInformationFile(
            handle, out _, new byte[] { 1 }, 1, NativeInterop.FileDispositionInformation);
        if (status != 0)
        {
            refusal = $"deleting it failed (status 0x{status:X8})";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates a folder that has <paramref name="ownerSid"/> as owner and the hardened, protected
    /// DACL from the moment it exists. Fails when anything already has that name.
    /// </summary>
    internal static bool TryCreateHardenedFolder(string path, string ownerSid, out string? refusal)
    {
        refusal = null;
        var descriptor = HardenedDescriptor(ownerSid, isDirectory: true);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);

        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var attributes = new NativeInterop.SECURITY_ATTRIBUTES
            {
                Length = (uint)Marshal.SizeOf<NativeInterop.SECURITY_ATTRIBUTES>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
            };
            if (!NativeInterop.CreateDirectory(path, ref attributes))
            {
                refusal = $"creating it failed (error {Marshal.GetLastWin32Error()})";
                return false;
            }
        }
        finally
        {
            pinned.Free();
        }

        return true;
    }

    /// <summary>Writes the owner and a protected DACL of SYSTEM and Administrators full control and Users read and execute.</summary>
    internal static bool ApplyHardened(SafeFileHandle handle, string ownerSid, bool isDirectory) =>
        SetSecurity(handle, NativeInterop.OWNER_SECURITY_INFORMATION | NativeInterop.DACL_SECURITY_INFORMATION,
            HardenedDescriptor(ownerSid, isDirectory));

    /// <summary>The hardened, protected DACL alone; the owner is left as it is.</summary>
    internal static bool ApplyHardenedDacl(SafeFileHandle handle, bool isDirectory) =>
        SetSecurity(handle, NativeInterop.DACL_SECURITY_INFORMATION, HardenedDescriptor(ownerSid: null, isDirectory));

    /// <summary>
    /// Opens a directory itself (never what a reparse point names) with WRITE_DAC and sets the
    /// hardened DACL on that handle, without propagating to existing entries. False, with nothing
    /// changed, when it cannot be opened or is a reparse point or not a directory.
    /// </summary>
    internal static bool TryHardenFolderDacl(string folder, out string? refusal)
    {
        refusal = null;
        using var handle = NativeInterop.CreateFile(
            folder, NativeInterop.WRITE_DAC | NativeInterop.FILE_READ_ATTRIBUTES,
            NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING, OpenFlags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            refusal = $"could not open it (error {Marshal.GetLastWin32Error()})";
            return false;
        }

        if (!NativeInterop.GetFileInformationByHandle(handle, out var info) ||
            (info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_DIRECTORY) == 0 ||
            (info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            refusal = "it is not a plain folder";
            return false;
        }

        if (!ApplyHardenedDacl(handle, isDirectory: true))
        {
            refusal = $"setting the DACL failed (error {Marshal.GetLastWin32Error()})";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Makes Administrators the owner of a file GameShift just wrote, through a handle on the entry
    /// itself. An elevated token may name Administrators as owner without any privilege.
    /// </summary>
    internal static bool TrySetAdministratorsOwner(string path)
    {
        using var handle = NativeInterop.CreateFile(
            path, NativeInterop.WRITE_OWNER, ShareAll,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING, OpenFlags, IntPtr.Zero);
        return !handle.IsInvalid && TrySetAdministratorsOwner(handle);
    }

    /// <summary>The same on a handle opened with WRITE_OWNER.</summary>
    internal static bool TrySetAdministratorsOwner(SafeFileHandle handle)
    {
        var descriptor = new RawSecurityDescriptor(
            ControlFlags.None, new SecurityIdentifier(JournalTrust.AdministratorsSid), group: null, systemAcl: null, discretionaryAcl: null);
        return SetSecurity(handle, NativeInterop.OWNER_SECURITY_INFORMATION, descriptor);
    }

    private static bool SetSecurity(SafeFileHandle handle, uint securityInfo, RawSecurityDescriptor descriptor)
    {
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return NativeInterop.SetKernelObjectSecurity(handle, securityInfo, bytes);
    }

    private static RawSecurityDescriptor HardenedDescriptor(string? ownerSid, bool isDirectory)
    {
        var inherit = isDirectory ? AceFlags.ObjectInherit | AceFlags.ContainerInherit : AceFlags.None;
        var dacl = new RawAcl(GenericAcl.AclRevision, 3);
        dacl.InsertAce(0, new CommonAce(inherit, AceQualifier.AccessAllowed, unchecked((int)JournalTrust.FileAllAccess),
            new SecurityIdentifier(JournalTrust.SystemSid), false, null));
        dacl.InsertAce(1, new CommonAce(inherit, AceQualifier.AccessAllowed, unchecked((int)JournalTrust.FileAllAccess),
            new SecurityIdentifier(JournalTrust.AdministratorsSid), false, null));
        dacl.InsertAce(2, new CommonAce(inherit, AceQualifier.AccessAllowed, unchecked((int)JournalTrust.FileReadAndExecute),
            new SecurityIdentifier(JournalTrust.UsersSid), false, null));

        return new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            ownerSid == null ? null : new SecurityIdentifier(ownerSid), group: null, systemAcl: null, discretionaryAcl: dacl);
    }

    private sealed class Entry : IJournalSecurityEntry
    {
        private readonly SafeFileHandle _handle;

        public Entry(SafeFileHandle handle, NativeInterop.BY_HANDLE_FILE_INFORMATION info)
        {
            _handle = handle;
            IsDirectory = (info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_DIRECTORY) != 0;
            IsReparsePoint = (info.FileAttributes & NativeInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
            LinkCount = (int)info.NumberOfLinks;
        }

        public bool IsDirectory { get; }
        public bool IsReparsePoint { get; }
        public int LinkCount { get; }

        public bool ApplyHardened() => WindowsJournalSecurity.ApplyHardened(_handle, JournalTrust.AdministratorsSid, IsDirectory);

        public void Dispose() => _handle.Dispose();
    }

    /// <summary>Enables token privileges for the process and restores their earlier state on dispose.</summary>
    private sealed class PrivilegeScope : IDisposable
    {
        private readonly IntPtr _token;
        private readonly List<NativeInterop.TOKEN_PRIVILEGES_ONE> _previous = new();

        private PrivilegeScope(IntPtr token) => _token = token;

        public static IDisposable Enable(IEnumerable<string> privileges)
        {
            if (!NativeInterop.OpenProcessToken(
                    NativeInterop.GetCurrentProcessHandle(),
                    NativeInterop.TOKEN_ADJUST_PRIVILEGES | NativeInterop.TOKEN_QUERY,
                    out var token))
                return new PrivilegeScope(IntPtr.Zero);

            var scope = new PrivilegeScope(token);
            foreach (var name in privileges)
            {
                if (!NativeInterop.LookupPrivilegeValue(null, name, out var luid))
                    continue;

                var state = new NativeInterop.TOKEN_PRIVILEGES_ONE
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = NativeInterop.SE_PRIVILEGE_ENABLED,
                };
                // A privilege the token does not hold stays off; the repair then fails where it needed it.
                if (NativeInterop.AdjustTokenPrivileges(token, false, ref state,
                        (uint)Marshal.SizeOf<NativeInterop.TOKEN_PRIVILEGES_ONE>(), out var previous, out _) &&
                    previous.PrivilegeCount > 0)
                {
                    scope._previous.Add(previous);
                }
            }

            return scope;
        }

        public void Dispose()
        {
            if (_token == IntPtr.Zero)
                return;

            foreach (var previous in _previous)
            {
                var state = previous;
                NativeInterop.AdjustTokenPrivileges(_token, false, ref state,
                    (uint)Marshal.SizeOf<NativeInterop.TOKEN_PRIVILEGES_ONE>(), out _, out _);
            }

            NativeInterop.CloseHandle(_token);
        }
    }
}
