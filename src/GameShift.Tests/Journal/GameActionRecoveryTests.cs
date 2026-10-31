using System.Text.Json;
using GameShift.Core.Journal;
using GameShift.Tests.TestHelpers;
using Microsoft.Win32;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Tests for <see cref="GameActionRecovery"/>, the watchdog-side undo path for per-game actions.
///
/// The registry tests replay records with the real AMD driver-value path into a throwaway HKCU tree
/// (see <see cref="TestRecoveryRegistry"/>), exercising the real restore code including the
/// type-faithful write-back for all four supported value kinds. The firewall/defender tests run
/// PowerShell through a fake, or use payloads that are refused before any PowerShell process would
/// be spawned. Recovery runs after a crash: it must never throw on garbage input, and it reports
/// whether each record was undone.
/// </summary>
public class GameActionRecoveryTests
{
    private const string AmdUmd = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000\UMD";

    private static Serilog.ILogger NoOpLogger => Logger.None;

    private static string RegistryPayload(string hive, string path, string name, string kind, bool existed, object? value) =>
        JsonSerializer.Serialize(new { hive, path, name, kind, existed, value });

    private static GameActionRevertResult RevertRegistry(string payload, TestRecoveryRegistry registry) =>
        GameActionRecovery.Revert("registry", payload, NoOpLogger, new FakePowerShellRunner().Run, registry.Registry);

    private static void RunRegistryRoundTrip(
        string kind, object originalValue, object overwrittenValue,
        RegistryValueKind expectedKind, Action<object?> assertRestored)
    {
        using var registry = new TestRecoveryRegistry();
        using (var key = registry.CreateKey(RecoveryHive.LocalMachine, AmdUmd))
        {
            // Simulate the "applied" state: the original value has been overwritten in-session.
            key.SetValue("AntiLag_DevMode", overwrittenValue);
        }

        var payload = RegistryPayload("HKLM", AmdUmd, "AntiLag_DevMode", kind, existed: true, value: originalValue);
        var result = RevertRegistry(payload, registry);

        Assert.Equal(RevertOutcome.Reverted, result.Outcome);
        using var readKey = registry.Open(RecoveryHive.LocalMachine, AmdUmd);
        Assert.NotNull(readKey);
        assertRestored(readKey!.GetValue("AntiLag_DevMode"));
        Assert.Equal(expectedKind, readKey.GetValueKind("AntiLag_DevMode"));
    }

    [Fact]
    public void Revert_Registry_RestoresStringValueAndKind() =>
        RunRegistryRoundTrip(
            kind: "String", originalValue: "original-value", overwrittenValue: "changed-by-session",
            expectedKind: RegistryValueKind.String,
            assertRestored: v => Assert.Equal("original-value", Assert.IsType<string>(v)));

    [Fact]
    public void Revert_Registry_RestoresDwordValueAndKind() =>
        RunRegistryRoundTrip(
            kind: "DWord", originalValue: 123, overwrittenValue: 999,
            expectedKind: RegistryValueKind.DWord,
            assertRestored: v => Assert.Equal(123, Assert.IsType<int>(v)));

    [Fact]
    public void Revert_Registry_RestoresQwordValueAndKind() =>
        RunRegistryRoundTrip(
            kind: "QWord", originalValue: 9876543210123L, overwrittenValue: 1L,
            expectedKind: RegistryValueKind.QWord,
            assertRestored: v => Assert.Equal(9876543210123L, Assert.IsType<long>(v)));

    [Fact]
    public void Revert_Registry_RestoresBinaryValueAndKind()
    {
        var original = new byte[] { 1, 2, 3, 255 };
        RunRegistryRoundTrip(
            kind: "Binary", originalValue: Convert.ToBase64String(original), overwrittenValue: new byte[] { 9 },
            expectedKind: RegistryValueKind.Binary,
            assertRestored: v => Assert.Equal(original, Assert.IsType<byte[]>(v)));
    }

    // The driver override recorded the value's existing kind, so an expandable string comes back as one.
    [Fact]
    public void Revert_Registry_RestoresExpandStringValueAndKind() =>
        RunRegistryRoundTrip(
            kind: "ExpandString", originalValue: "1", overwrittenValue: "0",
            expectedKind: RegistryValueKind.ExpandString,
            assertRestored: v => Assert.Equal("1", Assert.IsType<string>(v)));

    // Earlier versions journaled a multi-string as the text "System.String[]", so there is no
    // original to write back: the record is reported, and the value is left as the session set it.
    [Fact]
    public void Revert_Registry_MultiStringOriginal_IsFailedAndUntouched()
    {
        using var registry = new TestRecoveryRegistry();
        using (var key = registry.CreateKey(RecoveryHive.LocalMachine, AmdUmd))
            key.SetValue("AntiLag_DevMode", "0");

        var result = RevertRegistry(
            RegistryPayload("HKLM", AmdUmd, "AntiLag_DevMode", "MultiString", existed: true, value: "System.String[]"), registry);

        Assert.Equal(RevertOutcome.Failed, result.Outcome);
        Assert.Equal("0", registry.Value(RecoveryHive.LocalMachine, AmdUmd, "AntiLag_DevMode"));
        using var readKey = registry.Open(RecoveryHive.LocalMachine, AmdUmd);
        Assert.Equal(RegistryValueKind.String, readKey!.GetValueKind("AntiLag_DevMode"));
    }

    [Fact]
    public void Revert_Registry_DeletesValueThatDidNotExistBeforeApply()
    {
        using var registry = new TestRecoveryRegistry();
        using (var key = registry.CreateKey(RecoveryHive.LocalMachine, AmdUmd))
        {
            key.SetValue("AntiLag_DevMode", "0");
            key.SetValue("Chill_Enabled", "1");
        }

        var result = RevertRegistry(RegistryPayload("HKLM", AmdUmd, "AntiLag_DevMode", "String", existed: false, value: null), registry);

        Assert.Equal(RevertOutcome.Reverted, result.Outcome);
        Assert.Null(registry.Value(RecoveryHive.LocalMachine, AmdUmd, "AntiLag_DevMode"));
        Assert.Equal("1", registry.Value(RecoveryHive.LocalMachine, AmdUmd, "Chill_Enabled"));
    }

    [Fact]
    public void Revert_Registry_OriginalButKeyMissing_IsFailedAndNotCreated()
    {
        using var registry = new TestRecoveryRegistry();

        var result = RevertRegistry(RegistryPayload("HKLM", AmdUmd, "AntiLag_DevMode", "String", existed: true, value: "1"), registry);

        Assert.Equal(RevertOutcome.Failed, result.Outcome);
        Assert.Null(registry.Open(RecoveryHive.LocalMachine, AmdUmd));
    }

    // A value that existed but whose original could not be encoded was journaled as null.
    [Fact]
    public void Revert_Registry_ExistedWithoutRecordedOriginal_IsFailedAndUntouched()
    {
        using var registry = new TestRecoveryRegistry();
        using (var key = registry.CreateKey(RecoveryHive.LocalMachine, AmdUmd))
            key.SetValue("AntiLag_DevMode", "0");

        var result = RevertRegistry(RegistryPayload("HKLM", AmdUmd, "AntiLag_DevMode", "DWord", existed: true, value: null), registry);

        Assert.Equal(RevertOutcome.Failed, result.Outcome);
        Assert.Equal("0", registry.Value(RecoveryHive.LocalMachine, AmdUmd, "AntiLag_DevMode"));
    }

    [Theory]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", "DWord")]
    [InlineData("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"C:\Users\Public\evil.exe", "String")]
    [InlineData("HKLM", AmdUmd, "Debugger", "String")]
    public void Revert_Registry_RecordGameShiftDidNotWrite_IsSkippedAndUntouched(string hive, string path, string name, string kind)
    {
        using var registry = new TestRecoveryRegistry();
        var target = hive == "HKCU" ? RecoveryHive.CurrentUser : RecoveryHive.LocalMachine;
        using (var key = registry.CreateKey(target, path))
            key.SetValue(name, "user value");

        var result = RevertRegistry(RegistryPayload(hive, path, name, kind, existed: false, value: null), registry);

        Assert.Equal(RevertOutcome.Skipped, result.Outcome);
        Assert.Equal("user value", registry.Value(target, path, name));
    }

    [Fact]
    public void Revert_UnknownKindAndMalformedPayload_AreSkipped()
    {
        Assert.Equal(RevertOutcome.Skipped, GameActionRecovery.Revert("definitely-unknown-kind", "{}", NoOpLogger).Outcome);
        Assert.Equal(RevertOutcome.Skipped, GameActionRecovery.Revert("registry", "{ this is not valid json", NoOpLogger).Outcome);
        Assert.Equal(RevertOutcome.Skipped, GameActionRecovery.Revert("firewall", "{}", NoOpLogger).Outcome);
    }

    // The adapter is gone, and with it the value GameShift created: nothing is left to undo.
    [Fact]
    public void Revert_Registry_CreatedValueOnNonexistentKey_IsReverted()
    {
        using var registry = new TestRecoveryRegistry();

        var result = RevertRegistry(RegistryPayload("HKLM", AmdUmd, "AntiLag_DevMode", "String", existed: false, value: null), registry);

        Assert.Equal(RevertOutcome.Reverted, result.Outcome);
        Assert.Null(registry.Open(RecoveryHive.LocalMachine, AmdUmd));
    }

    [Fact]
    public void Revert_UnknownKind_DoesNotThrow()
    {
        var exception = Record.Exception(() => GameActionRecovery.Revert("definitely-unknown-kind", "{}", NoOpLogger));
        Assert.Null(exception);
    }

    [Fact]
    public void Revert_MalformedPayload_DoesNotThrow()
    {
        var exception = Record.Exception(() => GameActionRecovery.Revert("registry", "{ this is not valid json", NoOpLogger));
        Assert.Null(exception);
    }

    [Fact]
    public void Revert_Firewall_WithMissingRuleName_DoesNotThrow()
    {
        // Empty payload fails the ruleName check before any PowerShell process is started.
        var exception = Record.Exception(() => GameActionRecovery.Revert("firewall", "{}", NoOpLogger));
        Assert.Null(exception);
    }

    [Fact]
    public void Revert_Defender_WithMissingPaths_DoesNotThrow()
    {
        // Empty payload fails the paths check before any PowerShell process is started.
        var exception = Record.Exception(() => GameActionRecovery.Revert("defender", "{}", NoOpLogger));
        Assert.Null(exception);
    }

    private const string HostileValue = "x'\u2018\u2019\u201A\u201B; $(Get-Date) `\nend";

    [Fact]
    public void Revert_Defender_HostilePaths_ValueTravelsOnlyInEnvironment()
    {
        const string script = "Remove-MpPreference -ExclusionPath $env:GS_ARG0 -ErrorAction SilentlyContinue";
        var paths = new[] { @"C:\Games\Plain\", @"C:\Games\" + HostileValue + @"\" };
        var runner = new FakePowerShellRunner();

        GameActionRecovery.Revert("defender", JsonSerializer.Serialize(new { paths }), NoOpLogger, runner.Run);

        Assert.Equal(paths, runner.Calls.Select(c => Assert.Single(c.Values)));
        Assert.All(runner.Calls, c =>
        {
            Assert.Equal(script, c.Script);
            Assert.Equal(FakePowerShellRunner.ExpectedArguments(script), c.Arguments);
            Assert.Equal(FakePowerShellRunner.ManifestPath("ConfigDefender"), c.ModuleManifest, ignoreCase: true);
        });
    }

    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    [Theory]
    [InlineData("defender", 1, true)]
    [InlineData("defender", 0, false)]
    [InlineData("firewall", 1, true)]
    [InlineData("firewall", 0, false)]
    public void Revert_RemovalNotConfirmed_LogsWarningNotRemoved(string kind, int exitCode, bool exited)
    {
        var sink = new CapturingSink();
        var logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var runner = new FakePowerShellRunner
        {
            Respond = _ => new GameShift.Core.System.ProcessRunResult(
                exitCode, string.Empty, string.Empty, TimedOut: !exited, Exited: exited),
        };
        var payload = kind == "defender"
            ? JsonSerializer.Serialize(new { paths = new[] { @"C:\Games\A\" } })
            : JsonSerializer.Serialize(new { ruleName = "GameShift_OW2_In" });

        var result = GameActionRecovery.Revert(kind, payload, logger, runner.Run);

        Assert.Equal(RevertOutcome.Failed, result.Outcome);
        Assert.Single(runner.Calls);
        Assert.Contains(sink.Events, e => e.Level == Serilog.Events.LogEventLevel.Warning &&
            e.MessageTemplate.Text.Contains("not confirmed"));
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.Contains("Removed"));
    }

    [Theory]
    [InlineData("defender")]
    [InlineData("firewall")]
    public void Revert_RemovalConfirmed_LogsRemoved(string kind)
    {
        var sink = new CapturingSink();
        var logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var runner = new FakePowerShellRunner();
        var payload = kind == "defender"
            ? JsonSerializer.Serialize(new { paths = new[] { @"C:\Games\A\" } })
            : JsonSerializer.Serialize(new { ruleName = "GameShift_OW2_In" });

        var result = GameActionRecovery.Revert(kind, payload, logger, runner.Run);

        Assert.Equal(RevertOutcome.Reverted, result.Outcome);
        Assert.Contains(sink.Events, e => e.Level == Serilog.Events.LogEventLevel.Information &&
            e.MessageTemplate.Text.Contains("Removed"));
        Assert.DoesNotContain(sink.Events, e => e.Level >= Serilog.Events.LogEventLevel.Warning);
    }

    // A rule name travels to PowerShell only in the environment, and only a preset rule name gets
    // that far: a tampered record could otherwise delete one of the user's own rules.
    [Fact]
    public void Revert_Firewall_PresetRuleName_ValueTravelsOnlyInEnvironment()
    {
        const string script = "Remove-NetFirewallRule -DisplayName $env:GS_ARG0 -ErrorAction SilentlyContinue";
        var runner = new FakePowerShellRunner();

        GameActionRecovery.Revert("firewall", JsonSerializer.Serialize(new { ruleName = "GameShift_LoL_In" }), NoOpLogger, runner.Run);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(script, call.Script);
        Assert.Equal(FakePowerShellRunner.ExpectedArguments(script), call.Arguments);
        Assert.Equal(FakePowerShellRunner.ManifestPath("NetSecurity"), call.ModuleManifest, ignoreCase: true);
        Assert.Equal(new[] { "GameShift_LoL_In" }, call.Values);
    }

    [Theory]
    [InlineData("Block outbound telemetry")]
    [InlineData("GameShift " + HostileValue)]
    public void Revert_Firewall_AnyOtherRuleName_IsSkippedWithoutPowerShell(string ruleName)
    {
        var runner = new FakePowerShellRunner();

        var result = GameActionRecovery.Revert("firewall", JsonSerializer.Serialize(new { ruleName }), NoOpLogger, runner.Run);

        Assert.Equal(RevertOutcome.Skipped, result.Outcome);
        Assert.Empty(runner.Calls);
    }
}
