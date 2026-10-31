using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameShift.Core.Journal;
using GameShift.Tests.TestHelpers;
using Microsoft.Win32;
using Serilog;
using Serilog.Events;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// The fullscreen compatibility flag, AMD driver value and firewall rule game actions are gone, but
/// journals written while they existed still hold their "registry" and "firewall" records. Each
/// record, in the shape those actions wrote, must still be one that <see cref="GameActionRecovery"/>
/// undoes. PowerShell runs through a fake; the registry records are replayed exactly as journaled into
/// a throwaway HKCU tree that stands in for both hives, deleted after each test.
/// </summary>
public class RemovedPresetActionRecordTests
{
    private const string RemoveFirewallRuleScript =
        "Remove-NetFirewallRule -DisplayName $env:GS_ARG0 -ErrorAction SilentlyContinue";

    // One session's records as the removed actions journaled them.
    private const string OlderJournal = """
            {
              "Version": 2,
              "SessionActive": true,
              "Optimizations": [],
              "GameActions": [
                {
                  "Kind": "registry",
                  "Payload": "{\"hive\":\"HKCU\",\"path\":\"SOFTWARE\\\\Microsoft\\\\Windows NT\\\\CurrentVersion\\\\AppCompatFlags\\\\Layers\",\"name\":\"C:\\\\Riot Games\\\\VALORANT\\\\live\\\\ShooterGame\\\\Binaries\\\\Win64\\\\VALORANT-Win64-Shipping.exe\",\"kind\":\"String\",\"existed\":false,\"value\":null}",
                  "AppliedAt": "2026-10-20T18:00:00+00:00"
                },
                {
                  "Kind": "registry",
                  "Payload": "{\"hive\":\"HKLM\",\"path\":\"SYSTEM\\\\CurrentControlSet\\\\Control\\\\Class\\\\{4d36e968-e325-11ce-bfc1-08002be10318}\\\\0000\\\\UMD\",\"name\":\"AntiLag_DevMode\",\"kind\":\"String\",\"existed\":true,\"value\":\"1\"}",
                  "AppliedAt": "2026-10-20T18:00:01+00:00"
                },
                {
                  "Kind": "firewall",
                  "Payload": "{\"ruleName\":\"GameShift_OW2_In\"}",
                  "AppliedAt": "2026-10-20T18:00:02+00:00"
                },
                {
                  "Kind": "firewall",
                  "Payload": "{\"ruleName\":\"GameShift_Valorant_In\"}",
                  "AppliedAt": "2026-10-20T18:00:02+00:00"
                },
                {
                  "Kind": "firewall",
                  "Payload": "{\"ruleName\":\"GameShift_LoL_In\"}",
                  "AppliedAt": "2026-10-20T18:00:02+00:00"
                },
                {
                  "Kind": "firewall",
                  "Payload": "{\"ruleName\":\"GameShift_Deadlock_In\"}",
                  "AppliedAt": "2026-10-20T18:00:02+00:00"
                }
              ]
            }
            """;

    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static List<GameActionJournalEntry> Entries(string kind) =>
        JsonSerializer.Deserialize<SessionJournalData>(OlderJournal)!.GameActions.Where(e => e.Kind == kind).ToList();

    [Fact]
    public void FirewallRecords_FromAnOlderJournal_RemoveEachPresetRule()
    {
        var runner = new FakePowerShellRunner();
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        foreach (var entry in Entries("firewall"))
            GameActionRecovery.Revert(entry.Kind, entry.Payload, logger, runner.Run);

        Assert.All(runner.Calls, c =>
        {
            Assert.Equal(RemoveFirewallRuleScript, c.Script);
            Assert.Equal(FakePowerShellRunner.ManifestPath("NetSecurity"), c.ModuleManifest, ignoreCase: true);
        });
        Assert.Equal(
            new[] { "GameShift_OW2_In", "GameShift_Valorant_In", "GameShift_LoL_In", "GameShift_Deadlock_In" },
            runner.Calls.Select(c => Assert.Single(c.Values)));
        Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Warning);
    }

    private const string FullscreenLayers = "~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE";
    private const string LayersPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string AmdUmdPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000\UMD";

    [Fact]
    public void RegistryRecords_FromAnOlderJournal_AreUndoneByTheRegistryUndo()
    {
        using var registry = new TestRecoveryRegistry();
        var (fullscreen, driver) = (Entries("registry")[0], Entries("registry")[1]);

        // Fullscreen flag: GameShift created the value, so the undo deletes it and nothing else.
        var exePath = JsonNode.Parse(fullscreen.Payload)!["name"]!.GetValue<string>();
        using (var key = registry.CreateKey(RecoveryHive.CurrentUser, LayersPath))
        {
            key.SetValue(exePath, FullscreenLayers);
            key.SetValue("UserOwn", "~ RUNASADMIN");
        }

        // AMD driver value: it existed as the string "1" before GameShift wrote "0".
        using (var key = registry.CreateKey(RecoveryHive.LocalMachine, AmdUmdPath))
            key.SetValue("AntiLag_DevMode", "0");

        var runner = new FakePowerShellRunner().Run;
        var fullscreenResult = GameActionRecovery.Revert(fullscreen.Kind, fullscreen.Payload, Serilog.Core.Logger.None, runner, registry.Registry);
        var driverResult = GameActionRecovery.Revert(driver.Kind, driver.Payload, Serilog.Core.Logger.None, runner, registry.Registry);

        Assert.Equal(RevertOutcome.Reverted, fullscreenResult.Outcome);
        Assert.Equal(RevertOutcome.Reverted, driverResult.Outcome);
        Assert.Null(registry.Value(RecoveryHive.CurrentUser, LayersPath, exePath));
        Assert.Equal("~ RUNASADMIN", registry.Value(RecoveryHive.CurrentUser, LayersPath, "UserOwn"));
        using (var key = registry.Open(RecoveryHive.LocalMachine, AmdUmdPath)!)
        {
            Assert.Equal("1", key.GetValue("AntiLag_DevMode"));
            Assert.Equal(RegistryValueKind.String, key.GetValueKind("AntiLag_DevMode"));
        }
    }

    [Fact]
    public void RegistryKind_IsStillHandled_NotSkippedAsUnknown()
    {
        using var registry = new TestRecoveryRegistry();
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var runner = new FakePowerShellRunner().Run;

        // A record GameShift created on an adapter that is gone: nothing left to undo, and no warning.
        var registryResult = GameActionRecovery.Revert(
            "registry", Entries("registry")[1].Payload.Replace("\"existed\":true", "\"existed\":false"), logger, runner, registry.Registry);
        var retiredResult = GameActionRecovery.Revert("retired-kind", "{}", logger, runner, registry.Registry);

        Assert.Equal(RevertOutcome.Reverted, registryResult.Outcome);
        Assert.Equal(RevertOutcome.Skipped, retiredResult.Outcome);
        var warning = Assert.Single(sink.Events, e => e.Level >= LogEventLevel.Warning);
        Assert.Contains("Unknown GameAction kind", warning.MessageTemplate.Text);
        Assert.Equal("retired-kind", ((ScalarValue)warning.Properties["Kind"]).Value);
    }
}
