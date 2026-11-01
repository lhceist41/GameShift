using System.Runtime.InteropServices;
using System.Xml.Linq;
using GameShift.Core.Config;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;
using Serilog;
using Xunit;

namespace GameShift.Tests.SystemRunner;

/// <summary>
/// Startup task registration through its Task Scheduler seam, so no task is registered on the test
/// machine. The executable path is the test host, which exists.
/// </summary>
[Collection("ConfigState")]
public class StartupManagerTests
{
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly string ExePath = Environment.ProcessPath!;

    private sealed class FakeRegistrar : StartupManager.ITaskRegistrar
    {
        public List<(string Path, string Xml, int Flags, int LogonType)> Calls { get; } = new();
        public List<(string Folder, string Name)> Deletes { get; } = new();
        public List<string> FolderDeletes { get; } = new();
        public Exception? Throw { get; set; }
        public Dictionary<string, Exception> ThrowForTask { get; } = new();
        public Exception? FolderThrow { get; set; }
        public bool FolderEmpty { get; set; } = true;

        public void RegisterTask(string path, string xml, int flags, int logonType)
        {
            Calls.Add((path, xml, flags, logonType));
            if (Throw != null) throw Throw;
        }

        public void DeleteTask(string folder, string name)
        {
            Deletes.Add((folder, name));
            if (Throw != null) throw Throw;
            if (ThrowForTask.TryGetValue(name, out var forTask)) throw forTask;
        }

        public bool DeleteFolderIfEmpty(string folder)
        {
            FolderDeletes.Add(folder);
            if (FolderThrow != null) throw FolderThrow;
            return FolderEmpty;
        }
    }

    private static List<Serilog.Events.LogEvent> Capture(Action action)
    {
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            action();
        }
        finally
        {
            Log.Logger = previous;
        }
        return sink.Events;
    }

    private static bool HasHResult(Serilog.Events.LogEvent e, int hresult) =>
        e.Properties.TryGetValue("HResult", out var value) && value.ToString() == hresult.ToString();

    private static List<Serilog.Events.LogEvent> Unregister(FakeRegistrar registrar)
    {
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            StartupManager.UnregisterScheduledTask(registrar);
        }
        finally
        {
            Log.Logger = previous;
        }
        return sink.Events;
    }

    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public void Register_PassesTaskPathXmlStringCreateOrUpdateAndInteractiveToken()
    {
        var registrar = new FakeRegistrar();

        Assert.True(StartupManager.RegisterScheduledTask(registrar, ExePath, @"PC\Player"));

        var call = Assert.Single(registrar.Calls);
        Assert.Equal(@"GameShift\Startup", call.Path);
        Assert.Equal(6, call.Flags); // TASK_CREATE_OR_UPDATE, as schtasks /Create /F
        Assert.Equal(3, call.LogonType); // TASK_LOGON_INTERACTIVE_TOKEN, matching the XML principal
        Assert.Equal(StartupManager.BuildTaskXml(ExePath, @"PC\Player"), call.Xml);
    }

    [Fact]
    public void Register_XmlNamesTheExeAndUserEvenWithMarkupCharacters()
    {
        var registrar = new FakeRegistrar();

        StartupManager.RegisterScheduledTask(registrar, ExePath, @"PC\Tom & <Jerry>");

        var task = XDocument.Parse(Assert.Single(registrar.Calls).Xml);
        Assert.Equal(ExePath, task.Descendants(TaskNs + "Command").Single().Value);
        Assert.Equal(Path.GetDirectoryName(ExePath), task.Descendants(TaskNs + "WorkingDirectory").Single().Value);
        var principal = task.Descendants(TaskNs + "Principal").Single();
        Assert.Equal(@"PC\Tom & <Jerry>", principal.Element(TaskNs + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(TaskNs + "LogonType")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(TaskNs + "RunLevel")!.Value);
        Assert.Equal(@"PC\Tom & <Jerry>", task.Descendants(TaskNs + "LogonTrigger").Single().Element(TaskNs + "UserId")!.Value);
    }

    [Fact]
    public void Register_ExeMissing_DoesNotRegister()
    {
        var registrar = new FakeRegistrar();
        var missing = Path.Combine(Path.GetTempPath(), $"gameshift-missing-{Guid.NewGuid():N}.exe");

        Assert.False(StartupManager.RegisterScheduledTask(registrar, missing, @"PC\Player"));
        Assert.Empty(registrar.Calls);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))] // E_ACCESSDENIED
    [InlineData(unchecked((int)0x80070534))] // ERROR_NONE_MAPPED: the user in the XML does not resolve
    [InlineData(unchecked((int)0x80041318))] // a FACILITY_ITF code from the Task Scheduler
    public void Register_ComFailure_ReportsNotRegisteredWithTheHResult(int hresult)
    {
        var registrar = new FakeRegistrar { Throw = new COMException("register failed", hresult) };
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            Assert.False(StartupManager.RegisterScheduledTask(registrar, ExePath, @"PC\Player"));
        }
        finally
        {
            Log.Logger = previous;
        }

        Assert.Contains(sink.Events, e => e.Level == Serilog.Events.LogEventLevel.Error
            && e.Properties.TryGetValue("HResult", out var value)
            && value.ToString() == hresult.ToString());
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.StartsWith("[StartupManager] Registered"));
    }

    [Fact]
    public void Register_SchedulerUnavailable_ReportsNotRegistered()
    {
        var registrar = new FakeRegistrar { Throw = new InvalidOperationException("no Task Scheduler") };

        Assert.False(StartupManager.RegisterScheduledTask(registrar, ExePath, @"PC\Player"));
    }

    [Fact]
    public void Unregister_DeletesTheStartupTaskFromItsFolder()
    {
        var registrar = new FakeRegistrar();

        var events = Unregister(registrar);

        Assert.Equal(("GameShift", "Startup"), Assert.Single(registrar.Deletes));
        Assert.Contains(events, e => e.MessageTemplate.Text.StartsWith("[StartupManager] Unregistered"));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002))] // ERROR_FILE_NOT_FOUND: no such task
    [InlineData(unchecked((int)0x80070003))] // ERROR_PATH_NOT_FOUND: no GameShift folder
    public void Unregister_TaskNotRegistered_IsNothingToDo(int hresult)
    {
        // The message is in the display language; only the HRESULT may decide.
        var registrar = new FakeRegistrar { Throw = new COMException("Das System kann die angegebene Datei nicht finden.", hresult) };

        var events = Unregister(registrar);

        Assert.Single(registrar.Deletes);
        Assert.DoesNotContain(events, e => e.Level >= Serilog.Events.LogEventLevel.Warning);
        Assert.DoesNotContain(events, e => e.MessageTemplate.Text.StartsWith("[StartupManager] Unregistered"));
    }

    [Fact]
    public void Unregister_OtherFailure_WarnsWithTheHResult()
    {
        var hresult = unchecked((int)0x80070005); // E_ACCESSDENIED
        var registrar = new FakeRegistrar { Throw = new COMException("cannot find", hresult) };

        var events = Unregister(registrar);

        Assert.Contains(events, e => e.Level == Serilog.Events.LogEventLevel.Warning
            && e.Properties.TryGetValue("HResult", out var value)
            && value.ToString() == hresult.ToString());
        Assert.DoesNotContain(events, e => e.MessageTemplate.Text.StartsWith("[StartupManager] Unregistered"));
    }

    private static List<bool> ReconcileAtLaunch(
        string settingsFile, AppSettings settings, string? exePath = null, string? registeredInstallDirectory = null)
    {
        var calls = new List<bool>();
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = settingsFile;
        try
        {
            StartupManager.ReconcileAtLaunch(settings, exePath ?? ExePath, registeredInstallDirectory, calls.Add);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = previous;
        }
        return calls;
    }

    [Fact]
    public void ReconcileAtLaunch_FreshInstall_LeavesTheTaskToTheWizard()
    {
        using var temp = new TempPath();

        Assert.Empty(ReconcileAtLaunch(temp.GetFile("settings.json"), new AppSettings()));
    }

    [Fact]
    public void ReconcileAtLaunch_RefusedSettingsFolder_LeavesTheTaskAlone()
    {
        var refused = $@"\\gameshift-refused-{Guid.NewGuid():N}\share\GameShift\settings.json";

        Assert.Empty(ReconcileAtLaunch(refused, new AppSettings()));
        Assert.Empty(ReconcileAtLaunch(refused, new AppSettings { StartWithWindows = false }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconcileAtLaunch_SavedSetting_ReconcilesToIt(bool startWithWindows)
    {
        using var temp = new TempPath();
        var file = temp.GetFile("settings.json");
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = file;
        try
        {
            SettingsManager.Save(new AppSettings { StartWithWindows = startWithWindows });
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = previous;
        }

        Assert.Equal(new[] { startWithWindows }, ReconcileAtLaunch(file, new AppSettings { StartWithWindows = startWithWindows }));
    }

    // ── Logon argument ───────────────────────────────────────────────

    [Fact]
    public void TaskXml_StartsTheExeWithTheLogonArgument()
    {
        var xml = StartupManager.BuildTaskXml(ExePath, @"PC\Player");

        Assert.Contains("<Arguments>--logon</Arguments>", xml);
        var exec = XDocument.Parse(xml).Descendants(TaskNs + "Exec").Single();
        Assert.Equal(ExePath, exec.Element(TaskNs + "Command")!.Value);
        Assert.Equal("--logon", exec.Element(TaskNs + "Arguments")!.Value);
    }

    [Fact]
    public void RegisteredTask_ReadsBackWhatWasRegistered_EvenWithMarkupCharacters()
    {
        var path = @"C:\Games & <Tools>\GameShift.App.exe";

        var registered = StartupManager.ParseRegisteredTask(StartupManager.BuildTaskXml(path, @"PC\Player"));

        Assert.Equal(path, registered.Command);
        Assert.Equal("--logon", registered.Arguments);
    }

    [Fact]
    public void Reconcile_TaskWithoutTheLogonArgument_IsReRegisteredOnce()
    {
        var registrar = new FakeRegistrar();
        var registered = new StartupManager.RegisteredStartupTask(ExePath, null);

        StartupManager.ReconcileStartupTask(true, registered, registrar, ExePath, @"PC\Player");
        var afterFirstStart = StartupManager.ParseRegisteredTask(Assert.Single(registrar.Calls).Xml);
        StartupManager.ReconcileStartupTask(true, afterFirstStart, registrar, ExePath, @"PC\Player");

        Assert.Single(registrar.Calls);
        Assert.Equal("--logon", afterFirstStart.Arguments);
    }

    [Theory]
    [InlineData("--logon")]
    [InlineData("--LOGON")]
    public void Reconcile_TaskUpToDate_DoesNothing(string arguments)
    {
        var registrar = new FakeRegistrar();

        StartupManager.ReconcileStartupTask(
            true, new StartupManager.RegisteredStartupTask("\"" + ExePath.ToUpperInvariant() + "\"", arguments), registrar, ExePath, @"PC\Player");

        Assert.Empty(registrar.Calls);
        Assert.Empty(registrar.Deletes);
    }

    [Fact]
    public void Reconcile_TaskForAnotherExe_IsReRegistered()
    {
        var registrar = new FakeRegistrar();

        StartupManager.ReconcileStartupTask(
            true, new StartupManager.RegisteredStartupTask(@"C:\Old\GameShift.App.exe", "--logon"), registrar, ExePath, @"PC\Player");

        Assert.Equal(ExePath, StartupManager.ParseRegisteredTask(Assert.Single(registrar.Calls).Xml).Command);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Reconcile_TaskMissingOrUnreadable_IsRegistered(bool hasCommand, bool hasArguments)
    {
        var registrar = new FakeRegistrar();
        var registered = hasCommand || hasArguments
            ? new StartupManager.RegisteredStartupTask(hasCommand ? ExePath : null, hasArguments ? "--logon" : null)
            : null;

        StartupManager.ReconcileStartupTask(true, registered, registrar, ExePath, @"PC\Player");

        Assert.Single(registrar.Calls);
    }

    [Fact]
    public void Reconcile_NotWanted_DeletesAnExistingTaskOnly()
    {
        var registrar = new FakeRegistrar();

        StartupManager.ReconcileStartupTask(false, null, registrar, ExePath, @"PC\Player");
        Assert.Empty(registrar.Deletes);

        StartupManager.ReconcileStartupTask(false, new StartupManager.RegisteredStartupTask(ExePath, null), registrar, ExePath, @"PC\Player");
        Assert.Equal(("GameShift", "Startup"), Assert.Single(registrar.Deletes));
        Assert.Empty(registrar.Calls);
    }

    // ── A copy outside the registered install folder ─────────────────

    private static string SavedSettingsFile(TempPath temp, bool startWithWindows)
    {
        var file = temp.GetFile("settings.json");
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = file;
        try
        {
            SettingsManager.Save(new AppSettings { StartWithWindows = startWithWindows });
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = previous;
        }
        return file;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconcileAtLaunch_CopyOutsideTheRegisteredInstall_LeavesTheTaskAlone(bool startWithWindows)
    {
        using var temp = new TempPath();
        var file = SavedSettingsFile(temp, startWithWindows);
        var settings = new AppSettings { StartWithWindows = startWithWindows };

        Assert.Empty(ReconcileAtLaunch(file, settings, @"C:\Users\Player\Downloads\GameShift.App.exe", @"C:\Program Files\GameShift\"));
    }

    [Fact]
    public void ReconcileAtLaunch_UnknownExePathWithAnInstallRegistered_LeavesTheTaskAlone()
    {
        Assert.False(StartupManager.RunsFromRegisteredInstall(null, @"C:\Program Files\GameShift\"));
        Assert.False(StartupManager.RunsFromRegisteredInstall("GameShift.App.exe", @"C:\Program Files\GameShift\"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\GameShift\")]
    [InlineData(@"c:\program files\gameshift")]
    public void ReconcileAtLaunch_InstalledCopy_Reconciles(string registeredInstallDirectory)
    {
        using var temp = new TempPath();
        var file = SavedSettingsFile(temp, true);

        Assert.Equal(new[] { true }, ReconcileAtLaunch(
            file, new AppSettings { StartWithWindows = true }, @"C:\Program Files\GameShift\GameShift.App.exe", registeredInstallDirectory));
    }

    [Fact]
    public void ReconcileAtLaunch_NothingInstalled_Reconciles()
    {
        using var temp = new TempPath();
        var file = SavedSettingsFile(temp, true);

        Assert.Equal(new[] { true }, ReconcileAtLaunch(
            file, new AppSettings { StartWithWindows = true }, @"C:\Users\Player\Downloads\GameShift.App.exe", null));
    }

    // ── Legacy boot-recovery task ────────────────────────────────────

    [Fact]
    public void LegacyTask_IsDeleted_ThenAnEmptyFolder()
    {
        var registrar = new FakeRegistrar();

        var events = Capture(() => StartupManager.DeleteLegacyBootRecoveryTask(registrar));

        Assert.Equal(("GameShift", "BootRecovery"), Assert.Single(registrar.Deletes));
        Assert.Equal("GameShift", Assert.Single(registrar.FolderDeletes));
        Assert.Contains(events, e => e.MessageTemplate.Text.StartsWith("[StartupManager] Removed the legacy"));
        Assert.Contains(events, e => e.MessageTemplate.Text.StartsWith("[StartupManager] Removed the empty"));
    }

    [Fact]
    public void LegacyTask_FolderStillInUse_IsKept()
    {
        var registrar = new FakeRegistrar { FolderEmpty = false };

        var events = Capture(() => StartupManager.DeleteLegacyBootRecoveryTask(registrar));

        Assert.Single(registrar.FolderDeletes);
        Assert.DoesNotContain(events, e => e.MessageTemplate.Text.StartsWith("[StartupManager] Removed the empty"));
        Assert.DoesNotContain(events, e => e.Level >= Serilog.Events.LogEventLevel.Warning);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002))] // ERROR_FILE_NOT_FOUND: no such task
    [InlineData(unchecked((int)0x80070003))] // ERROR_PATH_NOT_FOUND: no GameShift folder
    public void LegacyTask_NotRegistered_IsQuiet(int hresult)
    {
        var registrar = new FakeRegistrar { Throw = new COMException("Das System kann die angegebene Datei nicht finden.", hresult) };

        var events = Capture(() => StartupManager.DeleteLegacyBootRecoveryTask(registrar));

        Assert.Single(registrar.Deletes);
        Assert.Empty(registrar.FolderDeletes);
        Assert.DoesNotContain(events, e => e.Level >= Serilog.Events.LogEventLevel.Warning);
        Assert.DoesNotContain(events, e => e.MessageTemplate.Text.StartsWith("[StartupManager] Removed"));
    }

    [Fact]
    public void LegacyTask_OtherFailure_WarnsWithTheHResultAndDoesNotThrow()
    {
        var hresult = unchecked((int)0x80070005); // E_ACCESSDENIED
        var registrar = new FakeRegistrar { Throw = new COMException("access denied", hresult) };

        var events = Capture(() => StartupManager.DeleteLegacyBootRecoveryTask(registrar));

        Assert.Contains(events, e => e.Level == Serilog.Events.LogEventLevel.Warning && HasHResult(e, hresult));
        Assert.Empty(registrar.FolderDeletes);
        Assert.DoesNotContain(events, e => e.MessageTemplate.Text.StartsWith("[StartupManager] Removed"));
    }

    [Fact]
    public void LegacyTask_SchedulerUnavailable_DoesNotThrow()
    {
        var registrar = new FakeRegistrar { Throw = new InvalidOperationException("no Task Scheduler") };

        var events = Capture(() => StartupManager.DeleteLegacyBootRecoveryTask(registrar));

        Assert.Contains(events, e => e.Level == Serilog.Events.LogEventLevel.Warning);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002))]
    [InlineData(unchecked((int)0x80070003))]
    public void TaskFolder_AlreadyGone_IsQuiet(int hresult)
    {
        var registrar = new FakeRegistrar { FolderThrow = new COMException("not found", hresult) };

        var events = Capture(() => StartupManager.DeleteTaskFolderIfEmpty(registrar));

        Assert.DoesNotContain(events, e => e.Level >= Serilog.Events.LogEventLevel.Warning);
    }

    [Fact]
    public void TaskFolder_OtherFailure_WarnsWithTheHResultAndDoesNotThrow()
    {
        var hresult = unchecked((int)0x80070005);
        var registrar = new FakeRegistrar { FolderThrow = new COMException("access denied", hresult) };

        var events = Capture(() => StartupManager.DeleteTaskFolderIfEmpty(registrar));

        Assert.Contains(events, e => e.Level == Serilog.Events.LogEventLevel.Warning && HasHResult(e, hresult));
    }

    // ── Uninstall: every GameShift task ──────────────────────────────

    [Fact]
    public void RemoveAllTasks_DeletesBothTasks_ThenTheEmptyFolder()
    {
        var registrar = new FakeRegistrar();

        var removed = StartupManager.RemoveAllTasks(registrar);

        Assert.True(removed);
        Assert.Equal(new[] { ("GameShift", "Startup"), ("GameShift", "BootRecovery") }, registrar.Deletes);
        Assert.Equal("GameShift", Assert.Single(registrar.FolderDeletes));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002))]
    [InlineData(unchecked((int)0x80070003))]
    public void RemoveAllTasks_TasksThatDoNotExist_CountAsRemoved_Quietly(int hresult)
    {
        var registrar = new FakeRegistrar { Throw = new COMException("Das System kann die angegebene Datei nicht finden.", hresult) };

        bool removed = false;
        var events = Capture(() => removed = StartupManager.RemoveAllTasks(registrar));

        Assert.True(removed);
        Assert.Equal(2, registrar.Deletes.Count);
        Assert.DoesNotContain(events, e => e.Level >= Serilog.Events.LogEventLevel.Warning);
    }

    [Theory]
    [InlineData("Startup")]
    [InlineData("BootRecovery")]
    public void RemoveAllTasks_AFailedDelete_IsReported_AndTheOtherTaskIsStillDeleted(string failing)
    {
        var hresult = unchecked((int)0x80070005);
        var registrar = new FakeRegistrar();
        registrar.ThrowForTask[failing] = new COMException("access denied", hresult);

        bool removed = true;
        var events = Capture(() => removed = StartupManager.RemoveAllTasks(registrar));

        Assert.False(removed);
        Assert.Equal(2, registrar.Deletes.Count);
        Assert.Contains(events, e => e.Level == Serilog.Events.LogEventLevel.Warning && HasHResult(e, hresult));
    }

    [Fact]
    public void RemoveAllTasks_FolderHoldingAnotherTask_IsKept_AndStillCountsAsRemoved()
    {
        var registrar = new FakeRegistrar { FolderEmpty = false };

        Assert.True(StartupManager.RemoveAllTasks(registrar));
        Assert.Single(registrar.FolderDeletes);
    }
}
