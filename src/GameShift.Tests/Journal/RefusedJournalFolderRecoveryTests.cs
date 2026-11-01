using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Microsoft.Win32.SafeHandles;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Startup recovery when a standard user put a junction in place of the journal folder, pointing at
/// a folder of theirs with an active session record. Run unelevated on temp folders with real
/// junctions, which need no privilege; the trust check is a counting fake that would accept the
/// record, so any read of it would show as a trust call and a replay attempt.
/// </summary>
public class RefusedJournalFolderRecoveryTests
{
    private const string Planted = """
{
  "Version": 2,
  "SessionActive": true,
  "ActiveGame": { "Name": "Planted record", "Executable": "planted.exe", "Pid": 4, "StartTime": "2026-10-31T10:00:00+01:00" },
  "Optimizations": [
    { "Name": "Opt1", "State": "Applied", "OriginalValue": "orig", "AppliedValue": "applied", "AppliedAt": "2026-10-31T10:00:00+01:00" }
  ],
  "GameActions": []
}
""";

    private readonly List<string> _sequence = new();
    private int _trustChecks;

    private static string MySid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    private static void MkLink(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit(10_000);
        Assert.Equal(0, process.ExitCode);
    }

    private static void RemoveLink(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(path);
    }

    /// <summary>A hardened folder grants Users only read, so the test gives itself full control back before cleanup.</summary>
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

    // Read access with read sharing only, on the junction itself: nobody can open it for delete.
    private static SafeFileHandle HoldJunction(string path) =>
        NativeInterop.CreateFile(path, NativeInterop.FILE_LIST_DIRECTORY | NativeInterop.FILE_READ_ATTRIBUTES,
            NativeInterop.FILE_SHARE_READ, IntPtr.Zero, NativeInterop.OPEN_EXISTING,
            NativeInterop.FILE_FLAG_BACKUP_SEMANTICS | NativeInterop.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

    private StartupRecoveryResult Run(JournalManager journal) =>
        StartupRecovery.Run(
            journal,
            _ =>
            {
                _trustChecks++;
                return new JournalTrustResult(true, Array.Empty<string>());
            },
            new WatchdogRevertEngine(Logger.None, new Dictionary<string, Func<IJournaledOptimization>>
            {
                ["Opt1"] = () => new RecordingMockJournaledOptimization("Opt1", _sequence),
            }),
            Logger.None,
            (_, _) => true);

    private static (string Target, string Dir) PlantJunction(TempPath temp)
    {
        var target = Path.Combine(temp.Path, "users-folder");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "state.json"), Planted);
        var dir = Path.Combine(temp.Path, "GameShift");
        MkLink(dir, target);
        return (target, dir);
    }

    [Fact]
    public void HeldJunction_ReadsNothingThroughIt_AndTellsTheUserRecoveryIsOff()
    {
        using var temp = new TempPath();
        var (target, dir) = PlantJunction(temp);

        using var held = HoldJunction(dir);
        try
        {
            Assert.False(held.IsInvalid);
            var journal = new JournalManager(Path.Combine(dir, "state.json"), MySid(), afterReparseRemoved: null);
            Assert.NotNull(journal.FolderRefusal);

            var result = Run(journal);

            Assert.Equal(StartupRecoveryOutcome.FolderUnavailable, result.Outcome);
            Assert.True(result.WasSessionActive);
            Assert.Empty(result.Items);
            Assert.Null(result.GameName);
            Assert.Equal(
                string.Format(CultureInfo.InvariantCulture, StartupRecovery.FolderUnavailableNotice, dir),
                result.Notice);
            Assert.Equal(0, _trustChecks);
            Assert.Empty(_sequence);
            Assert.True((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(new[] { "state.json" }, Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
            Assert.Equal(Planted, File.ReadAllText(Path.Combine(target, "state.json")));
        }
        finally
        {
            held.Dispose();
            RemoveLink(dir);
        }
    }

    [Fact]
    public void Junction_NotHeld_IsReplacedByAnEmptyFolder_AndThereIsNothingToSay()
    {
        using var temp = new TempPath();
        var (target, dir) = PlantJunction(temp);

        try
        {
            var journal = new JournalManager(Path.Combine(dir, "state.json"), MySid(), afterReparseRemoved: null);
            Assert.Null(journal.FolderRefusal);

            var result = Run(journal);

            Assert.Equal(StartupRecoveryOutcome.NothingToDo, result.Outcome);
            Assert.False(result.WasSessionActive);
            Assert.Null(result.Notice);
            Assert.Equal(0, _trustChecks);
            Assert.False((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(Planted, File.ReadAllText(Path.Combine(target, "state.json")));
        }
        finally
        {
            RemoveLink(dir);
            OpenUp(dir);
        }
    }

    [Fact]
    public void PlainFolder_IsNotRefused_AndItsRecordGoesToTheTrustCheck()
    {
        using var temp = new TempPath();
        var dir = Path.Combine(temp.Path, "GameShift");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), Planted);

        try
        {
            var journal = new JournalManager(Path.Combine(dir, "state.json"), MySid(), afterReparseRemoved: null);
            Assert.Null(journal.FolderRefusal);

            var result = Run(journal);

            Assert.Equal(1, _trustChecks);
            Assert.NotEqual(StartupRecoveryOutcome.FolderUnavailable, result.Outcome);
            Assert.True(result.WasSessionActive);
            Assert.Equal("Planted record", result.GameName);
        }
        finally
        {
            OpenUp(dir);
        }
    }
}
