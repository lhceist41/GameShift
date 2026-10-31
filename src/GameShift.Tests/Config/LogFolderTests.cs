using System.Security.AccessControl;
using System.Security.Principal;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// The logs folder the elevated app writes by path is only used when no standard user can create,
/// rename or link anything in it or its parent. Run unelevated with the test user as the owner
/// GameShift creates folders with; that the owner is Administrators in production is only visible
/// in an elevated run.
/// </summary>
public sealed class LogFolderTests
{
    private const string SystemSid = "S-1-5-18";
    private const string Admins = "S-1-5-32-544";
    private const string Users = "S-1-5-32-545";
    private const int FullControl = 0x1F01FF;
    private const int ReadAndExecute = 0x1200A9;

    private static string MySid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    private static JournalObjectSecurity Describe(string path)
    {
        using var handle = NativeInterop.CreateFile(
            path, NativeInterop.READ_CONTROL | NativeInterop.FILE_READ_ATTRIBUTES,
            NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE | NativeInterop.FILE_SHARE_DELETE,
            IntPtr.Zero, NativeInterop.OPEN_EXISTING,
            NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        return WindowsJournalSecurity.Describe(handle);
    }

    private static string AclSnapshot(string path)
    {
        var security = Describe(path);
        return $"{security.OwnerSid}|{security.DaclProtected}|" +
               string.Join(";", security.Dacl!.Select(a => $"{a.Sid}{a.Type}{a.AccessMask}{a.InheritOnly}"));
    }

    /// <summary>
    /// Creates a folder only SYSTEM, Administrators and the test user can write, standing in for
    /// %ProgramData%\GameShift as GameShift prepares it; a temp folder may inherit more.
    /// </summary>
    private static void CreateOwnedFolder(string folder)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { SystemSid, Admins, MySid() })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(folder).Create(security);
    }

    /// <summary>Lets Users write <paramref name="folder"/>, as a folder a standard user made or opened up would.</summary>
    private static void GrantUsersWrite(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(Users), FileSystemRights.Modify,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>A folder created hardened grants Users only read; the test owns it and opens it up before cleanup.</summary>
    private static void OpenUp(string folder)
    {
        if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(MySid()), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(security);
    }

    [Fact]
    public void Prepare_CreatesAMissingLogsFolder_WithTheOwnerAndTheProtectedHardenedDacl()
    {
        using var temp = new TempPath();
        var parent = Path.Combine(temp.Path, "GameShift");
        CreateOwnedFolder(parent);
        var logs = Path.Combine(parent, "logs");
        try
        {
            using var folder = LogFolder.Prepare(logs, MySid(), out var refusal);

            Assert.Null(refusal);
            Assert.NotNull(folder);
            var security = Describe(logs);
            Assert.Equal(MySid(), security.OwnerSid);
            Assert.True(security.DaclProtected);
            Assert.Equal(
                new[] { (SystemSid, JournalAceType.Allow, FullControl), (Admins, JournalAceType.Allow, FullControl), (Users, JournalAceType.Allow, ReadAndExecute) },
                security.Dacl!.Select(a => (a.Sid!, a.Type, a.AccessMask)));
        }
        finally
        {
            OpenUp(logs);
        }
    }

    [Fact]
    public void Prepare_CreatesAMissingParent_WithTheOwnerAndTheProtectedHardenedDacl()
    {
        using var temp = new TempPath();
        var parent = Path.Combine(temp.Path, "GameShift");
        try
        {
            // Unelevated, the logs folder itself then cannot be created in the read-only parent.
            LogFolder.Prepare(Path.Combine(parent, "logs"), MySid(), out _)?.Dispose();

            var security = Describe(parent);
            Assert.Equal(MySid(), security.OwnerSid);
            Assert.True(security.DaclProtected);
            Assert.Equal(new[] { SystemSid, Admins, Users }, security.Dacl!.Select(a => a.Sid));
        }
        finally
        {
            OpenUp(Path.Combine(parent, "logs"));
            OpenUp(parent);
        }
    }

    [Fact]
    public void Prepare_AnExistingLogsFolderOthersCanWrite_IsRefused_AndLeftAsItWas()
    {
        using var temp = new TempPath();
        CreateOwnedFolder(Path.Combine(temp.Path, "GameShift"));
        var logs = Path.Combine(temp.Path, "GameShift", "logs");
        CreateOwnedFolder(logs);
        GrantUsersWrite(logs);
        var before = AclSnapshot(logs);

        var folder = LogFolder.Prepare(logs, MySid(), out var refusal);

        Assert.Null(folder);
        Assert.Contains("admin-only", refusal);
        Assert.Equal(before, AclSnapshot(logs));
    }

    [Fact]
    public void Prepare_AParentOthersCanWrite_IsRefused_AndNoLogsFolderIsCreated()
    {
        using var temp = new TempPath();
        var parent = Path.Combine(temp.Path, "GameShift");
        CreateOwnedFolder(parent);
        GrantUsersWrite(parent);

        var folder = LogFolder.Prepare(Path.Combine(parent, "logs"), MySid(), out var refusal);

        Assert.Null(folder);
        Assert.Contains("admin-only", refusal);
        Assert.False(Directory.Exists(Path.Combine(parent, "logs")));
    }

    [Fact]
    public void Prepare_AJunctionInPlaceOfTheLogsFolder_IsRefused_AndTheTargetStaysAsItWas()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "victim");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        var before = UserDataFolderTests.Snapshot(target);
        var parent = Path.Combine(temp.Path, "GameShift");
        CreateOwnedFolder(parent);
        using var junction1 = UserDataFolderTests.Junction(Path.Combine(parent, "logs"), target);

        var folder = LogFolder.Prepare(Path.Combine(parent, "logs"), MySid(), out var refusal);

        Assert.Null(folder);
        Assert.Contains("junction", refusal);
        Assert.Equal(before, UserDataFolderTests.Snapshot(target));
    }

    [Fact]
    public void Prepare_AJunctionInPlaceOfTheParent_IsRefused_AndNothingIsCreatedInTheTarget()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "victim");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(temp.Path, "GameShift");
        using var junction2 = UserDataFolderTests.Junction(junction, target);

        var folder = LogFolder.Prepare(Path.Combine(junction, "logs"), MySid(), out var refusal);

        Assert.Null(folder);
        Assert.Contains("junction", refusal);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Prepare_AJunctionAboveTheParent_IsRefused_AndNoLogsFolderIsCreatedInTheTarget()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "victim");
        Directory.CreateDirectory(target);
        CreateOwnedFolder(Path.Combine(target, "GameShift"));
        var junction = Path.Combine(temp.Path, "ProgramData");
        using var junction3 = UserDataFolderTests.Junction(junction, target);

        var folder = LogFolder.Prepare(Path.Combine(junction, "GameShift", "logs"), MySid(), out var refusal);

        Assert.Null(folder);
        Assert.Contains("above", refusal);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(target, "GameShift")));
    }

    [Fact]
    public void APreparedLogsFolder_CannotBeRenamedAway()
    {
        using var temp = new TempPath();
        var parent = Path.Combine(temp.Path, "GameShift");
        CreateOwnedFolder(parent);
        var logs = Path.Combine(parent, "logs");
        try
        {
            using var folder = LogFolder.Prepare(logs, MySid(), out _);
            Assert.NotNull(folder);
            OpenUp(logs);

            Assert.ThrowsAny<IOException>(() => Directory.Move(logs, Path.Combine(parent, "moved")));
            Assert.True(Directory.Exists(logs));
        }
        finally
        {
            OpenUp(logs);
        }
    }
}
