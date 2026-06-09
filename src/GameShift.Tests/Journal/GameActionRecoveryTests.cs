using System.Text.Json;
using GameShift.Core.Journal;
using Microsoft.Win32;
using Serilog.Core;
using Xunit;

namespace GameShift.Tests.Journal;

/// <summary>
/// Tests for <see cref="GameActionRecovery"/>, the watchdog-side undo path for per-game actions.
///
/// The registry tests run against throwaway keys under HKCU\Software\GameShiftTests (created and
/// deleted per test), exercising the real restore code including the type-faithful write-back for
/// all four supported value kinds. The firewall/defender tests use payloads that fail validation
/// before any PowerShell process would be spawned, so they verify the no-throw contract without
/// touching the system. Recovery runs after a crash: it must never throw on garbage input.
/// </summary>
public class GameActionRecoveryTests
{
    private const string TestRoot = @"Software\GameShiftTests";

    private static Serilog.ILogger NoOpLogger => Logger.None;

    private static string NewTestKeyPath() => $@"{TestRoot}\{Guid.NewGuid():N}";

    private static string RegistryPayload(string path, string name, string kind, bool existed, object? value) =>
        JsonSerializer.Serialize(new { hive = "HKCU", path, name, kind, existed, value });

    private static void RunRegistryRoundTrip(
        string kind, object originalValue, object overwrittenValue,
        RegistryValueKind expectedKind, Action<object?> assertRestored)
    {
        var path = NewTestKeyPath();
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                // Simulate the "applied" state: the original value has been overwritten in-session.
                key!.SetValue("TestValue", overwrittenValue);
            }

            var payload = RegistryPayload(path, "TestValue", kind, existed: true, value: originalValue);
            GameActionRecovery.Revert("registry", payload, NoOpLogger);

            using var readKey = Registry.CurrentUser.OpenSubKey(path);
            Assert.NotNull(readKey);
            assertRestored(readKey!.GetValue("TestValue"));
            Assert.Equal(expectedKind, readKey.GetValueKind("TestValue"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
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

    [Fact]
    public void Revert_Registry_DeletesValueThatDidNotExistBeforeApply()
    {
        var path = NewTestKeyPath();
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key!.SetValue("CreatedBySession", "session-value");
            }

            var payload = RegistryPayload(path, "CreatedBySession", "String", existed: false, value: null);
            GameActionRecovery.Revert("registry", payload, NoOpLogger);

            using var readKey = Registry.CurrentUser.OpenSubKey(path);
            Assert.NotNull(readKey);
            Assert.Null(readKey!.GetValue("CreatedBySession"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Revert_Registry_OnNonexistentKey_DoesNotThrow()
    {
        var payload = RegistryPayload($@"{TestRoot}\does-not-exist-{Guid.NewGuid():N}", "X", "String", existed: false, value: null);
        var exception = Record.Exception(() => GameActionRecovery.Revert("registry", payload, NoOpLogger));
        Assert.Null(exception);
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
}
