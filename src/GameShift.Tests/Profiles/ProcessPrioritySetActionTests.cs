using System.Diagnostics;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Profiles;

/// <summary>
/// The launcher priority action shares Battle.net and LeagueClientUx with the game profile's
/// launcher demotion. Whichever restores last must not leave them lowered.
/// </summary>
public class ProcessPrioritySetActionTests
{
    private const int ClientPid = 4100;

    private readonly FakeProcessPriorities _processes = new();

    private ProcessPrioritySetAction NewAction() =>
        new("Client Priority Reduction", "LeagueClientUx.exe", ProcessPriorityClass.BelowNormal)
        {
            Processes = _processes,
        };

    [Fact]
    public void ClientAlreadyLoweredByAnotherWriter_IsLeftToThatWriter()
    {
        _processes.Add(ClientPid, "LeagueClientUx", ProcessPriorityClass.BelowNormal);
        var action = NewAction();

        action.Apply(new SystemStateSnapshot());
        // The other writer restores the client before the action's revert runs.
        _processes.SetByOtherWriter(ClientPid, ProcessPriorityClass.Normal);
        action.Revert(new SystemStateSnapshot());

        Assert.Empty(_processes.Writes);
        Assert.Equal(ProcessPriorityClass.Normal, _processes.PriorityOf(ClientPid));
    }

    [Fact]
    public void ClientAtNormal_IsLoweredAndRestored()
    {
        _processes.Add(ClientPid, "LeagueClientUx", ProcessPriorityClass.Normal);
        var action = NewAction();

        action.Apply(new SystemStateSnapshot());
        Assert.Equal(ProcessPriorityClass.BelowNormal, _processes.PriorityOf(ClientPid));

        action.Revert(new SystemStateSnapshot());
        Assert.Equal(ProcessPriorityClass.Normal, _processes.PriorityOf(ClientPid));
    }

    [Fact]
    public void ClientPidReusedByAnotherProcess_IsNotRestored()
    {
        _processes.Add(ClientPid, "LeagueClientUx", ProcessPriorityClass.Normal);
        var action = NewAction();
        action.Apply(new SystemStateSnapshot());

        _processes.Add(ClientPid, "OtherApp", ProcessPriorityClass.High); // client exited, PID reused
        action.Revert(new SystemStateSnapshot());

        Assert.Equal(ProcessPriorityClass.High, _processes.PriorityOf(ClientPid));
        Assert.Single(_processes.Writes);
    }

    [Theory]
    [InlineData("Battle.net", "Battle.net")]
    [InlineData("Battle.net.exe", "Battle.net")]
    [InlineData("LeagueClientUx", "LeagueClientUx")]
    [InlineData("LeagueClientUx.EXE", "LeagueClientUx")]
    public void ConfiguredName_DropsOnlyATrailingExe(string configured, string processName)
    {
        _processes.Add(ClientPid, processName, ProcessPriorityClass.Normal);
        var action = new ProcessPrioritySetAction("Launcher Priority Reduction", configured, ProcessPriorityClass.BelowNormal)
        {
            Processes = _processes,
        };

        action.Apply(new SystemStateSnapshot());
        Assert.Equal(ProcessPriorityClass.BelowNormal, _processes.PriorityOf(ClientPid));

        action.Revert(new SystemStateSnapshot());
        Assert.Equal(ProcessPriorityClass.Normal, _processes.PriorityOf(ClientPid));
    }

    [Theory]
    [InlineData(ProcessPriorityClass.Normal, ProcessPriorityClass.BelowNormal, false)]
    [InlineData(ProcessPriorityClass.High, ProcessPriorityClass.BelowNormal, false)]
    [InlineData(ProcessPriorityClass.BelowNormal, ProcessPriorityClass.BelowNormal, true)]
    [InlineData(ProcessPriorityClass.Idle, ProcessPriorityClass.BelowNormal, true)]
    [InlineData(ProcessPriorityClass.Normal, ProcessPriorityClass.High, false)]
    [InlineData(ProcessPriorityClass.Idle, ProcessPriorityClass.High, false)]
    [InlineData(ProcessPriorityClass.High, ProcessPriorityClass.High, true)]
    public void NeedsNoChange_OnlyLowersProcessesAboveADemotionTarget(
        ProcessPriorityClass current, ProcessPriorityClass target, bool expected)
    {
        Assert.Equal(expected, ProcessPriorityOrder.NeedsNoChange(current, target));
    }
}
