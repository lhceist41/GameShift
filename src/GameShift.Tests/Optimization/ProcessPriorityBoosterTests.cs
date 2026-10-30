using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Optimization;

/// <summary>
/// The booster changes only the game's own priority. When that boost fails nothing else was changed,
/// so the result must be Failed rather than an Applied entry the engine would later "revert".
/// </summary>
public class ProcessPriorityBoosterTests
{
    private const int GamePid = 7300;

    private readonly FakeProcessPriorities _processes = new();

    private ProcessPriorityBooster NewBooster(int processId = GamePid)
    {
        var booster = new ProcessPriorityBooster { Processes = _processes };
        var profile = new GameProfile
        {
            Id = "game",
            GameName = "Game",
            ExecutableName = "Game.exe",
            ProcessId = processId,
        };
        booster.CanApply(new SystemContext { Profile = profile, Snapshot = new SystemStateSnapshot() });
        return booster;
    }

    [Fact]
    public void BoostDenied_ReportsFailedAndIsNotTracked()
    {
        _processes.Add(GamePid, "Game", ProcessPriorityClass.Normal);
        _processes.GetPriorityFailure = new Win32Exception(5); // access denied by an anti-cheat
        var booster = NewBooster();

        var applied = booster.Apply();

        Assert.Equal(OptimizationState.Failed, applied.State);
        Assert.False(booster.IsApplied);
    }

    [Fact]
    public void UnexpectedFailure_ReportsFailed()
    {
        _processes.Add(GamePid, "Game", ProcessPriorityClass.Normal);
        _processes.GetNameFailure = new NotSupportedException("unexpected");
        var booster = NewBooster();

        var applied = booster.Apply();

        Assert.Equal(OptimizationState.Failed, applied.State);
        Assert.False(booster.IsApplied);
    }

    [Fact]
    public void GamePidReusedByAnotherProcess_IsNotGivenTheGamesOldPriority()
    {
        _processes.Add(GamePid, "Game", ProcessPriorityClass.Normal);
        var booster = NewBooster();
        Assert.Equal(OptimizationState.Applied, booster.Apply().State);
        Assert.Equal(ProcessPriorityClass.High, _processes.PriorityOf(GamePid));

        _processes.Add(GamePid, "OtherApp", ProcessPriorityClass.AboveNormal); // game exited, PID reused
        Assert.Equal(OptimizationState.Reverted, booster.Revert().State);

        Assert.Equal(ProcessPriorityClass.AboveNormal, _processes.PriorityOf(GamePid));
    }

    [Fact]
    public void NoGameProcess_IsANoOpWithNothingJournaled()
    {
        var booster = NewBooster(processId: 0);

        var applied = booster.Apply();

        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.Equal("""{"ifeoKeys":[]}""", applied.OriginalValue);
        Assert.Empty(_processes.Writes);
    }

    [Fact]
    public void JournalRecordOfTheLivePath_Reverts()
    {
        // The record earlier versions wrote when the boost used the live API (no IFEO entry).
        var result = new ProcessPriorityBooster().RevertFromRecord("""{"ifeoKeys":[]}""");

        Assert.Equal(OptimizationState.Reverted, result.State);
    }
}
