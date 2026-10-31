using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// The elevated app never writes the journal through a folder a standard user redirected, and
/// prepares the shared folder so no user owns or can write it. Run unelevated on temp folders with
/// real junctions and hard links, which need no privilege; the Administrators owner itself is only
/// checked where the test runs elevated.
/// </summary>
public class JournalFolderTests
{
    private const string SystemSid = "S-1-5-18";
    private const string Admins = "S-1-5-32-544";
    private const string Users = "S-1-5-32-545";
    private const int FullControl = 0x1F01FF;
    private const int ReadAndExecute = 0x1200A9;

    private static readonly (string, JournalAceType, int)[] HardenedAces =
    {
        (SystemSid, JournalAceType.Allow, FullControl),
        (Admins, JournalAceType.Allow, FullControl),
        (Users, JournalAceType.Allow, ReadAndExecute),
    };

    private static readonly GameProfile Profile = new()
    {
        Id = "test",
        GameName = "TestGame",
        ExecutableName = "test.exe",
        ProcessId = 1234,
    };

    private static string MySid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void MkLink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/c", "mklink", kind, link, target })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit(10_000);
        Assert.Equal(0, process.ExitCode);
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

    /// <summary>Names, contents and the folder's DACL, to show a folder was left exactly as it was.</summary>
    private static string Snapshot(string folder)
    {
        var text = new StringBuilder();
        var security = Describe(folder);
        text.Append(security.OwnerSid).Append('|').Append(security.DaclProtected).Append('|');
        foreach (var ace in security.Dacl!)
            text.Append(ace.Sid).Append(ace.Type).Append(ace.AccessMask).Append(ace.InheritOnly).Append(';');
        foreach (var file in Directory.GetFileSystemEntries(folder).OrderBy(f => f, StringComparer.Ordinal))
            text.Append('\n').Append(Path.GetFileName(file)).Append('=').Append(File.ReadAllText(file));
        return text.ToString();
    }

    /// <summary>
    /// A folder created hardened grants Users only read, so the test gives itself full control
    /// back (it is the owner) before the temp folder is deleted.
    /// </summary>
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

    private static void RemoveLink(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(path);
    }

    private static void WriteActiveJournal(string path) =>
        File.WriteAllText(path, "{\"Version\":2,\"SessionActive\":true,\"Optimizations\":[]}");

    // ── Writes through a redirected folder ────────────────────────────────

    [Fact]
    public void Writes_IntoAJunctionedFolder_AreRefused_AndTheTargetStaysAsItWas()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "someone-elses-folder");
        Directory.CreateDirectory(target);
        WriteActiveJournal(Path.Combine(target, "state.json"));
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        var before = Snapshot(target);
        var junction = Path.Combine(temp.Path, "GameShift");
        MkLink("/J", junction, target);

        try
        {
            var journal = new JournalManager(Path.Combine(junction, "state.json"));

            journal.StartSession(Profile);
            journal.RecordApplied(new OptimizationResult("Opt", "orig", "applied", OptimizationState.Applied));
            Assert.False(journal.RecordRecoveryAttempt());
            Assert.Null(journal.SaveEvidenceCopy("unrecovered-x"));
            journal.EndSession();

            Assert.Equal(before, Snapshot(target));
        }
        finally
        {
            RemoveLink(junction);
        }
    }

    // The folder is swapped for a junction after the manager was built and wrote there once: later
    // writes do not follow the path into the new target.
    [Fact]
    public void Writes_AfterTheFolderIsSwappedForAJunction_DoNotFollowIt()
    {
        using var temp = new TempPath();
        var dir = Path.Combine(temp.Path, "GameShift");
        Directory.CreateDirectory(dir);
        var journal = new JournalManager(Path.Combine(dir, "state.json"));
        journal.StartSession(Profile);
        Assert.True(File.Exists(Path.Combine(dir, "state.json")));

        Directory.Move(dir, Path.Combine(temp.Path, "moved-away"));
        var target = Path.Combine(temp.Path, "someone-elses-folder");
        Directory.CreateDirectory(target);
        var before = Snapshot(target);
        MkLink("/J", dir, target);

        try
        {
            journal.RecordApplied(new OptimizationResult("Opt", "orig", "applied", OptimizationState.Applied));
            Assert.Null(journal.SaveEvidenceCopy("unrecovered-x"));
            journal.EndSession();

            Assert.Equal(before, Snapshot(target));
        }
        finally
        {
            RemoveLink(dir);
        }
    }

    // A planted temp name or journal name that is a hard link to another file: the write replaces
    // the name and never writes into the linked file.
    [Theory]
    [InlineData("state.json.tmp")]
    [InlineData("state.json")]
    public void WriteFileAtomically_OverAHardLink_LeavesTheLinkedFileAlone(string planted)
    {
        using var temp = new TempPath();
        var victim = temp.GetFile("victim.txt");
        File.WriteAllText(victim, "victim");
        MkLink("/H", temp.GetFile(planted), victim);

        using (var folder = JournalFolder.Open(temp.Path, out var refusal))
        {
            Assert.Null(refusal);
            folder!.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("journal"));
        }

        Assert.Equal("victim", File.ReadAllText(victim));
        Assert.Equal(1, Describe(victim).LinkCount);
        Assert.Equal("journal", File.ReadAllText(temp.GetFile("state.json")));
        Assert.False(File.Exists(temp.GetFile("state.json.tmp")));
    }

    [Fact]
    public void Open_RefusesAJunction_AndReadsAndWritesInAPlainFolder()
    {
        using var temp = new TempPath();
        var real = Path.Combine(temp.Path, "real");
        Directory.CreateDirectory(real);
        var junction = Path.Combine(temp.Path, "GameShift");
        MkLink("/J", junction, real);

        try
        {
            Assert.Null(JournalFolder.Open(junction, out var refusal));
            Assert.Equal("it is a reparse point", refusal);

            using var folder = JournalFolder.Open(real, out refusal);
            Assert.Null(refusal);
            Assert.Null(folder!.ReadFile("state.json"));
            folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("one"));
            folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("two"));
            Assert.Equal("two", Encoding.UTF8.GetString(folder.ReadFile("state.json")!));
        }
        finally
        {
            RemoveLink(junction);
        }
    }

    // ── Replacing a file another program has open ─────────────────────────

    private static FileStream OpenReader(string path, FileShare share) =>
        new(path, FileMode.Open, FileAccess.Read, share);

    private static string ReadAll(FileStream reader) => new StreamReader(reader).ReadToEnd();

    [Fact]
    public void WriteFileAtomically_WhileAReaderHoldsTheFileWithDeleteSharing_ReplacesIt_AndTheReaderKeepsTheOldContent()
    {
        using var temp = new TempPath();
        using var folder = JournalFolder.Open(temp.Path, out _)!;
        folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("old"));
        var waits = new List<int>();
        folder.WaitBeforeReplaceRetry = waits.Add;

        using var reader = OpenReader(temp.GetFile("state.json"), FileShare.ReadWrite | FileShare.Delete);
        folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("new"));

        Assert.Empty(waits);
        Assert.Equal("new", Encoding.UTF8.GetString(folder.ReadFile("state.json")!));
        Assert.Equal("old", ReadAll(reader));
        Assert.False(File.Exists(temp.GetFile("state.json.tmp")));
    }

    [Fact]
    public void WriteFileAtomically_WhileAReaderHoldsTheFileWithoutDeleteSharing_RetriesFourTimes_ThenFailsCleanly()
    {
        using var temp = new TempPath();
        using var folder = JournalFolder.Open(temp.Path, out _)!;
        folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("old"));
        var waits = new List<int>();
        folder.WaitBeforeReplaceRetry = waits.Add;

        using (OpenReader(temp.GetFile("state.json"), FileShare.ReadWrite))
            Assert.Throws<IOException>(() => folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("new")));

        Assert.Equal(new[] { 1, 2, 3, 4 }, waits);
        Assert.Equal("old", File.ReadAllText(temp.GetFile("state.json")));
        Assert.False(File.Exists(temp.GetFile("state.json.tmp")));
    }

    [Fact]
    public void WriteFileAtomically_WhenTheReaderClosesBeforeTheRetry_Succeeds()
    {
        using var temp = new TempPath();
        using var folder = JournalFolder.Open(temp.Path, out _)!;
        folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("old"));
        var reader = OpenReader(temp.GetFile("state.json"), FileShare.ReadWrite);
        var waits = new List<int>();
        folder.WaitBeforeReplaceRetry = attempt =>
        {
            waits.Add(attempt);
            reader.Dispose();
        };

        try
        {
            folder.WriteFileAtomically("state.json", Encoding.UTF8.GetBytes("new"));
        }
        finally
        {
            reader.Dispose();
        }

        Assert.Equal(new[] { 1 }, waits);
        Assert.Equal("new", File.ReadAllText(temp.GetFile("state.json")));
        Assert.False(File.Exists(temp.GetFile("state.json.tmp")));
    }

    // The recovery path: an attempt is counted only when the new count reached the disk.
    [Fact]
    public void RecordRecoveryAttempt_WhileAReaderHoldsTheJournalWithDeleteSharing_Succeeds()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);
        journal.StartSession(Profile);

        using var reader = OpenReader(path, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(journal.RecordRecoveryAttempt());
        Assert.Equal(1, journal.PeekJournal()!.RecoveryAttempts);
    }

    [Fact]
    public void RecordRecoveryAttempt_WhileAReaderHoldsTheJournalWithoutDeleteSharing_ReturnsFalse_AndLeavesItUnchanged()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("state.json");
        var journal = new JournalManager(path);
        journal.StartSession(Profile);
        var before = File.ReadAllBytes(path);

        using (OpenReader(path, FileShare.ReadWrite))
            Assert.False(journal.RecordRecoveryAttempt());

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    // ── Preparing the shared folder ───────────────────────────────────────

    [Fact]
    public void Prepare_RemovesAJunction_LeavesItsTarget_AndCreatesAHardenedFolder()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "someone-elses-folder");
        Directory.CreateDirectory(target);
        WriteActiveJournal(Path.Combine(target, "state.json"));
        var before = Snapshot(target);
        var dir = Path.Combine(temp.Path, "GameShift");
        MkLink("/J", dir, target);
        var me = MySid();

        try
        {
            var setup = JournalManager.PrepareJournalDirectory(dir, me, Logger.None, out var reason);

            Assert.Equal(JournalFolderSetup.Ready, setup);
            Assert.Null(reason);
            Assert.Equal(before, Snapshot(target));

            var created = Describe(dir);
            Assert.True(created.IsDirectory);
            Assert.False(created.IsReparsePoint);
            Assert.Equal(me, created.OwnerSid);
            Assert.True(created.DaclProtected);
            Assert.Equal(HardenedAces, created.Dacl!.Select(a => (a.Sid!, a.Type, a.AccessMask)));
            Assert.Empty(Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            RemoveLink(dir);
            OpenUp(dir);
        }
    }

    // Unelevated, Administrators cannot be named as owner, so nothing is created; elevated, the
    // folder is owned by Administrators from the start. The elevated half runs on the VM.
    [Fact]
    public void Prepare_WithAdministratorsAsOwner_CreatesItOwnedByAdministratorsOrNothing()
    {
        using var temp = new TempPath();
        var dir = Path.Combine(temp.Path, "GameShift");

        try
        {
            var setup = JournalManager.PrepareJournalDirectory(dir, Admins, Logger.None, out var reason);

            if (IsElevated())
            {
                Assert.Equal(JournalFolderSetup.Ready, setup);
                Assert.Equal(Admins, Describe(dir).OwnerSid);
            }
            else
            {
                Assert.Equal(JournalFolderSetup.Unavailable, setup);
                Assert.Contains("could not be created", reason);
                Assert.False(Directory.Exists(dir));
            }
        }
        finally
        {
            OpenUp(dir);
        }
    }

    [Fact]
    public void Prepare_WithAFileInItsPlace_LeavesJournalingOff()
    {
        using var temp = new TempPath();
        var dir = temp.GetFile("GameShift");
        File.WriteAllText(dir, "not a folder");

        var setup = JournalManager.PrepareJournalDirectory(dir, MySid(), Logger.None, out var reason);

        Assert.Equal(JournalFolderSetup.Unavailable, setup);
        Assert.Equal("a file is in its place", reason);
        Assert.Equal("not a folder", File.ReadAllText(dir));
    }

    // A user puts the junction back between its removal and the folder's creation: the creation
    // fails, nothing is written through it, and the target stays as it was.
    [Fact]
    public void Prepare_WhenTheJunctionReturnsBeforeCreation_LeavesJournalingOffAndWritesNowhere()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "someone-elses-folder");
        Directory.CreateDirectory(target);
        var before = Snapshot(target);
        var dir = Path.Combine(temp.Path, "GameShift");
        MkLink("/J", dir, target);

        try
        {
            var setup = JournalManager.PrepareJournalDirectory(dir, MySid(), Logger.None, out var reason,
                afterReparseRemoved: () => MkLink("/J", dir, target));

            Assert.Equal(JournalFolderSetup.Unavailable, setup);
            Assert.Contains("could not be created", reason);
            Assert.True((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(before, Snapshot(target));
        }
        finally
        {
            RemoveLink(dir);
        }
    }

    // Whatever reappears may be the user's, even a plain folder: the manager writes nothing there
    // for the rest of the run, although later writes would otherwise find a plain folder.
    [Fact]
    public void Manager_WhenAFolderReturnsBeforeCreation_WritesNothingThisRun()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "someone-elses-folder");
        Directory.CreateDirectory(target);
        var before = Snapshot(target);
        var dir = Path.Combine(temp.Path, "GameShift");
        MkLink("/J", dir, target);

        try
        {
            var journal = new JournalManager(Path.Combine(dir, "state.json"), MySid(),
                afterReparseRemoved: () => Directory.CreateDirectory(dir));

            journal.StartSession(Profile);
            journal.RecordApplied(new OptimizationResult("Opt", "orig", "applied", OptimizationState.Applied));
            Assert.Null(journal.SaveEvidenceCopy("unrecovered-x"));

            Assert.False((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0);
            Assert.Empty(Directory.GetFileSystemEntries(dir));
            Assert.Equal(before, Snapshot(target));
        }
        finally
        {
            RemoveLink(dir);
        }
    }

    // The folder the manager prepared is then written normally. Unelevated, the hardened DACL lets
    // the test only read, so it grants itself what Administrators have before writing.
    [Fact]
    public void Manager_AfterReplacingAJunction_WritesTheJournalInTheNewFolder()
    {
        using var temp = new TempPath();
        var target = Path.Combine(temp.Path, "someone-elses-folder");
        Directory.CreateDirectory(target);
        var before = Snapshot(target);
        var dir = Path.Combine(temp.Path, "GameShift");
        MkLink("/J", dir, target);

        try
        {
            var journal = new JournalManager(Path.Combine(dir, "state.json"), MySid(), afterReparseRemoved: null);
            Assert.False(Describe(dir).IsReparsePoint);
            if (!IsElevated())
                OpenUp(dir);

            journal.StartSession(Profile);
            var evidence = journal.SaveEvidenceCopy("unrecovered-x");

            Assert.True(journal.PeekJournal()!.SessionActive);
            Assert.Equal(Path.Combine(dir, "state.unrecovered-x.json"), evidence);
            Assert.True(File.Exists(evidence));
            Assert.Equal(before, Snapshot(target));
        }
        finally
        {
            RemoveLink(dir);
            OpenUp(dir);
        }
    }
}
