using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameShift.Core.Profiles;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Profiles;

/// <summary>
/// Tests for <see cref="DefenderExclusionAction"/> through its process-start seam, so no PowerShell
/// or Defender call runs. They pin four contracts: a path value never reaches the script text, only
/// the configured paths are excluded whatever folder the game was started from, Revert removes exactly
/// what Apply created, and no competitive preset adds a Defender exclusion.
/// </summary>
public class DefenderExclusionActionTests
{
    private const string GetScript =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n(Get-MpPreference).ExclusionPath";
    private const string AddScript = "Add-MpPreference -ExclusionPath $env:GS_ARG0";
    private const string RemoveScript = "Remove-MpPreference -ExclusionPath $env:GS_ARG0";

    private static readonly string PowerShellExe =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

    public static IEnumerable<object[]> HostilePaths() => new[]
    {
        new object[] { @"C:\Games\it's\" },
        new object[] { "C:\\Games\\a\u2018b\\" },
        new object[] { "C:\\Games\\a\u2019b\\" },
        new object[] { "C:\\Games\\a\u201Ab\\" },
        new object[] { "C:\\Games\\a\u201Bb\\" },
        new object[] { @"C:\Games\a;b\" },
        new object[] { @"C:\Games\$(Get-Date)\" },
        new object[] { @"C:\Games\a`b\" },
        new object[] { "C:\\Games\\a\nb\\" },
        new object[] { "C:\\Games\\x\u2019; Get-Date; \u2018$(Get-Date)`\n'\\" },
    };

    private static (DefenderExclusionAction Action, FakePowerShellRunner Runner) Create(string[] paths)
    {
        var runner = new FakePowerShellRunner();
        var action = new DefenderExclusionAction("Test Defender Exclusions", paths) { RunProcess = runner.Run };
        return (action, runner);
    }

    private static void AssertValueOnlyInEnvironment(FakePowerShellRunner.Call call, string script, string value)
    {
        Assert.Equal(PowerShellExe, call.FileName, ignoreCase: true);
        Assert.Equal(script, call.Script);
        Assert.Equal(FakePowerShellRunner.ExpectedArguments(script), call.Arguments);
        Assert.Equal(FakePowerShellRunner.ManifestPath("ConfigDefender"), call.ModuleManifest, ignoreCase: true);
        Assert.Equal(new[] { value }, call.Values);
    }

    [Theory]
    [MemberData(nameof(HostilePaths))]
    public void ApplyAndRevert_HostilePath_CommandLineIsConstantAndValueTravelsInEnvironment(string path)
    {
        var (action, runner) = Create(new[] { path });

        action.Apply(new SystemStateSnapshot());
        action.Revert(new SystemStateSnapshot());

        var get = Assert.Single(runner.CallsFor(GetScript));
        Assert.Equal(FakePowerShellRunner.ExpectedArguments(GetScript), get.Arguments);
        Assert.Equal(FakePowerShellRunner.ManifestPath("ConfigDefender"), get.ModuleManifest, ignoreCase: true);
        Assert.Empty(get.Values);
        AssertValueOnlyInEnvironment(Assert.Single(runner.CallsFor(AddScript)), AddScript, path);
        AssertValueOnlyInEnvironment(Assert.Single(runner.CallsFor(RemoveScript)), RemoveScript, path);
        Assert.Equal(3, runner.Calls.Count);
    }

    [Theory]
    [InlineData(CompetitivePresets.Overwatch2Exe)]
    [InlineData(CompetitivePresets.ValorantExe)]
    [InlineData(CompetitivePresets.LeagueExe)]
    [InlineData(CompetitivePresets.DeadlockExe)]
    [InlineData(CompetitivePresets.OsuExe)]
    public void GetGameActions_Preset_AddsNoDefenderExclusion(string exeName)
    {
        var actions = CompetitivePresets.GetGameActions(exeName);

        Assert.NotEmpty(actions);
        Assert.Empty(actions.OfType<DefenderExclusionAction>());
    }

    [Fact]
    public void Apply_ExecutableAtArbitraryPath_ExcludesOnlyConfiguredPaths()
    {
        var configured = new[] { @"C:\Games\Configured\", @"C:\Games\Other\" };
        var (action, runner) = Create(configured);
        var arbitraryDir = Path.Combine(Path.GetTempPath(), "anywhere", Guid.NewGuid().ToString("N"));
        action.ExecutablePath = Path.Combine(arbitraryDir, "game.exe");

        action.Apply(new SystemStateSnapshot());

        var added = runner.CallsFor(AddScript).Select(c => Assert.Single(c.Values)).ToList();
        Assert.Equal(configured, added);
        Assert.DoesNotContain(runner.Calls.SelectMany(c => c.Values),
            v => v.StartsWith(arbitraryDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Revert_RemovesExactlyWhatApplyCreated()
    {
        const string userManaged = @"C:\Games\UserManaged\";
        const string addFails = @"C:\Games\AddFails\";
        const string addTimesOut = @"C:\Games\AddTimesOut\";
        const string created = @"C:\Games\Created\";
        var (action, runner) = Create(new[] { userManaged, addFails, addTimesOut, created });
        runner.Respond = call =>
        {
            if (call.Script == GetScript) return FakePowerShellRunner.Exit(0, "C:\\Games\\usermanaged\\\r\nC:\\Other\\\r\n");
            if (call.Script == AddScript && call.Values[0] == addFails) return FakePowerShellRunner.Exit(1);
            if (call.Script == AddScript && call.Values[0] == addTimesOut)
                return new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: true, Exited: false);
            return FakePowerShellRunner.Exit(0);
        };

        action.Apply(new SystemStateSnapshot());

        Assert.Equal(new[] { addFails, addTimesOut, created },
            runner.CallsFor(AddScript).Select(c => c.Values[0]));
        var record = action.GetCrashRevertRecord();
        Assert.NotNull(record);
        Assert.Equal("defender", record!.Kind);
        using (var doc = JsonDocument.Parse(record.Payload))
        {
            Assert.Equal(new[] { created },
                doc.RootElement.GetProperty("paths").EnumerateArray().Select(e => e.GetString()));
        }

        action.Revert(new SystemStateSnapshot());

        Assert.Equal(new[] { created }, runner.CallsFor(RemoveScript).Select(c => c.Values[0]));
        Assert.Null(action.GetCrashRevertRecord());
    }

    public static IEnumerable<object[]> UnconfirmedResults() => new[]
    {
        new object[] { new ProcessRunResult(1, string.Empty, string.Empty, TimedOut: false, Exited: true) },
        new object[] { new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: true, Exited: false) },
        new object[] { new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: false, Exited: false) },
    };

    [Theory]
    [MemberData(nameof(UnconfirmedResults))]
    public void Revert_RemovalNotConfirmed_PathStaysTrackedUntilRemoved(ProcessRunResult removeResult)
    {
        const string path = @"C:\Games\Created\";
        var (action, runner) = Create(new[] { path });
        action.Apply(new SystemStateSnapshot());
        runner.Respond = call => call.Script == RemoveScript ? removeResult : FakePowerShellRunner.Exit(0);

        action.Revert(new SystemStateSnapshot());

        var record = action.GetCrashRevertRecord();
        Assert.NotNull(record);
        using (var doc = JsonDocument.Parse(record!.Payload))
        {
            Assert.Equal(new[] { path },
                doc.RootElement.GetProperty("paths").EnumerateArray().Select(e => e.GetString()));
        }

        runner.Respond = _ => FakePowerShellRunner.Exit(0);
        action.Revert(new SystemStateSnapshot());

        Assert.Equal(new[] { path, path }, runner.CallsFor(RemoveScript).Select(c => c.Values[0]));
        Assert.Null(action.GetCrashRevertRecord());
    }

    [Fact]
    public void Apply_ExistingNonAsciiExclusion_IsTreatedAsUsers()
    {
        const string path = "C:\\Users\\J\u00fcrgen\\AppData\\Local\\osu!\\";
        var (action, runner) = Create(new[] { path });
        runner.Respond = call => call.Script == GetScript
            ? FakePowerShellRunner.Exit(0, path + "\r\n")
            : FakePowerShellRunner.Exit(0);

        action.Apply(new SystemStateSnapshot());
        action.Revert(new SystemStateSnapshot());

        Assert.Empty(runner.CallsFor(AddScript));
        Assert.Empty(runner.CallsFor(RemoveScript));
        Assert.Null(action.GetCrashRevertRecord());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void Apply_ExistingListUnreadable_AddsNothing(int exitCode, bool exited)
    {
        var (action, runner) = Create(new[] { @"C:\Games\A\" });
        runner.Respond = call => call.Script == GetScript
            ? new ProcessRunResult(exitCode, string.Empty, string.Empty, TimedOut: !exited, Exited: exited)
            : FakePowerShellRunner.Exit(0);

        action.Apply(new SystemStateSnapshot());
        action.Revert(new SystemStateSnapshot());

        Assert.Empty(runner.CallsFor(AddScript));
        Assert.Empty(runner.CallsFor(RemoveScript));
    }
}
