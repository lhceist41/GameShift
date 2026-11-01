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
}
