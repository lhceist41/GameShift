using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using GameShift.Core.Journal;
using GameShift.Tests.TestHelpers;
using Serilog;
using Serilog.Events;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// The Defender exclusion and process suspend game actions are gone, but journals written while
/// they existed still hold their "defender" and "suspend" records. Each record, in the shape those
/// actions wrote, must still reach its undo path in <see cref="GameActionRecovery"/>. PowerShell runs
/// through a fake, and the suspend record names a PID whose process has a different name, so nothing
/// on the machine is changed.
/// </summary>
public class RetiredGameActionRecordTests
{
    private const string RemoveDefenderExclusionScript =
        "Remove-MpPreference -ExclusionPath $env:GS_ARG0 -ErrorAction SilentlyContinue";

    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static GameActionJournalEntry SingleGameAction(string journalJson)
    {
        var journal = JsonSerializer.Deserialize<SessionJournalData>(journalJson)!;
        return Assert.Single(journal.GameActions);
    }

    [Fact]
    public void DefenderRecord_FromAnOlderJournal_RemovesEachRecordedExclusion()
    {
        // A preset Defender exclusion as the removed action journaled it: JSON payload inside the entry.
        var entry = SingleGameAction("""
            {
              "Version": 2,
              "SessionActive": true,
              "Optimizations": [],
              "GameActions": [
                {
                  "Kind": "defender",
                  "Payload": "{\"paths\":[\"C:\\\\Program Files (x86)\\\\Example Game\\\\\",\"D:\\\\Games\\\\Example\\\\\"]}",
                  "AppliedAt": "2026-06-01T18:00:00+00:00"
                }
              ]
            }
            """);
        var runner = new FakePowerShellRunner();

        GameActionRecovery.Revert(entry.Kind, entry.Payload, Serilog.Core.Logger.None, runner.Run);

        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, c =>
        {
            Assert.Equal(RemoveDefenderExclusionScript, c.Script);
            Assert.Equal(FakePowerShellRunner.ManifestPath("ConfigDefender"), c.ModuleManifest, ignoreCase: true);
        });
        Assert.Equal(
            new[] { @"C:\Program Files (x86)\Example Game\", @"D:\Games\Example\" },
            runner.Calls.Select(c => Assert.Single(c.Values)));
    }

    [Fact]
    public void SuspendRecord_FromAnOlderJournal_ReachesTheResumeGuard()
    {
        // The test host's own PID under another process name: the PID-reuse guard must skip it.
        int pid = Environment.ProcessId;
        Assert.NotEqual("LeagueClientUx", Process.GetCurrentProcess().ProcessName);
        var entry = SingleGameAction($$"""
            {
              "Version": 2,
              "SessionActive": true,
              "Optimizations": [],
              "GameActions": [
                {
                  "Kind": "suspend",
                  "Payload": "{\"name\":\"LeagueClientUx\",\"pids\":[{{pid}}]}",
                  "AppliedAt": "2026-06-01T18:00:00+00:00"
                }
              ]
            }
            """);
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        GameActionRecovery.Revert(entry.Kind, entry.Payload, logger);

        var skipped = Assert.Single(sink.Events, e => e.MessageTemplate.Text.Contains("skipping resume"));
        Assert.Equal(pid, (int)((ScalarValue)skipped.Properties["Pid"]).Value!);
        Assert.Equal("LeagueClientUx", ((ScalarValue)skipped.Properties["Expected"]).Value);
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.Contains("Resumed"));
    }
}
