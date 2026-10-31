using System.Security.AccessControl;
using System.Security.Principal;
using GameShift.Core.Journal;
using GameShift.Tests.TestHelpers;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// The journal is replayed elevated only when no standard user could have written it. The evaluator
/// is pure, so each rule is tested on a hand-built descriptor; the Windows reader and the in-place
/// rename are exercised read-only or on a throwaway temp folder; the repair is tested through its
/// decision with a fake store, since setting the Administrators owner needs elevation.
/// </summary>
public class JournalTrustTests
{
    private const string SystemSid = "S-1-5-18";
    private const string Admins = "S-1-5-32-544";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string CreatorOwner = "S-1-3-0";
    private const string OwnerRights = "S-1-3-4";
    private const string SomeUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private const int FullControl = 0x1F01FF;
    private const int ReadAndExecute = 0x1200A9;

    private static readonly JournalAce[] HardenedAces =
    {
        new(SystemSid, JournalAceType.Allow, FullControl, false),
        new(Admins, JournalAceType.Allow, FullControl, false),
        new(Users, JournalAceType.Allow, ReadAndExecute, false),
    };

    private static JournalObjectSecurity Folder(string? owner = Admins, IReadOnlyList<JournalAce>? dacl = null, bool reparse = false) =>
        new(owner, dacl ?? HardenedAces, DaclProtected: true, IsDirectory: true, IsReparsePoint: reparse, LinkCount: 1);

    private static JournalObjectSecurity File(string? owner = Admins, IReadOnlyList<JournalAce>? dacl = null, bool reparse = false, int links = 1) =>
        new(owner, dacl ?? HardenedAces, DaclProtected: false, IsDirectory: false, IsReparsePoint: reparse, LinkCount: links);

    private static JournalAce[] HardenedPlus(JournalAce extra) => HardenedAces.Append(extra).ToArray();

    // ── Evaluator ─────────────────────────────────────────────────────────

    [Fact]
    public void AdministratorsOwnedHardenedFolderAndFile_AreTrusted()
    {
        var result = JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(), File()));

        Assert.True(result.Trusted, string.Join("; ", result.Reasons));
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void SystemOwned_IsTrusted()
    {
        Assert.True(JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(owner: SystemSid), File(owner: SystemSid))).Trusted);
    }

    [Fact]
    public void DenyEntriesForUsers_DoNotMatter()
    {
        var dacl = HardenedPlus(new JournalAce(Users, JournalAceType.Deny, FullControl, false));
        Assert.True(JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(dacl: dacl), File(dacl: dacl))).Trusted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UserOwner_IsUntrusted(bool onFolder)
    {
        var descriptor = onFolder
            ? new JournalTrustDescriptor(Folder(owner: SomeUser), File())
            : new JournalTrustDescriptor(Folder(), File(owner: SomeUser));

        var result = JournalTrust.Evaluate(descriptor);

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("owned by " + SomeUser));
    }

    [Fact]
    public void MissingOwner_IsUntrusted()
    {
        Assert.False(JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(owner: null), File())).Trusted);
    }

    // FILE_WRITE_DATA (add file), FILE_APPEND_DATA (add folder), WRITE_EA, DELETE_CHILD,
    // WRITE_ATTRIBUTES, DELETE, WRITE_DAC, WRITE_OWNER, and generic write and all.
    [Theory]
    [InlineData(0x2)]
    [InlineData(0x4)]
    [InlineData(0x10)]
    [InlineData(0x40)]
    [InlineData(0x100)]
    [InlineData(0x10000)]
    [InlineData(0x40000)]
    [InlineData(0x80000)]
    [InlineData(0x40000000)]
    [InlineData(0x10000000)]
    [InlineData(0x02000000)]
    public void UsersWriteEntryOnFolder_IsUntrusted(int mask)
    {
        var dacl = HardenedPlus(new JournalAce(Users, JournalAceType.Allow, mask, false));

        var result = JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(dacl: dacl), File()));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("folder grants write rights") && r.Contains(Users));
    }

    [Fact]
    public void UsersWriteEntryOnFile_IsUntrusted()
    {
        var dacl = HardenedPlus(new JournalAce(AuthenticatedUsers, JournalAceType.Allow, 0x120116, false));

        var result = JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(), File(dacl: dacl)));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("file grants write rights") && r.Contains(AuthenticatedUsers));
    }

    // An inherit-only entry decides what a new file in the folder grants, so it counts too.
    [Fact]
    public void InheritOnlyUsersWriteEntry_IsUntrusted()
    {
        var dacl = HardenedPlus(new JournalAce(Users, JournalAceType.Allow, FullControl, InheritOnly: true));
        Assert.False(JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(dacl: dacl), File())).Trusted);
    }

    [Theory]
    [InlineData(unchecked((int)0x80000000))] // GENERIC_READ
    [InlineData(0x20000000)]                  // GENERIC_EXECUTE
    [InlineData(ReadAndExecute)]
    public void UsersReadOnlyEntries_AreTrusted(int mask)
    {
        var dacl = HardenedPlus(new JournalAce(Users, JournalAceType.Allow, mask, false));
        Assert.True(JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(dacl: dacl), File(dacl: dacl))).Trusted);
    }

    // %ProgramData% hands CREATOR OWNER full control to whoever creates a subfolder. Resolved to the
    // owners it applies to, it is harmless for an Administrators-created file and fatal for a user's.
    [Fact]
    public void CreatorOwnerEntry_ResolvesToTheFileOwner()
    {
        var dacl = HardenedPlus(new JournalAce(CreatorOwner, JournalAceType.Allow, FullControl, InheritOnly: true));

        Assert.True(JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(dacl: dacl), File())).Trusted);

        var result = JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(dacl: dacl), File(owner: SomeUser)));
        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains(CreatorOwner) && r.Contains("resolved to " + SomeUser));
    }

    [Fact]
    public void OwnerRightsEntry_ResolvesToTheOwner()
    {
        var dacl = HardenedPlus(new JournalAce(OwnerRights, JournalAceType.Allow, 0x40000, false));

        var result = JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(), File(owner: SomeUser, dacl: dacl)));

        Assert.Contains(result.Reasons, r => r.Contains(OwnerRights) && r.Contains("resolved to " + SomeUser));
    }

    [Fact]
    public void UnknownEntryTypeOrUnreadableSid_IsUntrusted()
    {
        Assert.False(JournalTrust.Evaluate(new JournalTrustDescriptor(
            Folder(dacl: HardenedPlus(new JournalAce(Users, JournalAceType.Other, 0x2, false))), File())).Trusted);
        Assert.False(JournalTrust.Evaluate(new JournalTrustDescriptor(
            Folder(dacl: HardenedPlus(new JournalAce(null, JournalAceType.Other, -1, false))), File())).Trusted);
    }

    [Fact]
    public void NullDacl_IsUntrusted()
    {
        var file = new JournalObjectSecurity(Admins, Dacl: null, DaclProtected: false, IsDirectory: false, IsReparsePoint: false, LinkCount: 1);

        var result = JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(), file));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("no DACL"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReparsePoint_IsUntrusted(bool onFolder)
    {
        var descriptor = onFolder
            ? new JournalTrustDescriptor(Folder(reparse: true), File())
            : new JournalTrustDescriptor(Folder(), File(reparse: true));

        var result = JournalTrust.Evaluate(descriptor);

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("reparse point"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void FileWithOtherThanOneLink_IsUntrusted(int links)
    {
        var result = JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(), File(links: links)));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains($"{links} links"));
    }

    [Fact]
    public void FolderThatIsAFile_OrFileThatIsAFolder_IsUntrusted()
    {
        var notAFolder = Folder() with { IsDirectory = false };
        var notAFile = File() with { IsDirectory = true };

        Assert.False(JournalTrust.Evaluate(new JournalTrustDescriptor(notAFolder, File())).Trusted);
        Assert.False(JournalTrust.Evaluate(new JournalTrustDescriptor(Folder(), notAFile)).Trusted);
    }

    // ── Windows reader (read-only) ────────────────────────────────────────

    // A temp folder grants its user full control, so the real reader must find that entry; a
    // failed read would be untrusted too, so the reason is checked as well.
    [Fact]
    public void Check_OnAFreshTempFolder_IsUntrustedForItsPermissions()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(path, "{}");

        var result = JournalTrust.Check(path);

        Assert.False(result.Trusted);
        Assert.DoesNotContain(result.Reasons, r => r.Contains("could not read"));
        Assert.Contains(result.Reasons, r => r.Contains("grants write rights") || r.Contains("owned by"));
    }

    [Fact]
    public void Check_OnAMissingFile_IsUntrusted()
    {
        using var temp = new TempPath();

        var result = JournalTrust.Check(temp.GetFile("state.json"));

        Assert.False(result.Trusted);
        Assert.Contains(result.Reasons, r => r.Contains("could not read"));
    }

    [Fact]
    public void Read_ReportsOwnerEntriesAndKinds()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(path, "{}");

        var descriptor = WindowsJournalSecurity.Read(path);

        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(descriptor.Folder.IsDirectory);
        Assert.False(descriptor.File.IsDirectory);
        Assert.False(descriptor.Folder.IsReparsePoint);
        Assert.Equal(1, descriptor.File.LinkCount);
        Assert.NotNull(descriptor.File.OwnerSid);
        Assert.NotNull(descriptor.File.Dacl);
        Assert.Contains(descriptor.File.Dacl!, ace => ace.Type == JournalAceType.Allow && ace.Sid == identity.User!.Value);
    }

    // Junctions and hard links need no privilege, which is why a standard user can plant them.
    private static void MkLink(string kind, string link, string target)
    {
        var start = new global::System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/c", "mklink", kind, link, target })
            start.ArgumentList.Add(arg);
        using var process = global::System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(10_000);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Check_ThroughAJunctionedFolder_IsUntrusted()
    {
        using var temp = new TempPath();
        var real = Path.Combine(temp.Path, "real");
        Directory.CreateDirectory(real);
        global::System.IO.File.WriteAllText(Path.Combine(real, "state.json"), "{}");
        var junction = Path.Combine(temp.Path, "GameShift");
        MkLink("/J", junction, real);

        try
        {
            var journal = Path.Combine(junction, "state.json");
            var descriptor = WindowsJournalSecurity.Read(journal);
            var result = JournalTrust.Check(journal);

            Assert.True(descriptor.Folder.IsReparsePoint);
            Assert.False(result.Trusted);
            Assert.Contains(result.Reasons, r => r.Contains("folder is a reparse point"));
            Assert.False(WindowsJournalSecurity.RenameInPlace(journal, "state.untrusted-x.json"));
            Assert.True(global::System.IO.File.Exists(Path.Combine(real, "state.json")));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void Check_OnAHardLinkedFile_SeesTheSecondLink()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(path, "{}");
        MkLink("/H", temp.GetFile("elsewhere.json"), path);

        var descriptor = WindowsJournalSecurity.Read(path);
        var result = JournalTrust.Check(path);

        Assert.Equal(2, descriptor.File.LinkCount);
        Assert.Contains(result.Reasons, r => r.Contains("2 links"));
    }

    // ── In-place rename ───────────────────────────────────────────────────

    [Fact]
    public void RenameInPlace_RenamesWithinTheSameFolder()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(path, "content");

        Assert.True(WindowsJournalSecurity.RenameInPlace(path, "state.untrusted-x.json"));

        Assert.False(global::System.IO.File.Exists(path));
        Assert.Equal("content", global::System.IO.File.ReadAllText(temp.GetFile("state.untrusted-x.json")));
    }

    [Fact]
    public void RenameInPlace_NeverReplacesAnExistingFile()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(path, "journal");
        global::System.IO.File.WriteAllText(temp.GetFile("taken.json"), "other");

        Assert.False(WindowsJournalSecurity.RenameInPlace(path, "taken.json"));

        Assert.Equal("journal", global::System.IO.File.ReadAllText(path));
        Assert.Equal("other", global::System.IO.File.ReadAllText(temp.GetFile("taken.json")));
    }

    [Theory]
    [InlineData(@"..\elsewhere.json")]
    [InlineData("sub/elsewhere.json")]
    [InlineData(@"C:\elsewhere.json")]
    public void RenameInPlace_RefusesAnythingButAPlainName(string newName)
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(path, "journal");

        Assert.Throws<ArgumentException>(() => WindowsJournalSecurity.RenameInPlace(path, newName));
        Assert.True(global::System.IO.File.Exists(path));
    }

    // ── Hardened descriptor written through a handle ──────────────────────

    // Writes the hardened DACL with the folder's current owner (setting the Administrators owner
    // needs elevation), then reads it back with the real reader. The temp folder's DACL is opened
    // up again before it is deleted.
    [Fact]
    public void ApplyHardened_WritesTheProtectedDaclTheEvaluatorAccepts()
    {
        using var temp = new TempPath();
        var file = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(file, "{}");
        var owner = WindowsJournalSecurity.Read(file);

        try
        {
            using (var entry = WindowsJournalSecurity.Instance.Open(temp.Path))
            using (var handle = OpenForSecurity(file))
            {
                Assert.NotNull(entry);
                Assert.True(WindowsJournalSecurity.ApplyHardened(handle, owner.File.OwnerSid!, isDirectory: false));
            }

            using (var handle = OpenForSecurity(temp.Path))
                Assert.True(WindowsJournalSecurity.ApplyHardened(handle, owner.Folder.OwnerSid!, isDirectory: true));

            var after = WindowsJournalSecurity.Read(file);
            Assert.True(after.Folder.DaclProtected);
            Assert.True(after.File.DaclProtected);
            Assert.Equal(new[] { SystemSid, Admins, Users }, after.File.Dacl!.Select(a => a.Sid));
            Assert.All(after.Folder.Dacl!, a => Assert.False(a.InheritOnly));
            var verdict = JournalTrust.Evaluate(after);
            Assert.DoesNotContain(verdict.Reasons, r => r.Contains("grants write rights"));
        }
        finally
        {
            OpenUp(temp.Path, file);
        }
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenForSecurity(string path)
    {
        var handle = GameShift.Core.System.NativeInterop.CreateFile(
            path,
            GameShift.Core.System.NativeInterop.WRITE_DAC | GameShift.Core.System.NativeInterop.WRITE_OWNER,
            GameShift.Core.System.NativeInterop.FILE_SHARE_READ | GameShift.Core.System.NativeInterop.FILE_SHARE_WRITE,
            IntPtr.Zero, GameShift.Core.System.NativeInterop.OPEN_EXISTING,
            GameShift.Core.System.NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | GameShift.Core.System.NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        return handle;
    }

    private static void OpenUp(string folder, string file)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var me = identity.User!;
        var folderSecurity = new DirectorySecurity();
        folderSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        folderSecurity.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(folderSecurity);

        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(fileSecurity);
    }

    // ── Repair decision ───────────────────────────────────────────────────

    private sealed class FakeEntry : IJournalSecurityEntry
    {
        private readonly FakeStore _store;
        private readonly string _path;

        public FakeEntry(FakeStore store, string path, bool isDirectory, bool isReparsePoint, int linkCount)
        {
            _store = store;
            _path = path;
            IsDirectory = isDirectory;
            IsReparsePoint = isReparsePoint;
            LinkCount = linkCount;
        }

        public bool IsDirectory { get; }
        public bool IsReparsePoint { get; }
        public int LinkCount { get; }

        public bool ApplyHardened()
        {
            _store.Hardened.Add(_path);
            return !_store.FailHardening.Contains(_path);
        }

        public void Dispose() { }
    }

    private sealed class FakeStore : IJournalSecurityStore
    {
        public readonly Dictionary<string, (bool Dir, bool Reparse, int Links)> Entries = new();
        public readonly List<string> Opened = new();
        public readonly List<string> Hardened = new();
        public readonly HashSet<string> FailHardening = new();
        public int PrivilegeScopes;
        public bool PrivilegesReleased;

        public IDisposable EnablePrivileges()
        {
            PrivilegeScopes++;
            return new Release(this);
        }

        public IJournalSecurityEntry? Open(string path)
        {
            Opened.Add(path);
            return Entries.TryGetValue(path, out var e) ? new FakeEntry(this, path, e.Dir, e.Reparse, e.Links) : null;
        }

        private sealed class Release : IDisposable
        {
            private readonly FakeStore _store;
            public Release(FakeStore store) => _store = store;
            public void Dispose() => _store.PrivilegesReleased = true;
        }
    }

    private const string RepairFolder = @"C:\ProgramData\GameShift";

    private static FakeStore StoreWithFolder(bool folderIsReparse = false)
    {
        var store = new FakeStore();
        store.Entries[RepairFolder] = (true, folderIsReparse, 1);
        return store;
    }

    // The repair cannot tell a file it wrote from one a user planted, even mid-repair, so it never
    // re-owns an entry: only the folder is opened and hardened.
    [Fact]
    public void Repair_HardensOnlyTheFolder_AndNeverOpensItsEntries()
    {
        var store = StoreWithFolder();
        foreach (var name in new[] { "state.json", "state.untrusted-1.json", "old" })
            store.Entries[Path.Combine(RepairFolder, name)] = (name == "old", false, 1);

        Assert.True(JournalTrust.Repair(RepairFolder, store, Logger.None));

        Assert.Equal(new[] { RepairFolder }, store.Opened);
        Assert.Equal(new[] { RepairFolder }, store.Hardened);
        Assert.Equal(1, store.PrivilegeScopes);
        Assert.True(store.PrivilegesReleased);
    }

    [Fact]
    public void Repair_OfAFolderThatIsAReparsePoint_ChangesNothing()
    {
        var store = StoreWithFolder(folderIsReparse: true);

        Assert.False(JournalTrust.Repair(RepairFolder, store, Logger.None));

        Assert.Empty(store.Hardened);
    }

    [Fact]
    public void Repair_StopsWhenTheFolderCannotBeOpenedOrHardened()
    {
        var unopenable = new FakeStore();
        Assert.False(JournalTrust.Repair(RepairFolder, unopenable, Logger.None));
        Assert.Empty(unopenable.Hardened);

        var store = StoreWithFolder();
        store.FailHardening.Add(RepairFolder);

        Assert.False(JournalTrust.Repair(RepairFolder, store, Logger.None));
        Assert.Equal(new[] { RepairFolder }, store.Hardened);
    }

    /// <summary>
    /// Opens real entries and hardens them with their current owner, as far as an unelevated test
    /// can go (naming Administrators as owner needs elevation).
    /// </summary>
    private sealed class OwnerKeepingStore : IJournalSecurityStore
    {
        public IDisposable EnablePrivileges() => new NoScope();

        public IJournalSecurityEntry? Open(string path)
        {
            var owner = DescribeEntry(path).OwnerSid!;
            return new RealEntry(OpenForSecurity(path), owner);
        }

        private sealed class NoScope : IDisposable
        {
            public void Dispose() { }
        }

        private sealed class RealEntry : IJournalSecurityEntry
        {
            private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle;
            private readonly string _owner;

            public RealEntry(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string owner)
            {
                _handle = handle;
                _owner = owner;
            }

            public bool IsDirectory => true;
            public bool IsReparsePoint => false;
            public int LinkCount => 1;
            public bool ApplyHardened() => WindowsJournalSecurity.ApplyHardened(_handle, _owner, IsDirectory);
            public void Dispose() => _handle.Dispose();
        }
    }

    private static JournalObjectSecurity DescribeEntry(string path)
    {
        using var handle = GameShift.Core.System.NativeInterop.CreateFile(
            path,
            GameShift.Core.System.NativeInterop.READ_CONTROL | GameShift.Core.System.NativeInterop.FILE_READ_ATTRIBUTES,
            GameShift.Core.System.NativeInterop.FILE_SHARE_READ | GameShift.Core.System.NativeInterop.FILE_SHARE_WRITE | GameShift.Core.System.NativeInterop.FILE_SHARE_DELETE,
            IntPtr.Zero, GameShift.Core.System.NativeInterop.OPEN_EXISTING,
            GameShift.Core.System.NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | GameShift.Core.System.NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        return WindowsJournalSecurity.Describe(handle);
    }

    // A user re-plants state.json before the repair locks the folder: the file keeps its owner and
    // DACL, so it still fails the trust check afterwards.
    [Fact]
    public void Repair_LeavesAFileNamedLikeTheJournalAsItWas()
    {
        using var temp = new TempPath();
        var planted = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(planted, "{}");
        var before = DescribeEntry(planted);

        try
        {
            Assert.True(JournalTrust.Repair(temp.Path, new OwnerKeepingStore(), Logger.None));

            var after = DescribeEntry(planted);
            Assert.Equal(before.OwnerSid, after.OwnerSid);
            Assert.Equal(before.DaclProtected, after.DaclProtected);
            Assert.Equal(before.Dacl!.Select(a => (a.Sid, a.Type, a.AccessMask)), after.Dacl!.Select(a => (a.Sid, a.Type, a.AccessMask)));
            Assert.True(DescribeEntry(temp.Path).DaclProtected);

            var verdict = JournalTrust.Check(planted);
            Assert.False(verdict.Trusted);
            if (before.OwnerSid != Admins)
                Assert.Contains(verdict.Reasons, r => r.Contains("file is owned by " + before.OwnerSid));
        }
        finally
        {
            OpenUp(temp.Path, planted);
        }
    }

    // ── Rename under a DACL that shuts the caller out ─────────────────────

    // Unelevated, the backup and restore privileges are not held, so the rename fails cleanly and
    // leaves the file. That the privileges let it through elevated is a VM check.
    [Fact]
    public void RenameInPlace_WhenDeleteIsDenied_SucceedsOnlyWithThePrivileges()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(path, "journal");
        using var identity = WindowsIdentity.GetCurrent();
        var me = identity.User!;
        var denyDelete = new FileSystemAccessRule(me, FileSystemRights.Delete, AccessControlType.Deny);
        var denyDeleteChild = new FileSystemAccessRule(me, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
        var fileSecurity = new FileInfo(path).GetAccessControl();
        fileSecurity.AddAccessRule(denyDelete);
        new FileInfo(path).SetAccessControl(fileSecurity);
        var folderSecurity = new DirectoryInfo(temp.Path).GetAccessControl();
        folderSecurity.AddAccessRule(denyDeleteChild);
        new DirectoryInfo(temp.Path).SetAccessControl(folderSecurity);
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        try
        {
            var renamed = WindowsJournalSecurity.RenameInPlace(path, "state.untrusted-x.json");

            if (!elevated)
            {
                Assert.False(renamed);
                Assert.Equal("journal", global::System.IO.File.ReadAllText(path));
            }
            else
            {
                Assert.NotEqual(renamed, global::System.IO.File.Exists(path));
            }
        }
        finally
        {
            var folder = new DirectoryInfo(temp.Path).GetAccessControl();
            folder.RemoveAccessRule(denyDeleteChild);
            new DirectoryInfo(temp.Path).SetAccessControl(folder);
            foreach (var file in Directory.GetFiles(temp.Path))
            {
                var security = new FileInfo(file).GetAccessControl();
                security.RemoveAccessRule(denyDelete);
                new FileInfo(file).SetAccessControl(security);
            }
        }
    }

    // ── Folder hardening at construction ──────────────────────────────────

    [Fact]
    public void HardenJournalDirectory_RefusesAJunction_AndLeavesItsTargetAlone()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "someone-elses-folder");
        Directory.CreateDirectory(target);
        var before = DescribeEntry(target);
        var junction = Path.Combine(temp.Path, "GameShift");
        MkLink("/J", junction, target);

        try
        {
            Assert.False(JournalManager.TryHardenJournalDirectory(junction, Logger.None));

            var after = DescribeEntry(target);
            Assert.Equal(before.OwnerSid, after.OwnerSid);
            Assert.Equal(before.DaclProtected, after.DaclProtected);
            Assert.Equal(before.Dacl!.Select(a => (a.Sid, a.Type, a.AccessMask, a.InheritOnly)),
                after.Dacl!.Select(a => (a.Sid, a.Type, a.AccessMask, a.InheritOnly)));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    // Set on the folder's handle: the folder gets the protected DACL, and nothing is propagated to
    // the entries already in it.
    [Fact]
    public void HardenJournalDirectory_OnAPlainFolder_SetsTheProtectedDaclOnTheFolderOnly()
    {
        using var temp = new TempPath();
        var file = temp.GetFile("state.json");
        global::System.IO.File.WriteAllText(file, "{}");
        var fileBefore = DescribeEntry(file);

        try
        {
            Assert.True(JournalManager.TryHardenJournalDirectory(temp.Path, Logger.None));

            var folder = DescribeEntry(temp.Path);
            Assert.True(folder.DaclProtected);
            Assert.Equal(new[] { SystemSid, Admins, Users }, folder.Dacl!.Select(a => a.Sid));
            var fileAfter = DescribeEntry(file);
            Assert.Equal(fileBefore.Dacl!.Select(a => (a.Sid, a.AccessMask)), fileAfter.Dacl!.Select(a => (a.Sid, a.AccessMask)));
        }
        finally
        {
            OpenUp(temp.Path, file);
        }
    }

    // ── Administrators as owner of a file GameShift writes ────────────────

    // Naming Administrators as owner needs an elevated token; unelevated the call fails and changes
    // nothing, which is what a save then tolerates.
    [Fact]
    public void TrySetAdministratorsOwner_SetsTheOwnerOnlyWhenElevated()
    {
        using var temp = new TempPath();
        var file = temp.GetFile("state.json.tmp");
        global::System.IO.File.WriteAllText(file, "{}");
        var before = DescribeEntry(file);
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        Assert.Equal(elevated, WindowsJournalSecurity.TrySetAdministratorsOwner(file));

        Assert.Equal(elevated ? Admins : before.OwnerSid, DescribeEntry(file).OwnerSid);
    }

    [Fact]
    public void Save_CompletesWhetherOrNotTheOwnerCanBeSet()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);

        journal.StartSession(new GameShift.Core.Profiles.GameProfile { Id = "t", GameName = "Game", ExecutableName = "game.exe" });
        Assert.NotNull(journal.SaveEvidenceCopy("unrecovered-test"));

        Assert.True(journal.PeekJournal()!.SessionActive);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Equal(Admins, DescribeEntry(path).OwnerSid);
            Assert.Equal(Admins, DescribeEntry(temp.GetFile("state.unrecovered-test.json")).OwnerSid);
        }
    }
}
