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
        public Exception? Throw { get; set; }

        public void RegisterTask(string path, string xml, int flags, int logonType)
        {
            Calls.Add((path, xml, flags, logonType));
            if (Throw != null) throw Throw;
        }
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

    private static List<bool> ReconcileAtLaunch(string settingsFile, AppSettings settings)
    {
        var calls = new List<bool>();
        var previous = SettingsManager.SettingsFilePathOverride;
        SettingsManager.SettingsFilePathOverride = settingsFile;
        try
        {
            StartupManager.ReconcileAtLaunch(settings, calls.Add);
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
}
