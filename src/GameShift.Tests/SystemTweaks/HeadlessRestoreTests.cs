using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.System;
using GameShift.Core.SystemTweaks;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.SystemTweaks;

/// <summary>
/// The uninstaller's headless commands, through fakes for the instance lock, crash recovery and the
/// lasting-change reverters, so nothing on the test machine is changed. Some tests read settings.json
/// from a temp folder through the real loader, so the class shares the config-state collection.
/// </summary>
[Collection("ConfigState")]
public class HeadlessRestoreTests
{
    private const string NetworkFix = "ndis_interrupt_moderation";

    private static readonly StartupRecoveryResult NoSession =
        new(WasSessionActive: false, StartupRecoveryOutcome.NothingToDo, Array.Empty<JournalRevertItem>());

    private sealed class Lock : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Run
    {
        public List<string> Calls { get; } = new();
        public FakeSystem System { get; } = new();
        public StartupRecoveryResult Recovery { get; set; } = NoSession;
        public Exception? RecoveryThrows { get; set; }
        public Exception? InventoryThrows { get; set; }
        public bool SettingsAvailable { get; set; } = true;
        public Exception? SummaryThrows { get; set; }
        public string? Summary { get; private set; }

        public int Restore() => HeadlessRestore.RestoreTweaks(
            () =>
            {
                Calls.Add("recover");
                return RecoveryThrows != null ? throw RecoveryThrows : Recovery;
            },
            () =>
            {
                Calls.Add("inventory");
                return InventoryThrows != null ? throw InventoryThrows : new LastingChangesInventory(System, Serilog.Core.Logger.None);
            },
            () => SettingsAvailable,
            text =>
            {
                Calls.Add("summary");
                if (SummaryThrows != null) throw SummaryThrows;
                Summary = text;
            },
            "Player",
            Serilog.Core.Logger.None);
    }

    private static Run WithRecordedNetworkFix()
    {
        var run = new Run();
        run.System.Settings.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = NetworkFix, ActionType = "SetNetAdapterProperty" });
        return run;
    }

    // ── Dispatch and the instance lock ───────────────────────────────

    [Fact]
    public void GameShiftRunning_ExitsWithThree_AndChangesNothing()
    {
        var calls = new List<string>();

        var code = HeadlessRestore.Run(
            new LaunchOptions(false, RestoreTweaks: true, RemoveTasks: true),
            () => null,
            () => { calls.Add("restore"); return 0; },
            () => { calls.Add("tasks"); return 0; },
            Serilog.Core.Logger.None);

        Assert.Equal(HeadlessExitCode.AlreadyRunning, code);
        Assert.Empty(calls);
    }

    [Fact]
    public void BothCommands_RunUnderTheLock_RestoreFirst_AndTheWorstCodeWins()
    {
        var calls = new List<string>();
        var instance = new Lock();

        var code = HeadlessRestore.Run(
            new LaunchOptions(false, RestoreTweaks: true, RemoveTasks: true),
            () => instance,
            () => { calls.Add("restore"); Assert.False(instance.Disposed); return HeadlessExitCode.SomethingLeft; },
            () => { calls.Add("tasks"); Assert.False(instance.Disposed); return HeadlessExitCode.Done; },
            Serilog.Core.Logger.None);

        Assert.Equal(new[] { "restore", "tasks" }, calls);
        Assert.Equal(HeadlessExitCode.SomethingLeft, code);
        Assert.True(instance.Disposed);
    }

    [Theory]
    [InlineData(true, false, "restore")]
    [InlineData(false, true, "tasks")]
    public void OnlyTheCommandAskedFor_Runs(bool restore, bool removeTasks, string expected)
    {
        var calls = new List<string>();

        HeadlessRestore.Run(
            new LaunchOptions(false, restore, removeTasks),
            () => new Lock(),
            () => { calls.Add("restore"); return 0; },
            () => { calls.Add("tasks"); return 0; },
            Serilog.Core.Logger.None);

        Assert.Equal(new[] { expected }, calls);
    }

    [Fact]
    public void ACommandThatThrows_ExitsWithTwo_AndTheOtherStillRuns()
    {
        var calls = new List<string>();

        var code = HeadlessRestore.Run(
            new LaunchOptions(false, RestoreTweaks: true, RemoveTasks: true),
            () => new Lock(),
            () => throw new InvalidOperationException("boom"),
            () => { calls.Add("tasks"); return 0; },
            Serilog.Core.Logger.None);

        Assert.Equal(HeadlessExitCode.Error, code);
        Assert.Equal(new[] { "tasks" }, calls);
    }

    // ── Restore ──────────────────────────────────────────────────────

    [Fact]
    public void RecordedChange_IsUndone_AndTheRunExitsWithZero()
    {
        var run = WithRecordedNetworkFix();

        var code = run.Restore();

        Assert.Equal(HeadlessExitCode.Done, code);
        Assert.Equal(new[] { "dpc:" + NetworkFix }, run.System.Calls);
        Assert.Empty(run.System.Settings.AppliedDpcFixes);
    }

    [Fact]
    public void RecoveryRuns_BeforeTheInventory_AndTheSummaryIsWrittenLast()
    {
        var run = WithRecordedNetworkFix();

        run.Restore();

        Assert.Equal(new[] { "recover", "inventory", "summary" }, run.Calls);
    }

    [Fact]
    public void FailedUndo_ExitsWithOne_AndKeepsItsLedgerEntry()
    {
        var run = WithRecordedNetworkFix();
        run.System.RevertsFail = true;

        var code = run.Restore();

        Assert.Equal(HeadlessExitCode.SomethingLeft, code);
        Assert.Single(run.System.Settings.AppliedDpcFixes);
        Assert.Contains("Failed", run.Summary);
    }

    [Fact]
    public void ChangesNeedingTheirOwnConfirmation_AreNeverUndone_AndAreListed()
    {
        var run = WithRecordedNetworkFix();
        run.System.CoreReservationPresent = true;
        run.System.StartupEntries.Add(new RenamedStartupEntry(true, "~Discord"));

        var code = run.Restore();

        Assert.Equal(HeadlessExitCode.SomethingLeft, code);
        Assert.Equal(new[] { "dpc:" + NetworkFix }, run.System.Calls);
        Assert.Contains("Not undone", run.Summary);
        Assert.Contains("Discord", run.Summary);
        Assert.Contains("CPU core reservation", run.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingRecorded_ExitsWithZero_AndSaysSoForThisAccount()
    {
        var run = new Run();

        var code = run.Restore();

        Assert.Equal(HeadlessExitCode.Done, code);
        Assert.Empty(run.System.Calls);
        Assert.Contains("\"Player\"", run.Summary);
        Assert.Contains("other Windows accounts", run.Summary);
        Assert.Contains("found no lasting changes", run.Summary);
    }

    [Theory]
    [InlineData(StartupRecoveryOutcome.Partial, true)]
    [InlineData(StartupRecoveryOutcome.Untrusted, true)]
    [InlineData(StartupRecoveryOutcome.GaveUp, true)]
    [InlineData(StartupRecoveryOutcome.FolderUnavailable, true)]
    [InlineData(StartupRecoveryOutcome.NothingToDo, true)] // an active session this version left alone
    public void RecoveryThatLeftSomething_ExitsWithOne(StartupRecoveryOutcome outcome, bool wasActive)
    {
        var run = new Run { Recovery = new(wasActive, outcome, Array.Empty<JournalRevertItem>(), JournalFolder: @"C:\ProgramData\GameShift") };

        Assert.Equal(HeadlessExitCode.SomethingLeft, run.Restore());
    }

    [Fact]
    public void FullyRecoveredSession_ExitsWithZero_AndIsInTheSummary()
    {
        var run = new Run { Recovery = new(true, StartupRecoveryOutcome.Recovered, Array.Empty<JournalRevertItem>(), GameName: "Valorant") };

        Assert.Equal(HeadlessExitCode.Done, run.Restore());
        Assert.Contains("Valorant", run.Summary);
    }

    [Fact]
    public void RecoveryThatThrows_ExitsWithTwo_AndTheLastingChangesAreStillUndone()
    {
        var run = WithRecordedNetworkFix();
        run.RecoveryThrows = new IOException("journal locked");

        var code = run.Restore();

        Assert.Equal(HeadlessExitCode.Error, code);
        Assert.Empty(run.System.Settings.AppliedDpcFixes);
        Assert.Contains("stopped with an error", run.Summary);
    }

    [Fact]
    public void UnusableSettingsFolder_ExitsWithOne()
    {
        var run = new Run { SettingsAvailable = false };

        Assert.Equal(HeadlessExitCode.SomethingLeft, run.Restore());
        Assert.Contains("could not be read", run.Summary);
        Assert.Contains("could not read all of its records", run.Summary);
        Assert.DoesNotContain("found no lasting changes", run.Summary);
    }

    [Fact]
    public void UnreadableSettings_ExitWithOne()
    {
        var run = new Run();
        run.System.LoadFails = true;

        Assert.Equal(HeadlessExitCode.SomethingLeft, run.Restore());
    }

    [Theory]
    [InlineData("{ \"AppliedDpcFixes\": [")]
    [InlineData("null")]
    [InlineData("[]")]
    public void SettingsFileThatIsNotSettings_IsKeptAside_ExitsWithOne_AndNamesIt(string contents)
    {
        using var temp = new TempPath();
        var path = temp.GetFile("settings.json");
        File.WriteAllText(path, contents);
        SettingsManager.SettingsFilePathOverride = path;
        try
        {
            var run = new Run();
            run.System.LoadFromSettingsFile = true;

            Assert.Equal(HeadlessExitCode.SomethingLeft, run.Restore());
            var kept = Assert.Single(SettingsManager.GetUnreadableSettingsFiles());
            Assert.Contains(kept, run.Summary);
            Assert.Contains("cannot be undone automatically", run.Summary);
            Assert.Contains("could not read all of its records", run.Summary);
            Assert.DoesNotContain("found no lasting changes", run.Summary);

            // Still warned on the next run, when settings.json is a fresh file again.
            SettingsManager.Save(new AppSettings());
            var again = new Run();
            again.System.LoadFromSettingsFile = true;
            Assert.Equal(HeadlessExitCode.SomethingLeft, again.Restore());
            Assert.Contains(kept, again.Summary);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void SettingsFileThatCannotBeOpened_ExitsWithOne_AndSaysSo()
    {
        using var temp = new TempPath();
        var path = temp.GetFile("settings.json");
        SettingsManager.SettingsFilePathOverride = path;
        try
        {
            SettingsManager.Save(new AppSettings());
            var run = new Run();
            run.System.LoadFromSettingsFile = true;

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Equal(HeadlessExitCode.SomethingLeft, run.Restore());

            Assert.Contains("settings could not be read", run.Summary);
            Assert.DoesNotContain("found no lasting changes", run.Summary);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void KeptFilesThatCannotBeChecked_ExitWithOne()
    {
        var run = new Run();
        run.System.KeptFilesThrow = true;

        Assert.Equal(HeadlessExitCode.SomethingLeft, run.Restore());
        Assert.Contains("kept aside", run.Summary);
    }

    [Fact]
    public void NoSettingsFile_ExitsWithZero_AndNothingIsRecorded()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var run = new Run();
            run.System.LoadFromSettingsFile = true;

            Assert.Equal(HeadlessExitCode.Done, run.Restore());
            Assert.Contains("found no lasting changes", run.Summary);
            Assert.DoesNotContain("could not be read", run.Summary);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void ChangeRecordedInTheSettingsFile_IsUndone()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            var recorded = new AppSettings();
            recorded.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = NetworkFix, ActionType = "SetNetAdapterProperty" });
            SettingsManager.Save(recorded);
            var run = new Run();
            run.System.LoadFromSettingsFile = true;

            Assert.Equal(HeadlessExitCode.Done, run.Restore());
            Assert.Equal(new[] { "dpc:" + NetworkFix }, run.System.Calls);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void InventoryThatCannotBeCreated_ExitsWithTwo_AndTheSummaryIsStillWritten()
    {
        var run = new Run { InventoryThrows = new InvalidOperationException("WMI unavailable") };

        Assert.Equal(HeadlessExitCode.Error, run.Restore());
        Assert.NotNull(run.Summary);
    }

    [Fact]
    public void SummaryThatCannotBeWritten_DoesNotChangeTheExitCode()
    {
        var run = WithRecordedNetworkFix();
        run.SummaryThrows = new UnauthorizedAccessException("denied");

        Assert.Equal(HeadlessExitCode.Done, run.Restore());
    }

    [Fact]
    public void ExitCodes_AreTheOnesTheUninstallerReads()
    {
        Assert.Equal(0, HeadlessExitCode.Done);
        Assert.Equal(1, HeadlessExitCode.SomethingLeft);
        Assert.Equal(2, HeadlessExitCode.Error);
        Assert.Equal(3, HeadlessExitCode.AlreadyRunning);
    }

    /// <summary>Reverters that drop their ledger entry only on success, as the real ones do.</summary>
    private sealed class FakeSystem : ILastingChangeSystem
    {
        public AppSettings Settings { get; } = new();
        public bool RevertsFail { get; set; }
        public bool LoadFails { get; set; }

        /// <summary>Reads settings.json and kept files through the real inventory loader instead of <see cref="Settings"/>.</summary>
        public bool LoadFromSettingsFile { get; set; }
        public bool KeptFilesThrow { get; set; }
        public bool CoreReservationPresent { get; set; }
        public List<RenamedStartupEntry> StartupEntries { get; } = new();
        public List<string> Calls { get; } = new();

        public AppSettings LoadSettings() =>
            LoadFails ? throw new IOException("settings locked")
            : LoadFromSettingsFile ? WindowsLastingChangeSystem.LoadRecordedSettings()
            : Settings;

        public IReadOnlyList<string> DescribeUnreadableRecords() =>
            KeptFilesThrow ? throw new IOException("folder busy")
            : LoadFromSettingsFile ? WindowsLastingChangeSystem.DescribeKeptSettingsFiles()
            : [];

        public ISystemTweak? FindTweak(string className) => null;

        public bool RevertTweak(ISystemTweak tweak)
        {
            Calls.Add("tweak");
            return false;
        }

        public DpcFixResult RevertDpcFix(string fixId)
        {
            Calls.Add("dpc:" + fixId);
            if (RevertsFail) return new DpcFixResult { Success = false, Message = "adapter busy" };
            Settings.AppliedDpcFixes.RemoveAll(f => f.FixId == fixId);
            return new DpcFixResult { Success = true, Message = "reverted" };
        }

        public bool TryReadBcdValues(out IReadOnlyDictionary<string, string?> values)
        {
            values = KernelTuningManager.AllSettings.ToDictionary(s => s.BcdKey, _ => (string?)null, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        public (bool Success, string Message) RevertKernelSetting(KernelTuningSetting setting)
        {
            Calls.Add("kernel:" + setting.Id);
            return (false, "not expected");
        }

        public void ForgetKernelSetting(KernelTuningSetting setting) => Calls.Add("forget:" + setting.Id);

        public bool IsCoreReservationPresent() => CoreReservationPresent;

        public string? RemoveCoreReservation()
        {
            Calls.Add("core");
            return null;
        }

        public bool ReEnableMemoryIntegrity()
        {
            Calls.Add("vbs");
            return false;
        }

        public IReadOnlyList<RenamedStartupEntry> ReadRenamedStartupEntries() => StartupEntries;

        public string? RestoreStartupEntry(RenamedStartupEntry entry)
        {
            Calls.Add("startup:" + entry.ValueName);
            return null;
        }
    }
}
