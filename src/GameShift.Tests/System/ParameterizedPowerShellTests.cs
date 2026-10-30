using System.Diagnostics;
using System.IO;
using System.Text;
using GameShift.Core.System;
using Xunit;

// Namespace is deliberately NOT "GameShift.Tests.System" (it would shadow the BCL System namespace).
namespace GameShift.Tests.SystemRunner;

/// <summary>
/// Tests for <see cref="ParameterizedPowerShell"/>. Start infos are only built and inspected, never
/// started; the <see cref="ParameterizedPowerShell.Run"/> tests use cmd.exe children, so no
/// PowerShell runs.
/// </summary>
public class ParameterizedPowerShellTests
{
    private const string Script = "Add-MpPreference -ExclusionPath $env:GS_ARG0";
    private const string Module = "ConfigDefender";

    private static string SystemModules =>
        Path.Combine(global::System.Environment.SystemDirectory, @"WindowsPowerShell\v1.0\Modules");

    private static string Cmd => Path.Combine(global::System.Environment.SystemDirectory, "cmd.exe");

    private static string Expected(string script) =>
        "-NoProfile -NonInteractive -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes("Import-Module -Name $env:GS_MODULE -ErrorAction Stop\n" + script));

    [Theory]
    [InlineData("C:\\Games\\it's\\")]
    [InlineData("C:\\Games\\a\u2018b\\")]
    [InlineData("C:\\Games\\a\u2019b\\")]
    [InlineData("C:\\Games\\a\u201Ab\\")]
    [InlineData("C:\\Games\\a\u201Bb\\")]
    [InlineData("C:\\Games\\a;b\\")]
    [InlineData("C:\\Games\\$(Get-Date)\\")]
    [InlineData("C:\\Games\\a`b\\")]
    [InlineData("C:\\Games\\a\nb\\")]
    [InlineData("\" -Command Get-Date \"")]
    public void CreateStartInfo_AnyValue_ArgumentLineIsTheConstantScriptAndValueIsInEnvironment(string value)
    {
        var psi = ParameterizedPowerShell.CreateStartInfo(Module, Script, value);

        Assert.Equal(Expected(Script), psi.Arguments);
        Assert.Empty(psi.ArgumentList);
        Assert.Equal(value, psi.Environment["GS_ARG0"]);
        Assert.False(psi.Environment.ContainsKey("GS_ARG1"));
        Assert.False(psi.UseShellExecute);
        Assert.True(Path.IsPathFullyQualified(psi.FileName));
        Assert.EndsWith(@"\WindowsPowerShell\v1.0\powershell.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateStartInfo_SeveralValues_MapToNumberedVariablesInOrder()
    {
        var psi = ParameterizedPowerShell.CreateStartInfo(Module, "$env:GS_ARG0 + $env:GS_ARG1", "first", "second");

        Assert.Equal("first", psi.Environment["GS_ARG0"]);
        Assert.Equal("second", psi.Environment["GS_ARG1"]);
        Assert.False(psi.Environment.ContainsKey("GS_ARG2"));
    }

    [Fact]
    public void CreateStartInfo_InheritedArgumentVariable_IsNotPassedToChild()
    {
        const string name = "GS_ARG3";
        global::System.Environment.SetEnvironmentVariable(name, "inherited");
        try
        {
            var psi = ParameterizedPowerShell.CreateStartInfo(Module, Script, "value");

            Assert.False(psi.Environment.ContainsKey(name));
            Assert.Equal("value", psi.Environment["GS_ARG0"]);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void CreateStartInfo_ChildResolvesModulesOnlyFromSystem32AndImportsByAbsolutePath()
    {
        var psi = ParameterizedPowerShell.CreateStartInfo("NetSecurity", "Get-NetFirewallRule -DisplayName $env:GS_ARG0", "rule");

        Assert.Equal(SystemModules, psi.Environment["PSModulePath"], ignoreCase: true);
        var manifest = psi.Environment["GS_MODULE"];
        Assert.NotNull(manifest);
        Assert.True(Path.IsPathFullyQualified(manifest!));
        Assert.Equal(Path.Combine(SystemModules, "NetSecurity", "NetSecurity.psd1"), manifest, ignoreCase: true);
        Assert.Equal(Expected("Get-NetFirewallRule -DisplayName $env:GS_ARG0"), psi.Arguments);
    }

    [Fact]
    public void CreateStartInfo_ModuleNotAPlainName_Throws()
    {
        Assert.Throws<ArgumentException>(() => ParameterizedPowerShell.CreateStartInfo("", Script, "value"));
        Assert.Throws<ArgumentException>(() => ParameterizedPowerShell.CreateStartInfo(@"..\Other", Script, "value"));
        Assert.Throws<ArgumentException>(() => ParameterizedPowerShell.CreateStartInfo("Config Defender", Script, "value"));
    }

    [Fact]
    public void CreateStartInfo_ValueWithNul_Throws()
    {
        Assert.Throws<ArgumentException>(() => ParameterizedPowerShell.CreateStartInfo(Module, Script, "a\0b"));
    }

    [Fact]
    public void Run_PassesEnvironmentToChildAndCapturesOutput()
    {
        var psi = new ProcessStartInfo(Cmd, "/c set GS_ARG0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["GS_ARG0"] = "from-environment";

        var r = ParameterizedPowerShell.Run(psi, 5000);

        Assert.True(r.Exited);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("GS_ARG0=from-environment", r.StdOut.Trim());
    }

    [Fact]
    public void Run_DecodesStdoutAsUtf8()
    {
        // `type` copies the file's UTF-8 bytes to the redirected stdout unchanged.
        using var temp = new GameShift.Tests.TestHelpers.TempPath();
        var file = temp.GetFile("utf8.txt");
        File.WriteAllText(file, "C:\\Users\\J\u00fcrgen\\osu!", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var psi = new ProcessStartInfo(Cmd, $"/c type \"{file}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var r = ParameterizedPowerShell.Run(psi, 5000);

        Assert.True(r.Exited);
        Assert.Equal("C:\\Users\\J\u00fcrgen\\osu!", r.StdOut.Trim());
    }

    [Fact]
    public void Run_WedgedChild_TimesOutWithoutHanging()
    {
        var psi = new ProcessStartInfo(Cmd, "/c ping -n 20 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var sw = Stopwatch.StartNew();

        var r = ParameterizedPowerShell.Run(psi, 1000);
        sw.Stop();

        Assert.True(r.TimedOut);
        Assert.False(r.Exited);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Run took {sw.Elapsed}");
    }
}
