using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using GameShift.Core.Journal;
using GameShift.Core.System;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Core.Updates;

/// <summary>
/// Security of an install: its folder, the exe in it, and every folder above it, nearest first.
/// </summary>
public sealed record InstallFolderDescriptor(
    JournalObjectSecurity Folder,
    JournalObjectSecurity Exe,
    IReadOnlyList<JournalObjectSecurity> Ancestors);

/// <param name="Reasons">Why the install is not trusted, for the log; empty when trusted.</param>
public sealed record InstallFolderTrustResult(bool Trusted, IReadOnlyList<string> Reasons)
{
    public static InstallFolderTrustResult Untrusted(string reason) => new(false, new[] { reason });
}

/// <summary>
/// Decides whether an install folder is admin-only, so the elevated app may treat what is in it as
/// its own: the folder and the exe are owned by SYSTEM, Administrators or TrustedInstaller, no entry
/// in their DACLs grants anyone else a write, append, delete, WRITE_DAC or WRITE_OWNER right, neither
/// is a reparse point, and the exe has one link. No folder above it may be a reparse point, have
/// another owner, or let anyone else delete or rename it or the folder below it, since renaming a
/// parent would put a different folder at the same path after this check.
/// </summary>
public static class InstallFolderTrust
{
    internal const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
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
    private const uint FileAllAccess = 0x1F01FF;

    private const uint FileDeleteChild = 0x40;
    private const uint Delete = 0x10000;
    private const uint WriteDac = 0x40000;
    private const uint WriteOwner = 0x80000;

    // FILE_WRITE_DATA (add file), FILE_APPEND_DATA (add subfolder), FILE_WRITE_EA, FILE_DELETE_CHILD,
    // FILE_WRITE_ATTRIBUTES, DELETE, WRITE_DAC, WRITE_OWNER.
    private const uint WriteRights = 0x2 | 0x4 | 0x10 | FileDeleteChild | 0x100 | Delete | WriteDac | WriteOwner;

    // What lets someone move a folder above the install out of the way: deleting or renaming it,
    // deleting or renaming the folder below it, or changing its DACL or owner to get those rights.
    private const uint AncestorRights = FileDeleteChild | Delete | WriteDac | WriteOwner;

    /// <summary>Reads the install's security and evaluates it. Never throws; anything unreadable is untrusted.</summary>
    public static InstallFolderTrustResult Check(string exePath)
    {
        try
        {
            return Evaluate(Read(exePath));
        }
        catch (Exception ex)
        {
            return InstallFolderTrustResult.Untrusted($"could not read the install's security: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a folder's security and evaluates it by the same rules, without an exe in it. Never
    /// throws; anything unreadable, including a missing folder, is untrusted.
    /// </summary>
    public static InstallFolderTrustResult CheckFolder(string folderPath)
    {
        try
        {
            var (folder, ancestors) = ReadFolder(folderPath);
            return EvaluateFolder(folder, ancestors);
        }
        catch (Exception ex)
        {
            return InstallFolderTrustResult.Untrusted($"could not read the folder's security: {ex.Message}");
        }
    }

    public static InstallFolderTrustResult Evaluate(InstallFolderDescriptor descriptor)
    {
        var reasons = new List<string>();
        // An inheritable CREATOR OWNER or OWNER RIGHTS entry on the folder applies to whoever owns
        // a child, so it resolves to the exe's owner as well as the folder's.
        EvaluateObject("install folder", descriptor.Folder, expectDirectory: true,
            new[] { descriptor.Folder.OwnerSid, descriptor.Exe.OwnerSid }, reasons);
        EvaluateObject("exe", descriptor.Exe, expectDirectory: false,
            new[] { descriptor.Exe.OwnerSid }, reasons);
        EvaluateAncestors("the install", descriptor.Ancestors, reasons);

        return new InstallFolderTrustResult(reasons.Count == 0, reasons);
    }

    /// <summary>A folder alone: the install folder rules without the exe, and every folder above it.</summary>
    public static InstallFolderTrustResult EvaluateFolder(
        JournalObjectSecurity folder, IReadOnlyList<JournalObjectSecurity> ancestors)
    {
        var reasons = new List<string>();
        EvaluateObject("folder", folder, expectDirectory: true, new[] { folder.OwnerSid }, reasons);
        EvaluateAncestors("the folder", ancestors, reasons);

        return new InstallFolderTrustResult(reasons.Count == 0, reasons);
    }

    private static void EvaluateAncestors(string below, IReadOnlyList<JournalObjectSecurity> ancestors, List<string> reasons)
    {
        for (int i = 0; i < ancestors.Count; i++)
            EvaluateAncestor($"folder {i + 1} above {below}", ancestors[i], reasons);
    }

    private static void EvaluateObject(
        string what, JournalObjectSecurity security, bool expectDirectory, string?[] owners, List<string> reasons)
    {
        EvaluateShape(what, security, expectDirectory, reasons);
        if (!expectDirectory && security.LinkCount != 1)
            reasons.Add($"the {what} has {security.LinkCount} links");

        if (security.Dacl == null)
        {
            reasons.Add($"the {what} has no DACL");
            return;
        }

        // Inherit-only entries count too: they decide who may write what is created in the folder.
        foreach (var ace in security.Dacl)
            CheckAce(what, ace, WriteRights, owners, reasons);
    }

    private static void EvaluateAncestor(string what, JournalObjectSecurity security, List<string> reasons)
    {
        EvaluateShape(what, security, expectDirectory: true, reasons);

        if (security.Dacl == null)
        {
            reasons.Add($"the {what} has no DACL");
            return;
        }

        // Inherit-only entries do not apply to this folder; whatever reaches the folder below it shows
        // up there as an inherited entry and is checked with that folder.
        foreach (var ace in security.Dacl)
        {
            if (!ace.InheritOnly)
                CheckAce(what, ace, AncestorRights, new[] { security.OwnerSid }, reasons);
        }
    }

    private static void EvaluateShape(string what, JournalObjectSecurity security, bool expectDirectory, List<string> reasons)
    {
        if (security.IsReparsePoint)
            reasons.Add($"the {what} is a reparse point");
        if (security.IsDirectory != expectDirectory)
            reasons.Add($"the {what} is {(security.IsDirectory ? "a directory" : "not a directory")}");
        if (!IsTrustedSid(security.OwnerSid))
            reasons.Add($"the {what} is owned by {security.OwnerSid ?? "nobody"}");
    }

    private static void CheckAce(string what, JournalAce ace, uint watchedRights, string?[] owners, List<string> reasons)
    {
        if (ace.Type == JournalAceType.Deny)
            return;

        var rights = MapGenericRights(unchecked((uint)ace.AccessMask)) & watchedRights;
        if (rights == 0)
            return;

        var principals = ace.Sid is CreatorOwnerSid or OwnerRightsSid ? owners : new[] { ace.Sid };
        foreach (var principal in principals)
        {
            if (!IsTrustedSid(principal))
            {
                var resolved = principal == ace.Sid ? string.Empty : $" (resolved to {principal ?? "nobody"})";
                reasons.Add($"the {what} grants rights 0x{rights:X} to {ace.Sid ?? "an unreadable entry"}{resolved}");
            }
        }
    }

    private static bool IsTrustedSid(string? sid) =>
        sid is JournalTrust.SystemSid or JournalTrust.AdministratorsSid or TrustedInstallerSid;

    private static uint MapGenericRights(uint mask)
    {
        var mapped = mask & ~(GenericRead | GenericWrite | GenericExecute | GenericAll | MaximumAllowed);
        if ((mask & GenericRead) != 0) mapped |= FileGenericRead;
        if ((mask & GenericWrite) != 0) mapped |= FileGenericWrite;
        if ((mask & GenericExecute) != 0) mapped |= FileGenericExecute;
        if ((mask & (GenericAll | MaximumAllowed)) != 0) mapped |= FileAllAccess;
        return mapped;
    }

    /// <summary>Builds the descriptor read-only, never through a reparse point. Throws when anything cannot be read.</summary>
    internal static InstallFolderDescriptor Read(string exePath)
    {
        var fullPath = Path.GetFullPath(exePath);
        var folder = Path.GetDirectoryName(fullPath) ?? throw new IOException("The exe path has no folder");

        JournalObjectSecurity folderSecurity, exeSecurity;
        using (var folderHandle = OpenForRead(folder))
            folderSecurity = WindowsJournalSecurity.Describe(folderHandle);
        using (var exeHandle = OpenForRead(fullPath))
            exeSecurity = WindowsJournalSecurity.Describe(exeHandle);

        return new InstallFolderDescriptor(folderSecurity, exeSecurity, ReadAncestors(folder));
    }

    /// <summary>A folder's security and that of every folder above it, read like <see cref="Read"/>.</summary>
    internal static (JournalObjectSecurity Folder, IReadOnlyList<JournalObjectSecurity> Ancestors) ReadFolder(string folderPath)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));

        JournalObjectSecurity folderSecurity;
        using (var folderHandle = OpenForRead(folder))
            folderSecurity = WindowsJournalSecurity.Describe(folderHandle);

        return (folderSecurity, ReadAncestors(folder));
    }

    private static List<JournalObjectSecurity> ReadAncestors(string folder)
    {
        var ancestors = new List<JournalObjectSecurity>();
        for (var parent = Path.GetDirectoryName(folder); parent != null; parent = Path.GetDirectoryName(parent))
        {
            using var handle = OpenForRead(parent);
            ancestors.Add(WindowsJournalSecurity.Describe(handle));
        }

        return ancestors;
    }

    private static SafeFileHandle OpenForRead(string path)
    {
        var handle = NativeInterop.CreateFile(
            path,
            NativeInterop.READ_CONTROL | NativeInterop.FILE_READ_ATTRIBUTES,
            NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE | NativeInterop.FILE_SHARE_DELETE,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING,
            NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open {path} (error {error})");
        }

        return handle;
    }
}
