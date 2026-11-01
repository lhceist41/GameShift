using GameShift.Core.System;
using Xunit;

namespace GameShift.Tests.SystemRunner;

public class LaunchOptionsTests
{
    [Fact]
    public void NoArguments_IsAStartTheUserMade()
    {
        Assert.False(LaunchOptions.Parse([]).StartedAtLogon);
        Assert.False(LaunchOptions.Parse(null).StartedAtLogon);
        Assert.False(LaunchOptions.None.StartedAtLogon);
    }

    [Theory]
    [InlineData("--logon")]
    [InlineData("--LOGON")]
    [InlineData("--Logon")]
    public void LogonArgument_IsRecognisedInAnyCase(string argument)
    {
        Assert.True(LaunchOptions.Parse([argument]).StartedAtLogon);
    }

    [Fact]
    public void UnknownArguments_AreIgnored()
    {
        Assert.True(LaunchOptions.Parse(["--minimized", "--logon", "extra"]).StartedAtLogon);
        Assert.False(LaunchOptions.Parse(["--minimized", "/logon", "--logon=1", "logon"]).StartedAtLogon);
    }

    [Fact]
    public void LogonArgument_IsTheOneTheStartupTaskPasses()
    {
        Assert.Equal("--logon", StartupManager.LogonArgument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--logon")]
    [InlineData("--restore restore-tweaks /restore-tweaks --remove-task --restore-tweaks=1")]
    public void WithoutAHeadlessFlag_TheAppStarts(string commandLine)
    {
        var options = LaunchOptions.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.False(options.IsHeadless);
        Assert.False(options.RestoreTweaks);
        Assert.False(options.RemoveTasks);
    }

    [Theory]
    [InlineData("--restore-tweaks", true, false)]
    [InlineData("--RESTORE-TWEAKS", true, false)]
    [InlineData("--remove-tasks", false, true)]
    [InlineData("--Remove-Tasks", false, true)]
    public void HeadlessFlags_AreRecognisedInAnyCase_AndMeanNoAppStart(string argument, bool restore, bool removeTasks)
    {
        var options = LaunchOptions.Parse(["--logon", argument]);

        Assert.True(options.IsHeadless);
        Assert.Equal(restore, options.RestoreTweaks);
        Assert.Equal(removeTasks, options.RemoveTasks);
        Assert.True(options.StartedAtLogon);
    }

    [Fact]
    public void BothHeadlessFlags_AreKept()
    {
        var options = LaunchOptions.Parse(["--remove-tasks", "--restore-tweaks"]);

        Assert.True(options.RestoreTweaks);
        Assert.True(options.RemoveTasks);
    }

    [Fact]
    public void HeadlessArguments_AreTheOnesTheUninstallerPasses()
    {
        Assert.Equal("--restore-tweaks", LaunchOptions.RestoreTweaksArgument);
        Assert.Equal("--remove-tasks", LaunchOptions.RemoveTasksArgument);
    }
}
